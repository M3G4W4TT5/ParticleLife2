using ParticleLife.Core.Experiments;

namespace ParticleLife.Viewer;

/// <summary>Persists viewer experiments for replay by the headless CLI.</summary>
public static class ExperimentStorage
{
    /// <summary>Saves complete initial conditions in the existing format, refusing to overwrite any file.</summary>
    /// <param name="path">New output path; missing parent directories are created.</param>
    /// <param name="experiment">Exact reset target, not the current evolved state or a run report.</param>
    /// <remarks>Serialization finishes before file creation. Exclusive creation protects against concurrent
    /// writers as well as existing files. I/O and allocations occur outside numerical advancement.</remarks>
    /// <exception cref="IOException">The path exists or cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the destination is denied.</exception>
    /// <exception cref="ArgumentException">The path or experiment is invalid.</exception>
    public static void SaveNew(string path, ExperimentDefinition experiment)
    {
        ExclusiveFileStorage.Save(path, ExperimentFile.Serialize(experiment) + "\n");
    }
}
