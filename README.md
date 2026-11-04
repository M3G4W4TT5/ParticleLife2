# Particle Life 2

Particle Life 2 is a C# project for exploring emergence through particle simulations. The first development phase modernizes the original two-dimensional Particle Life simulation around an engine-independent numerical core.

The project prioritizes explicit models, numerical correctness, measurable performance, readable code, and scientific explanations in C# XML documentation comments. MonoGame is the proposed first viewer; Unity integration, three-dimensional models, and gameplay are later work.

The repository currently contains the [architecture and migration plan](docs/architecture-and-migration.md) and [contributor guidance](CONTRIBUTING.md). Application implementation has not started.

The plan starts with a reproducible serial simulation and a runner without graphics, adapts the MonoGame viewer, and then adds measured spatial acceleration. C# XML comments will be the source for generated API documentation. Markdown records project decisions and workflows rather than duplicating code documentation.

The original [ParticleLife](https://github.com/M3G4W4TT5/ParticleLife) is the migration reference. [ParticleLife v0.2 Prototype](https://github.com/M3G4W4TT5/ParticleLife_v0.2_Prototype) supplies research ideas for later work.
