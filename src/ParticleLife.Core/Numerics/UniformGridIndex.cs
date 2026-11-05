using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>Maps full signed integer cell coordinates to contiguous, reusable particle ranges.</summary>
/// <remarks>
/// <para>By default, nonperiodic cell width h is the smallest power of two at least as large as the maximum cutoff R, so R &lt;= h &lt; 2R.
/// Mapping uses floor(ScaleB(position, -log2(h))), avoiding rounded division at cell faces.
/// Scaling by a power of two is exact unless it underflows or overflows. Underflowed negative values
/// map to -1, their exact floor; positive underflow maps to zero. Values with magnitude at least 2^50
/// cell widths use direct fallback. This conservative bound retains fractional resolution and leaves
/// ample Int64 range for neighbour offsets; conversion never silently saturates or overflows.</para>
/// <para>Every point separated by less than R lies in the receiver cell or one of its eight adjacent
/// cells. Since h is exactly representable, an exact component separation at least h cannot round
/// below h on subtraction. Thus direct acceptance of a rounded component below R also implies a
/// true separation below h and is covered by this search. Default nonperiodic queries visit nine distinct keys once.
/// Dictionary hashing accelerates lookup but full coordinate equality determines cell identity,
/// including under hash collisions. Counting and a stable scatter produce one contiguous range per
/// occupied cell. Each range preserves ascending source storage order; physical state is never reordered.</para>
/// <para>Nonperiodic heterogeneous receiver rows can restrict that same nine-cell search to a
/// conservative row-radius rectangle. Fractional cell coordinates and the radius are rounded
/// outward before selecting neighbour columns/rows. This skips only cells that fail the pair
/// kernel's component cutoff, including subnormal distances; no circular/squared-distance
/// approximation can remove a neighbour, and retained cells keep their original query order.</para>
/// <para>Periodic worlds instead tile each axis with equal cells at least R wide. Up to five cells
/// per axis include a conservative extra ring for floating-point division at cell faces. A wrapped
/// query visits at most the number of cells on that axis, so small grids never duplicate a source.
/// Cell counts are capped at 2^50 per axis; sparse allocation still follows particle count. Pair
/// acceptance uses the same minimum-image geometry as the direct reference.</para>
/// <para>At stable population, expected construction is O(N), query overhead O(N), and traversal
/// O(candidate pairs), which remains quadratic in dense cells. After shrink, clearing the dictionary
/// costs O(retained capacity). Slots and arrays reserve for N cells; no occupancy limit is imposed.
/// Warmed builds and traversals allocate zero managed bytes even when occupancy grows from one to N
/// cells. A build is exclusive. Completed ranges support concurrent readers until the next build.</para>
/// </remarks>
internal sealed class UniformGridIndex
{
    internal const double CoordinateLimit = 1125899906842624; // 2^50 cell widths.
    private readonly Dictionary<CellCoordinate, int> cells;
    private int cellExponent;
    private CellCoordinate[] particleCells = [];
    private int[] particleCellIds = [];
    private int[] starts = [];
    private int[] ends = [];
    private int[] sources = [];
    private int count;
    private int cellCapacity;
    private bool periodic;
    private double worldWidth, worldHeight, periodicCellWidth, periodicCellHeight;
    private long columns, rows;

    internal UniformGridIndex(double cutoff, IEqualityComparer<CellCoordinate>? comparer = null)
    {
        cells = new Dictionary<CellCoordinate, int>(comparer);
        ReconfigureCutoff(cutoff);
    }

    internal double CellWidth { get; private set; }

    /// <summary>Configures a finite periodic tiling after selecting the maximum interaction cutoff.</summary>
    /// <remarks>Periodic cells divide each world axis evenly and are at least the cutoff wide.
    /// The query includes one extra cell on each side to cover floating-point face mapping;
    /// wrapped visits are deduplicated even in two-cell worlds. The sparse dictionary still
    /// allocates by population, never by the product of world cell counts.</remarks>
    internal void ConfigureBoundary(ModelParameters parameters, double cutoff)
    {
        periodic = parameters.Boundary == BoundaryKind.Periodic;
        worldWidth = parameters.WorldWidth;
        worldHeight = parameters.WorldHeight;
        if (periodic)
        {
            columns = (long)Math.Clamp(Math.Floor(worldWidth / cutoff), 2, CoordinateLimit);
            rows = (long)Math.Clamp(Math.Floor(worldHeight / cutoff), 2, CoordinateLimit);
            periodicCellWidth = worldWidth / columns;
            periodicCellHeight = worldHeight / rows;
            CellWidth = Math.Min(periodicCellWidth, periodicCellHeight);
        }
        cells.Clear();
        count = 0;
    }

