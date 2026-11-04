using ParticleLife.Core.State;

namespace ParticleLife.Core.Experiments;

/// <summary>Computes exact geometric diagnostics using instance-owned reusable storage.</summary>
/// <remarks>
/// <para>Every call reads one completed state and builds its own spatial index from those positions.
/// It neither reads a force evaluator's index nor retains a state view. Call only while the state owner
/// is not stepping or resetting; an instance must not be sampled concurrently. No static scratch is used.</para>
/// <para>Connectivity has exactly the meaning of <see cref="Diagnostics.Sample"/>: an undirected edge
/// exists when double.Hypot of the component-wise displacement is less than or equal to the radius.
/// No squared-radius assumption is made. Exact coincident pairs are counted with a dictionary and
/// merged first. Other edges are needed only between distinct components; connectivity work can end
/// when one component remains, without skipping coincidence counting.</para>
/// <para>Expected work is O(N + spatial candidates), with quadratic worst cases. Unindexable finite
/// positions or an unrepresentable cell width use direct pair enumeration with the same reusable
/// component storage. Buffers and dictionaries retain their maximum population capacity; clearing
/// them costs O(retained capacity). Construction and growth allocate. A warmed successful sample
/// allocates only its immutable <see cref="DiagnosticSample"/> result, independent of population.
/// Sampling and its failures never mutate the input or affect the numerical trajectory.</para>
/// </remarks>
public sealed class DiagnosticSampler
{
    /// <summary>Identifies the exact spatial connectivity and coincidence algorithm in reports.</summary>
    public const string AlgorithmVersion = "spatial-components/1";

    private const double CoordinateLimit = 1125899906842624; // 2^50 cell widths.
    private readonly Dictionary<CellCoordinate, int> heads = new();
    private readonly Dictionary<Vector2d, int> coincident = new();
    private int[] parents = [];
    private int[] sizes = [];
    private int[] next = [];
    private CellCoordinate[] cells = [];
    private int cellCapacity;
    private int coincidenceCapacity;

    /// <summary>Gets work and capacity metadata from the last successful sample, or default before sampling.</summary>
    /// <remarks>Failed measurements may change private scratch storage but do not publish this value.</remarks>
    public DiagnosticExecution LastExecution { get; private set; }

