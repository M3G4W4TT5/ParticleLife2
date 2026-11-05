namespace ParticleLife.Core.Models;

/// <summary>Defines the two-dimensional world's force and coordinate boundary rule.</summary>
public enum BoundaryKind
{
    /// <summary>Applies a linear confining layer; finite off-world particles remain valid.</summary>
    SoftWall,

    /// <summary>Applies no wall force and leaves positions unbounded.</summary>
    Open,

    /// <summary>Mirrors positions at each edge and reverses the normal velocity component.</summary>
    Reflecting,

    /// <summary>Wraps positions and evaluates one minimum-image interaction across each seam.</summary>
    Periodic
}
