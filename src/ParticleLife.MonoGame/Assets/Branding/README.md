# Particle Life 2 artwork

The identity uses aqua and yellow pixel lettering, an orange superscript, a dithered green leaf and colourful particles on dark teal. The artwork was created with built-in image generation from the author's concept and selected logo direction.

- `logo.png`: approved logo, 1536 × 1024.
- `splash.png`: wide startup artwork, 1672 × 941. Embedded in the host and fitted without cropping or stretching.
- `icon.png`: PL² icon master, 1254 × 1254.
- `Icon.ico`: Windows executable icon, with 16, 24, 32, 48, 64, 128 and 256 pixel frames.
- `Icon.bmp`: 64 × 64, 24-bit BMP for the native window and taskbar icon. Its embedded logical name is `Icon.bmp`, which DesktopGL loads automatically.

The PNG artwork is source material. Keep it in the repository; application builds require no image generator or image conversion tools. The splash texture is loaded once and released when dismissed. The original logo and icon master are not embedded in the executable.

To re-export the platform icons, run from this directory with ImageMagick 7.1.2:

```powershell
magick icon.png -filter Box -define icon:auto-resize=256,128,64,48,32,24,16 Icon.ico
magick icon.png -filter Box -resize 64x64 -alpha off -type TrueColor BMP3:Icon.bmp
```

Box filtering preserves average colour coverage when reducing the large pixel clusters. Inspect the smallest sizes after any artwork change. The window icon is a native desktop icon; it does not require a web favicon.
