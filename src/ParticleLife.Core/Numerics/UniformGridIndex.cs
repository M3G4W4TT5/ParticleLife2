using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>Maps full signed integer cell coordinates to contiguous, reusable particle ranges.</summary>
/// <remarks>
/// <para>The cell width h is the smallest power of two at least as large as cutoff R, so R &lt;= h &lt; 2R.
/// Mapping uses floor(ScaleB(position, -log2(h))), avoiding rounded division at cell faces.
/// Scaling by a power of two is exact unless it underflows or overflows. Underflowed negative values
/// map to -1, their exact floor; positive underflow maps to zero. Values with magnitude at least 2^50
/// cell widths use direct fallback. This conservative bound retains fractional resolution and leaves
/// ample Int64 range for neighbour offsets; conversion never silently saturates or overflows.</para>
/// <para>Every point separated by less than R lies in the receiver cell or one of its eight adjacent
/// cells. Since h is exactly representable, an exact component separation at least h cannot round
/// below h on subtraction. Thus direct acceptance of a rounded component below R also implies a
/// true separation below h and is covered by this search. Query visits the nine distinct keys once.
/// Dictionary hashing accelerates lookup but full coordinate equality determines cell identity,
/// including under hash collisions. Counting and a stable scatter produce one contiguous range per
/// occupied cell. Each range preserves ascending source storage order; physical state is never reordered.</para>
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
    private readonly int cellExponent;
    private CellCoordinate[] particleCells = [];
    private int[] particleCellIds = [];
    private int[] starts = [];
    private int[] ends = [];
    private int[] sources = [];
    private int count;
    private int cellCapacity;

    internal UniformGridIndex(double cutoff, IEqualityComparer<CellCoordinate>? comparer = null)
    {
        ModelParameters.RequirePositiveFinite(cutoff, nameof(cutoff));
        ModelParameters.RequirePositiveFinite(cutoff * cutoff, nameof(cutoff));
        cellExponent = Math.ILogB(cutoff);
        if (Math.ScaleB(1, cellExponent) < cutoff)
        {
            cellExponent++;
        }

        CellWidth = Math.ScaleB(1, cellExponent);
        cells = new Dictionary<CellCoordinate, int>(comparer);
    }

    internal double CellWidth { get; }

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

    /// <summary>Visits occupied adjacent-cell packed ranges in the original M4 neighbour-cell order.</summary>
    internal RangeEnumerator CandidateRanges(int receiver)
    {
        if ((uint)receiver >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(receiver));
        }

        return new RangeEnumerator(this, particleCells[receiver]);
    }

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

    /// <summary>Visits nine distinct keys; the immutable dictionary permits concurrent queries.</summary>
    internal struct RangeEnumerator
    {
        private readonly UniformGridIndex index;
        private readonly CellCoordinate center;
        private int cellOffset;

        internal RangeEnumerator(UniformGridIndex index, CellCoordinate center)
        {
            this.index = index;
            this.center = center;
            cellOffset = 0;
            Current = default;
        }

        internal CellRange Current { get; private set; }

        internal bool MoveNext()
        {
            while (cellOffset < 9)
            {
                CellCoordinate cell = new(center.X + cellOffset % 3 - 1, center.Y + cellOffset / 3 - 1);
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

    /// <summary>Visits every source in the nine cell ranges once in ascending within-cell storage order.</summary>
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
