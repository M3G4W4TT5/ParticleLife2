using System.Runtime.ExceptionServices;

namespace ParticleLife.Core.Numerics;

/// <summary>Runs a fixed callback on a bounded number of persistent workers, including the caller.</summary>
/// <remarks>
/// Worker zero executes on the calling thread. Other workers sleep on reusable events between calls.
/// Run is a full barrier even when one callback throws: every worker finishes before an exception is
/// rethrown on the caller, and another invocation may safely reuse the pool. Successful warmed runs
/// allocate no managed memory. Callback ownership and disjoint output ranges belong to the evaluator.
/// Run and Dispose must not overlap, and calls are not reentrant. Dispose joins every background thread.
/// </remarks>
internal sealed class ForceWorkerPool : IDisposable
{
    private readonly Action<int> callback;
    private readonly Worker[] workers;
    private bool disposed;

    internal ForceWorkerPool(int workerCount, Action<int> callback)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workerCount, ForceWorkerConfiguration.MaximumWorkers);
        ArgumentNullException.ThrowIfNull(callback);
        this.callback = callback;
        workers = new Worker[workerCount - 1];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = new Worker(callback, i + 1);
        }

        try
        {
            foreach (Worker worker in workers)
            {
                worker.Start();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Gets managed bytes allocated by background callbacks in the last completed barrier.</summary>
    /// <remarks>The calling thread is measured separately. Thread/event setup is outside these deltas.</remarks>
    internal long LastBackgroundAllocatedBytes { get; private set; }

    /// <summary>Runs one callback per worker and joins all callbacks before returning or throwing.</summary>
    internal void Run()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (Worker worker in workers)
        {
            worker.Request.Set();
        }

        Exception? error = null;
        try
        {
            callback(0);
        }
        catch (Exception failure)
        {
            error = failure;
        }

        long allocated = 0;
        foreach (Worker worker in workers)
        {
            worker.Completed.WaitOne();
            allocated += worker.AllocatedBytes;
            error ??= worker.Error;
        }

        LastBackgroundAllocatedBytes = allocated;
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    /// <summary>Stops and joins all background threads; repeated disposal is harmless.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (Worker worker in workers)
        {
            worker.Stop();
        }
    }

    /// <summary>Owns one sleeping thread and its independent error/allocation result.</summary>
    private sealed class Worker
    {
        private readonly Action<int> callback;
        private readonly int workerId;
        private readonly Thread thread;
        private bool stopping;
        private bool started;

        internal Worker(Action<int> callback, int workerId)
        {
            this.callback = callback;
            this.workerId = workerId;
            thread = new Thread(Loop) { IsBackground = true, Name = $"ParticleLife force {workerId}" };
        }

        internal AutoResetEvent Request { get; } = new(false);

        internal AutoResetEvent Completed { get; } = new(false);

        internal Exception? Error { get; private set; }

        internal long AllocatedBytes { get; private set; }

        internal void Start()
        {
            thread.Start();
            started = true;
        }

        internal void Stop()
        {
            Volatile.Write(ref stopping, true);
            if (started)
            {
                Request.Set();
                thread.Join();
            }

            Request.Dispose();
            Completed.Dispose();
        }

        private void Loop()
        {
            while (true)
            {
                Request.WaitOne();
                if (Volatile.Read(ref stopping))
                {
                    return;
                }

                Error = null;
                long before = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    callback(workerId);
                }
                catch (Exception error)
                {
                    Error = error;
                }
                finally
                {
                    AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    Completed.Set();
                }
            }
        }
    }
}
