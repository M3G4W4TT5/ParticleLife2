namespace ParticleLife.Viewer;

/// <summary>A completed local index, prepared entirely away from the UI/graphics thread.</summary>
/// <param name="Directory">Absolute scanned folder.</param><param name="Query">Search that produced this snapshot.</param>
/// <param name="Entries">At most 300 retained matching items.</param><param name="Folders">At most 50 sorted subfolders.</param>
/// <param name="Archives">At most 50 sorted compressed shares.</param>
public sealed record DiscoveryLibraryScan(string Directory, string Query, IReadOnlyList<DiscoveryEntry> Entries,
    IReadOnlyList<string> Folders, IReadOnlyList<string> Archives);

/// <summary>Coalesces library requests into one cancellable scan and one latest pending request.</summary>
/// <remarks>Request and Poll belong to one UI thread. Worker code only reads local files. A newer
/// request cancels the previous generation; obsolete results and errors never publish. Cancellation
/// is checked between file reads and enumeration steps, not by blocking the UI until I/O returns.</remarks>
public sealed class DiscoveryLibraryScanner : IDisposable
{
    private RequestData? pending;
    private Task<Completion>? running;
    private CancellationTokenSource? cancellation;
    private long generation;
    private bool disposed;

    /// <summary>Gets whether an active or coalesced latest request is pending.</summary>
    public bool IsScanning => running is not null || pending is not null;

    /// <summary>Requests a scan, replacing any unapplied search without growing a task queue.</summary>
    /// <param name="directory">Local folder, normalized without performing I/O on this thread.</param><param name="query">Search text.</param>
    /// <param name="favouritesOnly">Favourite filter.</param><param name="recentFirst">Recent rather than alphabetic ordering.</param>
    public void Request(string directory, string query = "", bool favouritesOnly = false, bool recentFirst = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        pending = new(++generation, Path.GetFullPath(directory), query, favouritesOnly, recentFirst);
        cancellation?.Cancel();
        StartPending();
    }

    /// <summary>Polls only completed work and starts the latest coalesced request after the old worker exits.</summary>
    /// <param name="result">Latest completed snapshot, or null.</param><param name="error">Latest scan failure, or null; obsolete failures are discarded.</param>
    /// <returns>True when the current generation completed; false while pending or after discarding obsolete work.</returns>
    public bool Poll(out DiscoveryLibraryScan? result, out Exception? error)
    {
        result = null; error = null;
        if (running?.IsCompleted != true) { return false; }
        Completion completed = running.GetAwaiter().GetResult();
        running = null; cancellation!.Dispose(); cancellation = null;
        StartPending();
        if (completed.Generation != generation) { return false; }
        result = completed.Result; error = completed.Error;
        return true;
    }

    /// <summary>Cancels active and pending work without waiting for a file-system read.</summary>
    public void Cancel()
    {
        generation++; pending = null; cancellation?.Cancel();
    }

    private void StartPending()
    {
        if (running is not null || pending is not RequestData request) { return; }
        pending = null;
        cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        running = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<DiscoveryEntry> entries = DiscoveryLibrary.Read(request.Directory, request.Query, request.FavouritesOnly, request.RecentFirst, token);
                string[] folders = BoundedPaths(System.IO.Directory.EnumerateDirectories(request.Directory), token);
                string[] archives = BoundedPaths(System.IO.Directory.EnumerateFiles(request.Directory, "*.plshare"), token);
                return new Completion(request.Generation, new(request.Directory, request.Query, entries, folders, archives), null);
            }
            catch (Exception error) { return new Completion(request.Generation, null, error); }
        });
    }

    private static string[] BoundedPaths(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        SortedSet<string> retained = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            retained.Add(path);
            if (retained.Count > 50) { retained.Remove(retained.Max!); }
        }
        return retained.ToArray();
    }

    /// <summary>Cancels scanning; the worker holds no editor, graphics resource or callback into the UI.</summary>
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true; Cancel();
        CancellationTokenSource? source = cancellation;
        if (running is not null) { _ = running.ContinueWith(_ => source?.Dispose(), TaskScheduler.Default); }
        else { source?.Dispose(); }
    }

    private sealed record RequestData(long Generation, string Directory, string Query, bool FavouritesOnly, bool RecentFirst);
    private sealed record Completion(long Generation, DiscoveryLibraryScan? Result, Exception? Error);
}
