using System.Text;

namespace ParticleLife.Viewer;

/// <summary>Publishes completely serialized files without replacing an existing destination.</summary>
/// <remarks>Temporary files share the destination directory so rename is within the same volume.
/// A companion is published before the scientific document; if final publication fails, only this
/// operation's new companion is removed. Existing files and concurrent writers remain protected.</remarks>
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
