using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Searchable local discovery library and explicit validated file/clipboard sharing.</summary>
internal sealed class ExperimentFileBrowser : IDisposable
{
    private readonly EditorSession editor;
    private readonly Action<string> report;
    private readonly Action suppressPlayback, baselineChanged;
    private readonly Func<string, string?> updateRecent;
    private readonly UiPanel panel;
    private readonly DiscoveryLibraryScanner scanner = new();
    private readonly List<UiWidget> libraryWidgets = [];
    private DiscoveryLibraryScan scan = new("", "", [], [], []);
    private Task<Action>? fileTask;
    private Task? fileOwnerTask;
    private bool fileOperationActive;
    private bool rebuildPending;
    private volatile bool disposed;
    private bool finishWorkerDuringShutdown;
    private ReplayNotice? replayNotice;
    private string directory, name = "experiment", description = "", tags = "", query = "", mapping = "";
    private string? selected;
    private DiscoveryEntry? selectedEntry;
    private PresetDefinition? selectedPreset;
    private bool favouritesOnly, recentFirst = true;
    private PresetComponents components = PresetComponents.Coefficients;
    private string result = "Save a discovery or choose an item. Load and replay remain explicit actions.";
    internal bool IsOpen { get; private set; }
    internal Rectangle Bounds => panel.Bounds;
    internal Rectangle Body => panel.Body;
    internal void Arrange(Rectangle bounds) => panel.Arrange(bounds);
    internal void SetStatus(string text) => result = text;

    internal ExperimentFileBrowser(EditorSession editor, string directory, Action<string> report, Action suppressPlayback, Action baselineChanged,
        Func<string, string?>? updateRecent = null)
    {
        this.editor = editor; this.directory = Path.GetFullPath(directory); this.report = report;
        this.suppressPlayback = suppressPlayback; this.baselineChanged = baselineChanged;
        this.updateRecent = updateRecent ?? TouchRecent;
        editor.Failed += Error;
        panel = new UiPanel(Error, dismissOnEscape: true);
        panel.Toolbar.Add(new UiRow(new UiButton(() => "Close", Close), new UiButton(() => "Parent", Parent),
            new UiButton(() => "Refresh", Refresh, "Read local metadata and bounded headers. No files are read while drawing.")));
        panel.Toolbar.Add(new UiTextField("Folder", () => this.directory, Navigate, "Local folder. Existing scientific files and companions are never overwritten."));
    }

    internal void Open(bool pointerHeld = false)
    {
        panel.PrepareForOpen(pointerHeld); IsOpen = true;
        name = $"experiment-{DateTime.Now:yyyyMMdd-HHmmss}"; Refresh();
    }
    internal void Close() { panel.ClearFocus(); IsOpen = false; scanner.Cancel(); }
    /// <summary>Delivers file completion even while the modal is closed; called on the UI thread.</summary>
    internal void Pump()
    {
        if (disposed) { return; }
        if (scanner.Poll(out DiscoveryLibraryScan? ready, out Exception? scanError))
        {
            if (ready is not null) { scan = ready; ReplaceLibraryWidgets(); }
            else if (scanError is not OperationCanceledException && scanError is not null) { Error(scanError.Message); }
        }
        if (fileOwnerTask?.IsFaulted == true)
        {
            FileFailed(fileOwnerTask.Exception!.GetBaseException()); fileOwnerTask = null;
        }
        else if (fileOwnerTask?.IsCanceled == true) { fileOwnerTask = null; FinishFile("File operation cancelled before owner publication.", refresh: false); }
        else if (fileOwnerTask?.IsCompleted == true) { fileOwnerTask = null; }
        if (fileTask?.IsCompleted == true)
        {
            Task<Action> completed = fileTask; fileTask = null;
            try { completed.GetAwaiter().GetResult()(); }
            catch (Exception error) when (ViewerErrors.IsExpected(error)) { FileFailed(error); }
        }
        RefreshReplayStatus();
        if (rebuildPending && !panel.EditingText) { Rebuild(); }
    }

    /// <summary>Observes successful publication even when an entire replay fits between UI polls.</summary>
    internal void RefreshReplayStatus()
    {
        if (replayNotice is not { Finished: false } notice || editor.IsReplaying) { return; }
        EditorPublication publication = editor.Publication;
        notice.Message = publication.BaselineVersion != notice.StartingBaseline
            ? "Replay completed at the recorded terminal boundary, paused."
            : publication.Error is not null ? "Replay failed: " + publication.Error + ". Previous completed experiment retained."
            : notice.CancelRequested ? "Replay cancelled. Previous completed experiment retained, paused."
            : "Replay stopped before publishing a new baseline. Previous completed experiment retained, paused.";
        notice.Finished = true;
        PresentReplayNotice(notice);
    }

