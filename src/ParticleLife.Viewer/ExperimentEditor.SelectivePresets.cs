using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;

namespace ParticleLife.Viewer;

/// <summary>Selects reusable preset components without importing state, execution or unrelated rules.</summary>
[Flags]
public enum PresetComponents
{
    /// <summary>Retains every component.</summary>
    None = 0,
    /// <summary>Imports directed coefficients for pairs whose two species are mapped.</summary>
    Coefficients = 1,
    /// <summary>Imports effective directed core/cutoff radii for pairs whose two species are mapped.</summary>
    Radii = 2,
    /// <summary>Imports colours for mapped target species, retaining all other appearance choices.</summary>
    Palette = 4,
    /// <summary>Imports future position/velocity settings and mapped generation counts, without generating particles.</summary>
    Generation = 8
}

public sealed partial class ExperimentEditor
{
    /// <summary>Atomically applies explicitly selected preset components using a target-to-source species map.</summary>
    /// <param name="preset">Validated source preset; its particles, execution, global rules and playback are not imported.</param>
    /// <param name="components">Components to import; zero is a no-op.</param>
    /// <param name="sourceForTarget">One source index for each current target species; -1 explicitly retains that species.</param>
    /// <remarks>Both species must be mapped before a directed coefficient or radius changes. Radius imports
    /// materialize effective pair tables, retaining unmapped radii; global cutoff/core settings remain unchanged.
    /// Palette imports retain camera, effects and all other appearance. Generation affects future particles only.
    /// All candidates validate before a single undoable publication. Particle identities, motion and clock remain.</remarks>
    public void ApplySelectivePreset(PresetDefinition preset, PresetComponents components, ReadOnlySpan<int> sourceForTarget)
    {
        EnsureNotReplaying();
        ArgumentNullException.ThrowIfNull(preset);
        const PresetComponents supported = PresetComponents.Coefficients | PresetComponents.Radii | PresetComponents.Palette | PresetComponents.Generation;
        if ((components & ~supported) != 0) { throw new ArgumentOutOfRangeException(nameof(components)); }
        int count = Configuration.Matrix.SpeciesCount;
        if (sourceForTarget.Length != count) { throw new ArgumentException("Provide one source index per current target species; use -1 to retain.", nameof(sourceForTarget)); }
        bool anyMapped = false;
        foreach (int source in sourceForTarget)
        {
            if (source < -1 || source >= preset.Configuration.Matrix.SpeciesCount)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceForTarget), "Source species must be -1 (retain) or a valid preset species index.");
            }
            anyMapped |= source >= 0;
        }
        if (components == PresetComponents.None) { return; }
        EditorValues before = CaptureValues();
        InteractionMatrix current = Configuration.Matrix;
        double[] coefficients = current.Values.ToArray();
        PairInteractionRanges? radii = current.Ranges;
        if ((components & (PresetComponents.Coefficients | PresetComponents.Radii)) != 0)
        {
            bool importRadii = components.HasFlag(PresetComponents.Radii) && anyMapped;
            PairInteractionRanges? effective = importRadii ? radii ?? PairInteractionRanges.FromGlobal(count, Configuration.Parameters) : null;
            double[]? cores = effective is null ? null : effective.CoreRadii.ToArray();
            double[]? cutoffs = effective is null ? null : effective.CutoffRadii.ToArray();
            for (int receiver = 0; receiver < count; receiver++)
            {
                for (int source = 0; source < count; source++)
                {
                    int fromReceiver = sourceForTarget[receiver], fromSource = sourceForTarget[source];
                    if (fromReceiver < 0 || fromSource < 0) { continue; }
                    int index = receiver * count + source;
                    if (components.HasFlag(PresetComponents.Coefficients)) { coefficients[index] = preset.Configuration.Matrix[fromReceiver, fromSource]; }
                    if (cores is not null)
                    {
                        cores[index] = preset.Configuration.Matrix.CoreRadius(fromReceiver, fromSource, preset.Configuration.Parameters);
                        cutoffs![index] = preset.Configuration.Matrix.CutoffRadius(fromReceiver, fromSource, preset.Configuration.Parameters);
                    }
                }
            }
            if (cores is not null) { radii = new PairInteractionRanges(count, cores, cutoffs!); }
        }
        AppearanceSettings appearance = before.Appearance;
        if (components.HasFlag(PresetComponents.Palette) && anyMapped)
        {
            uint[] palette = new uint[Math.Max(count, appearance.SpeciesColors.Length)];
            for (int i = 0; i < palette.Length; i++)
            {
                palette[i] = i < count && sourceForTarget[i] >= 0 ? preset.Appearance.ColorForSpecies(sourceForTarget[i]) : appearance.ColorForSpecies(i);
            }
            appearance = appearance.WithPalette(palette);
        }
        int[] counts = (int[])before.Populations.Clone();
        GenerationSettings generation = before.Generation;
        if (components.HasFlag(PresetComponents.Generation))
        {
            generation = preset.Generation;
            for (int i = 0; i < count; i++) { if (sourceForTarget[i] >= 0) { counts[i] = preset.Populations[sourceForTarget[i]]; } }
            PresetDefinition.ValidatePopulations(counts, count);
        }
        SimulationConfiguration configuration = new(Configuration.Parameters, new InteractionMatrix(count, coefficients, radii),
            Configuration.TimeStep, Configuration.Backend, Configuration.WorkerCount);
        appearance.Validate();
        generation.Validate();
        Apply(before with { Configuration = configuration, Appearance = appearance, Generation = generation, Populations = counts },
            null, false, "Apply selected preset components", separateAction: true);
    }
}
