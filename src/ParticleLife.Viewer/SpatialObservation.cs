using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Defines a bounded display grid independent of force evaluation and its storage.</summary>
/// <param name="Origin">Lower coordinate corner of the sampled rectangle.</param>
/// <param name="Width">Positive finite rectangle width in L.</param><param name="Height">Positive finite height in L.</param>
/// <param name="Columns">One through 128 observation columns.</param><param name="Rows">One through 128 observation rows.</param>
public sealed record SpatialObservationRequest(Vector2d Origin, double Width, double Height, int Columns, int Rows)
{
    /// <summary>Validates a bounded finite observation rectangle.</summary>
    public void Validate()
    {
        if (!Origin.IsFinite || !double.IsFinite(Width) || Width <= 0 || !double.IsFinite(Height) || Height <= 0
            || !double.IsFinite(Origin.X + Width) || !double.IsFinite(Origin.Y + Height)
            || Columns < 1 || Columns > 128 || Rows < 1 || Rows > 128)
        { throw new ArgumentOutOfRangeException(nameof(Width), "Observation rectangles must be finite with 1..128 cells per axis."); }
    }
}

/// <summary>Owns one optional spatial sample and its actual completed-state boundary.</summary>
/// <remarks>Counts are display bins, not truncated neighbour lists. Outside particles remain in the
/// total statistics. A sample never implies current-state freshness; consumers display its clock.</remarks>
public sealed class SpatialObservation
{
    private readonly int[] bins;
    /// <summary>Creates a validated independently owned sample.</summary>
    /// <param name="request">The sampled display rectangle.</param><param name="counts">Row-major population counts.</param>
    /// <param name="outside">Particles outside the rectangle.</param><param name="meanSpeed">Mean speed in L/T.</param>
    /// <param name="maximumSpeed">Maximum speed in L/T.</param><param name="step">Completed source step.</param>
    /// <param name="time">Completed source time.</param><param name="milliseconds">Measured sampling/readback wall cost.</param>
    public SpatialObservation(SpatialObservationRequest request, ReadOnlySpan<int> counts, int outside,
        double meanSpeed, double maximumSpeed, long step, double time, double milliseconds)
    {
        request.Validate();
        if (counts.Length != request.Columns * request.Rows || outside < 0 || step < 0
            || !double.IsFinite(time) || time < 0 || !double.IsFinite(milliseconds) || milliseconds < 0
            || !double.IsFinite(meanSpeed) || meanSpeed < 0 || !double.IsFinite(maximumSpeed) || maximumSpeed < meanSpeed)
        { throw new ArgumentException("Invalid observation values."); }
        long total = outside;
        foreach (int count in counts)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count); total += count;
        }
        Count = checked((int)total);
        Request = request; bins = counts.ToArray(); Outside = outside;
        MeanSpeed = meanSpeed; MaximumSpeed = maximumSpeed; Step = step; Time = time; Milliseconds = milliseconds;
    }
    /// <summary>Gets the sampled rectangle and bin resolution.</summary>
    public SpatialObservationRequest Request { get; }
    /// <summary>Gets immutable row-major occupancy counts.</summary>
    public ReadOnlySpan<int> Counts => bins;
    /// <summary>Gets all sampled particles, including outside particles.</summary>
    public int Count { get; }
    /// <summary>Gets particles outside the sampled rectangle.</summary>
    public int Outside { get; }
    /// <summary>Gets sampled mean speed in L/T.</summary>
    public double MeanSpeed { get; }
    /// <summary>Gets sampled maximum speed in L/T.</summary>
    public double MaximumSpeed { get; }
    /// <summary>Gets the source completed step.</summary>
    public long Step { get; }
    /// <summary>Gets the source completed time.</summary>
    public double Time { get; }
    /// <summary>Gets measured sampling wall milliseconds, excluding later rendering.</summary>
    public double Milliseconds { get; }
}

