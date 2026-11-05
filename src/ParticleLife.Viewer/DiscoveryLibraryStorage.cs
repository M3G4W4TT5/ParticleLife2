namespace ParticleLife.Viewer;

/// <summary>Publishes library metadata or binary shares under the existing exclusive save-reservation convention.</summary>
internal static class DiscoveryLibraryStorage
{
    internal static void PublishScientific(string path, byte[] scientific, byte[]? companion)
    {
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string settingsPath = path + ".viewer.json";
        string settingsReservation = settingsPath + ".save.lock";
        bool publishedSettings = false;
        // Reserve even an absent incoming companion: an old companion-only destination must not
        // be rebound accidentally to imported scientific bytes.
        using FileStream heldSettings = new(settingsReservation, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            if (File.Exists(settingsPath)) { throw new IOException("Viewer companion destination already exists."); }
            Publish(path, output =>
            {
                if (companion is not null)
                {
                    string temporary = Path.Combine(directory, ".discovery-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            stream.Write(companion); stream.Flush(flushToDisk: true);
                        }
                        File.Move(temporary, settingsPath, overwrite: false); publishedSettings = true;
                    }
                    finally { TryDelete(temporary); }
                }
                output.Write(scientific);
            });
        }
        catch { if (publishedSettings) { TryDelete(settingsPath); } throw; }
        finally { heldSettings.Dispose(); TryDelete(settingsReservation); }
    }

    internal static void Publish(string path, Action<Stream> write, bool replace = false)
    {
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".discovery-" + Guid.NewGuid().ToString("N") + ".tmp");
        string reservation = path + ".save.lock";
        using FileStream held = new(reservation, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            if (!replace && File.Exists(path)) { throw new IOException("Destination already exists: " + path); }
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: replace);
        }
        finally
        {
            TryDelete(temporary);
            held.Dispose();
            TryDelete(reservation);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
