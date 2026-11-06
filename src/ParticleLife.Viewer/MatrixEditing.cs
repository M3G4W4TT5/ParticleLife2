using System.Globalization;
using ParticleLife.Core.Models;

namespace ParticleLife.Viewer;

/// <summary>One directed receiver-row/source-column selection.</summary>
public readonly record struct MatrixCell(int Receiver, int Source);

/// <summary>Tracks an explicit directed selection without changing model coefficients.</summary>
public sealed class MatrixSelection
{
    private readonly HashSet<MatrixCell> cells = [];
    private MatrixCell anchor;
    /// <summary>Creates an initial single-cell selection.</summary><param name="speciesCount">Positive dimension.</param>
    public MatrixSelection(int speciesCount) { Resize(speciesCount); }
    /// <summary>Gets the current matrix dimension.</summary>
    public int SpeciesCount { get; private set; }
    /// <summary>Gets selected directed cells.</summary>
    public IReadOnlyCollection<MatrixCell> Cells => cells;
    /// <summary>Gets the selected cell count.</summary>
    public int Count => cells.Count;
    /// <summary>Resets to one valid cell when dimensions change.</summary><param name="speciesCount">Positive dimension.</param>
    public void Resize(int speciesCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speciesCount);
        if (SpeciesCount == speciesCount) { return; }
        SpeciesCount = speciesCount; cells.Clear(); anchor = new(0, 0); cells.Add(anchor);
    }
    /// <summary>Selects, toggles or extends a rectangular cell range from the previous anchor.</summary>
    /// <param name="receiver">Receiver row.</param><param name="source">Source column.</param>
    /// <param name="extend">Select a rectangle from the current anchor.</param><param name="additive">Retain existing cells, toggling a single cell.</param>
    public void Select(int receiver, int source, bool extend = false, bool additive = false)
    {
        Validate(receiver); Validate(source);
        MatrixCell target = new(receiver, source);
        if (!additive) { cells.Clear(); }
        if (extend)
        {
            for (int row = Math.Min(anchor.Receiver, receiver); row <= Math.Max(anchor.Receiver, receiver); row++)
            {
                for (int column = Math.Min(anchor.Source, source); column <= Math.Max(anchor.Source, source); column++) { cells.Add(new(row, column)); }
            }
        }
        else
        {
            if (!additive || !cells.Remove(target)) { cells.Add(target); }
            anchor = target;
        }
    }
    /// <summary>Selects one whole directed receiver row.</summary><param name="receiver">Receiver row.</param><param name="additive">Retain existing cells.</param>
    public void SelectRow(int receiver, bool additive = false)
    {
        Validate(receiver); if (!additive) { cells.Clear(); }
        for (int source = 0; source < SpeciesCount; source++) { cells.Add(new(receiver, source)); }
        anchor = new(receiver, 0);
    }
    /// <summary>Selects one whole directed source column.</summary><param name="source">Source column.</param><param name="additive">Retain existing cells.</param>
    public void SelectColumn(int source, bool additive = false)
    {
        Validate(source); if (!additive) { cells.Clear(); }
        for (int receiver = 0; receiver < SpeciesCount; receiver++) { cells.Add(new(receiver, source)); }
        anchor = new(0, source);
    }
    /// <summary>Selects all directed entries.</summary>
    public void SelectAll()
    {
        cells.Clear();
        for (int receiver = 0; receiver < SpeciesCount; receiver++)
        {
            for (int source = 0; source < SpeciesCount; source++) { cells.Add(new(receiver, source)); }
        }
    }
    private void Validate(int index)
    {
        if (index < 0 || index >= SpeciesCount) { throw new ArgumentOutOfRangeException(nameof(index)); }
    }
}

/// <summary>Selects a bulk coefficient edit over an explicit directed selection.</summary>
public enum MatrixBulkOperation
{
    /// <summary>Set each coefficient to the supplied value.</summary>
    Set,
    /// <summary>Multiply each coefficient and clamp to [-1,1].</summary>
    Scale,
    /// <summary>Generate one seeded uniform value per affected directed entry.</summary>
    Randomize,
    /// <summary>Set affected coefficients to zero.</summary>
    Reset
}

