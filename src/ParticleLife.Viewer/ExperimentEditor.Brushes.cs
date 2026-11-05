using ParticleLife.Core.State;
using ParticleLife.Core.Models;

namespace ParticleLife.Viewer;

public sealed partial class ExperimentEditor
{
    private BrushStroke? brushStroke;
    private bool applyingBrushSample;

    /// <summary>Begins one direct-tool undo gesture and captures its immutable settings and starting point.</summary>
    /// <param name="settings">Validated live tool settings.</param><param name="point">Finite actual world point.</param>
    /// <remarks>Each applied sample still produces its own completed-boundary structural session event.
    /// Current storage uses full actual snapshots per applied sample: memory grows with population times
    /// applied samples and is not silently truncated. This intentionally precedes compact session storage.</remarks>
    public void BeginBrushStroke(BrushSettings settings, Vector2d point)
    {
        EnsureNotReplaying();
        settings.Validate(Configuration.Matrix.SpeciesCount);
        if (!point.IsFinite) { throw new ArgumentOutOfRangeException(nameof(point)); }
        EndBrushStroke();
        EndGesture();
        ParticleSnapshot initial = Snapshot();
        HashSet<long> grabbed = [];
        if (settings.Kind == BrushKind.Move)
        {
            for (int i = 0; i < initial.Count; i++)
            {
                if ((settings.SpeciesFilter < 0 || initial.Species[i] == settings.SpeciesFilter)
                    && settings.Contains(initial.Positions[i], point, Configuration.Parameters)) { grabbed.Add(initial.Ids[i]); }
            }
        }
        brushStroke = new BrushStroke(settings, point, initial, CaptureValues(), grabbed);
    }

    /// <summary>Applies one actual direct-tool sample to the owner's current completed state.</summary>
    /// <param name="point">Finite world point; unapplied native previews may be coalesced before this call.</param>
    /// <remarks>Move uses displacement from the last actually applied point, so coalesced pointer
    /// requests retain the full drag displacement. Other tools select against this completed state.</remarks>
    public void ApplyBrushSample(Vector2d point)
    {
        EnsureNotReplaying();
        if (!point.IsFinite) { throw new ArgumentOutOfRangeException(nameof(point)); }
        BrushStroke stroke = brushStroke ?? throw new InvalidOperationException("Begin a brush stroke before applying samples.");
        BrushSettings settings = stroke.Settings;
        settings.Validate(Configuration.Matrix.SpeciesCount);
        ParticleSnapshot before = Snapshot();
        int additions = settings.Kind == BrushKind.Add ? settings.AddCount : 0;
        EnsureIdentityCapacity(additions);
        List<long> ids = new(checked(before.Count + additions));
        List<Vector2d> positions = new(ids.Capacity), velocities = new(ids.Capacity);
        List<int> species = new(ids.Capacity);
        List<(long Identity, Vector2d Position, Vector2d Velocity)> firstTouches = [];
        List<long> addedIdentities = [];
        Vector2d movement = WorldBoundary.Displacement(stroke.PreviousPoint, point, Configuration.Parameters);
        for (int i = 0; i < before.Count; i++)
        {
            bool hit = settings.Kind == BrushKind.Move ? stroke.Grabbed.Contains(before.Ids[i])
                : (settings.SpeciesFilter < 0 || before.Species[i] == settings.SpeciesFilter) && settings.Contains(before.Positions[i], point, Configuration.Parameters);
            Vector2d position = before.Positions[i], velocity = before.Velocities[i];
            int assignment = before.Species[i];
            if (hit && settings.Kind != BrushKind.Add)
            {
                if (settings.Kind == BrushKind.Erase)
                {
                    firstTouches.Add((before.Ids[i], position, velocity));
                    continue;
                }
                if (settings.Kind == BrushKind.Move) { position = new(position.X + movement.X, position.Y + movement.Y); }
                else if (settings.Kind == BrushKind.PaintSpecies) { assignment = settings.Species; }
                else
                {
                    double direction = settings.DirectionRadians;
                    Vector2d displacement = WorldBoundary.Displacement(position, point, Configuration.Parameters);
                    double dx = displacement.X, dy = displacement.Y;
                    if (settings.Kind != BrushKind.DirectionalForce)
                    {
                        if (dx == 0 && dy == 0) { ids.Add(before.Ids[i]); positions.Add(position); velocities.Add(velocity); species.Add(assignment); continue; }
                        direction = Math.Atan2(dy, dx) + (settings.Kind == BrushKind.Repel ? Math.PI : 0);
                    }
                    double impulse = settings.Intensity * Configuration.TimeStep;
                    velocity = new(velocity.X + impulse * Math.Cos(direction), velocity.Y + impulse * Math.Sin(direction));
                }
                WorldBoundary.Map(ref position, ref velocity, Configuration.Parameters);
                if (position != before.Positions[i] || velocity != before.Velocities[i] || assignment != before.Species[i])
                {
                    firstTouches.Add((before.Ids[i], before.Positions[i], before.Velocities[i]));
                }
            }
            ids.Add(before.Ids[i]); positions.Add(position); velocities.Add(velocity); species.Add(assignment);
        }
        Random random = new(unchecked(Generation.Seed + stroke.Samples));
        for (int i = 0; i < additions; i++)
        {
            double x, y;
            if (settings.Shape == BrushShape.Circle)
            {
                double radius = Math.Sqrt(random.NextDouble()) * settings.Radius, angle = random.NextDouble() * Math.Tau;
                x = radius * Math.Cos(angle); y = radius * Math.Sin(angle);
            }
            else { x = (2 * random.NextDouble() - 1) * settings.Radius; y = (2 * random.NextDouble() - 1) * settings.Radius; }
            Vector2d position = new(point.X + x, point.Y + y);
            Vector2d velocity = ParticleGeneration.Velocity(random, position, Configuration.Parameters, Generation);
            WorldBoundary.Map(ref position, ref velocity, Configuration.Parameters);
            long identity = nextIdentity + i;
            ids.Add(identity); positions.Add(position);
            velocities.Add(velocity);
            species.Add(settings.Species);
            addedIdentities.Add(identity);
        }
        ParticleSnapshot after = new(ids.ToArray(), positions.ToArray(), velocities.ToArray(), species.ToArray(), before.Time, before.Step);
        applyingBrushSample = true;
        long previousRevision = Revision;
        try
        {
            Apply(CaptureValues(), after, false, $"Live {settings.Kind} brush sample", before,
                additions > 0 ? Metadata("viewer-add-brush", unchecked(Generation.Seed + stroke.Samples)) : null);
        }
        finally { applyingBrushSample = false; }
        if (Revision != previousRevision)
        {
            foreach (var touch in firstTouches)
            {
                stroke.Affected.Add(touch.Identity);
                stroke.FirstTouch.TryAdd(touch.Identity, (touch.Position, touch.Velocity));
            }
            foreach (long identity in addedIdentities) { stroke.Affected.Add(identity); }
        }
        stroke.PreviousPoint = point;
        stroke.Samples++;
    }