    /// <summary>Samples speeds, exact coincidences and inclusive geometric connected components.</summary>
    /// <param name="state">Completed read-only state; do not mutate its owner during this call.</param>
    /// <param name="clusterDistance">Any finite positive inclusive radius in reduced length units.</param>
    /// <returns>An immutable result carrying the measured state's time and step.</returns>
    /// <remarks>Speed validation is a linear pass in storage order, independent of geometric queries.
    /// Radius squares may underflow or overflow: the final predicate always uses double.Hypot.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The radius is nonpositive or non-finite.</exception>
    /// <exception cref="InvalidOperationException">A position, velocity or resulting speed is non-finite.</exception>
    public DiagnosticSample Sample(ParticleStateView state, double clusterDistance)
    {
        if (!double.IsFinite(clusterDistance) || clusterDistance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clusterDistance), "Cluster distance must be finite and positive.");
        }

        EnsureCapacity(state.Count);
        heads.Clear();
        coincident.Clear();
        double meanSpeed = 0;
        double maxSpeed = 0;
        long overlaps = 0;
        int clusters = state.Count;
        int largest = state.Count == 0 ? 0 : 1;
        for (int i = 0; i < state.Count; i++)
        {
            parents[i] = i;
            sizes[i] = 1;
            next[i] = -1;
            Vector2d position = state.Positions[i];
            Vector2d velocity = state.Velocities[i];
            if (!position.IsFinite || !velocity.IsFinite)
            {
                throw new InvalidOperationException($"Non-finite state at particle {state.Ids[i]}.");
            }

            double speed = double.Hypot(velocity.X, velocity.Y);
            if (!double.IsFinite(speed))
            {
                throw new InvalidOperationException($"Speed overflow at particle {state.Ids[i]}.");
            }

            meanSpeed += (speed - meanSpeed) / (i + 1);
            maxSpeed = Math.Max(maxSpeed, speed);
            if (coincident.TryGetValue(position, out int first))
            {
                // Before geometric unions, the first occurrence owns exactly this coincidence group.
                overlaps += sizes[first];
                parents[i] = first;
                sizes[first]++;
                clusters--;
                largest = Math.Max(largest, sizes[first]);
                next[i] = -2; // Only distinct positions need spatial queries.
            }
            else
            {
                coincident.Add(position, i);
            }
        }

        DiagnosticFallbackReason fallback = BuildIndex(state.Positions, clusterDistance);
        long candidates = 0;
        long distances = 0;
        if (fallback == DiagnosticFallbackReason.None)
        {
            ConnectSpatial(state.Positions, clusterDistance, ref clusters, ref largest, ref candidates, ref distances);
        }
        else
        {
            ConnectDirect(state.Positions, clusterDistance, ref clusters, ref largest, ref candidates, ref distances);
        }

        DiagnosticSample sample = new(state.Time, state.Step, state.Count, state.Count, overlaps,
            meanSpeed, maxSpeed, clusterDistance, clusters, largest);
        LastExecution = new(fallback == DiagnosticFallbackReason.None, fallback, candidates, distances,
            parents.Length, cellCapacity, coincidenceCapacity, heads.Count,
            28L * parents.Length + 36L * (cellCapacity + (long)coincidenceCapacity));
        return sample;
    }

    /// <summary>Reserves all possible distinct positions and cells before a sample, independent of occupancy.</summary>
    private void EnsureCapacity(int count)
    {
        if (count <= parents.Length)
        {
            return;
        }

        parents = new int[count];
        sizes = new int[count];
        next = new int[count];
        cells = new CellCoordinate[count];
        cellCapacity = heads.EnsureCapacity(count);
        coincidenceCapacity = coincident.EnsureCapacity(count);
    }

    /// <summary>Maps every distinct position before publishing a usable linked-cell index.</summary>
    /// <remarks>
    /// Width h is the smallest power of two strictly greater than radius R, so R &lt; h &lt;= 2R.
    /// The strict inequality matters for inclusive queries: with h=R, positions -epsilon and h can
    /// occupy cells -1 and 1 while their rounded displacement equals R. With h&gt;R, an accepted
    /// rounded component cannot span two cells. Binary scaling avoids division rounding; signed
    /// underflow is mapped to its exact floor. The conservative 2^50 bound preserves fractional
    /// resolution and keeps neighbour offsets safely within Int64. Largest finite radii whose next
    /// binary width is infinite fall back, preserving the complete positive finite radius domain.
    /// </remarks>
    private DiagnosticFallbackReason BuildIndex(ReadOnlySpan<Vector2d> positions, double radius)
    {
        int exponent = Math.ILogB(radius) + 1;
        if (!double.IsFinite(Math.ScaleB(1, exponent)))
        {
            return DiagnosticFallbackReason.CellWidthRange;
        }

        for (int i = 0; i < positions.Length; i++)
        {
            if (next[i] == -2)
            {
                continue;
            }

            if (!TryMap(positions[i].X, exponent, out long x) || !TryMap(positions[i].Y, exponent, out long y))
            {
                return DiagnosticFallbackReason.CoordinateRange;
            }

            cells[i] = new(x, y);
        }

        for (int i = positions.Length - 1; i >= 0; i--)
        {
            if (next[i] == -2)
            {
                continue;
            }

            next[i] = heads.TryGetValue(cells[i], out int head) ? head : -1;
            heads[cells[i]] = i;
        }

        return DiagnosticFallbackReason.None;
    }

    /// <summary>Visits each eligible unordered spatial pair at most once, stopping when connectivity is complete.</summary>
    private void ConnectSpatial(ReadOnlySpan<Vector2d> positions, double radius, ref int clusters,
        ref int largest, ref long candidates, ref long distances)
    {
        for (int i = 0; i < positions.Length && clusters > 1; i++)
        {
            if (next[i] == -2)
            {
                continue;
            }

            CellCoordinate center = cells[i];
            for (int y = -1; y <= 1 && clusters > 1; y++)
            {
                for (int x = -1; x <= 1 && clusters > 1; x++)
                {
                    if (!heads.TryGetValue(new(center.X + x, center.Y + y), out int source))
                    {
                        continue;
                    }

                    for (int j = source; j >= 0 && clusters > 1; j = next[j])
                    {
                        if (j > i)
                        {
                            ConnectPair(positions, i, j, radius, ref clusters, ref largest, ref candidates, ref distances);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Uses exact direct enumeration when spatial indexing cannot represent a valid input.</summary>
    private void ConnectDirect(ReadOnlySpan<Vector2d> positions, double radius, ref int clusters,
        ref int largest, ref long candidates, ref long distances)
    {
        for (int i = 0; i < positions.Length && clusters > 1; i++)
        {
            if (next[i] == -2)
            {
                continue;
            }

            for (int j = i + 1; j < positions.Length && clusters > 1; j++)
            {
                if (next[j] != -2)
                {
                    ConnectPair(positions, i, j, radius, ref clusters, ref largest, ref candidates, ref distances);
                }
            }
        }
    }

    /// <summary>Tests the reference distance predicate only when an edge can join two different components.</summary>
    /// <remarks>Skipping an already connected pair cannot change the graph's components. Coincidences
    /// were fully counted before any early exit, so the count is independent of traversal order.</remarks>
    private void ConnectPair(ReadOnlySpan<Vector2d> positions, int i, int j, double radius, ref int clusters,
        ref int largest, ref long candidates, ref long distances)
    {
        candidates++;
        int a = FindRoot(i);
        int b = FindRoot(j);
        if (a == b)
        {
            return;
        }

        distances++;
        Vector2d displacement = positions[j] - positions[i];
        if (double.Hypot(displacement.X, displacement.Y) > radius)
        {
            return;
        }

        if (sizes[a] < sizes[b])
        {
            (a, b) = (b, a);
        }

        parents[b] = a;
        sizes[a] += sizes[b];
        clusters--;
        largest = Math.Max(largest, sizes[a]);
    }

    /// <summary>Compresses paths within private reusable connectivity storage.</summary>
    private int FindRoot(int node)
    {
        while (parents[node] != node)
        {
            parents[node] = parents[parents[node]];
            node = parents[node];
        }

        return node;
    }

    /// <summary>Maps by exact binary scaling and signed floor, rejecting unsafe coordinates before conversion.</summary>
    private static bool TryMap(double value, int exponent, out long coordinate)
    {
        double scaled = Math.ScaleB(value, -exponent);
        if (!double.IsFinite(scaled) || Math.Abs(scaled) >= CoordinateLimit)
        {
            coordinate = 0;
            return false;
        }

        coordinate = scaled == 0 && value < 0 ? -1 : (long)Math.Floor(scaled);
        return true;
    }

    /// <summary>Uses complete signed coordinates for cell identity, including under dictionary hash collisions.</summary>
    private readonly record struct CellCoordinate(long X, long Y);
}

/// <summary>Identifies a direct diagnostic connectivity fallback without changing measurement meaning.</summary>
public enum DiagnosticFallbackReason
{
    /// <summary>Spatial indexing represented the entire input.</summary>
    None,

    /// <summary>The power-of-two width strictly greater than the radius is not finite.</summary>
    CellWidthRange,

    /// <summary>A position lies outside the conservative coordinate range for that cell width.</summary>
    CoordinateRange
}

/// <summary>Describes the last successful diagnostic's algorithm work and retained scratch capacity.</summary>
/// <param name="UsedSpatialIndex">True when spatially filtered connectivity was used.</param>
/// <param name="FallbackReason">Reason for exact direct connectivity, or None.</param>
/// <param name="CandidatePairs">Distinct-position pairs visited before connectivity was complete.</param>
/// <param name="DistanceEvaluations">Hypot predicates evaluated between different components.</param>
/// <param name="ParticleCapacity">Number of entries retained in each component/link/coordinate array.</param>
/// <param name="CellCapacity">Dictionary slots reserved for spatial cell keys.</param>
/// <param name="CoincidenceCapacity">Dictionary slots reserved for exact position keys.</param>
/// <param name="OccupiedCells">Occupied spatial cells; zero when direct fallback was used.</param>
/// <param name="EstimatedRetainedBytes">Estimated payload bytes excluding headers, padding and the result object.</param>
/// <remarks>The payload estimate uses 28 bytes per retained particle and 36 bytes per dictionary slot
/// in the current .NET x64 layout. It is not total process memory. Candidate and distance counts
/// deliberately omit redundant within-component queries and are not force acceptance counts.</remarks>
public readonly record struct DiagnosticExecution(bool UsedSpatialIndex, DiagnosticFallbackReason FallbackReason,
    long CandidatePairs, long DistanceEvaluations, int ParticleCapacity, int CellCapacity, int CoincidenceCapacity,
    int OccupiedCells, long EstimatedRetainedBytes);
