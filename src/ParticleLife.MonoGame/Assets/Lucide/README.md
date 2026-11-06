# Brush and history icons

The thirteen SVGs in this directory are unmodified assets from **Lucide 0.468.0**,
commit [`f12b0de177fbc2a6795e99be065887e72b237123`](https://github.com/lucide-icons/lucide/tree/f12b0de177fbc2a6795e99be065887e72b237123/icons).
Each file comes from that commit's `icons/<name>.svg`. The official tag was resolved
through GitHub's Git reference API. No icon font or runtime network request is used.

The assets use their original `viewBox="0 0 24 24"`, `fill="none"`,
`stroke-width="2"`, round line caps and round joins. `currentColor` is resolved to
white when creating the atlas; the native renderer tints its alpha coverage with
the UI colour. Path coordinates, relative geometry and stroke proportions remain
unchanged. Icons occupy 96 × 96 cells, in this order:

1. `mouse-pointer-2` — Navigate only
2. `hand` — Move particles
3. `circle-plus` — Add particles
4. `paintbrush` — Paint species
5. `circle-minus` — Erase
6. `magnet` — Attract
7. `expand` — Repel
8. `wind` — Directional force
9. `undo-2` — Undo
10. `redo-2` — Redo
11. `circle-dashed` — Circle shape
12. `square-dashed` — Square shape
13. `circle-check` — Species selection

`lucide-atlas.png` is a curated, embedded runtime asset. Regenerate it offline
from these sources with `pwsh -NoProfile -File scripts/build-lucide-atlas.ps1`.
The optional asset-authoring script uses **ImageMagick 7.1.2-3**, its **librsvg
2.40.20** SVG renderer and Lanczos reduction from a supersampled render. The
ordinary .NET build does not require ImageMagick. The atlas is premultiplied and
given area-filtered mip levels once at native resource creation, so Draw does
not perform file I/O or rasterization.

Atlas SHA-256: `36df84ff10a48bf06844c5f3e6aa7e29e0004c286334c742b0b6b8c284e8310b`.

Lucide's ISC notice is retained verbatim in `LICENSE`. The original Feather MIT
notice is also included in `FEATHER-LICENSE` for inherited portions, from
[Feather v4.29.2](https://github.com/feathericons/feather/blob/1b002399e8758fb2bccea4d07312dc9e0f43dcb8/LICENSE).
Both notices accompany native build and publish output.
