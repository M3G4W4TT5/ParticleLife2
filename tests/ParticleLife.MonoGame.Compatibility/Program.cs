using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

if (args.Length == 1 && args[0] == "--gpu-measurement-checks")
{
    GpuMeasurementChecks.Run();
    return;
}

if (args.Length > 0 && args[0] == "--save-worker")
{
    Environment.ExitCode = StorageChecks.RunWorker(args);
    return;
}

if (args.Length > 0 && args[0] == "--replay-completion-checks")
{
    ReplayCompletionChecks.Run();
    return;
}

// Compile and managed-assembly smoke check only: no window or native graphics device.
Vector2d position = new(0.25, 0.75);
Vector2 displayPosition = new((float)position.X, (float)position.Y);
Console.WriteLine($"Core net10.0 / MonoGame {typeof(Game).Assembly.GetName().Version}: {displayPosition}");
RenderingChecks.Run();
GuidanceChecks.Run();
GpuMeasurementChecks.Run();

// Exercise the real native numeric widget against the actual editor without creating a Game,
// SDL window, texture or graphics device. These checks complement native interaction checks.
using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 4, 2, 31));
ExperimentEditor editor = new(playback);
UiNumber number = new("Cutoff", () => editor.Configuration.Parameters.Cutoff, value =>
{
    SimulationConfiguration current = editor.Configuration;
    editor.ApplyConfiguration(new SimulationConfiguration(current.Parameters with { Cutoff = value },
        current.Matrix, current.TimeStep, current.Backend, current.WorkerCount));
}, 0.1, 0.01, 0.5, "Reduced length", beginGesture: editor.BeginGesture, endGesture: editor.EndGesture)
{
    Bounds = new Rectangle(0, 0, 400, 28)
};

number.Focus(true);
number.Key(Keys.A, true, false);
number.Text('-');
number.Key(Keys.Enter, false, false);
Require(editor.Configuration.Parameters.Cutoff == 0.1, "Incomplete numeric input reached the simulation.");
number.Key(Keys.A, true, false);
foreach (char character in "0.12345678912345678")
{
    number.Text(character);
}
number.Key(Keys.Enter, false, false);
Require(editor.Configuration.Parameters.Cutoff == double.Parse("0.12345678912345678", System.Globalization.CultureInfo.InvariantCulture),
    "Exact numeric input lost double precision.");

double beforeGesture = editor.Configuration.Parameters.Cutoff;
number.Press(new Point(number.SliderBounds.X + number.SliderBounds.Width / 4, number.SliderBounds.Center.Y));
playback.SingleStep();
number.Drag(new Point(number.SliderBounds.X + number.SliderBounds.Width * 3 / 4, number.SliderBounds.Center.Y));
number.Release();
int appliedEvents = editor.SessionEventCount;
Require(appliedEvents >= 3, "Slider samples were not represented in scientific replay.");
editor.Undo();
Require(editor.Configuration.Parameters.Cutoff == beforeGesture && playback.Observe().Step == 1,
    "One slider gesture did not undo at the current completed boundary.");
editor.Redo();
Require(editor.Configuration.Parameters.Cutoff != beforeGesture && playback.Observe().Step == 1,
    "Slider redo rewound dynamics or failed to reapply the value.");
number.Key(Keys.A, true, false);
number.Text('-');
Require(!number.Focus(false) && number.Focused, "Invalid numeric blur allowed a following Apply action.");
number.Key(Keys.Escape, false, false);
Require(number.Focus(false), "Cancelling an invalid numeric draft did not release focus.");

string savedName = "valid";
UiTextField fileName = new("Save name", () => savedName, value =>
{
    if (value.Contains('/'))
    {
        throw new ArgumentException("Invalid name.");
    }
    savedName = value;
});
fileName.Focus(true);
foreach (char character in "bad/name")
{
    fileName.Text(character);
}
Require(!fileName.Focus(false) && savedName == "valid", "Invalid save-name blur allowed a following save action.");
fileName.Key(Keys.Escape, false, false);
fileName.Key(Keys.Home, false, false);
fileName.Key(Keys.Right, false, false);
fileName.Text('X');
fileName.Key(Keys.Enter, false, false);
Require(savedName == "vXalid", "Text-field cursor insertion did not preserve surrounding characters.");

