using System.Globalization;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Selects an exact menu value. Direction is presented in clockwise degrees.</summary>
internal enum RadialBrushParameter { Radius, Strength, ParticlesPerSample, Direction }

/// <summary>Owns session-only brush preferences, preview selection and cancellable menu drafts.</summary>
/// <remarks>This class never observes particles, changes playback, or creates editor history. A caller
/// captures <see cref="ActiveBrush"/> only when starting an actual completed-boundary brush gesture.
/// Radius and shape are shared; all remaining values belong to each of the seven editing tools.</remarks>
internal sealed class RadialBrushState
{
    internal static readonly string[] ToolNames = ["Navigate only", "Move", "Add particles", "Paint species", "Erase", "Attract", "Repel", "Directional force"];
    internal static readonly string[] ToolDescriptions = ["Pan and zoom the world", "Drag particles", "Add particles per sample", "Change particle species", "Remove particles", "Pull particles inward", "Push particles outward", "Push along an angle"];
    private readonly BrushSettings[] preferences = Enumerable.Range(0, 7).Select(index => new BrushSettings { Kind = (BrushKind)index }).ToArray();
    private double radius = 0.06;
    private BrushShape shape;
    private int hoverCandidate = -1;
    private double hoverElapsed;
    private double contextElapsed;
    private double transition;
    private double dragStart;
    private RadialBrushParameter? dragging;
    private RadialBrushParameter? numeric;
    private string draft = string.Empty;
    private int cursor;
    private bool selected;
    private int speciesCount = 1;

    internal int ActiveTool { get; private set; }
    internal int FocusedTool { get; private set; }
    internal bool BrushEnabled => ActiveTool > 0;
    internal bool IsOpen { get; private set; }
    internal bool IsVisible => IsOpen || transition > 0;
    internal bool CompactDetails { get; private set; }
    internal bool ReducedMotion { get; set; }
    internal double Visibility => transition;
    internal double ContextVisibility => Math.Clamp(contextElapsed / 0.12, 0, 1);
    internal bool IsDragging => dragging.HasValue;
    internal bool EditingNumeric => numeric.HasValue;
    internal RadialBrushParameter? EditingParameter => numeric ?? dragging;
    internal string NumericDraft => draft;
    internal int NumericCursor => cursor;
    internal bool NumericSelected => selected;
    internal string? ValidationError { get; private set; }
    internal int AffectedPage { get; private set; }
    internal int DestinationPage { get; private set; }
    internal int SpeciesCount => speciesCount;
    internal const int SpeciesPerPage = 7;
    internal double Radius => radius;
    internal BrushShape Shape => shape;
    internal BrushSettings ActiveBrush => Settings(ActiveTool);
    internal BrushSettings FocusedBrush => Settings(FocusedTool);

    private BrushSettings Settings(int tool) => preferences[Math.Max(0, tool - 1)] with { Radius = radius, Shape = shape };

    internal void Open()
    {
        IsOpen = true;
        FocusedTool = ActiveTool;
        CompactDetails = false;
        hoverCandidate = -1;
        contextElapsed = 0;
        ValidationError = null;
    }

    /// <summary>Closes after retaining completed changes; unfinished numeric drafts and drags cancel.</summary>
    internal void Close()
    {
        CancelEdit();
        IsOpen = false;
        hoverCandidate = -1;
    }

    internal void Tick(double elapsed)
    {
        elapsed = Math.Clamp(elapsed, 0, 0.1);
        transition = Math.Clamp(transition + (IsOpen ? elapsed / (ReducedMotion ? 0.07 : 0.19) : -elapsed / 0.12), 0, 1);
        contextElapsed += elapsed;
    }

    /// <summary>Delays preview switches while preserving the current context across gaps and captured controls.</summary>
    internal void Hover(int tool, double elapsed)
    {
        if (!IsOpen || IsDragging || EditingNumeric || tool < 0 || tool > 7)
        {
            hoverCandidate = -1;
            hoverElapsed = 0;
            return;
        }
        if (tool == FocusedTool) { hoverCandidate = -1; hoverElapsed = 0; return; }
        if (tool != hoverCandidate) { hoverCandidate = tool; hoverElapsed = 0; }
        hoverElapsed += Math.Max(0, elapsed);
        if (hoverElapsed >= 0.14) { FocusTool(tool); }
    }

