using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>Maps full signed integer cell coordinates to reusable particle-link lists.</summary>
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
/// Dictionary hashing accelerates lookup
/// but full coordinate equality determines cell identity, including under hash collisions. Lists have
/// no occupancy limit and contain every source once. Physical positions are never clamped or wrapped.</para>
/// <para>At stable population, expected construction is O(N), query overhead O(N), and traversal
/// O(candidate pairs), which remains quadratic in dense cells. After population shrink, clearing the
/// retained dictionary adds O(retained cell capacity) work. Slots are reserved for N particles/cells;
/// retained space is O(maximum population), independent of nominal world size and coordinate extent.
/// At established population capacity all builds and traversals allocate zero managed bytes, even
/// when occupancy grows from one cell to N cells. Instances and enumerators are not thread safe.</para>
/// </remarks>
internal sealed class UniformGridIndex
{
    internal const double CoordinateLimit = 1125899906842624; // 2^50 cell widths.
    private readonly Dictionary<CellCoordinate, int> heads;
    private readonly int cellExponent;
    private int[] next = [];
    private CellCoordinate[] particleCells = [];
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
        heads = new Dictionary<CellCoordinate, int>(comparer);
    }

    internal double CellWidth { get; }

    internal GridIndexStatistics Statistics => new(next.Length, cellCapacity, heads.Count,
        20L * next.Length + 36L * cellCapacity, CellWidth);

    /// <summary>Rebuilds all lists from one stable input, retaining capacity and clearing all prior entries.</summary>
    /// <returns>False if any coordinate requires whole-evaluation direct fallback; no partial index remains usable.</returns>
    /// <remarks>Caller validates finite state before building. No view or position array is retained.</remarks>
    internal bool Build(ReadOnlySpan<Vector2d> positions)
    {
        heads.Clear();
        count = 0;
        if (positions.Length > next.Length)
        {
            next = new int[positions.Length];
            particleCells = new CellCoordinate[positions.Length];
            cellCapacity = heads.EnsureCapacity(positions.Length);
        }

        // Map first, so an out-of-range position cannot leave a partially usable index.
        for (int i = 0; i < positions.Length; i++)
        {
            if (!TryMap(positions[i].X, out long x) || !TryMap(positions[i].Y, out long y))
            {
                return false;
            }

            particleCells[i] = new CellCoordinate(x, y);
        }

        // Prepending in reverse storage order makes each cell list ascend by source index.
        for (int i = positions.Length - 1; i >= 0; i--)
        {
            CellCoordinate cell = particleCells[i];
            next[i] = heads.TryGetValue(cell, out int head) ? head : -1;
            heads[cell] = i;
        }

        count = positions.Length;
        return true;
    }

    /// <summary>Enumerates candidates including the receiver, without allocation or shared query storage.</summary>
    /// <remarks>Valid only until the next build; the caller excludes self and applies exact pair acceptance.</remarks>
    internal CandidateEnumerator Candidates(int receiver)
    {
        if ((uint)receiver >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(receiver));
        }

        return new CandidateEnumerator(this, particleCells[receiver]);
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

    /// <summary>The complete two-dimensional key; a hash is never used as cell identity.</summary>
    internal readonly record struct CellCoordinate(long X, long Y);

    /// <summary>Visits nine distinct cell keys and each cell's complete source list exactly once.</summary>
    internal struct CandidateEnumerator
    {
        private readonly UniformGridIndex index;
        private readonly CellCoordinate center;
        private int cellOffset;
        private int nextSource;

        internal CandidateEnumerator(UniformGridIndex index, CellCoordinate center)
        {
            this.index = index;
            this.center = center;
            cellOffset = 0;
            nextSource = -1;
            Current = -1;
        }

        internal CandidateEnumerator GetEnumerator() => this;

        internal int Current { get; private set; }

        internal bool MoveNext()
        {
            while (nextSource < 0 && cellOffset < 9)
            {
                CellCoordinate cell = new(center.X + cellOffset % 3 - 1, center.Y + cellOffset / 3 - 1);
                cellOffset++;
                nextSource = index.heads.TryGetValue(cell, out int head) ? head : -1;
            }

            if (nextSource < 0)
            {
                return false;
            }

            Current = nextSource;
            nextSource = index.next[nextSource];
            return true;
        }
    }
}
