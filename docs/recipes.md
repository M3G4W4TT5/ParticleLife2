# Recipes

Recipes are starting points for exploring emergence in the existing Particle Life model. Each combines a directed interaction matrix with a species count and population mix. The names describe patterns to look for; their appearance depends on density, seed, damping, boundaries and simulation time.

In the editor, expand **Built-in recipes**, choose a recipe, then use **New from recipe**. This starts a new baseline with the suggested rules and populations, using the current seed. It preserves execution backend/workers, presentation and playback settings. **Apply matrix live** instead applies only the matrix to a compatible species count, retaining the particles, clock and other settings. The Generation recipe section remains available for creating experiments with your own population and placement settings.

## Interaction rules

Matrix rows are **receivers** and columns are **sources**. Positive values attract and negative values repel in the outer interaction interval. All species share the same repulsive core. In cyclic recipes, next and previous wrap around the species indices.

| Recipe / `--preset` key | Default population | Outer interaction coefficients |
| --- | --- | --- |
| Random / `random` | 500 particles, 7 species | Each directed entry independently sampled in `[-1, 1)`. |
| Chains / `chains` | 500 particles, 7 species | Self and both adjacent species `+1`; others `−1`. |
| Snakes / `snakes` | 500 particles, 7 species | Self `+1`, next `+0.2`, others `0`; at one species the next rule takes precedence. |
| Core and halo / `core-halo` | 84 core + 336 halo | Matrix `[[+0.7, +0.2], [+0.9, −0.6]]`, ordered core, halo. |
| Alternating bands / `alternating-bands` | 210 of each of 2 species | Self `−0.6`, other `+0.8`. |
| Ribbons / `ribbons` | 60 of each of 7 species | Self `−0.2`, both adjacent species `+0.8`, others `−0.5`. |
| Cyclic pursuit / `cyclic-pursuit` | 140 of each of 3 species | Self `+0.3`, next `+0.8`, previous `−0.8`. |
| Chase cycle 5 / `chase-cycle-5` | 84 of each of 5 species | Self `0.141/1.07`, previous `0.929/1.07`, next `−1`, others `0`. |
| Chase cycle 6 / `chase-cycle-6` | 70 of each of 6 species | Same rule as Chase cycle 5, on six species. |
| Species islands / `species-islands` | 70 of each of 6 species | Self `+0.7`, others `−0.35`. |
| Biased ribbons **(experimental)** / `biased-ribbons` | 60 of each of 7 species | Self `−0.2`, next `+0.95`, previous `+0.65`, others `−0.5`. |

The eight additional recipes use 420 particles by default. Core and halo uses a 1:4 population ratio; for another total, the core population is rounded to the nearest one-fifth and the remainder becomes halo. The others distribute particles equally, assigning any remainder to the lowest species indices. Historical Random, Chains and Snakes retain independently sampled species assignments.

All built-in recipes start in a 1 × 1 world with uniform positions and zero velocities, cutoff `0.1`, force scale `1`, repulsive-core fraction `0.3`, damping half-life `0.025`, timestep `1/144`, wall width `0.05` and wall strength `1.44`. Length, time and force use the project's reduced units. These values remain editable after generation.

## What to explore

**Core and halo** encourages compact cores surrounded by more dispersed halo particles. **Alternating bands** favours unlike neighbours, while **Ribbons** replaces Chains' self-attraction with self-repulsion to encourage coloured rows. **Species islands** favours separation into groups of the same species. These often settle after their initial rearrangement.

**Cyclic pursuit** and **Chase cycles** add different responses in opposite directions, allowing persistent motion and group rearrangement. **Biased ribbons** retains Ribbons' average adjacent attraction while adding a directional preference. It can form moving bands and loops, but its motion and accumulation at the walls vary strongly between seeds; it remains experimental.

Species count is part of the recipe. Core and halo and Alternating bands require two species, Cyclic pursuit requires three, and Chase cycle 5/6 require their named counts. Ribbons and Biased ribbons support at least three species; Species islands supports at least two. Random, Chains and Snakes support any positive count, but Chains becomes entirely attractive at three or fewer species and Snakes becomes reciprocal at two. An incompatible matrix template cannot be applied live; start a new recipe or change the species count explicitly.

Soft walls exert a force rather than reflecting or clipping particles, so off-world particles remain possible. Population size, world size and cutoff affect the number of interacting neighbours. Changing these can change the resulting structures, even if the matrix is unchanged. A finite run showing activity does not establish a physical phase or long-time stability.

## Start or save a recipe

Both native startup and the CLI accept the same keys. Omitted particle and species counts use the selected recipe's defaults; explicit compatible values override them:

```sh
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build -- --preset core-halo --seed 1
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build -- --preset ribbons --particles 700 --species 7 --seed 42
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- create --preset cyclic-pursuit --seed 1 --output artifacts/cyclic-pursuit.json
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- run artifacts/cyclic-pursuit.json --steps 17280 --sample-every 720 --output artifacts/cyclic-pursuit-run.json
```

The [preset examples](../examples/presets) contain the eight additional recipes with seed 1. In **Files**, select one and choose **Preset new** for a fresh population, or **Preset live** to apply compatible settings without replacing particles. File presets contain generation instructions, whereas an experiment contains actual initial positions, velocities and species. The editor's file-based generator can produce a different arrangement from the built-in generator even with the same seed. Save **initial**, **from here**, or a CLI-created experiment to retain exact values; a seed alone does not guarantee identical results across runtimes.

## Scientific inspiration

Nonreciprocal coupling can turn static patterns into traveling ones in the diffusive model of [You, Baskaran and Marchetti (2020)](https://arxiv.org/abs/2005.07684). This motivates changing directional coupling separately from reciprocal attraction; their thresholds do not directly apply to our force law.

Chase cycle coefficients adapt the coupling ratios from Eq. 18 and the Movie 8 parameters of [Ouazan-Reboul, Agudo-Canalejo and Golestanian (2023)](https://www.nature.com/articles/s41467-023-40241-w.pdf). Only the interaction network is adapted: their Brownian, three-dimensional, inverse-square, periodic model differs from our deterministic two-dimensional model with finite-range forces and soft walls. The recipes do not simulate metabolism or reproduce that paper's physical system. The other new coefficients are exploratory choices for this model.

The implemented matrix rules, species constraints and generation conventions are documented alongside `PresetCatalog` and `PresetGenerator` in C# XML comments.