    /// <summary>Retains cancellation feedback alongside a still-pending recent-file metadata update.</summary>
    internal void CancelReplay()
    {
        editor.CancelSessionReplay();
        string message = editor.IsBackground ? "Replay cancellation queued; waiting for the current step to finish."
            : "Replay cancelled. Previous completed experiment retained, paused.";
        if (replayNotice is { Finished: false } notice)
        {
            notice.CancelRequested = true;
            notice.Message = message;
            PresentReplayNotice(notice);
            RefreshReplayStatus();
        }
        else { SetStatus(message); report(message); }
    }

    /// <summary>Records an actual sequential replay failure without changing a completed baseline.</summary>
    internal void ReportReplayFailure(string failure)
    {
        string message = "Replay failed: " + failure + ". Previous completed experiment retained.";
        if (replayNotice is { } notice)
        {
            notice.Message = message; notice.Finished = true; PresentReplayNotice(notice);
        }
        else { SetStatus(message); report(message); }
    }

    private void PresentReplayNotice(ReplayNotice notice)
    {
        result = ReplayText(notice); report(result);
    }

    private static string ReplayText(ReplayNotice notice) => notice.Message + Warning(notice.MetadataWarning);
    internal void Update(Rectangle bounds, float scale, MouseState mouse, IReadOnlyList<UiInputEvent> input)
    {
        panel.Bounds = bounds; panel.Update(mouse, scale, input);
        if (panel.DismissRequested) { Close(); }
    }
    private void Navigate(string path)
    {
        if (fileOperationActive) { throw new InvalidOperationException("Finish the current file operation before changing folders."); }
        string next = Path.GetFullPath(path);
        directory = next; selected = null; selectedEntry = null; selectedPreset = null;
        panel.ScrollToTop(); Refresh();
    }
    private void Parent() { if (Directory.GetParent(directory)?.FullName is string parent) { Navigate(parent); } }

    private void Refresh()
    {
        scan = new("", "", [], [], []);
        scanner.Request(directory, query, favouritesOnly, recentFirst);
        ReplaceLibraryWidgets();
        Rebuild();
    }

    private void Rebuild()
    {
        // A completed background scan/save must not discard an unrelated in-progress draft.
        // Thumbnail rows update independently; rebuilding the forms waits for validated blur.
        if (panel.EditingText) { rebuildPending = true; return; }
        rebuildPending = false;
        try
        {
            bool current = scan.Directory == directory && scan.Query == query;
            IReadOnlyList<DiscoveryEntry> entries = current ? scan.Entries : [];
            if (selected is not null && selectedEntry is not null) { selectedEntry = entries.FirstOrDefault(entry => entry.Path == selected) ?? selectedEntry; }
            panel.Content.Clear();
            libraryWidgets.Clear();
            panel.Content.Add(new UiTextField("Search", () => query, value => { query = value; Refresh(); }, "Search names, descriptions, tags and filenames; Enter applies."));
            panel.Content.Add(new UiRow(new UiToggle("Favourites", () => favouritesOnly, value => { favouritesOnly = value; Refresh(); }),
                new UiToggle("Recent first", () => recentFirst, value => { recentFirst = value; Refresh(); })));
            panel.Content.Add(new UiLabel(() => result + (scanner.IsScanning ? " Scanning locally..." : "")
                + (fileOperationActive ? " File operation in progress; further file actions wait." : ""), 70, UiCanvas.Accent));
            BuildSave(); BuildSelected();
            AppendLibraryWidgets(current, entries);
        }
        catch (Exception error) when (ViewerErrors.IsExpected(error)) { Error(error.Message); }
    }

    private void ReplaceLibraryWidgets()
    {
        foreach (UiWidget widget in libraryWidgets) { panel.Content.Remove(widget); }
        libraryWidgets.Clear();
        bool current = scan.Directory == directory && scan.Query == query;
        AppendLibraryWidgets(current, current ? scan.Entries : []);
    }

