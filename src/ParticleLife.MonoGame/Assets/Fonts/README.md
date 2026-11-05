# UI font

IBM Plex Sans Regular is bundled under the SIL Open Font License in `OFL.txt`.
The unmodified source is from [IBM Plex](https://github.com/IBM/plex/blob/master/packages/plex-sans/fonts/complete/ttf/IBMPlexSans-Regular.ttf), downloaded 5 November 2026.
Its SHA-256 is `975dcda37d80f038dcd143c22e33ca2d97a0cc5a929aace1c749153b0fe1afa5`.

`plex-sans-atlas.png` and `plex-sans-metrics.json` are curated runtime assets embedded in the executable. They retain the supplied font's proportional advances and antialiased glyph coverage. They are intentionally tracked; no downloaded or locally installed font is required to build or run the app.

To deliberately regenerate them on Windows with the included source, run `pwsh -NoProfile -File scripts/build-ui-font.ps1` from the repository root. This optional asset-authoring command uses System.Drawing and is separate from the cross-platform application build. Review the resulting assets and metrics together.
