using System.Text;

namespace ParticleLife.Viewer;

/// <summary>Publishes completely serialized files without replacing an existing destination.</summary>
/// <remarks>Atomic CreateNew reservations serialize cooperating writers across processes for every
/// destination, including the companion. File.Move(overwrite: false) alone is insufficient on Unix:
/// its existence check can race with another writer's rename. Temporary files share the destination
/// directory so rename is within the same volume. Reservations remain held through rollback.
/// A companion is published before the scientific document; if final publication fails, only this
/// operation's new companion is removed. A crashed writer can leave a .save.lock reservation;
/// fail closed until it is removed after confirming no writer is active. This is not a transaction
/// for readers or a protocol for unrelated applications that ignore the reservations.</remarks>
internal static class ExclusiveFileStorage
{
    internal static void Save(string path, string content, string? companionContent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, ".particle-life-" + Guid.NewGuid().ToString("N") + ".tmp");
        string companionPath = fullPath + ".viewer.json";
        string companionTemporary = temporaryPath + ".viewer";
        using Reservation mainReservation = new(fullPath);
        using Reservation? companionReservation = companionContent is null ? null : new(companionPath);
        bool companionPublished = false;
        try
        {
            WriteTemporary(temporaryPath, content);
            if (companionContent is not null)
            {
                WriteTemporary(companionTemporary, companionContent);
                File.Move(companionTemporary, companionPath, overwrite: false);
                companionPublished = true;
            }

            File.Move(temporaryPath, fullPath, overwrite: false);
        }
        catch
        {
            if (companionPublished)
            {
                TryDelete(companionPath);
            }

            throw;
        }
        finally
        {
            TryDelete(temporaryPath);
            TryDelete(companionTemporary);
        }
    }

    /// <summary>Owns an exclusively created sidecar until publication and rollback have finished.</summary>
    /// <remarks>Creation is the cross-process arbitration point; no check-then-create is used.
    /// Failed acquisition never removes another writer's reservation. Acquisition does not wait,
    /// so overlapping main/companion destinations cannot deadlock. Closing precedes deletion for
    /// Windows compatibility; until deletion, new writers continue to fail exclusive creation.</remarks>
    private sealed class Reservation : IDisposable
    {
        private readonly string path;
        private readonly FileStream stream;

        internal Reservation(string destination)
        {
            path = destination + ".save.lock";
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (UnauthorizedAccessException error)
            {
                // Windows may report a competing reservation pending deletion as access denied.
                // Reservation failures share the public save-collision IOException contract;
                // retain the native error to distinguish permissions from contention in diagnostics.
                throw new IOException("The save destination could not be reserved: " + destination, error);
            }
        }

        public void Dispose()
        {
            stream.Dispose();
            TryDelete(path);
        }
    }

    private static void WriteTemporary(string path, string content)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write(content);
            writer.Flush();
        }

        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The original publication error is more useful than a temporary-file cleanup error.
        }
        catch (UnauthorizedAccessException)
        {
            // No existing destination is ever removed to work around access restrictions.
        }
    }
}