InteractionMatrixWidget matrix = new(editor) { Bounds = new Rectangle(0, 0, 460, 200) };
matrix.Select(0, 1);
matrix.Key(Keys.Down, false, false);
Require(matrix.Receiver == 1 && matrix.Source == 1, "Matrix row navigation changed its source column.");
matrix.Key(Keys.Left, false, false);
Require(matrix.Receiver == 1 && matrix.Source == 0, "Directed matrix selection lost its orientation.");
bool overflowRejected = false;
try
{
    LiveExperimentEditor.ValidateDraftPopulation([1, 1], 0, int.MaxValue);
}
catch (ArgumentOutOfRangeException)
{
    overflowRejected = true;
}
Require(overflowRejected, "A population draft overflow could reach layout and crash drawing.");
UiPanel routed = new(_ => { });
int applyCount = 0;
UiButton apply = new(() => "Apply", () => applyCount++);
routed.Content.Add(number);
routed.Content.Add(apply);
routed.Arrange(new Rectangle(10, 10, 500, 400));
routed.Update(default, 1,
[
    UiInputEvent.KeyPress(Keys.Tab, false, false),
    UiInputEvent.Text('0', false, false),
    UiInputEvent.Text('.', false, false),
    UiInputEvent.Text('2', false, false),
    UiInputEvent.Text('5', false, false),
    UiInputEvent.KeyPress(Keys.Enter, false, false)
]);
Require(editor.Configuration.Parameters.Cutoff == 0.25, "Ordered text/Enter routing committed before its earlier characters.");
routed.Update(default, 1,
[
    UiInputEvent.KeyPress(Keys.A, true, false),
    UiInputEvent.Text('-', false, false),
    UiInputEvent.KeyPress(Keys.Tab, false, false)
]);
Require(number.Focused, "Tab abandoned an invalid field.");
MouseState clickApply = new(apply.Bounds.X + 10, apply.Bounds.Y + 10, 0, ButtonState.Pressed,
    ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
routed.Update(clickApply, 1, []);
Require(applyCount == 0 && editor.Configuration.Parameters.Cutoff == 0.25, "An invalid draft allowed a clicked Apply through native input routing.");
routed.ClearFocus();
routed.Update(default, 1,
[
    UiInputEvent.KeyPress(Keys.Tab, false, false),
    UiInputEvent.KeyPress(Keys.D1, false, false),
    UiInputEvent.Text('1', false, false),
    UiInputEvent.KeyPress(Keys.Escape, false, false)
]);
Require(!routed.HasFocus && routed.ConsumedKeyboard, "A batch that gained then released widget focus could leak consumed keys into simulation shortcuts.");
string modalValue = "before";
UiPanel modal = new(_ => { }, dismissOnEscape: true);
modal.Content.Add(new UiTextField("Name", () => modalValue, value => modalValue = value));
modal.Arrange(new Rectangle(10, 10, 500, 400));
modal.Update(default, 1,
[
    UiInputEvent.KeyPress(Keys.Tab, false, false),
    UiInputEvent.Text('x', false, false),
    UiInputEvent.KeyPress(Keys.Enter, false, false),
    UiInputEvent.KeyPress(Keys.Escape, false, false)
]);
Require(modalValue == "x" && !modal.DismissRequested && !modal.HasFocus,
    "A later modal Escape discarded earlier ordered text/Enter events.");
modal.Update(default, 1, [UiInputEvent.KeyPress(Keys.Escape, false, false)]);
Require(modal.DismissRequested, "An unfocused modal did not accept ordered Escape dismissal.");
int selectableSpecies = 7;
int selectedSpecies = 0;
UiNumber selector = new("Species", () => selectedSpecies, value => selectedSpecies = (int)value,
    0, 0, 31, "Existing species index", integral: true, maximumValue: () => selectableSpecies - 1)
{
    Bounds = new Rectangle(0, 0, 400, 28)
};
selector.Press(new Point(selector.SliderBounds.Right, selector.SliderBounds.Center.Y));
selector.Release();
Require(selectedSpecies == 6 && selector.SliderFraction == 1, "Species selector range did not end at the last valid species.");
selectableSpecies = 1;
selector.Press(new Point(selector.SliderBounds.Right, selector.SliderBounds.Center.Y));
selector.Release();
Require(selectedSpecies == 0 && selector.SliderFraction == 0, "A one-species selector produced an invalid index or undefined thumb fraction.");
// Compact hit targets preserve exact-entry, reset and slider semantics in one inspector row.
Require(number.Height == 28 && !number.SliderBounds.Intersects(number.ValueBounds)
    && !number.ValueBounds.Intersects(number.ResetBounds), "Compact numeric hit targets overlap.");
number.Focus(true);
number.Press(number.ValueBounds.Center);
number.Key(Keys.A, true, false);
foreach (char character in "0.23456789123456789")
{
    number.Text(character);
}
number.Key(Keys.Enter, false, false);
Require(editor.Configuration.Parameters.Cutoff == double.Parse("0.23456789123456789", System.Globalization.CultureInfo.InvariantCulture),
    "Compact exact-entry hit target did not retain double precision.");
number.Press(number.ResetBounds.Center);
Require(editor.Configuration.Parameters.Cutoff == 0.1, "Compact reset target did not restore the explicit default.");
number.Focus(false);

// Dropdown navigation is a draft; only an explicit row click or confirmation changes selection.
int selectedOption = 2;
int selectionChanges = 0;
int toolbarActions = 0;
UiDropdown choices = new("Recipe", Enumerable.Range(0, 18).Select(index => $"Recipe {index}").ToArray(),
    () => selectedOption, index => { selectedOption = index; selectionChanges++; });
UiButton toolbarAction = new(() => "Action", () => toolbarActions++);
UiPanel choicePanel = new(_ => { });
choicePanel.Toolbar.Add(toolbarAction);
choicePanel.Content.Add(choices);
choicePanel.Arrange(new Rectangle(10, 10, 400, 420));
Click(choicePanel, choices.Bounds.Center);
Require(choices.IsOpen && selectedOption == 2 && selectionChanges == 0,
    "Opening a dropdown changed its selection.");
Require(choicePanel.Bounds.Contains(choices.PopupBounds), "Dropdown popup escaped panel clipping.");
choicePanel.Update(default, 1,
[
    UiInputEvent.KeyPress(Keys.End, false, false),
    UiInputEvent.KeyPress(Keys.Escape, false, false)
]);
Require(!choices.IsOpen && selectedOption == 2 && selectionChanges == 0 && choicePanel.ConsumedKeyboard,
    "Cancelling keyboard dropdown navigation applied a choice or leaked the key.");
choicePanel.Update(default, 1,
[
    UiInputEvent.KeyPress(Keys.Enter, false, false),
    UiInputEvent.KeyPress(Keys.End, false, false)
]);
Require(choices.IsOpen && choices.HighlightedIndex == 17 && choices.FirstVisibleIndex > 0,
    "Long dropdown keyboard navigation did not expose its final option.");
Require(choices.PopupBounds.Contains(choices.OptionBounds(17)), "Navigated option is not visible inside popup.");
choicePanel.Update(default, 1, [UiInputEvent.KeyPress(Keys.Enter, false, false)]);
Require(!choices.IsOpen && selectedOption == 17 && selectionChanges == 1,
    "Explicit dropdown confirmation did not apply exactly one selection.");
Click(choicePanel, choices.Bounds.Center);
Click(choicePanel, toolbarAction.Bounds.Center);
Require(!choices.IsOpen && toolbarActions == 0, "Dropdown dismissal clicked through to the toolbar.");
Click(choicePanel, toolbarAction.Bounds.Center);
Require(toolbarActions == 1, "Toolbar could not be used after dropdown dismissal.");
Click(choicePanel, choices.Bounds.Center);
choicePanel.Update(MouseAt(choices.PopupBounds.Center, wheel: 120), 1, []);
Require(selectedOption == 17 && selectionChanges == 1, "Scrolling options committed an unintended choice.");
int pointerChoice = choices.FirstVisibleIndex;
Click(choicePanel, choices.OptionBounds(pointerChoice).Center, wheel: 120);
Require(selectedOption == pointerChoice && selectionChanges == 2 && !choices.IsOpen,
    "Visible dropdown row did not select the displayed option exactly once.");

// Keyboard navigation remains authoritative until the pointer actually moves again.
Click(choicePanel, choices.Bounds.Center, wheel: 120);
Point stationaryPointer = choices.OptionBounds(choices.FirstVisibleIndex).Center;
choicePanel.Update(MouseAt(stationaryPointer, wheel: 120), 1, []);
choicePanel.Update(MouseAt(stationaryPointer, wheel: 120), 1, [UiInputEvent.KeyPress(Keys.End, false, false)]);
choicePanel.Update(MouseAt(stationaryPointer, wheel: 120), 1, []);
choicePanel.Update(MouseAt(stationaryPointer, wheel: 120), 1, [UiInputEvent.KeyPress(Keys.Enter, false, false)]);
Require(selectedOption == 17, "A stationary pointer overrode keyboard dropdown navigation.");

// A compact menu must never put an option underneath the host's title-bar close icon.
int compactSelection = 0;
UiDropdown compactChoice = new("Choice", Enumerable.Range(0, 18).Select(index => $"Option {index}").ToArray(),
    () => compactSelection, index => compactSelection = index);
UiPanel compactChoices = new(_ => { });
compactChoices.Toolbar.Add(new UiButton(() => "Toolbar", () => { }));
compactChoices.Content.Add(compactChoice);
compactChoices.Arrange(new Rectangle(10, 10, 350, 140));
Click(compactChoices, compactChoice.Bounds.Center);
Require(compactChoice.IsOpen && compactChoice.PopupBounds.Top >= compactChoices.Bounds.Y + 34,
    "A compact dropdown covered the title close control.");
Click(compactChoices, new Point(compactChoices.Bounds.Right - 20, compactChoices.Bounds.Y + 16));
Require(!compactChoice.IsOpen && compactSelection == 0, "A title-bar click selected a hidden menu option.");

// A dropdown cannot bypass an invalid numeric draft when focus changes.
UiPanel guardedChoices = new(_ => { });
guardedChoices.Content.Add(number);
guardedChoices.Content.Add(choices);
guardedChoices.Arrange(new Rectangle(0, 0, 400, 400));
guardedChoices.Update(default, 1,
[
    UiInputEvent.KeyPress(Keys.Tab, false, false),
    UiInputEvent.Text('-', false, false)
]);
Click(guardedChoices, choices.Bounds.Center);
Require(number.Focused && !choices.IsOpen, "Opening a dropdown abandoned an invalid numeric draft.");
guardedChoices.ClearFocus();

// Collapsed or replaced content must never leave an invisible field consuming keyboard input.
UiSection collapsible = new("Parameters", true);
collapsible.Children.Add(number);
UiPanel sectionPanel = new(_ => { });
sectionPanel.Content.Add(collapsible);
sectionPanel.Arrange(new Rectangle(0, 0, 400, 400));
sectionPanel.Update(default, 1,
[
    UiInputEvent.KeyPress(Keys.Tab, false, false),
    UiInputEvent.KeyPress(Keys.Tab, false, false)
]);
Require(number.Focused, "Section child did not receive focus.");
collapsible.Expanded = false;
sectionPanel.Arrange(sectionPanel.Bounds);
Require(!number.Focused && !sectionPanel.Capturing, "Collapsed section retained an invisible focused control.");

// Wheel and press can arrive in one native sample; activate the post-scroll target.
UiPanel scrollingPanel = new(_ => { });
int clickedRow = -1;
for (int index = 0; index < 10; index++)
{
    int row = index;
    scrollingPanel.Content.Add(new UiButton(() => $"Action {row}", () => clickedRow = row));
}
scrollingPanel.Arrange(new Rectangle(0, 0, 400, 240));
Point scrollClick = scrollingPanel.Content[0].Bounds.Center;
scrollingPanel.Update(MouseAt(scrollClick, pressed: true, wheel: -120), 1, []);
Require(scrollingPanel.Content[1].Bounds.Contains(scrollClick) && clickedRow == 1,
    "A wheel-and-press sample activated the stale pre-scroll target.");

// A newly opened modal inherits the opening button state, never the hidden panel's old edge.
int presentedActions = 0;
UiPanel presentedPanel = new(_ => { });
UiButton presentedAction = new(() => "Action", () => presentedActions++);
presentedPanel.Content.Add(presentedAction);
for (int index = 0; index < 8; index++)
{
    presentedPanel.Content.Add(new UiLabel(() => "Overflow", 28));
}
presentedPanel.Arrange(new Rectangle(0, 0, 400, 240));
Point presentedPoint = presentedAction.Bounds.Center;
presentedPanel.PrepareForOpen(pointerHeld: true);
presentedPanel.Update(MouseAt(presentedPoint, pressed: true, wheel: -960), 1, []);
Require(presentedActions == 0 && presentedAction.Bounds.Contains(presentedPoint),
    "An opening press or accumulated hidden-panel wheel input reached a newly presented modal.");
presentedPanel.Update(MouseAt(presentedPoint, wheel: -960), 1, []);
presentedPanel.Update(MouseAt(presentedPoint, pressed: true, wheel: -960), 1, []);
Require(presentedActions == 1, "A modal did not accept a real click after the opening press was released.");
presentedPanel.PrepareForOpen(pointerHeld: false);
presentedPanel.Update(MouseAt(presentedPoint, pressed: true, wheel: -960), 1, []);
Require(presentedActions == 2, "Keyboard opening discarded the first fresh pointer click.");

// A held Files press must not activate the newly opened compact library. Then exercise
// its current keyboard Save pattern workflow, including scrolling a short modal.
string modalDirectory = Path.GetFullPath(Path.Combine("artifacts", "modal-pointer-check", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(modalDirectory);
using (PlaybackController modalPlayback = new(PresetGenerator.Create(PresetKind.Random, 4, 2, 31)))
{
    ExperimentEditor modalEditor = new(modalPlayback);
    using LiveExperimentEditor modalSurface = new(modalEditor, modalDirectory, _ => { }, () => { });
    Rectangle smallInspector = new(14, 165, 392, 121);
    Rectangle smallBrowser = new(14, 60, 392, 264);
    // Files is the third control in the four-button Undo / Redo / Files / Guide row.
    Point filesAndSave = new(250, 218);
    modalSurface.Update(smallInspector, smallBrowser, 1, MouseAt(filesAndSave), []);
    modalSurface.Update(smallInspector, smallBrowser, 1, MouseAt(filesAndSave, pressed: true), []);
    Require(modalSurface.IsModal, "The compact Files action did not open its browser.");
    modalSurface.Update(smallInspector, smallBrowser, 1, MouseAt(filesAndSave, pressed: true), []);
    Require(Directory.GetFiles(modalDirectory, "*.json").Length == 0,
        "Holding the compact Files opening click accidentally saved an experiment.");
    modalSurface.Update(smallInspector, smallBrowser, 1, MouseAt(filesAndSave), []);
    for (int index = 0; index < 8; index++)
    {
        modalSurface.Update(smallInspector, smallBrowser, 1, MouseAt(filesAndSave),
            [UiInputEvent.KeyPress(Keys.Tab, false, false)]);
    }
    modalSurface.Update(smallInspector, smallBrowser, 1, MouseAt(filesAndSave),
        [UiInputEvent.KeyPress(Keys.Enter, false, false)]);
    for (int index = 0; index < 5; index++)
    {
        modalSurface.Update(smallInspector, smallBrowser, 1, MouseAt(filesAndSave),
            [UiInputEvent.KeyPress(Keys.Tab, false, false)]);
    }
    modalSurface.Update(smallInspector, smallBrowser, 1, MouseAt(filesAndSave),
        [UiInputEvent.KeyPress(Keys.Enter, false, false)]);
    // File encoding/publication is asynchronous. Closing the modal must not strand completion.
    modalSurface.Update(smallInspector, smallBrowser, 1, MouseAt(filesAndSave),
        [UiInputEvent.KeyPress(Keys.Escape, false, false)]);
    Require(!modalSurface.IsModal, "The library did not close while its save completed in the background.");
    long saveStarted = Stopwatch.GetTimestamp();
    while (Directory.GetFiles(modalDirectory, "*.discovery.json").Length == 0
        && Stopwatch.GetElapsedTime(saveStarted).TotalSeconds < 10)
    {
        modalSurface.Update(smallInspector, smallBrowser, 1, MouseAt(filesAndSave), []);
        Thread.Sleep(1);
    }
    Require(Directory.GetFiles(modalDirectory, "*.experiment.json").Length == 1
        && Directory.GetFiles(modalDirectory, "*.viewer.json").Length == 1
        && Directory.GetFiles(modalDirectory, "*.discovery.json").Length == 1,
        "The compact library's keyboard Save pattern workflow did not save the experiment, companion and preview.");
}

// At high UI scale a small native pane can be only 80 logical pixels high.
UiPanel shortPanel = new(_ => { });
UiButton shortToolbar = new(() => "Toolbar", () => { });
UiButton shortContent = new(() => "Content", () => { });
shortPanel.Toolbar.Add(shortToolbar);
shortPanel.Content.Add(shortContent);
shortPanel.Arrange(new Rectangle(10, 10, 350, 80));
Require(shortPanel.Body.Height >= shortContent.Height && shortPanel.Bounds.Contains(shortPanel.Body)
    && shortPanel.Footer == Rectangle.Empty, "A short pane's footer consumed or escaped its last full control row.");
shortPanel.Update(default, 1,
[
    UiInputEvent.KeyPress(Keys.Tab, false, false),
    UiInputEvent.KeyPress(Keys.Tab, false, false)
]);
Require(shortContent.Focused && shortPanel.Body.Contains(shortContent.Bounds),
    "Keyboard navigation could not expose a complete row in an 80px pane.");
shortPanel.Arrange(new Rectangle(10, 10, 350, 108));
Require(shortPanel.Footer.Height == 18 && shortPanel.Bounds.Contains(shortPanel.Footer)
    && shortPanel.Body.Height >= shortContent.Height, "Intermediate pane height did not use a bounded one-line footer.");
shortPanel.Arrange(new Rectangle(10, 10, 350, 180));
Require(shortPanel.Footer.Height == 38 && shortPanel.Bounds.Contains(shortPanel.Footer),
    "Normal pane height did not restore its bounded two-line footer.");

// Inspector prose must reserve every actual line at its assigned width, including the final word/seed.
UiPanel wrappingPanel = new(_ => { });
UiLabel worldNote = new(() => "World draft: apply together. Positions and velocities stay.", 32);
UiLabel populationNote = new(() => "Population draft: survivors keep their identities and motion.", 32);
UiLabel recipeSummary = new(() => "420 particles / 2 species (84 core / 336 halo) / seed 2147483647", 22);
UiLabel minimumNote = new(() => "Short note.", 48);
UiButton afterNotes = new(() => "Apply", () => { });
wrappingPanel.Content.AddRange([worldNote, populationNote, recipeSummary, minimumNote, afterNotes]);
wrappingPanel.Arrange(new Rectangle(0, 0, 350, 500));
Require(worldNote.Height == 36 && populationNote.Height == 36 && recipeSummary.Height == 36,
    "A narrow inspector label clipped its second line instead of reserving its full wrapped height.");
Require(populationNote.Bounds.Y >= worldNote.Bounds.Bottom && recipeSummary.Bounds.Y >= populationNote.Bounds.Bottom
    && minimumNote.Bounds.Y >= recipeSummary.Bounds.Bottom && afterNotes.Bounds.Y >= minimumNote.Bounds.Bottom,
    "Wrapped labels overlapped following controls.");
Require(minimumNote.Height == 48, "Wrapped measurement discarded an explicit minimum label height.");
wrappingPanel.Arrange(new Rectangle(0, 0, 600, 500));
Require(worldNote.Height == 32 && populationNote.Height == 32 && recipeSummary.Height == 22,
    "Widening the inspector retained unnecessary wrapped-line height.");
Require(UiCanvas.MeasureWrappedHeight("First\n\nThird", 326) == 54
    && UiCanvas.MeasureWrappedHeight("abcd", 1) == 72 && UiCanvas.MeasureWrappedHeight("", 326) == 0,
    "Wrapped layout did not preserve explicit blank lines or split an overlong word consistently.");

// Use the actual native recipe actions, including selection's no-op semantics and switching
// to a smaller species matrix. This needs no graphics device.
LiveExperimentEditor recipePanel = new(editor, Path.GetTempPath(), _ => { }, () => { });
ExperimentDefinition beforeRecipeSelection = playback.Experiment;
long beforeRecipeRevision = editor.Revision;
recipePanel.SelectRecipe(PresetKind.CoreHalo);
Require(ReferenceEquals(beforeRecipeSelection, playback.Experiment) && editor.Revision == beforeRecipeRevision,
    "Selecting a recipe changed the experiment before an explicit action.");
recipePanel.StartSelectedRecipe();
Require(playback.Observe().Count == 420 && editor.Configuration.Matrix.SpeciesCount == 2
    && editor.Populations.SequenceEqual(new[] { 84, 336 }) && playback.Observe().Step == 0,
    "Native recipe action did not establish the complete core/halo baseline.");
playback.SingleStep();
recipePanel.SelectRecipe(PresetKind.AlternatingBands);
recipePanel.ApplySelectedRecipeMatrix();
Require(playback.Observe().Step == 1 && playback.Observe().Count == 420 && editor.Configuration.Matrix[0, 0] == -0.6,
    "Native matrix-only recipe action regenerated particles or reset the clock.");
recipePanel.SelectRecipe(PresetKind.BiasedRibbons);
recipePanel.StartSelectedRecipe();
recipePanel.SelectRecipe(PresetKind.CoreHalo);
recipePanel.StartSelectedRecipe();
recipePanel.Update(new Rectangle(0, 0, 460, 700), new Rectangle(0, 0, 800, 700), 1, default, []);
Require(editor.Configuration.Matrix.SpeciesCount == 2 && recipePanel.Graph is not null,
    "Native recipe switch failed to refresh the smaller matrix.");
Console.WriteLine("Native widget integration: compact exact input, dropdown routing, validation, live slider/replay/undo, matrix navigation and built-in recipes passed without graphics.");
StartupChecks.Run();
ReplayCompletionChecks.Run();
await StorageChecks.RunAsync();

static MouseState MouseAt(Point point, bool pressed = false, int wheel = 0) => new(point.X, point.Y, wheel,
    pressed ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released,
    ButtonState.Released, ButtonState.Released, ButtonState.Released);

static void Click(UiPanel panel, Point point, int wheel = 0)
{
    panel.Update(MouseAt(point, wheel: wheel), 1, []);
    panel.Update(MouseAt(point, pressed: true, wheel: wheel), 1, []);
    panel.Update(MouseAt(point, wheel: wheel), 1, []);
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class CompatibilityGame : Game
{
    // Compiling an actual Game subclass catches host API incompatibility without opening a window.
    protected override void Update(GameTime gameTime) => base.Update(gameTime);
}
