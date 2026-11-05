using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Samples independent position and velocity components for explicit allocating editor actions.</summary>
/// <remarks>Seeds are provenance. Exact generated values are recorded in experiment or structural session payloads.</remarks>
public static class ParticleGeneration
{
    /// <summary>Samples a finite position in the nominal world for one explicit recipe.</summary>
    /// <param name="random">Caller-owned seeded generator.</param><param name="index">Index within the generated set.</param>
    /// <param name="count">Positive generated set size.</param><param name="species">Particle species.</param>
    /// <param name="speciesCount">Positive species count.</param><param name="parameters">World geometry.</param>
    /// <param name="settings">Validated position recipe.</param><returns>A position in reduced length units.</returns>
    public static Vector2d Position(Random random, int index, int count, int species, int speciesCount,
        ModelParameters parameters, GenerationSettings settings)
    {
        double width = parameters.WorldWidth, height = parameters.WorldHeight;
        double extent = Math.Min(width, height);
        double t = (index + 0.5) / Math.Max(1, count);
        double angle;
        double radius;
        switch (settings.Placement)
        {
            case PlacementKind.Uniform: return new(random.NextDouble() * width, random.NextDouble() * height);
            case PlacementKind.Grid:
                int columns = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(count * (width / height))), 1, Math.Max(1, count));
                int rows = (int)Math.Ceiling((double)count / columns);
                return new(((index % columns + 0.5) / columns) * width, ((index / columns + 0.5) / rows) * height);
            case PlacementKind.Disk:
                radius = Math.Sqrt(random.NextDouble()) * (extent / 2);
                angle = random.NextDouble() * Math.Tau;
                break;
            case PlacementKind.Ring: radius = extent * 0.34; angle = t * Math.Tau; break;
            case PlacementKind.Line: return new(t * width, height / 2);
            case PlacementKind.Spiral: radius = extent * 0.46 * t; angle = t * Math.Tau * 3; break;
            case PlacementKind.Stripe:
                return new(((index % 5 + 0.25 + random.NextDouble() * 0.5) / 5) * width, random.NextDouble() * height);
            case PlacementKind.Cluster:
            case PlacementKind.SpeciesGroups:
                int groups = settings.Placement == PlacementKind.SpeciesGroups ? speciesCount : Math.Min(8, Math.Max(2, speciesCount));
                int group = settings.Placement == PlacementKind.SpeciesGroups ? species : index % groups;
                int groupColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(groups * (width / height))));
                int groupRows = (int)Math.Ceiling((double)groups / groupColumns);
                double cellWidth = width / groupColumns, cellHeight = height / groupRows;
                radius = Math.Sqrt(random.NextDouble()) * Math.Min(cellWidth, cellHeight) * 0.28;
                angle = random.NextDouble() * Math.Tau;
                return new((group % groupColumns + 0.5) * cellWidth + radius * Math.Cos(angle),
                    (group / groupColumns + 0.5) * cellHeight + radius * Math.Sin(angle));
            default: throw new ArgumentOutOfRangeException(nameof(settings));
        }
        return new(width / 2 + radius * Math.Cos(angle), height / 2 + radius * Math.Sin(angle));
    }

    /// <summary>Samples a velocity independently of existing species and position generation.</summary>
    /// <param name="random">Caller-owned seeded generator.</param><param name="position">Actual position for radial modes.</param>
    /// <param name="parameters">World geometry.</param><param name="settings">Validated velocity recipe.</param>
    /// <returns>Finite intended velocity in reduced length/time; validation rejects unrepresentable results at the edit boundary.</returns>
    public static Vector2d Velocity(Random random, Vector2d position, ModelParameters parameters, GenerationSettings settings)
    {
        if (settings.Velocity == VelocityKind.Zero) { return default; }
        double radial = Math.Atan2(position.Y / parameters.WorldHeight - 0.5, position.X / parameters.WorldWidth - 0.5);
        double direction = settings.Velocity switch
        {
            VelocityKind.UniformDirection => settings.DirectionRadians,
            VelocityKind.RandomDirection => random.NextDouble() * Math.Tau,
            VelocityKind.RadialOutward => radial,
            VelocityKind.RadialInward => radial + Math.PI,
            VelocityKind.Tangential => radial + Math.PI / 2,
            _ => throw new ArgumentOutOfRangeException(nameof(settings))
        };
        return new(settings.Speed * Math.Cos(direction), settings.Speed * Math.Sin(direction));
    }
}

/// <summary>Selects an explicit live species reassignment without changing identity or motion.</summary>
public enum SpeciesAssignmentKind
{
    /// <summary>Uniform independent random species.</summary>
    Random,
    /// <summary>Even cycling assignments over the selected identities.</summary>
    Equal,
    /// <summary>Assign by horizontal position bands.</summary>
    HorizontalBands,
    /// <summary>Assign by vertical position bands.</summary>
    VerticalBands,
    /// <summary>Advance each affected assignment by one, modulo species count.</summary>
    Rotate,
    /// <summary>Reverse species indices.</summary>
    Flip,
    /// <summary>Assign the selected species explicitly.</summary>
    SelectedSpecies
}