    internal void FocusTool(int tool)
    {
        if (tool < 0 || tool > 7 || IsDragging || EditingNumeric) { return; }
        if (FocusedTool != tool) { contextElapsed = 0; }
        FocusedTool = tool;
        hoverCandidate = -1;
        hoverElapsed = 0;
        ValidationError = null;
    }

    internal void ActivateFocused(bool compact = false)
    {
        if (IsDragging || EditingNumeric) { return; }
        ActiveTool = FocusedTool;
        CompactDetails = compact && FocusedTool > 0;
    }

    internal void Back() { CompactDetails = false; }

    internal void SetShape(BrushShape value)
    {
        if (FocusedTool == 0 || !Enum.IsDefined(value)) { return; }
        shape = value;
        ActiveTool = FocusedTool;
    }

    internal double Value(RadialBrushParameter parameter) => parameter switch
    {
        RadialBrushParameter.Radius => radius,
        RadialBrushParameter.Strength => FocusedBrush.Intensity,
        RadialBrushParameter.ParticlesPerSample => FocusedBrush.AddCount,
        RadialBrushParameter.Direction => NormalizeDegrees(FocusedBrush.DirectionRadians * 180 / Math.PI),
        _ => throw new ArgumentOutOfRangeException(nameof(parameter))
    };

    internal static string ParameterName(RadialBrushParameter parameter) => parameter switch
    {
        RadialBrushParameter.Radius => "Radius",
        RadialBrushParameter.Strength => "Strength",
        RadialBrushParameter.ParticlesPerSample => "Particles per sample",
        RadialBrushParameter.Direction => "Direction",
        _ => throw new ArgumentOutOfRangeException(nameof(parameter))
    };

    internal static string Units(RadialBrushParameter parameter) => parameter switch
    {
        RadialBrushParameter.Radius => "L",
        RadialBrushParameter.Strength => "L/T²",
        RadialBrushParameter.Direction => "°",
        _ => string.Empty
    };

    /// <summary>Uses the shortest round-trip representation, preserving exact edits without binary padding.</summary>
    internal string Readout(RadialBrushParameter parameter) => Value(parameter).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Formats a concise live readout; entering the field still exposes the full stored precision.</summary>
    internal string DisplayValue(RadialBrushParameter parameter) => Value(parameter).ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>Validates before changing preferences. Exact values are independent of convenient slider ranges.</summary>
    internal bool SetParameter(RadialBrushParameter parameter, double value)
    {
        if (FocusedTool == 0) { return false; }
        if (!double.IsFinite(value) || parameter == RadialBrushParameter.Radius && value <= 0
            || parameter == RadialBrushParameter.Strength && value < 0
            || parameter == RadialBrushParameter.ParticlesPerSample && (value < 1 || value > 4096 || value != Math.Truncate(value)))
        {
            ValidationError = parameter switch
            {
                RadialBrushParameter.Radius => "Enter a number above 0.",
                RadialBrushParameter.Strength => "Enter 0 or a positive number.",
                RadialBrushParameter.ParticlesPerSample => "Enter a whole number, 1–4096.",
                _ => "Enter a finite angle in degrees."
            };
            return false;
        }
        BrushSettings current = preferences[FocusedTool - 1];
        switch (parameter)
        {
            case RadialBrushParameter.Radius: radius = value; break;
            case RadialBrushParameter.Strength: preferences[FocusedTool - 1] = current with { Intensity = value }; break;
            case RadialBrushParameter.ParticlesPerSample: preferences[FocusedTool - 1] = current with { AddCount = (int)value }; break;
            case RadialBrushParameter.Direction: preferences[FocusedTool - 1] = current with { DirectionRadians = NormalizeDegrees(value) * (Math.PI / 180) }; break;
        }
        ActiveTool = FocusedTool;
        ValidationError = null;
        return true;
    }

    /// <summary>Consumes menu wheel changes; Navigate never changes radius or camera zoom.</summary>
    internal void Wheel(int delta)
    {
        if (!IsOpen || FocusedTool == 0 || IsDragging || EditingNumeric || delta == 0) { return; }
        double next = radius * Math.Pow(1.12, delta / 120.0);
        if (double.IsFinite(next) && next > 0) { SetParameter(RadialBrushParameter.Radius, next); }
    }