    private void AppendLibraryWidgets(bool current, IReadOnlyList<DiscoveryEntry> entries)
    {
            foreach (string folder in current ? scan.Folders : [])
            {
                string path = folder;
                Add(new UiButton(() => "Folder / " + Path.GetFileName(path), () => Navigate(path), enabled: () => !fileOperationActive));
            }
            foreach (DiscoveryEntry entry in entries)
            {
                DiscoveryEntry item = entry;
                Add(new DiscoveryItemWidget(item, () => { if (!fileOperationActive) { Select(item); } }, () => selected == item.Path));
            }
            foreach (string file in current ? scan.Archives : [])
            {
                string path = file;
                Add(new UiButton(() => "Compressed share / " + Path.GetFileName(path), () =>
                {
                    if (fileOperationActive) { return; }
                    selected = path; selectedEntry = null; selectedPreset = null;
                    result = "Selected compressed share. Import validates it into a new file; loading is separate.";
                    Rebuild(); panel.ScrollToTop();
                }, "Portable compressed artifact; never extracted to an archive-provided path."));
            }
            Add(new UiLabel(() => $"Up to {DiscoveryLibrary.MaximumEntries} matching items shown; refine search for a larger folder. Previews sample at most 256 particles.", 50));
        void Add(UiWidget widget) { libraryWidgets.Add(widget); panel.Content.Add(widget); }
    }

    private void BuildSave()
    {
        UiSection save = new("Save / share a discovery");
        save.Children.Add(new UiTextField("New file name", () => name, value => { ValidateName(value); name = value; }));
        save.Children.Add(new UiTextField("Description", () => description, value => description = value));
        save.Children.Add(new UiTextField("Tags (comma separated)", () => tags, value => tags = value));
        save.Children.Add(new UiRow(SaveButton("Save preset", "preset", DiscoveryKind.Preset), SaveButton("Save pattern", "experiment", DiscoveryKind.Pattern)));
        save.Children.Add(new UiRow(SaveButton("Save initial", "initial", DiscoveryKind.Initial), SaveButton("Save session", "session", DiscoveryKind.Session)));
        save.Children.Add(new UiButton(() => "Copy current preset", () =>
        {
            string title = name;
            BeginOwnerFile("Copy current preset", owner => owner.CapturePreset(title), preset =>
                StartWorker(() => DiscoveryShare.ClipboardText(preset), text =>
                {
                    NativeClipboard.Write(text); FinishFile("Copied validated preset JSON. Current particles are not copied.", refresh: false);
                }), requiresCapture: false);
        }, "Validated small preset text; large scientific states use compressed file sharing.", () => !fileOperationActive));
        save.Children.Add(new UiButton(() => "Paste preset for review", () =>
        {
            string text = NativeClipboard.Read();
            BeginFile("Validate clipboard preset", () => DiscoveryShare.ParseClipboard(text), preset =>
            {
                selectedPreset = preset; selected = null; selectedEntry = null; PrepareMapping();
                FinishFile("Clipboard preset validated. Choose components and alignment before applying.", refresh: false); panel.ScrollToTop();
            });
        }, "Validate strict preset JSON first; pasting never applies rules or creates particles.", () => !fileOperationActive));
        save.Children.Add(new UiLabel(() => "Preset = recipe; Pattern = completed state; Initial = reset baseline. After GPU execution, Save session includes an actual TERMINAL capture plus actions.", 70));
        panel.Content.Add(save);
    }

