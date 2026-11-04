namespace ParticleLife.Core.Experiments;

/// <summary>Records how initial conditions were created, independently of their saved values.</summary>
/// <param name="Name">Generator identity, such as <c>uniform-random</c> or <c>manual</c>.</param>
/// <param name="Version">Version of the generator's rules.</param>
/// <param name="Seed">Optional generator seed; it is provenance, not a replacement for initial state.</param>
/// <param name="Runtime">Runtime used during generation, or an explicit manual provenance description.</param>
/// <remarks>A seed does not guarantee identical generated values across runtimes or generator versions.</remarks>
public sealed record GeneratorMetadata(string Name, string Version, int? Seed, string Runtime);