    internal void BeginDrag(RadialBrushParameter parameter)
    {
        if (FocusedTool == 0 || EditingNumeric) { return; }
        dragging = parameter;
        dragStart = Value(parameter);
        ActiveTool = FocusedTool;
    }

    /// <summary>Applies a captured presentation value while retaining the original value for cancellation.</summary>
    internal void DragValue(double value) { if (dragging.HasValue) { SetParameter(dragging.Value, value); } }
    internal void EndDrag() { dragging = null; }

    internal void BeginNumeric(RadialBrushParameter parameter)
    {
        if (FocusedTool == 0 || IsDragging) { return; }
        ActiveTool = FocusedTool;
        numeric = parameter;
        draft = Readout(parameter);
        cursor = draft.Length;
        selected = true;
        ValidationError = null;
    }

    internal void NumericText(char character)
    {
        if (!EditingNumeric || char.IsControl(character) || draft.Length >= 128) { return; }
        if (selected) { draft = string.Empty; cursor = 0; }
        draft = draft.Insert(cursor++, character.ToString());
        selected = false;
        ValidationError = null;
    }

    internal void NumericKey(Keys key, bool control)
    {
        if (!EditingNumeric) { return; }
        if (control && key == Keys.A) { selected = true; cursor = draft.Length; }
        else if (key is Keys.Home or Keys.End or Keys.Left or Keys.Right)
        {
            cursor = key == Keys.Home || key == Keys.Left && selected ? 0
                : key == Keys.End || key == Keys.Right && selected ? draft.Length
                : Math.Clamp(cursor + (key == Keys.Left ? -1 : 1), 0, draft.Length);
            selected = false;
        }
        else if (key is Keys.Back or Keys.Delete)
        {
            if (selected) { draft = string.Empty; cursor = 0; }
            else if (key == Keys.Back && cursor > 0) { draft = draft.Remove(--cursor, 1); }
            else if (key == Keys.Delete && cursor < draft.Length) { draft = draft.Remove(cursor, 1); }
            selected = false;
            ValidationError = null;
        }
    }

    internal bool TryCommitNumeric()
    {
        if (!numeric.HasValue) { return true; }
        if (!double.TryParse(draft, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            ValidationError = "Enter a complete finite number.";
            return false;
        }
        if (!SetParameter(numeric.Value, value)) { return false; }
        numeric = null;
        return true;
    }

    /// <summary>Escape cancels the unfinished draft first; the host closes only if this returns false.</summary>
    internal bool CancelEdit()
    {
        bool cancelled = IsDragging || EditingNumeric;
        if (dragging.HasValue) { SetParameter(dragging.Value, dragStart); }
        dragging = null;
        numeric = null;
        ValidationError = null;
        return cancelled;
    }

    internal void SelectSpecies(int species, bool destination)
    {
        if (FocusedTool == 0 || species < (destination ? 0 : -1) || species >= speciesCount) { return; }
        BrushSettings current = preferences[FocusedTool - 1];
        preferences[FocusedTool - 1] = destination ? current with { Species = species } : current with { SpeciesFilter = species };
        ActiveTool = FocusedTool;
    }

    internal void PageSpecies(int delta, bool destination)
    {
        int page = destination ? DestinationPage : AffectedPage;
        page = Math.Clamp(page + delta, 0, (speciesCount - 1) / SpeciesPerPage);
        if (destination) { DestinationPage = page; } else { AffectedPage = page; }
    }

    /// <summary>Repairs only unavailable selections, retaining ordering and identities for surviving species.</summary>
    internal void ReconcileSpecies(int count)
    {
        if (speciesCount == Math.Max(1, count)) { return; }
        speciesCount = Math.Max(1, count);
        for (int i = 0; i < preferences.Length; i++)
        {
            BrushSettings current = preferences[i];
            preferences[i] = current with { Species = Math.Min(current.Species, speciesCount - 1), SpeciesFilter = current.SpeciesFilter >= speciesCount ? -1 : current.SpeciesFilter };
        }
        DestinationPage = Math.Min(DestinationPage, (speciesCount - 1) / SpeciesPerPage);
        AffectedPage = Math.Min(AffectedPage, (speciesCount - 1) / SpeciesPerPage);
    }

    private static double NormalizeDegrees(double value) => ((value % 360) + 360) % 360;
}