    private void BuildSelected()
    {
        UiSection detail = new(selectedPreset is not null && selected is null ? "Clipboard preset / explicit components" : "Selected item / load and details", selected is not null || selectedPreset is not null);
        detail.Children.Add(new UiLabel(() => selected is null ? selectedPreset?.Name ?? "Select a discovery below." : Path.GetFileName(selected), 28));
        detail.Children.Add(new UiRow(LoadButton("Load state", DiscoveryLoadAction.LoadState, kind => kind is DiscoveryKind.Pattern or DiscoveryKind.Initial),
            LoadButton("Replay actions", DiscoveryLoadAction.ReplayActions, kind => kind == DiscoveryKind.Session, replay: true)));
        detail.Children.Add(new UiRow(LoadButton("Preset live", DiscoveryLoadAction.PresetLive, kind => kind == DiscoveryKind.Preset, live: true),
            LoadButton("Preset new", DiscoveryLoadAction.PresetNew, kind => kind == DiscoveryKind.Preset)));
        if (selectedEntry?.Metadata.Kind == DiscoveryKind.CapturedSession)
        {
            detail.Children.Add(new UiLabel(() => "Stored TERMINAL capture plus actions. Restore exact values on CPU, or explicitly re-simulate CPU actions with a potentially different trajectory.", 65, UiCanvas.Accent));
            detail.Children.Add(new UiRow(LoadButton("Restore capture", DiscoveryLoadAction.RestoreCapturedState, kind => kind == DiscoveryKind.CapturedSession),
                LoadButton("Re-simulate CPU", DiscoveryLoadAction.ResimulateCapturedOnCpu, kind => kind == DiscoveryKind.CapturedSession, replay: true)));
        }
        if (selectedEntry is DiscoveryEntry entry)
        {
            string displayName = entry.Metadata.Name, displayDescription = entry.Metadata.Description, displayTags = string.Join(", ", entry.Metadata.Tags);
            detail.Children.Add(new UiTextField("Display name", () => displayName, value => displayName = value));
            detail.Children.Add(new UiTextField("Description", () => displayDescription, value => displayDescription = value));
            detail.Children.Add(new UiTextField("Tags", () => displayTags, value => displayTags = value));
            detail.Children.Add(new UiRow(new UiButton(() => "Update details / rename", () =>
            {
                DiscoveryMetadata metadata = entry.Metadata with { Name = displayName, Description = displayDescription, Tags = ParseTags(displayTags) };
                BeginFile("Update library details", () => { DiscoveryLibrary.Write(entry.Path, metadata); return DiscoveryLibrary.Inspect(entry.Path); }, updated =>
                {
                    selectedEntry = updated; FinishFile("Library details updated. Scientific filename and contents are unchanged.");
                }, finishDuringShutdown: true);
            }, "Atomic local metadata update. Display rename keeps file paths and bound companions intact.", () => !fileOperationActive && selected == entry.Path),
                new UiButton(() => entry.Metadata.Favourite ? "Unfavourite" : "Favourite", () =>
                {
                    DiscoveryMetadata metadata = entry.Metadata with { Favourite = !entry.Metadata.Favourite };
                    BeginFile("Update favourite", () => { DiscoveryLibrary.Write(entry.Path, metadata); return DiscoveryLibrary.Inspect(entry.Path); }, updated =>
                    {
                        selectedEntry = updated; FinishFile("Favourite updated.");
                    }, finishDuringShutdown: true);
                }, enabled: () => !fileOperationActive && selected == entry.Path)));
        }
        detail.Children.Add(new UiRow(new UiButton(() => "Export .plshare", ExportShare, "Compress exact scientific/companion bytes into a protected local artifact; never publish online.", () => !fileOperationActive && selectedEntry is not null),
            new UiButton(() => "Import share", ImportShare, "Validate fixed archive entries, strict document, bound companion and limits before publishing a new local file.", () => !fileOperationActive && selected?.EndsWith(".plshare", StringComparison.OrdinalIgnoreCase) == true)));
        if (selectedPreset is not null)
        {
            detail.Children.Add(new UiLabel(() => $"Selective preset: {selectedPreset.Configuration.Matrix.SpeciesCount} source -> {editor.Configuration.Matrix.SpeciesCount} current target species.", 42));
            foreach ((string label, PresetComponents flag) in new[] { ("Coefficients", PresetComponents.Coefficients), ("Core / cutoff radii", PresetComponents.Radii), ("Palette only", PresetComponents.Palette), ("Future generation", PresetComponents.Generation) })
            {
                PresetComponents component = flag;
                detail.Children.Add(new UiToggle(label, () => components.HasFlag(component), value => components = value ? components | component : components & ~component));
            }
            detail.Children.Add(new UiTextField("Source index per target S0,S1,...", () => mapping, value => mapping = value,
                "Explicit target-to-preset alignment. Comma-separated source indices; -1 retains that target. A pair changes only when both target species are mapped."));
            detail.Children.Add(new UiButton(() => "Apply selected components", () =>
            {
                PresetDefinition preset = selectedPreset!; PresetComponents requested = components;
                int[] alignment = ParseMapping(mapping);
                if (!BeginOperation("Apply selected preset components")) { return; }
                QueueOwner("Apply selected preset components", owner => owner.ApplySelectivePreset(preset, requested, alignment),
                    () => FinishFile("Selected components applied; particles, clock, execution and unrelated settings retained.", refresh: false), requiresCapture: false);
                suppressPlayback();
            }, "Validate before one undoable application. No particles are generated. Radii retain unmapped pair values.", () => !fileOperationActive && !rebuildPending && selectedPreset is not null));
        }
        panel.Content.Add(detail);
    }