    /// <summary>Invalidates completed ranges and updates cell geometry while retaining every capacity.</summary>
    /// <remarks>Call only between builds/evaluations. The next build remaps all coordinates from the
    /// completed input; no old cell assignment can survive a cutoff power-of-two transition.</remarks>
    internal void ReconfigureCutoff(double cutoff)
    {
        ModelParameters.RequirePositiveFinite(cutoff, nameof(cutoff));
        ModelParameters.RequirePositiveFinite(cutoff * cutoff, nameof(cutoff));
        int nextExponent = Math.ILogB(cutoff);
        if (Math.ScaleB(1, nextExponent) < cutoff)
        {
            nextExponent++;
        }

        cellExponent = nextExponent;
        CellWidth = Math.ScaleB(1, cellExponent);
        cells.Clear();
        count = 0;
    }

    internal GridIndexStatistics Statistics => new(sources.Length, cellCapacity, cells.Count,
        32L * sources.Length + 36L * cellCapacity, CellWidth);

    /// <summary>Maps packed range offsets to the original particle storage indices.</summary>
    internal ReadOnlySpan<int> PackedSources => sources.AsSpan(0, count);

    /// <summary>Rebuilds all ranges from one stable input, clearing old entries and retaining capacity.</summary>
    /// <returns>False when any coordinate requires whole-evaluation direct fallback; no partial index is usable.</returns>
    /// <remarks>Caller validates finite state. No input position span is retained. Stable scatter preserves
    /// the M4 linked-list source ordering within each cell, so packing alone does not change force sums.</remarks>
    internal bool Build(ReadOnlySpan<Vector2d> positions)
    {
        cells.Clear();
        count = 0;
        if (positions.Length > sources.Length)
        {
            particleCells = new CellCoordinate[positions.Length];
            particleCellIds = new int[positions.Length];
            starts = new int[positions.Length];
            ends = new int[positions.Length];
            sources = new int[positions.Length];
            cellCapacity = cells.EnsureCapacity(positions.Length);
        }

        // Complete coordinate validation before publishing any occupied cell.
        for (int i = 0; i < positions.Length; i++)
        {
            if (periodic)
            {
                double normalizedX = WorldBoundary.WrapValidated(positions[i].X, worldWidth);
                double normalizedY = WorldBoundary.WrapValidated(positions[i].Y, worldHeight);
                particleCells[i] = new((long)Math.Min(columns - 1, Math.Floor(normalizedX / periodicCellWidth)),
                    (long)Math.Min(rows - 1, Math.Floor(normalizedY / periodicCellHeight)));
                continue;
            }
            if (!TryMap(positions[i].X, out long x) || !TryMap(positions[i].Y, out long y))
            {
                return false;
            }

            particleCells[i] = new CellCoordinate(x, y);
        }

        for (int i = 0; i < positions.Length; i++)
        {
            CellCoordinate cell = particleCells[i];
            if (!cells.TryGetValue(cell, out int cellId))
            {
                cellId = cells.Count;
                cells.Add(cell, cellId);
                ends[cellId] = 0;
            }

            particleCellIds[i] = cellId;
            ends[cellId]++;
        }

        int offset = 0;
        for (int cellId = 0; cellId < cells.Count; cellId++)
        {
            starts[cellId] = offset;
            offset += ends[cellId];
            ends[cellId] = starts[cellId];
        }

        // Ends act as scatter cursors and finish as exclusive range ends.
        for (int i = 0; i < positions.Length; i++)
        {
            sources[ends[particleCellIds[i]]++] = i;
        }

        count = positions.Length;
        return true;
    }

    /// <summary>Enumerates original source indices including self, without shared mutable query state.</summary>
    /// <remarks>Valid until the next build. The caller excludes self and applies exact pair acceptance.</remarks>
    internal CandidateEnumerator Candidates(int receiver) => new(this, CandidateRanges(receiver));

    /// <summary>Visits occupied packed ranges, retaining the original M4 order for nonperiodic worlds.</summary>
    internal RangeEnumerator CandidateRanges(int receiver)
    {
        if ((uint)receiver >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(receiver));
        }

