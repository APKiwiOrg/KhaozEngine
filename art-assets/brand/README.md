# Khaoz Engine artwork

The Rift identity pairs an angular diamond and lightning-shaped cut with custom geometric lettering.
The icon, standalone mark and logo share the same geometry.

## Assets

| Use | Asset |
| --- | --- |
| Logo on dark backgrounds | [KhaozEngine-logo-dark.svg](KhaozEngine-logo-dark.svg) |
| Logo on light backgrounds | [KhaozEngine-logo-light.svg](KhaozEngine-logo-light.svg) |
| One-color logo on light backgrounds | [KhaozEngine-logo-mono.svg](KhaozEngine-logo-mono.svg) |
| Standalone mark | `KhaozEngine-mark-{dark,light,mono}.svg` |
| Browser favicon | [favicon.svg](favicon.svg) |
| PNG icons, 16 to 1024 px | [../icon/generated/png/](../icon/generated/png/) |
| ICO favicon or Windows icon | [../icon/generated/windows/KhaozEngine.ico](../icon/generated/windows/KhaozEngine.ico) |
| macOS Dock or bundle icon | [../icon/generated/macos/KhaozEngine.icns](../icon/generated/macos/KhaozEngine.icns) |

Each logo and standalone mark also has a transparent PNG and a layered Aseprite source with the same
stem. The SVG lettering is outlined geometry and needs no installed font. Variant names describe the
intended background, so `dark` contains ivory lettering.

The repository README selects the appropriate logo for the reader's color scheme. The showcase uses
the existing generated window icon, which also supplies its runtime macOS Dock image.

## Palette and spacing

| Color | Hex | Role |
| --- | --- | --- |
| Rift orange | `#FF6838` | Lower half of the symbol |
| Ivory | `#F2F0E7` | Upper half and lettering on dark backgrounds |
| Charcoal | `#101318` | Icon tile |
| Ink | `#191E25` | Upper half and lettering on light backgrounds |
| Slate | `#ABB3BC` | Secondary lettering on dark backgrounds |
| Dark slate | `#53606D` | Secondary lettering on light backgrounds |

Keep the existing aspect ratio and leave clear space around the mark. Use the tiled icon at favicon
and Dock sizes, where its background keeps the symbol visible. Use the full wordmark where the
ENGINE lettering remains readable. Avoid adding shadows, gradients or outlines to the standalone mark.

## Editing and export

Artwork was authored through the Aseprite MCP. The Aseprite files retain separate symbol and lettering
layers. Keep the SVG counterparts aligned when changing the geometry or palette.

The [icon master](../icon/KhaozEngine-icon-master.aseprite) is the editable tiled artwork. Export it
to `art-assets/icon/KhaozEngine-icon-master.png`, then run from the repository root:

```sh
python3 scripts/generate-icons.py
```

The existing generator owns the platform pack. Do not edit its output files by hand. Keep
`favicon.svg` aligned with the tiled master when changing that artwork.