    private void Select(DiscoveryEntry entry)
    {
        selected = entry.Path; selectedEntry = entry; selectedPreset = null;
        if (entry.Metadata.Kind == DiscoveryKind.Preset)
        {
            BeginFile("Review preset", () =>
            {
                if (new FileInfo(entry.Path).Length > 4 * 1024 * 1024) { throw new ArgumentException("Selective review is limited to 4 MiB; larger files can still use explicit full preset loading."); }
                return ExperimentEditor.PrepareDiscoveryLoad(entry.Path, DiscoveryLoadAction.ReviewPreset);
            }, prepared =>
            {
                selectedPreset = prepared.Preset; PrepareMapping(); FinishFile("Preset validated for explicit component review.", refresh: false); panel.ScrollToTop();
            });
        }
        Rebuild(); panel.ScrollToTop();
    }
    private void PrepareMapping() => mapping = string.Join(',', Enumerable.Range(0, editor.Configuration.Matrix.SpeciesCount)
        .Select(index => index < selectedPreset!.Configuration.Matrix.SpeciesCount ? index : -1));

    private UiButton SaveButton(string caption, string suffix, DiscoveryKind kind) => new(() => caption, () =>
    {
        ValidateName(name); string path = Path.Combine(directory, $"{name}.{suffix}.json"), title = name, about = description;
        string[] labels = ParseTags(tags);
        BeginOwnerFile(caption, owner => owner.PrepareDiscoverySave(kind, title, about, labels), prepared =>
            StartWorker(() => prepared.Write(path), outcome =>
            {
                selected = outcome.Path; selectedEntry = outcome.Entry; selectedPreset = null;
                FinishFile($"Saved {Path.GetFileName(outcome.Path)}; active state and clock unchanged." + Warning(outcome.MetadataWarning));
            }, finishDuringShutdown: true), requiresCapture: PreparedDiscoverySave.RequiresCurrentCapture(kind));
        suppressPlayback(); panel.ClearFocus();
    }, "Capture completed state once, then serialize/write off the UI thread. Existing scientific or companion files are protected.", () => !fileOperationActive);

    private UiButton LoadButton(string label, DiscoveryLoadAction action, Func<DiscoveryKind, bool> accepts, bool replay = false, bool live = false) => new(() => label, () =>
    {
        string path = selected!;
        BeginFile(label, () => ExperimentEditor.PrepareDiscoveryLoad(path, action), prepared =>
        {
            long startingBaseline = 0;
            QueueOwner(label, owner => { startingBaseline = owner.BaselineVersion; prepared.Apply(owner); }, () =>
            {
                if (!live && !replay) { baselineChanged(); }
                string message = prepared.Notice ?? (replay ? "Replay started from recorded initial state; Esc cancels. Thumbnail shows INITIAL geometry."
                    : live ? "Preset applied live; particles and clock retained." : "Loaded paused. Close the library to explore.");
                if (replay)
                {
                    ReplayNotice notice = new(startingBaseline, message);
                    replayNotice = notice;
                    RefreshReplayStatus();
                    if (!notice.Finished) { PresentReplayNotice(notice); }
                    StartWorker(() => updateRecent(path), warning =>
                    {
                        notice.MetadataWarning = warning;
                        RefreshReplayStatus();
                        FinishFile(ReplayText(notice));
                    }, finishDuringShutdown: true);
                }
                else { StartWorker(() => updateRecent(path), warning => FinishFile(message + Warning(warning)), finishDuringShutdown: true); }
            }, requiresCapture: PreparedDiscoveryLoad.RequiresCurrentCapture(action));
        });
        suppressPlayback();
    }, label, () => !fileOperationActive && selectedEntry is not null && accepts(selectedEntry.Metadata.Kind));

