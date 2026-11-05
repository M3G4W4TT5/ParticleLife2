# Discoveries and local sharing

Open **Files** for the discovery library. Search matches display names, descriptions, tags and filenames. Favourite and recent views use local metadata; up to 300 matching items are shown, so refine the search for larger folders. Rename changes the library's display name, preserving the scientific filename and its bound viewer companion. Descriptions, tags and favourites live in atomic `.discovery.json` sidecars beside the original files. Moving a discovery manually should include its `.viewer.json` and `.discovery.json` companions when present.

Save choices have different meanings:

| Choice | Stored content | Thumbnail |
| --- | --- | --- |
| Preset | Reusable rules, generation recipe, palette and other saved settings | Explicitly labelled generated recipe sample; never an evolved pattern |
| Pattern | Actual completed particle values and rules, rebased to a new initial experiment | Sample of that captured completed state |
| Initial | Original reset baseline | Sample of the original initial positions, even after the live scene evolves |
| Session | Initial state and recorded applied changes | Explicitly labelled **INITIAL** geometry; it does not pretend to show the terminal pattern |

Previews contain at most 256 sampled points. A million-particle preset generates only its bounded preview sample. Refresh reads bounded metadata/header data, never full large particle arrays; drawing reads only the cached preview. Legacy files remain loadable and appear with a clear missing-preview label. If a scientific file changes outside the app, its old thumbnail is hidden rather than shown as current. Failed or unsupported scientific loads retain the active valid experiment. Existing scientific files and viewer companions are protected against overwrite.

Select a preset to review its components. **Coefficients**, **Core / cutoff radii**, **Palette only**, and **Future generation** are independent choices. The alignment field lists one source-preset index for each current target species, in order `S0,S1,...`; `-1` explicitly retains that target. A directed pair changes only if both target species are mapped. For example, `1,-1,0` swaps source species 0/1 into target species 2/0 and retains target species 1. Radius imports materialize effective directed tables while retaining unmapped radii and the global parameter values. Palette imports retain camera/effects. Generation imports future recipes/counts and does not create or resample particles. Validation completes before one undoable application; particle identities, motion, clock, execution, playback and unselected components remain.

**Copy current preset** produces strict small preset JSON. **Paste preset for review** validates it and reveals the component/alignment controls without applying it. Clipboard text is limited to 1 Mi characters. Clipboard import accepts presets; large patterns and sessions use files.

**Export .plshare** creates a compressed local artifact. Share that file explicitly; nothing is uploaded or published. **Import share** validates it into a new explicit local filename, then Load / Replay / Preset remains a separate action. The ZIP wrapper has fixed entries `share.json` (wrapper version 1), `document.json`, and an optional `document.json.viewer.json`; it preserves the scientific and viewer-companion UTF-8 bytes exactly. Unknown/duplicate entries, archive-provided extraction paths, unsupported schemas, malformed state, mismatched companion hashes and destination collisions are rejected. Import limits are 256 MiB compressed, 256 MiB per scientific document, 4 MiB per viewer companion and 512 MiB expanded in total. Larger recordings remain explicit uncompressed files; limits never truncate a file while claiming it is complete.

## Pending consolidated native cases

These cases remain **Pending** until the integrated native review; graphics-free tests do not establish their visual/input result.

| Case | Setup and actions | Expected result |
| --- | --- | --- |
| V18-library | CPU experiment at 500 and 100k; save preset/pattern/initial/session after evolving; inspect thumbnails at normal and minimum window sizes | Four readable kinds; initial/session thumbnails show original geometry, pattern shows captured geometry, preset says generated sample; no draw-time loading stalls |
| V18-find | Add descriptions/tags; favourite, rename display, search by each field; load, reopen and select Recent | Correct filtered cards and recent order; metadata survives reopening; scientific filename and bytes remain unchanged |
| V19-selective | Three target/two source species; map `1,-1,0`; import coefficient, radii, palette and generation separately; undo/redo; try bad index and incompatible periodic radius | Only selected mapped components change, retained entries/particles/clock remain; invalid candidates apply nothing |
| V19-share | Copy/paste a preset, inspect before applying; export/import pattern/session .plshare; try duplicate names and malformed/bound-companion mismatch | Explicit validation and review; imported bytes/values preserved; errors retain active experiment and protected files |
| V20-capture | After P5 integration, repeat library session actions with GPU/captured recordings | Capture restore and action re-simulation are separately labelled; preview source remains honest; no hidden per-frame GPU readback |