        return new RangeEnumerator(this, particleCells[receiver]);
    }

    /// <summary>Visits a conservative nonperiodic receiver-row rectangle within the original nine cells.</summary>
    /// <remarks>Radius is the largest cutoff in this receiver's row and does not exceed cell width.
    /// Columns/rows are removed only when every source fails the pair kernel's component cutoff.</remarks>
    internal RangeEnumerator CandidateRanges(int receiver, Vector2d position, double radius) =>
        new(this, particleCells[receiver], position, radius);

    /// <summary>Uses exact binary scaling and signed floor, returning false before unsafe conversion.</summary>
    internal bool TryMap(double position, out long coordinate)
    {
        double scaled = Math.ScaleB(position, -cellExponent);
        if (!double.IsFinite(scaled) || Math.Abs(scaled) >= CoordinateLimit)
        {
            coordinate = 0;
            return false;
        }

        coordinate = scaled == 0 && position < 0 ? -1 : (long)Math.Floor(scaled);
        return true;
    }

    /// <summary>The complete two-dimensional key; a hash is never cell identity.</summary>
    internal readonly record struct CellCoordinate(long X, long Y);

    /// <summary>One occupied cell's contiguous packed interval, with an exclusive end.</summary>
    internal readonly record struct CellRange(int Start, int End);

    /// <summary>Visits distinct candidate keys; the completed dictionary permits concurrent queries.</summary>
    internal struct RangeEnumerator
    {
        private readonly UniformGridIndex index;
        private readonly CellCoordinate center;
        private int cellOffset;
        private readonly int visitWidth;
        private readonly int visitHeight;
        private readonly int reachX;
        private readonly int reachY;

        internal RangeEnumerator(UniformGridIndex index, CellCoordinate center)
        {
            this.index = index;
            this.center = center;
            cellOffset = 0;
            visitWidth = index.periodic ? (int)Math.Min(5, index.columns) : 3;
            visitHeight = index.periodic ? (int)Math.Min(5, index.rows) : 3;
            reachX = reachY = index.periodic ? 2 : 1;
            Current = default;
        }

        /// <summary>Resolves coarse component interval bounds once per receiver, without per-cell geometry tests.</summary>
        /// <remarks>Power-of-two scaling is exact except subnormal underflow. The negative-underflow
        /// cell maps to -1, giving a safe fraction of one. One outward ULP on each interval/radius
        /// covers arithmetic rounding. Because h >= the maximum cutoff, the original nine-cell
        /// proof still applies; selected neighbouring columns/rows form a contiguous rectangle.</remarks>
        internal RangeEnumerator(UniformGridIndex index, CellCoordinate center, Vector2d position, double radius)
        {
            this.index = index; this.center = center; cellOffset = 0;
            double x = Math.ScaleB(position.X, -index.cellExponent) - center.X;
            double y = Math.ScaleB(position.Y, -index.cellExponent) - center.Y;
            double upperRadius = Math.BitIncrement(Math.ScaleB(radius, -index.cellExponent));
            bool left = Math.BitDecrement(x) < upperRadius, right = Math.BitDecrement(1 - x) < upperRadius;
            bool top = Math.BitDecrement(y) < upperRadius, bottom = Math.BitDecrement(1 - y) < upperRadius;
            reachX = left ? 1 : 0; reachY = top ? 1 : 0;
            visitWidth = 1 + reachX + (right ? 1 : 0);
            visitHeight = 1 + reachY + (bottom ? 1 : 0);
            Current = default;
        }

        internal CellRange Current { get; private set; }

        internal bool MoveNext()
        {
            while (cellOffset < visitWidth * visitHeight)
            {
                long x = center.X + cellOffset % visitWidth - reachX;
                long y = center.Y + cellOffset / visitWidth - reachY;
                if (index.periodic)
                {
                    x = (x % index.columns + index.columns) % index.columns;
                    y = (y % index.rows + index.rows) % index.rows;
                }
                CellCoordinate cell = new(x, y);
                cellOffset++;
                if (index.cells.TryGetValue(cell, out int cellId))
                {
                    Current = new(index.starts[cellId], index.ends[cellId]);
                    return true;
                }
            }

            return false;
        }

    }

    /// <summary>Visits every candidate source once in ascending within-cell storage order.</summary>
    internal struct CandidateEnumerator
    {
        private readonly UniformGridIndex index;
        private RangeEnumerator ranges;
        private int cursor;
        private int end;

        internal CandidateEnumerator(UniformGridIndex index, RangeEnumerator ranges)
        {
            this.index = index;
            this.ranges = ranges;
            cursor = 0;
            end = 0;
            Current = -1;
        }

        internal CandidateEnumerator GetEnumerator() => this;

        internal int Current { get; private set; }

        internal bool MoveNext()
        {
            if (cursor == end)
            {
                if (!ranges.MoveNext())
                {
                    return false;
                }

                cursor = ranges.Current.Start;
                end = ranges.Current.End;
            }

            Current = index.sources[cursor++];
            return true;
        }
    }
}
