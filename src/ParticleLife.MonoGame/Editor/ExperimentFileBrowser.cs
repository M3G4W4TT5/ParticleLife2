using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Searchable local discovery library and explicit validated file/clipboard sharing.</summary>
internal sealed class ExperimentFileBrowser
{
    private readonly EditorSession editor;
    private readonly Action<string> report;
    private readonly Action suppressPlayback, baselineChanged;
    private readonly UiPanel panel;
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

    internal ExperimentFileBrowser(EditorSession editor, string directory, Action<string> report, Action suppressPlayback, Action baselineChanged)
    {
        this.editor = editor; this.directory = Path.GetFullPath(directory); this.report = report;
        this.suppressPlayback = suppressPlayback; this.baselineChanged = baselineChanged;
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
    internal void Close() { panel.ClearFocus(); IsOpen = false; }
    internal void Update(Rectangle bounds, float scale, MouseState mouse, IReadOnlyList<UiInputEvent> input)
    {
        panel.Bounds = bounds; panel.Update(mouse, scale, input);
        if (panel.DismissRequested) { Close(); }
    }
    private void Navigate(string path)
    {
        string next = Path.GetFullPath(path);
        if (!Directory.Exists(next)) { throw new DirectoryNotFoundException("Folder does not exist."); }
        directory = next; selected = null; selectedEntry = null; selectedPreset = null;
        panel.ScrollToTop(); Refresh();
    }
    private void Parent() { if (Directory.GetParent(directory)?.FullName is string parent) { Navigate(parent); } }

    private void Refresh()
    {
        try
        {
            IReadOnlyList<DiscoveryEntry> entries = DiscoveryLibrary.Read(directory, query, favouritesOnly, recentFirst);
            if (selected is not null && selectedEntry is not null) { selectedEntry = entries.FirstOrDefault(entry => entry.Path == selected) ?? DiscoveryLibrary.Inspect(selected); }
            panel.Content.Clear();
            panel.Content.Add(new UiTextField("Search", () => query, value => { query = value; Refresh(); }, "Search names, descriptions, tags and filenames; Enter applies."));
            panel.Content.Add(new UiRow(new UiToggle("Favourites", () => favouritesOnly, value => { favouritesOnly = value; Refresh(); }),
                new UiToggle("Recent first", () => recentFirst, value => { recentFirst = value; Refresh(); })));
            panel.Content.Add(new UiLabel(() => result, 52, UiCanvas.Accent));
            BuildSave(); BuildSelected();
            foreach (string folder in Directory.EnumerateDirectories(directory).Order(StringComparer.OrdinalIgnoreCase).Take(50))
            {
                string path = folder;
                panel.Content.Add(new UiButton(() => "Folder / " + Path.GetFileName(path), () => Navigate(path)));
            }
            foreach (DiscoveryEntry entry in entries)
            {
                DiscoveryEntry item = entry;
                panel.Content.Add(new DiscoveryItemWidget(item, () => Select(item), () => selected == item.Path));
            }
            foreach (string file in Directory.EnumerateFiles(directory, "*.plshare").Order(StringComparer.OrdinalIgnoreCase).Take(50))
            {
                string path = file;
                panel.Content.Add(new UiButton(() => "Compressed share / " + Path.GetFileName(path), () =>
                {
                    selected = path; selectedEntry = null; selectedPreset = null;
                    result = "Selected compressed share. Import validates it into a new file; loading is separate.";
                    Refresh(); panel.ScrollToTop();
                }, "Portable compressed artifact; never extracted to an archive-provided path."));
            }
            panel.Content.Add(new UiLabel(() => $"Up to {DiscoveryLibrary.MaximumEntries} matching items shown; refine search for a larger folder. Previews sample at most 256 particles.", 50));
        }
        catch (Exception error) when (ViewerErrors.IsExpected(error)) { Error(error.Message); }
    }

    private void BuildSave()
    {
        UiSection save = new("Save / share a discovery");
        save.Children.Add(new UiTextField("New file name", () => name, value => { ValidateName(value); name = value; }));
        save.Children.Add(new UiTextField("Description", () => description, value => description = value));
        save.Children.Add(new UiTextField("Tags (comma separated)", () => tags, value => tags = value));
        save.Children.Add(new UiRow(SaveButton("Save preset", "preset", DiscoveryKind.Preset, (owner, path, title) => owner.SavePreset(path, title)),
            SaveButton("Save pattern", "experiment", DiscoveryKind.Pattern, (owner, path, title) => owner.SaveCurrent(path, title))));
        save.Children.Add(new UiRow(SaveButton("Save initial", "initial", DiscoveryKind.Initial, (owner, path, title) => owner.SaveInitial(path, title)),
            SaveButton("Save session", "session", DiscoveryKind.Session, (owner, path, title) => owner.SaveSession(path, title))));
        save.Children.Add(new UiButton(() => "Copy current preset", () =>
        {
            string? text = null;
            Run("Copy current preset", owner => text = DiscoveryShare.ClipboardText(owner.CapturePreset(name)),
                () => { NativeClipboard.Write(text!); result = "Copied validated preset JSON. Current particles are not copied."; });
        }, "Validated small preset text; large scientific states use compressed file sharing."));
        save.Children.Add(new UiButton(() => "Paste preset for review", () =>
        {
            selectedPreset = DiscoveryShare.ParseClipboard(NativeClipboard.Read()); selected = null; selectedEntry = null;
            PrepareMapping(); result = "Clipboard preset validated. Choose components and alignment before applying.";
            Refresh(); panel.ScrollToTop();
        }, "Validate strict preset JSON first; pasting never applies rules or creates particles."));
        save.Children.Add(new UiLabel(() => "Preset = recipe; Pattern = exact completed state; Initial = reset baseline; Session = recorded actions. Session thumbnails show INITIAL geometry.", 70));
        panel.Content.Add(save);
    }

    private void BuildSelected()
    {
        UiSection detail = new(selectedPreset is not null && selected is null ? "Clipboard preset / explicit components" : "Selected item / load and details", selected is not null || selectedPreset is not null);
        detail.Children.Add(new UiLabel(() => selected is null ? selectedPreset?.Name ?? "Select a discovery below." : Path.GetFileName(selected), 28));
        detail.Children.Add(new UiRow(LoadButton("Load state", (owner, path) => owner.LoadExperiment(path), kind => kind is DiscoveryKind.Pattern or DiscoveryKind.Initial),
            LoadButton("Replay actions", (owner, path) => owner.BeginSessionReplay(path), kind => kind == DiscoveryKind.Session, replay: true)));
        detail.Children.Add(new UiRow(LoadButton("Preset live", (owner, path) => owner.ApplyPreset(path, false), kind => kind == DiscoveryKind.Preset, live: true),
            LoadButton("Preset new", (owner, path) => owner.ApplyPreset(path, true), kind => kind == DiscoveryKind.Preset)));
        if (selectedEntry is DiscoveryEntry entry)
        {
            string displayName = entry.Metadata.Name, displayDescription = entry.Metadata.Description, displayTags = string.Join(", ", entry.Metadata.Tags);
            detail.Children.Add(new UiTextField("Display name", () => displayName, value => displayName = value));
            detail.Children.Add(new UiTextField("Description", () => displayDescription, value => displayDescription = value));
            detail.Children.Add(new UiTextField("Tags", () => displayTags, value => displayTags = value));
            detail.Children.Add(new UiRow(new UiButton(() => "Update details / rename", () =>
            {
                DiscoveryLibrary.Write(entry.Path, entry.Metadata with { Name = displayName, Description = displayDescription, Tags = ParseTags(displayTags) });
                result = "Library details updated. Scientific filename and contents are unchanged."; Refresh();
            }, "Atomic local metadata update. Display rename keeps file paths and bound companions intact."),
                new UiButton(() => entry.Metadata.Favourite ? "Unfavourite" : "Favourite", () =>
                {
                    DiscoveryLibrary.Write(entry.Path, entry.Metadata with { Favourite = !entry.Metadata.Favourite }); Refresh();
                })));
        }
        detail.Children.Add(new UiRow(new UiButton(() => "Export .plshare", ExportShare, "Compress exact scientific/companion bytes into a protected local artifact; never publish online.", () => selectedEntry is not null),
            new UiButton(() => "Import share", ImportShare, "Validate fixed archive entries, strict document, bound companion and limits before publishing a new local file.", () => selected?.EndsWith(".plshare", StringComparison.OrdinalIgnoreCase) == true)));
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
                Run("Apply selected preset components", owner => owner.ApplySelectivePreset(preset, requested, alignment),
                    () => { result = "Selected components applied; particles, clock, execution and unrelated settings retained."; report(result); });
                suppressPlayback();
            }, "Validate before one undoable application. No particles are generated. Radii retain unmapped pair values."));
        }
        panel.Content.Add(detail);
    }

    private void Select(DiscoveryEntry entry)
    {
        selected = entry.Path; selectedEntry = entry; selectedPreset = null;
        if (entry.Metadata.Kind == DiscoveryKind.Preset)
        {
            if (new FileInfo(entry.Path).Length > 4 * 1024 * 1024) { throw new ArgumentException("Selective review is limited to 4 MiB; larger files can still use explicit full preset loading."); }
            selectedPreset = PresetFile.Load(entry.Path); PrepareMapping();
        }
        Refresh(); panel.ScrollToTop();
    }
    private void PrepareMapping() => mapping = string.Join(',', Enumerable.Range(0, editor.Configuration.Matrix.SpeciesCount)
        .Select(index => index < selectedPreset!.Configuration.Matrix.SpeciesCount ? index : -1));

    private UiButton SaveButton(string caption, string suffix, DiscoveryKind kind, Action<ExperimentEditor, string, string> save) => new(() => caption, () =>
    {
        ValidateName(name); string path = Path.Combine(directory, $"{name}.{suffix}.json"), title = name, about = description;
        string[] labels = ParseTags(tags);
        Run(caption, owner =>
        {
            DiscoveryMetadata metadata = DiscoveryLibrary.Capture(owner, kind, title, about, labels);
            save(owner, path, title); DiscoveryLibrary.Write(path, metadata);
        }, () => { selected = path; selectedEntry = DiscoveryLibrary.Inspect(path); selectedPreset = null; result = $"Saved {Path.GetFileName(path)}; active state and clock unchanged."; report(result); Refresh(); });
        suppressPlayback(); panel.ClearFocus();
    }, "Exclusive save with a correctly sourced bounded thumbnail. Existing scientific or companion files are protected.");

    private UiButton LoadButton(string label, Action<ExperimentEditor, string> load, Func<DiscoveryKind, bool> accepts, bool replay = false, bool live = false) => new(() => label, () =>
    {
        string path = selected!;
        Run(label, owner => load(owner, path), () =>
        {
            if (!live && !replay) { baselineChanged(); }
            DiscoveryEntry? item = DiscoveryLibrary.Inspect(path);
            if (item is not null) { DiscoveryLibrary.Write(path, item.Metadata with { LastOpenedAtUtc = DateTimeOffset.UtcNow }); }
            result = replay ? "Replay started from recorded initial state; Esc cancels. Thumbnail shows INITIAL geometry."
                : live ? "Preset applied live; particles and clock retained." : "Loaded paused. Close the library to explore.";
            report(result); Refresh();
        });
        suppressPlayback();
    }, label, () => selectedEntry is not null && accepts(selectedEntry.Metadata.Kind));

    private void ExportShare()
    {
        ValidateName(name); string source = selected!, output = Path.Combine(directory, name + ".plshare");
        Run("Export compressed share", _ => DiscoveryShare.Export(source, output),
            () => { result = $"Created {Path.GetFileName(output)}. Share this file explicitly; nothing was uploaded."; Refresh(); });
        suppressPlayback();
    }
    private void ImportShare()
    {
        ValidateName(name); string source = selected!, output = Path.Combine(directory, name + ".imported.json");
        Run("Import compressed share", _ => DiscoveryShare.Import(source, output), () =>
        {
            selected = output; selectedEntry = DiscoveryLibrary.Inspect(output); selectedPreset = null;
            result = "Imported a validated local artifact. Choose its explicit Load / Replay / Preset action."; Refresh();
        });
        suppressPlayback();
    }
    private static string[] ParseTags(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static int[] ParseMapping(string text) => text.Split(',').Select(value =>
        int.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int index)
            ? index : throw new ArgumentException("Species alignment needs comma-separated whole source indices; use -1 to retain.")).ToArray();
    private void Run(string label, Action<ExperimentEditor> action, Action completed) => editor.Run(label, action, () =>
    {
        try { completed(); } catch (Exception error) when (ViewerErrors.IsExpected(error)) { Error(error.Message); }
    });
    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        { throw new ArgumentException("Use a file name without folder separators or reserved characters."); }
    }
    private void Error(string text) { result = "Error: " + text; report(result); }
    internal void DrawChrome(UiCanvas canvas) => panel.DrawChrome(canvas, "Discovery library");
    internal void DrawBody(UiCanvas canvas) => panel.DrawBody(canvas);
}
