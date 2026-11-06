using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Editor;

namespace ParticleLife.MonoGame;

internal sealed partial class ParticleLifeGame
{
    private Rectangle BrushButtonBounds => new(56, GraphicsDevice.Viewport.Height - 44, 30, 28);

    /// <summary>Transfers canvas ownership before inspector/shortcut dispatch, consuming both edges of modal interactions.</summary>
    /// <remarks>The canvas still observes actual held-button baselines every frame. Opening finishes an existing
    /// stroke once, and a dismissed/held press therefore cannot start another gesture. Playback is unaffected.</remarks>
    private bool RouteBrushMenu(MouseState mouse, double elapsed)
    {
        bool openBefore = brushMenu.IsOpen;
        bool leftPressed = mouse.LeftButton == ButtonState.Pressed && previousCanvasMouse.LeftButton == ButtonState.Released;
        bool middlePressed = mouse.MiddleButton == ButtonState.Pressed && previousCanvasMouse.MiddleButton == ButtonState.Released;
        bool keyboardToggle = input.Any(entry => !entry.IsText && entry.Key == Keys.B && !entry.Control);
        bool pointerToggle = middlePressed && (openBefore || worldBounds.Contains(mouse.Position));
        bool buttonToggle = leftPressed && BrushButtonBounds.Contains(mouse.Position);
        bool canOpen = !editor.Appearance.ShowHelp && !editor.IsReplaying && !resizingInspector;
        if (canOpen && (pointerToggle || buttonToggle || keyboardToggle && !surface.CapturesKeyboard))
        {
            if (openBefore)
            {
                // B typed in an exact numeric field is text, not a menu toggle.
                if (!keyboardToggle || !surface.BrushState.EditingNumeric) { brushMenu.Close(); }
            }
            else if (surface.TryReleaseFocus())
            {
                EndCanvasGesture();
                Point anchor = pointerToggle ? mouse.Position : worldBounds.Center;
                Vector2d world = CreateWorldTransform().ToWorld(new Vector2d(anchor.X, anchor.Y));
                MouseState logicalMouse = LogicalMouse(mouse);
                brushMenu.Open(MenuViewport, new Point((int)(anchor.X / BrushMenuScale), (int)(anchor.Y / BrushMenuScale)), world, logicalMouse);
            }
        }
        bool consumed = openBefore || brushMenu.IsVisible || canOpen && (pointerToggle || buttonToggle);
        // Toggle keys are consumed before the menu's ordered keyboard routing; numeric B remains literal.
        IReadOnlyList<UiInputEvent> menuInput = input;
        if (consumed && keyboardToggle && !surface.BrushState.EditingNumeric)
        {
            menuInput = input.Where(entry => entry.IsText ? entry.Character is not ('b' or 'B') : entry.Key != Keys.B).ToArray();
        }
        brushMenu.Update(LogicalMouse(mouse), consumed ? menuInput : [], elapsed, MenuViewport);
        return consumed;
    }

    private float BrushMenuScale => RadialBrushLayout.EffectiveScale(GraphicsDevice.Viewport.Bounds, uiScale);

    private Rectangle MenuViewport => new(8, 8, Math.Max(1, (int)(GraphicsDevice.Viewport.Width / BrushMenuScale) - 16),
        Math.Max(1, (int)(GraphicsDevice.Viewport.Height / BrushMenuScale) - 16));

    private MouseState LogicalMouse(MouseState mouse) => new((int)(mouse.X / BrushMenuScale), (int)(mouse.Y / BrushMenuScale), mouse.ScrollWheelValue,
        mouse.LeftButton, mouse.MiddleButton, mouse.RightButton, mouse.XButton1, mouse.XButton2);

    private void DrawBrushButton()
    {
        Rectangle bounds = BrushButtonBounds;
        canvas.Glass(bounds, 5);
        if (bounds.Contains(Mouse.GetState().Position) || brushMenu.IsOpen)
        {
            canvas.Rounded(bounds, UiCanvas.Accent * 0.14f, 5);
        }
        icons.Draw(batch, "circle-ellipsis", new Rectangle(bounds.X + 5, bounds.Y + 4, 20, 20), UiCanvas.Accent);
        if (bounds.Contains(Mouse.GetState().Position))
        {
            Rectangle tooltip = new(bounds.X, bounds.Y - 27, 254, 23);
            canvas.Glass(tooltip, 5);
            canvas.TextCentered($"Brush tools · B · {RadialBrushState.ToolNames[surface.BrushState.ActiveTool]}", tooltip, UiCanvas.Ink, 1.2f);
        }
    }
}
