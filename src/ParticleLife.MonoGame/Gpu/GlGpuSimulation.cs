using System.Runtime.InteropServices;
using ParticleLife.Core.Execution;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Owns one float32 resident simulation on the MonoGame graphics thread and current OpenGL context.</summary>
/// <remarks>Normal steps read only sixteen-byte build/completion status, never particle state. The
/// completed input buffer remains renderable while a candidate is indexed and integrated. Each poll
/// performs at most one fence query and one bounded receiver submission; no positive fence wait occurs.
/// The unordered atomic scatter can change summation order, so action re-simulation is not bitwise
/// repeatable. Explicit captures own copied actual float values and retain the binary64 host clock.
/// The owner must stop submissions before changing rules, replacing state or transferring ownership.</remarks>
internal sealed unsafe partial class GlGpuSimulation : IResidentSimulation, IResidentSpatialObservation
{
    /// <summary>Targets this many estimated source-entry visits in one independently queued receiver batch.</summary>
    /// <remarks>Nine times the maximum hash-bucket occupancy bounds one receiver's visited entries,
    /// including hash collisions. Dividing this budget by that bound chooses a batch size, never a
    /// neighbour limit. At least one receiver is processed, even if its own traversal exceeds the
    /// estimate. This is not a GPU-duration guarantee; cancellation waits for the current batch fence.</remarks>
    private const long CandidateVisitsPerSubmission = 536_870_912;

    /// <summary>Caps receiver parallelism per submission while retaining all members in subsequent batches.</summary>
    /// <remarks>Every batch reads the same completed input. No partially integrated output or clock
    /// publishes; the entire candidate must finish and pass numerical-status validation first.</remarks>
    private const int MaximumReceiversPerSubmission = 65_536;
    private readonly GpuComputeApi api;
    private readonly List<GpuProgram> programs = [];
    private readonly GpuProgram clear, count, scan, addScan, scatter, integrate, summarize, reduce;
    private GpuWorkspace workspace = null!;
    private GpuModelData model = null!;
    private uint rules;
    private nint stepFence, captureFence, summaryFence;
    private StepStage stage;
    private bool cancelStep, disposed;
    private int nextReceiver, receiverChunk;
    private int[] speciesCounts = [];
    private ParticleSnapshot? currentCapture;
    private SimulationBoundary captureBoundary, summaryBoundary;
    private long sequence;