/// <summary>Validates matrix clipboard data and computes explicit bulk coefficient replacements.</summary>
public static class MatrixEditing
{
    /// <summary>Builds coefficients in stable row-major order, optionally including each reverse direction.</summary>
    /// <param name="matrix">Current immutable matrix.</param><param name="selection">Explicit directed selected cells.</param>
    /// <param name="operation">Bulk edit.</param><param name="value">Set coefficient or finite scale factor.</param>
    /// <param name="seed">Random-generation provenance seed.</param><param name="linkReverse">Include transposed entries.</param>
    /// <returns>Owned complete coefficient values, preserving unselected entries.</returns>
    public static double[] Apply(InteractionMatrix matrix, IEnumerable<MatrixCell> selection, MatrixBulkOperation operation,
        double value, int seed, bool linkReverse = false)
    {
        if (!Enum.IsDefined(operation) || !double.IsFinite(value) || (operation == MatrixBulkOperation.Set && Math.Abs(value) > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Set values must be in [-1,1]; scale factors must be finite.");
        }
        double[] values = matrix.Values.ToArray();
        bool[] selected = new bool[values.Length];
        foreach (MatrixCell cell in selection)
        {
            _ = matrix[cell.Receiver, cell.Source];
            selected[cell.Receiver * matrix.SpeciesCount + cell.Source] = true;
            if (linkReverse) { selected[cell.Source * matrix.SpeciesCount + cell.Receiver] = true; }
        }
        Random random = new(seed);
        for (int i = 0; i < values.Length; i++)
        {
            if (!selected[i]) { continue; }
            values[i] = operation switch
            {
                MatrixBulkOperation.Set => value,
                MatrixBulkOperation.Scale => Math.Clamp(values[i] * value, -1, 1),
                MatrixBulkOperation.Randomize => linkReverse && i / matrix.SpeciesCount > i % matrix.SpeciesCount
                    ? values[(i % matrix.SpeciesCount) * matrix.SpeciesCount + i / matrix.SpeciesCount] : 2 * random.NextDouble() - 1,
                MatrixBulkOperation.Reset => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
        }
        return values;
    }

    /// <summary>Exports a full exact receiver-row/source-column matrix as tab-separated invariant numbers.</summary>
    /// <param name="matrix">Source matrix.</param><returns>One line per receiver, G17 coefficients separated by tabs.</returns>
    public static string Export(InteractionMatrix matrix)
    {
        string[] rows = new string[matrix.SpeciesCount];
        for (int receiver = 0; receiver < rows.Length; receiver++)
        {
            string[] values = new string[rows.Length];
            for (int source = 0; source < values.Length; source++) { values[source] = matrix[receiver, source].ToString("G17", CultureInfo.InvariantCulture); }
            rows[receiver] = string.Join('\t', values);
        }
        return string.Join('\n', rows);
    }

    /// <summary>Parses a complete square TSV/CSV/space-separated matrix without changing active state.</summary>
    /// <param name="text">Clipboard text in receiver-row/source-column order.</param><param name="speciesCount">Required dimension.</param>
    /// <returns>Owned coefficients only after all dimensions and finite [-1,1] values pass validation.</returns>
    public static double[] Import(string text, int speciesCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speciesCount);
        string[] rows = text.Replace("\r", "", StringComparison.Ordinal).Trim().Split('\n', StringSplitOptions.TrimEntries);
        if (rows.Length != speciesCount) { throw new ArgumentException($"Expected {speciesCount} receiver rows.", nameof(text)); }
        double[] result = new double[checked(speciesCount * speciesCount)];
        for (int receiver = 0; receiver < speciesCount; receiver++)
        {
            string row = rows[receiver];
            string[] cells = row.Contains('\t') ? row.Split('\t', StringSplitOptions.TrimEntries)
                : row.Contains(',') ? row.Split(',', StringSplitOptions.TrimEntries)
                : row.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cells.Length != speciesCount) { throw new ArgumentException($"Receiver row {receiver} must have {speciesCount} source values.", nameof(text)); }
            for (int source = 0; source < speciesCount; source++)
            {
                if (!double.TryParse(cells[source], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || Math.Abs(value) > 1)
                {
                    throw new ArgumentException($"Coefficient [{receiver},{source}] must be finite and in [-1,1].", nameof(text));
                }
                result[receiver * speciesCount + source] = value;
            }
        }
        return result;
    }
}
