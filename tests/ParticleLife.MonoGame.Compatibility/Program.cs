using Microsoft.Xna.Framework;
using ParticleLife.Core.State;

// Compile and managed-assembly smoke check only: no window or native graphics device.
Vector2d position = new(0.25, 0.75);
Vector2 displayPosition = new((float)position.X, (float)position.Y);
Console.WriteLine($"Core net10.0 / MonoGame {typeof(Game).Assembly.GetName().Version}: {displayPosition}");

internal sealed class CompatibilityGame : Game
{
    // Compiling an actual Game subclass catches host API incompatibility without opening a window.
    protected override void Update(GameTime gameTime) => base.Update(gameTime);
}