    internal GlGpuSimulation(SimulationConfiguration configuration, ParticleSnapshot state)
    {
        // Exact loading and explicit precision conversion are separate decisions made by the caller.
        CaptureTransfer.Convert(state, StatePrecision.Float32);
        GpuModelData candidateModel = new(configuration);
        GpuParticle[] particles = GpuParticle.Encode(state);
        candidateModel.ValidateState(particles);
        api = new();
        try
        {
            using GpuComputeScope scope = new(api);
            clear = Program(GpuKernels.Clear);
            count = Program(GpuKernels.Count);
            scan = Program(GpuKernels.Scan);
            addScan = Program(GpuKernels.AddScanOffsets);
            scatter = Program(GpuKernels.Scatter);
            integrate = Program(GpuKernels.Integrate);
            summarize = Program(GpuKernels.SummarizeParticles);
            reduce = Program(GpuKernels.ReduceSummaries);
            workspace = new(api, particles);
            rules = CreateRules(candidateModel);
            model = candidateModel;
            SetBoundary(state);
            Execution = new(ExecutionHardware.Gpu, StatePrecision.Float32, GpuModelData.Implementation,
                RuntimeInformation.FrameworkDescription,
                Marshal.PtrToStringAnsi(api.Gl.GetString(0x1F01)),
                "OpenGL " + Marshal.PtrToStringAnsi(api.Gl.GetString(0x1F02)),
                Marshal.PtrToStringAnsi(api.Gl.GetString(0x1F00)), false);
            api.Gl.Check("constructing GPU simulation");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public SimulationConfiguration Configuration => model.Configuration;
    public SimulationBoundary Boundary { get; private set; }
    public ExecutionProvenance Execution { get; }
    public bool IsStepPending => stage != StepStage.Idle;
    public bool IsCapturePending => captureFence != 0;
    public bool HasCurrentCapture => currentCapture is not null;
    public ReadOnlyMemory<int> SpeciesCounts => speciesCounts;
    public ResidentSummary? LatestSummary { get; private set; }
    public long FullStateReadbacks { get; private set; }
    public long MetadataReadbackBytes { get; private set; }

    /// <summary>Gets the completed buffer for immediate draw submission on this owner/context.</summary>
    /// <remarks>The handle allocation survives until Replace/Dispose, but its completed-state meaning
    /// must not be retained across later steps. Native commands share one ordered context, so submitted
    /// vertex reads complete before a subsequent step reuses that allocation as an output.</remarks>
    internal uint CompletedBuffer => workspace.Input;
    internal long CompletedSequence => sequence;
    internal GpuComputeApi Api => api;

    public void SubmitStep()
    {
        EnsureOwner();
        if (IsStepPending || IsCapturePending)
        {
            throw new InvalidOperationException("Only one GPU step or explicit capture may be pending.");
        }
        if (Boundary.Step == long.MaxValue || !double.IsFinite(Boundary.Time + Configuration.TimeStep)
            || Boundary.Time + Configuration.TimeStep <= Boundary.Time)
        {
            throw new InvalidOperationException("The simulation clock cannot represent another completed step.");
        }
        measurements?.Begin(Boundary.Step + 1, workspace.Count);
        using GpuMeasurementInterval measurementInterval = measurements?.Interval(polling: false) ?? default;
        using GpuComputeScope scope = new(api);
        int timingToken = measurements?.BeginGroup(GpuCommandStage.Grid) ?? -1;
        BindGrid();
        ClearCounts(resetStatus: true);
        count.Configure(model, workspace.Count, workspace.BucketCount);
        Dispatch(workspace.Count);
        ScanBuckets();
        BindGrid();
        ClearCounts(resetStatus: false);
        scatter.Configure(model, workspace.Count, workspace.BucketCount);
        Dispatch(workspace.Count);
        api.MemoryBarrier(GpuComputeApi.BufferUpdateBarrier);
        api.Gl.Check("submitting GPU grid construction");
        measurements?.EndGroup(timingToken);
        stepFence = api.Fence();
        stage = StepStage.Grid;
        cancelStep = false;
        nextReceiver = 0;
    }

    public bool PollStep()
    {
        EnsureOwner();
        using GpuMeasurementInterval measurementInterval = measurements?.Interval(polling: true) ?? default;
        if (!IsStepPending || !api.IsComplete(stepFence))
        {
            return false;
        }
        DeleteFence(ref stepFence);
        if (cancelStep)
        {
            stage = StepStage.Idle;
            measurements?.Complete(wasPublished: false, RetainedDeviceBufferBytes);
            return false;
        }
        using GpuComputeScope scope = new(api);
        if (stage == StepStage.Grid)
        {
            GpuStatus status = ReadStatus();
            RejectFailedCandidate(status);
            // Larger independent receiver batches can fill more workgroups. A single fence still
            // bounds queued integration work; cancellation retains the prior completed input buffer.
            // Every receiver examines every visited entry, even when its own work exceeds the budget.
            long visits = Math.Max(1L, (long)status.MaximumOccupancy * 9);
            receiverChunk = (int)Math.Clamp(CandidateVisitsPerSubmission / visits, 1, MaximumReceiversPerSubmission);
            measurements?.RecordGrid(status.MaximumOccupancy, receiverChunk);
            stage = StepStage.Forces;
        }
        else if (nextReceiver >= workspace.Count)
        {
            GpuStatus status = ReadStatus();
            RejectFailedCandidate(status);
            (workspace.Input, workspace.Output) = (workspace.Output, workspace.Input);
            Boundary = new(workspace.Count, checked(Boundary.Step + 1), Boundary.Time + Configuration.TimeStep);
            currentCapture = null;
            sequence++;
            stage = StepStage.Idle;
            measurements?.Complete(wasPublished: true, RetainedDeviceBufferBytes);
            return true;
        }
        int timingToken = measurements?.BeginGroup(GpuCommandStage.Integration) ?? -1;
        BindGrid();
        integrate.Configure(model, workspace.Count, workspace.BucketCount);
        int receivers = Math.Min(receiverChunk, workspace.Count - nextReceiver);
        api.Uniform4i(integrate.ReceiverRange, nextReceiver, receivers, 0, 0);
        Dispatch(receivers);
        api.MemoryBarrier(GpuComputeApi.StorageBarrier | GpuComputeApi.VertexBarrier | GpuComputeApi.BufferUpdateBarrier);
        api.Gl.Check("submitting GPU integration");
        measurements?.EndGroup(timingToken);
        nextReceiver += receivers;
        stepFence = api.Fence();
        return false;
    }

    public void CancelPendingStep()
    {
        EnsureOwner();
        cancelStep = true;
    }

    public void BeginCapture()
    {
        EnsureOwner();
        if (IsStepPending || IsCapturePending)
        {
            throw new InvalidOperationException("Capture starts only at an idle completed numerical boundary.");
        }
        using GpuComputeScope scope = new(api);
        api.MemoryBarrier(GpuComputeApi.BufferUpdateBarrier);
        api.Gl.BindBuffer(GpuComputeApi.CopyReadBuffer, workspace.Input);
        api.Gl.BindBuffer(GpuComputeApi.CopyWriteBuffer, workspace.Staging);
        if (workspace.Count != 0)
        {
            api.CopyBufferSubData(GpuComputeApi.CopyReadBuffer, GpuComputeApi.CopyWriteBuffer, 0, 0,
                checked((nint)((long)workspace.Count * sizeof(GpuParticle))));
        }
        api.Gl.Check("requesting explicit GPU state copy");
        captureBoundary = Boundary;
        captureFence = api.Fence();
    }

    public ParticleSnapshot? PollCapture()
    {
        EnsureOwner();
        if (captureFence == 0 || !api.IsComplete(captureFence))
        {
            return null;
        }
        DeleteFence(ref captureFence);
        using GpuComputeScope scope = new(api);
        GpuParticle[] particles = new GpuParticle[captureBoundary.Count];
        fixed (GpuParticle* pointer = particles)
        {
            if (particles.Length != 0)
            {
                Read(workspace.Staging, checked((nint)((long)particles.Length * sizeof(GpuParticle))), pointer);
            }
        }
        api.Gl.Check("reading explicit GPU capture");
        ParticleSnapshot result = GpuParticle.Decode(particles, captureBoundary.Time, captureBoundary.Step);
        FullStateReadbacks++;
        currentCapture = result;
        return result;
    }

    public ParticleStateView ObserveCaptured()
    {
        if (currentCapture is null) { throw new StateCaptureRequiredException(); }
        return currentCapture.Observe();
    }

    public void Reconfigure(SimulationConfiguration configuration, ParticleSnapshot? replacementState = null)
    {
        EnsureIdle();
        if (replacementState is not null)
        {
            if (replacementState.Step != Boundary.Step || replacementState.Time != Boundary.Time)
            {
                throw new ArgumentException("A live GPU edit must retain the completed clock.", nameof(replacementState));
            }
            Replace(configuration, replacementState);
            return;
        }
        if (configuration.Matrix.SpeciesCount < speciesCounts.Length
            && speciesCounts.AsSpan(configuration.Matrix.SpeciesCount).ContainsAnyExcept(0))
        {
            throw new ArgumentException("The candidate matrix removes a species still present in resident state.", nameof(configuration));
        }
        GpuModelData candidate = new(configuration);
        using GpuComputeScope scope = new(api);
        uint candidateRules = CreateRules(candidate);
        uint previous = rules;
        rules = candidateRules;
        model = candidate;
        Array.Resize(ref speciesCounts, configuration.Matrix.SpeciesCount);
        api.Gl.DeleteBuffers(1, ref previous);
        LatestSummary = null;
    }

    public void Replace(SimulationConfiguration configuration, ParticleSnapshot state)
    {
        EnsureIdle();
        CaptureTransfer.Convert(state, StatePrecision.Float32);
        GpuModelData candidateModel = new(configuration);
        GpuParticle[] particles = GpuParticle.Encode(state);
        candidateModel.ValidateState(particles);
        using GpuComputeScope scope = new(api);
        GpuWorkspace candidate = new(api, particles);
        uint candidateRules;
        try
        {
            candidateRules = CreateRules(candidateModel);
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
        GpuWorkspace previousWorkspace = workspace;
        uint previousRules = rules;
        workspace = candidate;
        rules = candidateRules;
        model = candidateModel;
        SetBoundary(state);
        previousWorkspace.Dispose();
        api.Gl.DeleteBuffers(1, ref previousRules);
    }

    public void RequestSummary()
    {
        EnsureOwner();
        if (summaryFence != 0)
        {
            return;
        }
        using GpuComputeScope scope = new(api);
        summarize.Use();
        Bind(0, workspace.Input);
        Bind(1, workspace.Summaries[0]);
        api.Gl.Uniform1i(summarize.Count, workspace.Count);
        Dispatch(workspace.Count);
        for (int index = 1; index < workspace.Summaries.Length; index++)
        {
            reduce.Use();
            Bind(0, workspace.Summaries[index - 1]);
            Bind(1, workspace.Summaries[index]);
            api.Gl.Uniform1i(reduce.Count, workspace.SummaryCounts[index - 1]);
            Dispatch(workspace.SummaryCounts[index - 1]);
        }
        api.MemoryBarrier(GpuComputeApi.BufferUpdateBarrier);
        api.Gl.Check("submitting GPU speed summary");
        summaryBoundary = Boundary;
        summaryFence = api.Fence();
    }

    public void PollSummary()
    {
        EnsureOwner();
        if (summaryFence == 0 || !api.IsComplete(summaryFence))
        {
            return;
        }
        DeleteFence(ref summaryFence);
        using GpuComputeScope scope = new(api);
        GpuSummary summary;
        Read(workspace.Summaries[^1], sizeof(GpuSummary), &summary);
        MetadataReadbackBytes += sizeof(GpuSummary);
        api.Gl.Check("reading GPU speed summary");
        if (summary.Flags == 0 && float.IsFinite(summary.MeanSpeed) && float.IsFinite(summary.MaximumSpeed))
        {
            LatestSummary = new(checked((int)summary.Count), summary.MeanSpeed, summary.MaximumSpeed,
                summaryBoundary.Step, summaryBoundary.Time);
        }
    }

    private GpuProgram Program(string source)
    {
        GpuProgram program = new(api, source);
        programs.Add(program);
        return program;
    }

    private uint CreateRules(GpuModelData candidate)
    {
        fixed (GpuPairRule* pointer = candidate.Rules)
        {
            return api.CreateBuffer(checked((nint)((long)candidate.Rules.Length * sizeof(GpuPairRule))), pointer);
        }
    }

    private void SetBoundary(ParticleSnapshot state)
    {
        InvalidateSpatialObservation();
        Boundary = new(state.Count, state.Step, state.Time);
        speciesCounts = new int[Configuration.Matrix.SpeciesCount];
        foreach (int species in state.Species)
        {
            speciesCounts[species]++;
        }
        currentCapture = state;
        LatestSummary = null;
        sequence++;
    }

    private void Bind(uint index, uint buffer) => api.BindBufferBase(GpuComputeApi.StorageBuffer, index, buffer);

    private void BindGrid()
    {
        Bind(0, workspace.Input);
        Bind(1, workspace.Output);
        Bind(2, workspace.Counts);
        Bind(3, workspace.Offsets);
        Bind(4, workspace.Indices);
        Bind(5, workspace.Cells);
        Bind(6, rules);
        Bind(7, workspace.Status);
    }

    private void ClearCounts(bool resetStatus)
    {
        clear.Use();
        api.Gl.Uniform1i(clear.BucketCount, workspace.BucketCount);
        api.Gl.Uniform1i(clear.ClearStatus, resetStatus ? 1 : 0);
        Dispatch(workspace.BucketCount);
    }

    private void ScanBuckets()
    {
        for (int level = 0; level < workspace.ScanCounts.Length; level++)
        {
            scan.Use();
            Bind(0, level == 0 ? workspace.Counts : workspace.ScanSums[level - 1]);
            Bind(1, workspace.ScanOffsets[level]);
            Bind(2, workspace.ScanSums[level]);
            api.Gl.Uniform1i(scan.Count, workspace.ScanCounts[level]);
            api.DispatchCompute((uint)GpuWorkspace.DivideUp(workspace.ScanCounts[level], 512), 1, 1);
            measurements?.Dispatch();
            api.MemoryBarrier(GpuComputeApi.StorageBarrier);
        }
        for (int level = workspace.ScanCounts.Length - 2; level >= 0; level--)
        {
            addScan.Use();
            Bind(0, workspace.ScanOffsets[level]);
            Bind(1, workspace.ScanOffsets[level + 1]);
            api.Gl.Uniform1i(addScan.Count, workspace.ScanCounts[level]);
            Dispatch(workspace.ScanCounts[level]);
        }
    }

    private void Dispatch(int count)
    {
        api.DispatchCompute((uint)Math.Max(1, GpuWorkspace.DivideUp(count, 256)), 1, 1);
        measurements?.Dispatch();
        api.MemoryBarrier(GpuComputeApi.StorageBarrier);
    }

    private void Read(uint buffer, nint bytes, void* destination)
    {
        api.Gl.BindBuffer(GpuComputeApi.StorageBuffer, buffer);
        api.GetBufferSubData(GpuComputeApi.StorageBuffer, 0, bytes, destination);
    }

    private GpuStatus ReadStatus()
    {
        GpuStatus result;
        Read(workspace.Status, sizeof(GpuStatus), &result);
        MetadataReadbackBytes += sizeof(GpuStatus);
        api.Gl.Check("reading GPU completion status");
        return result;
    }

    private void RejectFailedCandidate(GpuStatus status)
    {
        if (status.Flags != 0)
        {
            stage = StepStage.Idle;
            measurements?.Complete(wasPublished: false, RetainedDeviceBufferBytes);
            throw new InvalidOperationException($"GPU candidate rejected at logical slot {status.FailedSlot} (status {status.Flags}). "
                + "The previous completed state is retained; change the model or capture it for CPU continuation.");
        }
    }

    private void EnsureOwner()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        api.Gl.EnsureOwner();
    }

    private void EnsureIdle()
    {
        EnsureOwner();
        PollSummary();
        // An optional observation may be abandoned. Native commands already submitted remain ordered;
        // deleting their fence does not cancel writes or make their backing buffers reusable early.
        DeleteFence(ref summaryFence);
        if (IsStepPending || IsCapturePending)
        {
            throw new InvalidOperationException("Resident replacement requires completed step, capture and summary submissions.");
        }
    }

    private void DeleteFence(ref nint fence)
    {
        if (fence != 0 && api.Gl.CanUseOwner)
        {
            api.DeleteSync(fence);
        }
        fence = 0;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        DeleteFence(ref stepFence);
        DeleteFence(ref captureFence);
        DeleteFence(ref summaryFence);
        spatialSampler?.Dispose();
        measurements?.Dispose();
        workspace?.Dispose();
        if (rules != 0 && api.Gl.CanUseOwner)
        {
            api.Gl.DeleteBuffers(1, ref rules);
        }
        foreach (GpuProgram program in programs)
        {
            program.Dispose();
        }
        api.Dispose();
        disposed = true;
    }

    private enum StepStage { Idle, Grid, Forces }
    [StructLayout(LayoutKind.Sequential)] private struct GpuStatus { internal uint Flags, MaximumOccupancy, FailedSlot, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct GpuSummary { internal float MeanSpeed, MaximumSpeed; internal uint Count, Flags; }
}
