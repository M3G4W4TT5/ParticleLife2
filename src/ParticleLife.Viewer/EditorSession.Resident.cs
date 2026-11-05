using ParticleLife.Core.Execution;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

public sealed partial class EditorSession
{
    private ResidentExperiment? residentOwner;
    private Task<ExperimentEditor>? cpuDetach;
    private Task? cpuSwitch;
    private TaskCompletionSource? switchCompletion;
    private Func<SimulationConfiguration, ParticleSnapshot, IResidentSimulation>? residentFactory;
    private bool allowConversion, transferWasBackground;
    private readonly Queue<DeferredCommand> deferredCommands = new();

    /// <summary>Gets whether an ordered hardware handoff is still pending.</summary>
    public bool IsSwitchingExecution => switchCompletion is not null;

    /// <summary>Gets the current resident implementation only on its graphics owner thread.</summary>
    public IResidentSimulation? Resident => residentOwner?.Editor.Playback.Resident;

    /// <summary>Gets whether the resident completed buffer is available for normal drawing.</summary>
    public bool CanRenderResident => Resident is not null && residentOwner?.Faulted == false;

    /// <summary>Queues an explicit CPU-to-GPU transfer, preserving command order and the actual editor on failure.</summary>
    /// <param name="factory">Graphics-owner construction using already converted exact upload state.</param>
    /// <param name="allowPrecisionConversion">Explicit consent to round double particle values to GPU float32.</param>
    /// <returns>Completion after adoption or the real construction/conversion failure.</returns>
    /// <remarks>The CPU reaches a completed boundary and pauses. Later commands are retained until the
    /// destination exists. A rejected factory reattaches the same CPU editor, including baseline and undo.</remarks>
    public Task SwitchToGpu(Func<SimulationConfiguration, ParticleSnapshot, IResidentSimulation> factory,
        bool allowPrecisionConversion = false)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (IsSwitchingExecution || Resident is not null) { throw new InvalidOperationException("A GPU owner or execution transfer is already active."); }
        residentFactory = factory;
        allowConversion = allowPrecisionConversion;
        transferWasBackground = background is not null;
        switchCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Add(new(switchCompletion.Task, null));
        cpuDetach = background is not null ? background.DetachAsync() : Task.FromResult(sequential!);
        lastPreviewKey = null;
        return switchCompletion.Task;
    }

    /// <summary>Queues a GPU-to-CPU transfer from a newly requested actual capture, or an explicit last-checkpoint recovery.</summary>
    /// <param name="recoverLatestCapture">True deliberately discards uncaptured state after a device failure.</param>
    /// <returns>Completion after CPU construction and handoff; failure retains the active resident owner.</returns>
    public Task SwitchToCpu(bool recoverLatestCapture = false)
    {
        if (IsSwitchingExecution) { throw new InvalidOperationException("An execution transfer is already pending."); }
        if (residentOwner is null) { return Task.CompletedTask; }
        residentOwner.RequestPause();
        cpuSwitch = residentOwner.Submit(recoverLatestCapture ? "Recover CPU checkpoint" : "Capture and switch to CPU",
            editor =>
            {
                if (recoverLatestCapture) { editor.RecoverCpuFromLatestCapture(); }
                else { editor.ReturnToCpu(); }
            }, !recoverLatestCapture, null);
        switchCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Add(new(switchCompletion.Task, null));
        lastPreviewKey = null;
        return switchCompletion.Task;
    }

    /// <summary>Reports a render/context failure while retaining honest latest-capture recovery metadata.</summary>
    /// <param name="message">Actual native failure.</param>
    public void ReportResidentFailure(string message) => residentOwner?.ReportFailure(message);

    private Task SubmitOwned(string label, Action<ExperimentEditor> action, string? previewKey, bool requiresCapture)
    {
        if (IsSwitchingExecution)
        {
            if (deferredCommands.Count >= 128) { throw new InvalidOperationException("The execution-transfer command queue is full."); }
            DeferredCommand command = new(label, action, previewKey, requiresCapture);
            deferredCommands.Enqueue(command);
            return command.Completion.Task;
        }
        if (residentOwner is not null) { return residentOwner.Submit(label, action, requiresCapture, previewKey); }
        if (background is not null) { return background.Submit(label, action, previewKey); }
        action(sequential!);
        return Task.CompletedTask;
    }

    private void PumpExecutionTransfer()
    {
        if (cpuDetach is null || !cpuDetach.IsCompleted) { return; }
        ExperimentEditor? editor = null;
        IResidentSimulation? candidate = null;
        Exception? failure = null;
        try
        {
            editor = cpuDetach.GetAwaiter().GetResult();
            background?.Dispose();
            background = null;
            editor.Playback.SetPaused(true);
            ParticleStateView state = editor.Playback.Observe();
            ParticleSnapshot upload = CaptureTransfer.Convert(new(state.Ids, state.Positions, state.Velocities, state.Species, state.Time, state.Step),
                StatePrecision.Float32, allowConversion);
            candidate = residentFactory!(editor.Configuration, upload);
            editor.AdoptResident(candidate);
            candidate = null;
            residentOwner = new(editor);
            sequential = null;
        }
        catch (Exception error)
        {
            candidate?.Dispose();
            failure = error;
            if (editor is not null)
            {
                if (transferWasBackground) { background = new(editor); sequential = null; }
                else { sequential = editor; }
            }
        }
        cpuDetach = null;
        residentFactory = null;
        FinishSwitch(failure);
    }

    private void CompleteCpuTransfer()
    {
        if (cpuSwitch is not null && !cpuSwitch.IsCompleted) { return; }
        Exception? failure = cpuSwitch?.Exception?.GetBaseException();
        if (cpuSwitch is not null && cpuSwitch.IsCanceled) { failure = new OperationCanceledException("CPU transfer was cancelled."); }
        if (residentOwner is not null && residentOwner.Editor.Playback.Resident is null)
        {
            background = residentOwner.MoveToBackground();
            residentOwner.Dispose();
            residentOwner = null;
        }
        if (cpuSwitch is not null)
        {
            cpuSwitch = null;
            FinishSwitch(failure);
        }
    }

    private void FinishSwitch(Exception? failure)
    {
        TaskCompletionSource completion = switchCompletion!;
        switchCompletion = null;
        if (failure is null) { completion.TrySetResult(); }
        else { completion.TrySetException(failure); }
        while (deferredCommands.TryDequeue(out DeferredCommand? command))
        {
            try
            {
                Task execution = SubmitOwned(command.Label, command.Operation, command.PreviewKey, command.RequiresCapture);
                ForwardCompletion(execution, command.Completion);
            }
            catch (Exception error) { command.Completion.TrySetException(error); }
        }
    }

    internal static void ForwardCompletion(Task source, TaskCompletionSource destination)
    {
        source.ContinueWith(task =>
        {
            if (task.IsCanceled) { destination.TrySetCanceled(); }
            else if (task.IsFaulted) { destination.TrySetException(task.Exception!.InnerExceptions); }
            else { destination.TrySetResult(); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed record DeferredCommand(string Label, Action<ExperimentEditor> Operation, string? PreviewKey, bool RequiresCapture)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
