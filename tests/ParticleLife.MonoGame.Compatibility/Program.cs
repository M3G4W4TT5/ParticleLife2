using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

// Compile and managed-assembly smoke check only: no window or native graphics device.
Vector2d position = new(0.25, 0.75);
Vector2 displayPosition = new((float)position.X, (float)position.Y);
Console.WriteLine($"Core net10.0 / MonoGame {typeof(Game).Assembly.GetName().Version}: {displayPosition}");

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
    Bounds = new Rectangle(0, 0, 460, 68)
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
number.Press(new Point(120, 45));
playback.SingleStep();
number.Drag(new Point(280, 45));
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
    Bounds = new Rectangle(0, 0, 460, 68)
};
selector.Press(new Point(454, 45));
selector.Release();
Require(selectedSpecies == 6 && selector.SliderFraction == 1, "Species selector range did not end at the last valid species.");
selectableSpecies = 1;
selector.Press(new Point(454, 45));
selector.Release();
Require(selectedSpecies == 0 && selector.SliderFraction == 0, "A one-species selector produced an invalid index or undefined thumb fraction.");
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
Console.WriteLine("Native widget integration: draft validation, live slider/replay/undo, matrix navigation and built-in recipes passed without graphics.");
StartupChecks.Run();

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
