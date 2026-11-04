using Microsoft.Xna.Framework.Input;

namespace ParticleLife.MonoGame.Editor;

/// <summary>One ordered native input event with modifiers captured before a short chord is released.</summary>
/// <remarks>Separating all keys from all text would allow Enter to commit before characters received
/// earlier in the same frame. This immutable event retains their order and never samples modifiers
/// later, after SDL may already have processed the matching key-up events.</remarks>
internal readonly record struct UiInputEvent(Keys Key, char Character, bool Control, bool Shift, bool IsText)
{
    internal static UiInputEvent KeyPress(Keys key, bool control, bool shift) => new(key, '\0', control, shift, false);
    internal static UiInputEvent Text(char character, bool control, bool shift) => new(Keys.None, character, control, shift, true);
}