    /// <summary>Completes one brush undo action while retaining every actually applied replay event.</summary>
    /// <remarks>Undo restores affected identities to their first-touch motion and original membership;
    /// unrelated surviving particles retain their current motion. Redo restores saved actual values.</remarks>
    public void EndBrushStroke()
    {
        if (brushStroke is not BrushStroke stroke) { return; }
        brushStroke = null;
        if (!stroke.Changed) { return; }
        Vector2d[] positions = stroke.Initial.Positions.ToArray(), velocities = stroke.Initial.Velocities.ToArray();
        for (int i = 0; i < stroke.Initial.Count; i++)
        {
            if (stroke.FirstTouch.TryGetValue(stroke.Initial.Ids[i], out var motion))
            {
                positions[i] = motion.Position;
                velocities[i] = motion.Velocity;
            }
        }
        ParticleSnapshot before = new(stroke.Initial.Ids, positions, velocities, stroke.Initial.Species, stroke.Initial.Time, stroke.Initial.Step);
        AddHistory(new UndoEntry($"Live {stroke.Settings.Kind} brush stroke", stroke.BeforeValues, CaptureValues(), before,
            Snapshot(), false, stroke.Affected));
        Revision++;
    }

    private sealed class BrushStroke(BrushSettings settings, Vector2d point, ParticleSnapshot initial, EditorValues beforeValues, HashSet<long> grabbed)
    {
        internal readonly BrushSettings Settings = settings;
        internal Vector2d PreviousPoint = point;
        internal readonly ParticleSnapshot Initial = initial;
        internal readonly EditorValues BeforeValues = beforeValues;
        internal readonly HashSet<long> Grabbed = grabbed;
        internal readonly HashSet<long> Affected = [];
        internal readonly Dictionary<long, (Vector2d Position, Vector2d Velocity)> FirstTouch = [];
        internal int Samples;
        internal bool Changed;
    }
}
