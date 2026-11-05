using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

public sealed partial class ExperimentEditor
{
    /// <summary>Applies a complete exact coefficient replacement while retaining particle state and the clock.</summary>
    /// <param name="coefficients">Complete row-major coefficient values.</param><param name="label">Undo/replay action label.</param>
    /// <param name="seed">Optional random-generation provenance seed.</param>
    public void ApplyMatrixCoefficients(ReadOnlySpan<double> coefficients, string label = "Edit matrix coefficients", int? seed = null)
    {
        InteractionMatrix matrix = new(Configuration.Matrix.SpeciesCount, coefficients);
        Apply(CaptureValues() with { Configuration = WithMatrix(matrix) }, null, false, label,
            generator: seed is null ? null : Metadata("viewer-bulk-matrix", seed));
    }

    /// <summary>Regenerates only positions at the current boundary, preserving actual velocities, identities and species.</summary>
    /// <param name="speciesFilter">Species to regenerate, or -1 for all species.</param>
    public void RegeneratePositions(int speciesFilter = -1) => RegenerateComponent(positions: true, speciesFilter);

    /// <summary>Regenerates only velocities at the current boundary, retaining actual positions, identities and species.</summary>
    /// <param name="speciesFilter">Species to regenerate, or -1 for all species.</param>
    public void RegenerateVelocities(int speciesFilter = -1) => RegenerateComponent(positions: false, speciesFilter);

    private void RegenerateComponent(bool positions, int speciesFilter)
    {
        EnsureNotReplaying();
        if (speciesFilter < -1 || speciesFilter >= Configuration.Matrix.SpeciesCount) { throw new ArgumentOutOfRangeException(nameof(speciesFilter)); }
        ParticleSnapshot before = Snapshot();
        Vector2d[] newPositions = before.Positions.ToArray();
        Vector2d[] newVelocities = before.Velocities.ToArray();
        Random random = new(Generation.Seed);
        for (int i = 0; i < before.Count; i++)
        {
            if (speciesFilter >= 0 && before.Species[i] != speciesFilter) { continue; }
            if (positions)
            {
                newPositions[i] = ParticleGeneration.Position(random, i, before.Count, before.Species[i],
                    Configuration.Matrix.SpeciesCount, Configuration.Parameters, Generation);
            }
            else { newVelocities[i] = ParticleGeneration.Velocity(random, before.Positions[i], Configuration.Parameters, Generation); }
        }
        string component = positions ? "positions" : "velocities";
        Apply(CaptureValues(), new ParticleSnapshot(before.Ids, newPositions, newVelocities, before.Species, before.Time, before.Step),
            true, $"Regenerate live {component}", before, Metadata($"viewer-{component}-{(positions ? Generation.Placement.ToString() : Generation.Velocity.ToString())}", Generation.Seed));
    }

    /// <summary>Reassigns an exact seeded subset of current species, preserving every identity, position and velocity.</summary>
    /// <param name="kind">Assignment operation.</param><param name="fraction">Fraction from zero through one.</param>
    /// <param name="targetSpecies">Species used by the explicit selected-species operation.</param>
    /// <remarks>Exactly round(count*fraction) shuffled indices are considered. Future generation counts remain independent.</remarks>
    public void ReassignSpecies(SpeciesAssignmentKind kind, double fraction = 1, int targetSpecies = 0)
    {
        EnsureNotReplaying();
        int count = Configuration.Matrix.SpeciesCount;
        if (!Enum.IsDefined(kind) || !double.IsFinite(fraction) || fraction < 0 || fraction > 1
            || targetSpecies < 0 || targetSpecies >= count) { throw new ArgumentOutOfRangeException(nameof(fraction)); }
        ParticleSnapshot before = Snapshot();
        int[] species = before.Species.ToArray();
        int[] indices = Enumerable.Range(0, before.Count).ToArray();
        Random random = new(Generation.Seed);
        int affected = (int)Math.Round(before.Count * fraction, MidpointRounding.AwayFromZero);
        for (int i = 0; i < affected; i++)
        {
            int selected = random.Next(i, indices.Length);
            (indices[i], indices[selected]) = (indices[selected], indices[i]);
            int index = indices[i];
            species[index] = kind switch
            {
                SpeciesAssignmentKind.Random => random.Next(count),
                SpeciesAssignmentKind.Equal => i % count,
                SpeciesAssignmentKind.HorizontalBands => Math.Clamp((int)Math.Floor(before.Positions[index].Y / Configuration.Parameters.WorldHeight * count), 0, count - 1),
                SpeciesAssignmentKind.VerticalBands => Math.Clamp((int)Math.Floor(before.Positions[index].X / Configuration.Parameters.WorldWidth * count), 0, count - 1),
                SpeciesAssignmentKind.Rotate => (species[index] + 1) % count,
                SpeciesAssignmentKind.Flip => count - 1 - species[index],
                SpeciesAssignmentKind.SelectedSpecies => targetSpecies,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }
        Apply(CaptureValues(), new ParticleSnapshot(before.Ids, before.Positions, before.Velocities, species, before.Time, before.Step),
            false, $"Reassign live species: {kind}", before, Metadata($"viewer-species-{kind}", Generation.Seed));
    }
}
