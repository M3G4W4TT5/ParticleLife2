using System.Runtime.InteropServices;
using ParticleLife.MonoGame.Rendering;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Owns eight reusable timestamp pairs, independent of particle capacity and numerical buffers.</summary>
/// <remarks>Created only by explicit measurement opt-in. Queries never wait for availability: a full
/// pool skips timing and reports incomplete coverage, without changing submitted numerical work.
/// Timestamp intervals cover one ordered command group; summing them excludes delays between groups.
/// They are GPU command intervals, not a claim of shader-only occupancy or display/present duration.</remarks>
internal sealed class GpuTimestampPool : IGpuTimestampSource
{
    private const int Capacity = 8;
    private readonly OpenGlDrawApi gl;
    private readonly OpenGlDrawApi.GenObjectsCall generate;
    private readonly OpenGlDrawApi.DeleteObjectsCall delete;
    private readonly CounterCall counter;
    private readonly ResultIntegerCall integer;
    private readonly ResultUnsigned64Call unsigned64;
    private readonly Slot[] slots = new Slot[Capacity];
    private bool disposed;

    internal GpuTimestampPool(OpenGlDrawApi gl)
    {
        this.gl = gl;
        generate = gl.Load<OpenGlDrawApi.GenObjectsCall>("glGenQueries");
        delete = gl.Load<OpenGlDrawApi.DeleteObjectsCall>("glDeleteQueries");
        counter = gl.Load<CounterCall>("glQueryCounter");
        integer = gl.Load<ResultIntegerCall>("glGetQueryObjectiv");
        unsigned64 = gl.Load<ResultUnsigned64Call>("glGetQueryObjectui64v");
        QueryIntegerCall query = gl.Load<QueryIntegerCall>("glGetQueryiv");
        query(0x8E28, 0x8864, out int bits);
        if (bits < 32) { throw new NotSupportedException("This context does not expose a useful GL timestamp counter."); }
        try
        {
            for (int index = 0; index < slots.Length; index++)
            {
                generate(1, out slots[index].Start);
                generate(1, out slots[index].End);
            }
            gl.Check("creating opt-in timestamp queries");
        }
        catch { Dispose(); throw; }
    }

    public int Begin(long tag, GpuCommandStage stage)
    {
        for (int index = 0; index < slots.Length; index++)
        {
            ref Slot slot = ref slots[index];
            if (slot.InUse) { continue; }
            slot.InUse = true;
            slot.Ended = false;
            slot.Tag = tag;
            slot.Stage = stage;
            counter(slot.Start, 0x8E28);
            return index;
        }
        return -1;
    }

    public void End(int token)
    {
        if (token < 0) { return; }
        counter(slots[token].End, 0x8E28);
        slots[token].Ended = true;
    }

    public bool TryTake(out GpuTimestampInterval interval)
    {
        for (int index = 0; index < slots.Length; index++)
        {
            ref Slot slot = ref slots[index];
            if (!slot.InUse || !slot.Ended) { continue; }
            integer(slot.End, 0x8867, out int available);
            if (available == 0) { continue; }
            unsigned64(slot.Start, 0x8866, out ulong start);
            unsigned64(slot.End, 0x8866, out ulong end);
            interval = new(slot.Tag, slot.Stage, end >= start ? end - start : 0, end >= start);
            slot.InUse = false;
            return true;
        }
        interval = default;
        return false;
    }

    public void Dispose()
    {
        if (disposed) { return; }
        if (gl.CanUseOwner)
        {
            for (int index = 0; index < slots.Length; index++)
            {
                if (slots[index].Start != 0) { delete(1, ref slots[index].Start); }
                if (slots[index].End != 0) { delete(1, ref slots[index].End); }
            }
        }
        disposed = true;
    }

    private struct Slot
    {
        internal uint Start, End;
        internal bool InUse, Ended;
        internal long Tag;
        internal GpuCommandStage Stage;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void CounterCall(uint id, uint target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ResultIntegerCall(uint id, uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ResultUnsigned64Call(uint id, uint name, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void QueryIntegerCall(uint target, uint name, out int value);
}

internal enum GpuCommandStage { Grid, Integration, Render }
internal readonly record struct GpuTimestampInterval(long Tag, GpuCommandStage Stage, ulong Nanoseconds, bool Valid);

/// <summary>Supplies asynchronously available timestamp pairs independently of numerical fences.</summary>
/// <remarks>The native implementation owns a bounded query pool. A managed source exercises delayed,
/// missing and invalid query delivery without a graphics device. Taking a result must never wait.</remarks>
internal interface IGpuTimestampSource : IDisposable
{
    int Begin(long tag, GpuCommandStage stage);
    void End(int token);
    bool TryTake(out GpuTimestampInterval interval);
}
