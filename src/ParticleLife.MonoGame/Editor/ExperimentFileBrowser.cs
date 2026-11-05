using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Explicit local file workflows; typed names use exclusive creation and failures retain active state.</summary>
internal sealed class ExperimentFileBrowser
{
    private readonly ExperimentEditor editor;
    private readonly Action<string> report;
    private readonly Action suppressPlayback;
    private readonly Action baselineChanged;
    private readonly UiPanel panel;
    private string directory;
    private string name = "experiment";
    private string? selected;
    private string result = "Choose a file below, then choose how to load it. Saving never changes the running state.";
    internal bool IsOpen { get; private set; }
    internal Rectangle Bounds => panel.Bounds;
    internal Rectangle Body => panel.Body;
    internal void Arrange(Rectangle bounds) => panel.Arrange(bounds);
    internal void SetStatus(string text) => result = text;

    internal ExperimentFileBrowser(ExperimentEditor editor, string directory, Action<string> report, Action suppressPlayback, Action baselineChanged)
    {
        this.editor = editor;
        this.directory = directory;
        this.report = report;
        this.suppressPlayback = suppressPlayback;
        this.baselineChanged = baselineChanged;
        panel = new UiPanel(Error, dismissOnEscape: true);
        panel.Toolbar.Add(new UiRow(
            new UiButton(() => "Close", Close, "Return to the experiment without loading or saving."),
            new UiButton(() => "Parent", Parent, "Browse the parent folder."),
            new UiButton(() => "Refresh", Refresh, "Refresh this folder's JSON file list.")));
        panel.Toolbar.Add(new UiTextField("Folder", () => this.directory, Navigate,
            "An absolute or relative local folder path. Enter opens it. The file list shows folders and JSON files."));
    }

    internal void Open(bool pointerHeld = false)
    {
        panel.PrepareForOpen(pointerHeld);
        IsOpen = true;
        name = $"experiment-{DateTime.Now:yyyyMMdd-HHmmss}";
        Refresh();
    }

    internal void Close()
    {
        panel.ClearFocus();
        IsOpen = false;
    }

    internal void Update(Rectangle bounds, float scale, MouseState mouse, IReadOnlyList<UiInputEvent> input)
    {
        panel.Bounds = bounds;
        panel.Update(mouse, scale, input);
        if (panel.DismissRequested)
        {
            Close();
        }
    }

    private void Navigate(string path)
    {
        string candidate = Path.GetFullPath(path);
        if (!Directory.Exists(candidate))
        {
            throw new DirectoryNotFoundException("Folder does not exist.");
        }
        directory = candidate;
        selected = null;
        result = "Choose a file below, then choose how to load it. Saving never changes the running state.";
        panel.ScrollToTop();
        Refresh();
    }

    private void Parent()
    {
        string? parent = Directory.GetParent(directory)?.FullName;
        if (parent is not null)
        {
            Navigate(parent);
        }
    }

    private void Refresh()
    {
        try
        {
            Directory.CreateDirectory(directory);
            string[] folders = Directory.GetDirectories(directory).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] files = Directory.GetFiles(directory, "*.json")
                .Where(path => !path.EndsWith(".viewer.json", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            panel.Content.Clear();
            panel.Content.Add(new UiTextField("Save name", () => name, value =>
            {
                ValidateName(value);
                name = value;
            }, "Choose a useful name. Files are exclusively created; an existing name fails safely. Append a new name to save again."));
            panel.Content.Add(new UiRow(
                SaveButton("Save preset", "preset", editor.SavePreset),
                SaveButton("Save from here", "experiment", editor.SaveCurrent)));
            panel.Content.Add(new UiRow(
                SaveButton("Save initial", "initial", editor.SaveInitial),
                SaveButton("Save session", "session", editor.SaveSession)));
            panel.Content.Add(new UiLabel(() => result, 42, UiCanvas.Accent));
            panel.Content.Add(new UiLabel(() => selected is null ? "Select a JSON file below" : $"Selected: {Path.GetFileName(selected)}", 24));
            panel.Content.Add(new UiRow(
                LoadButton("Load experiment", path => editor.LoadExperiment(path), "Exact saved initial values become a new baseline at time/step zero, paused."),
                LoadButton("Replay session", path => editor.BeginSessionReplay(path), "Run recorded step-boundary changes through the shared replay engine to its saved terminal boundary, then pause.", startsReplay: true)));
            panel.Content.Add(new UiRow(
                LoadButton("Preset live", path => editor.ApplyPreset(path, false), "Apply compatible rules and recipe while retaining particles and the clock. Generation is a separate action.", live: true),
                LoadButton("Preset new", path => editor.ApplyPreset(path, true), "Generate a new population explicitly from the preset; establish a new baseline at time/step zero.")));
            panel.Content.Add(new UiLabel(() => "Preset: recipe. From here: exact state. Session: initial state and changes.", 36));
            foreach (string folder in folders)
            {
                string path = folder;
                panel.Content.Add(new UiButton(() => "Folder / " + Path.GetFileName(path), () => Navigate(path), "Open this folder."));
            }
            foreach (string file in files)
            {
                string path = file;
                panel.Content.Add(new UiButton(() => (selected == path ? "> " : "  ") + Path.GetFileName(path), () => selected = path,
                    path));
            }
            if (folders.Length + files.Length == 0)
            {
                panel.Content.Add(new UiLabel(() => "This folder has no JSON files yet.", 24));
            }
        }
        catch (Exception exception) when (ViewerErrors.IsExpected(exception))
        {
            Error(exception.Message);
            panel.Content.Clear();
            panel.Content.Add(new UiLabel(() => result, 42, UiCanvas.Error));
            panel.Content.Add(new UiLabel(() => "Choose an accessible folder to continue.", 36));
        }
    }

    private UiButton SaveButton(string caption, string suffix, Action<string, string> save) => new(() => caption, () =>
    {
        ValidateName(name);
        string path = Path.Combine(directory, $"{name}.{suffix}.json");
        save(path, name);
        selected = path;
        result = $"Saved {Path.GetFileName(path)}. The experiment and clock are unchanged.";
        report($"Saved {caption[5..].ToLowerInvariant()}: {path}");
        suppressPlayback();
        panel.ClearFocus();
        Refresh();
    }, caption switch
    {
        "Save preset" => "Reusable current rules, matrix, generation recipe, execution and appearance. It does not reproduce an evolved pattern.",
        "Save from here" => "Exact completed positions, velocities, identities and rules; rebase to time/step zero with source boundary metadata.",
        "Save initial" => "Export the original reset baseline, not the current evolved state or the current live rules.",
        _ => "Exact starting experiment plus every applied trajectory change and terminal boundary. Headless replay uses simulation steps."
    });

    private UiButton LoadButton(string label, Action<string> load, string help, bool startsReplay = false, bool live = false) => new(() => label, () =>
    {
        if (selected is null)
        {
            return;
        }
        load(selected);
        suppressPlayback();
        if (!startsReplay && !live)
        {
            baselineChanged();
        }
        result = $"{label}: {Path.GetFileName(selected)}. " + (startsReplay
            ? "Replay started; Esc cancels."
            : live ? "Applied live; particle state, clock and playback status retained."
            : "Loaded paused; close this window to inspect.");
        report(result);
    }, help, () => selected is not null);

    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Use a file name without folder separators or reserved characters.");
        }
    }

    private void Error(string text)
    {
        result = "Error: " + text;
        report(result);
    }

    internal void DrawChrome(UiCanvas canvas) => panel.DrawChrome(canvas, "Experiment library");
    internal void DrawBody(UiCanvas canvas) => panel.DrawBody(canvas);
}
