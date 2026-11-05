# Small experiments to explore

These files keep actual initial values so their meaning can be inspected without generating a large population.

| File | Try this |
| --- | --- |
| `experiments/two-species.json` | Original six-particle soft-wall fixture. It retains schema-1 direct-search meaning. |
| `experiments/periodic-seam.json` | Four particles face partners across opposite edges. Pause and single-step; the shortest interaction crosses the seam. |
| `experiments/directed-ranges.json` | Six particles with reflecting edges and different ranges in each direction. Select `0 <- 1` and `1 <- 0` to compare the curves. |
| `sessions/live-edits.session.json` | A small recorded editing timeline, including terminal edits. Use the CLI `replay` command or Replay session in the library. |
| `presets/*.preset.json` | Reusable starting recipes. Preset live applies compatible settings; Preset new explicitly creates a new population. |

From a source checkout:

```sh
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build -- examples/experiments/periodic-seam.json --paused
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- validate examples/experiments/directed-ranges.json
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- run examples/experiments/directed-ranges.json --steps 20 --sample-every 0 --output artifacts/directed-ranges-run.json
```

The range tables use receiver rows and source columns. Their entries are lengths, not screen sizes. These examples demonstrate the rules; they are not performance workloads or physical validation cases. The larger reproducible workload ladder belongs under ignored `artifacts/`, prepared through the [performance tooling](../docs/performance-p1.md).

For a discovery exercise, open a recipe, change a matrix coefficient, save From here with a new name, then give it a description and tag in the library. Save a Session separately to retain the applied editing history. See the [quickstart](../docs/quickstart.md) and [discovery guide](../docs/discovery-library.md).