/// <summary>Samples a borrowed completed CPU buffer only when an optional observation is requested.</summary>
public sealed class SpatialSampler
{
    private int[] bins = [];
    /// <summary>Scans a consistent completed state without modifying it or retaining borrowed spans.</summary>
    /// <param name="positions">Completed positions.</param><param name="velocities">Same-boundary velocities.</param>
    /// <param name="step">Source step.</param><param name="time">Source time.</param><param name="request">Observation geometry.</param>
    /// <returns>An independently owned small sample; particle state is not captured.</returns>
    public SpatialObservation Sample(ReadOnlySpan<Vector2d> positions, ReadOnlySpan<Vector2d> velocities,
        long step, double time, SpatialObservationRequest request)
    {
        request.Validate();
        if (positions.Length != velocities.Length) { throw new ArgumentException("Position and velocity lengths differ."); }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int length = request.Columns * request.Rows;
        if (bins.Length < length) { bins = new int[length]; }
        bins.AsSpan(0, length).Clear();
        int outside = 0;
        double mean = 0, maximum = 0;
        for (int i = 0; i < positions.Length; i++)
        {
            Vector2d p = positions[i], v = velocities[i];
            double x = (p.X - request.Origin.X) / request.Width, y = (p.Y - request.Origin.Y) / request.Height;
            if (x >= 0 && x <= 1 && y >= 0 && y <= 1)
            {
                int column = Math.Min(request.Columns - 1, (int)(x * request.Columns));
                int row = Math.Min(request.Rows - 1, (int)(y * request.Rows));
                bins[row * request.Columns + column]++;
            }
            else { outside++; }
            double magnitude = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
            double speed = magnitude == 0 ? 0 : magnitude * Math.Sqrt(Math.Pow(v.X / magnitude, 2) + Math.Pow(v.Y / magnitude, 2));
            speed = Math.Min(double.MaxValue, speed);
            mean = mean * ((double)i / (i + 1)) + speed / (i + 1);
            maximum = Math.Max(maximum, speed);
        }
        return new SpatialObservation(request, bins.AsSpan(0, length), outside, Math.Min(mean, maximum), maximum,
            step, time, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}

/// <summary>Follows the population-weighted centres of occupied display bins within a selected region.</summary>
/// <remarks>This is a deliberately coarse visual tracking heuristic, not organism or identity detection.
/// Empty samples retain the previous centre and report lost contact. Periodic displacement uses the
/// minimum image around that centre so a selected pattern can cross a seam.</remarks>
public sealed class RegionTracker
{
    /// <summary>Gets whether a region has been selected.</summary>
    public bool Active { get; private set; }
    /// <summary>Gets the latest heuristic centre in world units.</summary>
    public Vector2d Centre { get; private set; }
    /// <summary>Gets the selected world radius.</summary>
    public double Radius { get; private set; }
    /// <summary>Gets particles contributing through their occupied bin centres in the latest sample.</summary>
    public int TrackedCount { get; private set; }
    /// <summary>Gets the source step of the most recent tracking observation.</summary>
    public long SampleStep { get; private set; }
    /// <summary>Selects a finite circular region without changing any particle.</summary>
    /// <param name="centre">World centre.</param><param name="radius">Positive finite world radius.</param>
    public void Start(Vector2d centre, double radius)
    {
        if (!centre.IsFinite || !double.IsFinite(radius) || radius <= 0) { throw new ArgumentOutOfRangeException(nameof(radius)); }
        Centre = centre; Radius = radius; TrackedCount = 0; Active = true;
    }
    /// <summary>Stops visual tracking.</summary>
    public void Stop() { Active = false; TrackedCount = 0; }
    /// <summary>Updates the approximate region centre from a completed display sample.</summary>
    /// <param name="sample">Owned sample with an explicit source boundary.</param><param name="parameters">Source world geometry.</param>
    public void Update(SpatialObservation sample, ModelParameters parameters)
    {
        if (!Active) { return; }
        SpatialObservationRequest grid = sample.Request;
        Vector2d offset = default;
        int count = 0;
        for (int row = 0; row < grid.Rows; row++)
        {
            for (int column = 0; column < grid.Columns; column++)
            {
                int occupants = sample.Counts[row * grid.Columns + column];
                if (occupants == 0) { continue; }
                Vector2d point = new(grid.Origin.X + (column + 0.5) * grid.Width / grid.Columns,
                    grid.Origin.Y + (row + 0.5) * grid.Height / grid.Rows);
                Vector2d displacement = WorldBoundary.Displacement(Centre, point, parameters);
                if (Math.Abs(displacement.X / Radius) > 1 || Math.Abs(displacement.Y / Radius) > 1
                    || Math.Pow(displacement.X / Radius, 2) + Math.Pow(displacement.Y / Radius, 2) > 1) { continue; }
                int total = checked(count + occupants);
                offset = offset * ((double)count / total) + displacement * ((double)occupants / total);
                count = total;
            }
        }
        if (count > 0)
        {
            Centre += offset;
            if (parameters.Boundary == BoundaryKind.Periodic)
            { Centre = new(WorldBoundary.Wrap(Centre.X, parameters.WorldWidth), WorldBoundary.Wrap(Centre.Y, parameters.WorldHeight)); }
        }
        TrackedCount = count; SampleStep = sample.Step;
    }
}