    private void ExportShare()
    {
        ValidateName(name); string source = selected!, output = Path.Combine(directory, name + ".plshare");
        BeginFile("Export compressed share", () => { DiscoveryShare.Export(source, output); return output; }, path =>
            FinishFile($"Created {Path.GetFileName(path)}. Share this file explicitly; nothing was uploaded."), finishDuringShutdown: true);
        suppressPlayback();
    }
    private void ImportShare()
    {
        ValidateName(name); string source = selected!, output = Path.Combine(directory, name + ".imported.json");
        BeginFile("Import compressed share", () => DiscoveryShare.ImportDetailed(source, output), outcome =>
        {
            selected = outcome.Path; selectedEntry = outcome.Entry; selectedPreset = null;
            FinishFile($"Imported {Path.GetFileName(outcome.Path)}. Choose its explicit Load / Replay / Preset action." + Warning(outcome.MetadataWarning));
        }, finishDuringShutdown: true);
        suppressPlayback();
    }
    private static string[] ParseTags(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static int[] ParseMapping(string text) => text.Split(',').Select(value =>
        int.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int index)
            ? index : throw new ArgumentException("Species alignment needs comma-separated whole source indices; use -1 to retain.")).ToArray();
    private bool BeginOperation(string label)
    {
        if (fileOperationActive) { return false; }
        fileOperationActive = true; result = label + ": preparing..."; Rebuild(); return true;
    }
    private void BeginFile<T>(string label, Func<T> work, Action<T> completed, bool finishDuringShutdown = false)
    {
        if (BeginOperation(label)) { StartWorker(work, completed, finishDuringShutdown); }
    }
    private void BeginOwnerFile<T>(string label, Func<ExperimentEditor, T> capture, Action<T> completed, bool requiresCapture) where T : class
    {
        if (!BeginOperation(label)) { return; }
        T? prepared = null;
        QueueOwner(label, owner => prepared = capture(owner), () => completed(prepared!), requiresCapture);
    }
    private void StartWorker<T>(Func<T> work, Action<T> completed, bool finishDuringShutdown = false)
    {
        finishWorkerDuringShutdown = finishDuringShutdown;
        fileTask = Task.Run(() =>
        {
            T prepared = work();
            return (Action)(() => completed(prepared));
        });
    }
    private void QueueOwner(string label, Action<ExperimentEditor> action, Action completed, bool requiresCapture)
    {
        try
        {
            fileOwnerTask = editor.Run(label, owner => { if (!disposed) { action(owner); } }, () =>
            {
                fileOwnerTask = null;
                if (disposed) { return; }
                try { completed(); } catch (Exception error) when (ViewerErrors.IsExpected(error)) { FileFailed(error); }
            }, requiresCapture: requiresCapture);
        }
        catch { fileOperationActive = false; throw; }
    }
    private static string? TouchRecent(string path)
    {
        try
        {
            DiscoveryEntry? entry = DiscoveryLibrary.Inspect(path);
            return entry is null ? "Recent metadata unavailable." : DiscoveryLibrary.RecordSaved(path,
                entry.Metadata with { LastOpenedAtUtc = DateTimeOffset.UtcNow }).MetadataWarning;
        }
        catch (Exception error) when (ViewerErrors.IsExpected(error)) { return "Recent metadata not updated: " + error.Message; }
    }
    private static string Warning(string? text) => text is null ? "" : " Warning: " + text;
    private void FinishFile(string message, bool refresh = true)
    {
        fileOperationActive = false; result = message; report(message);
        if (!disposed && IsOpen) { if (refresh) { Refresh(); } else { Rebuild(); } }
    }
    private void FileFailed(Exception error)
    {
        fileOperationActive = false; Error(error.Message); Rebuild();
    }
    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        { throw new ArgumentException("Use a file name without folder separators or reserved characters."); }
    }
    private void Error(string text) { result = "Error: " + text; report(result); }
    /// <summary>Cancels scanning and finishes already dispatched artifact writes before native shutdown.</summary>
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true; scanner.Dispose(); editor.Failed -= Error;
        if (fileTask is Task<Action> work)
        {
            fileTask = null;
            try
            {
                Action completed = work.GetAwaiter().GetResult();
                if (finishWorkerDuringShutdown) { completed(); }
                else { report("Prepared file operation cancelled during shutdown; no pending load was applied."); }
            }
            catch (Exception error) when (ViewerErrors.IsExpected(error)) { report("File operation failed during shutdown: " + error.Message); }
        }
        else if (fileOperationActive) { report("Queued file continuation cancelled during shutdown; completed owner actions remain applied."); }
        fileOperationActive = false;
    }
    internal void DrawChrome(UiCanvas canvas) => panel.DrawChrome(canvas, "Discovery library");
    internal void DrawBody(UiCanvas canvas) => panel.DrawBody(canvas);

    /// <summary>Keeps replay outcome and optional metadata feedback together until both have completed.</summary>
    private sealed class ReplayNotice(long startingBaseline, string message)
    {
        internal long StartingBaseline { get; } = startingBaseline;
        internal string Message { get; set; } = message;
        internal string? MetadataWarning { get; set; }
        internal bool CancelRequested { get; set; }
        internal bool Finished { get; set; }
    }
}
