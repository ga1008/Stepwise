# Bundled annotation font

`NotoSansCJKsc-Regular.otf` is the unmodified, complete **Noto Sans CJK SC Regular 2.004** static OpenType font. It is loaded locally by the annotation editor; no font installation or network connection is required.

- Upstream: https://github.com/notofonts/noto-cjk
- Official release: https://github.com/notofonts/noto-cjk/releases/tag/Sans2.004
- Release date: 2022-01-27
- Pinned commit: `523d033d6cb47f4a80c58a35753646f5c3608a78`
- Official font URL: https://raw.githubusercontent.com/notofonts/noto-cjk/523d033d6cb47f4a80c58a35753646f5c3608a78/Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf
- Official license URL: https://raw.githubusercontent.com/notofonts/noto-cjk/523d033d6cb47f4a80c58a35753646f5c3608a78/LICENSE
- Retrieved and inspected: 2026-10-09
- Copyright embedded in the font: © 2014–2021 Adobe (http://www.adobe.com/).
- License: **SIL Open Font License 1.1**, included verbatim as `OFL.txt`.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| NotoSansCJKsc-Regular.otf | 16,437,364 | `2c76254f6fc379fddfce0a7e84fb5385bb135d3e399294f6eeb6680d0365b74b` |
| OFL.txt | See original file | `6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2` |

The supplied font has 65,535 glyphs and 44,810 mapped code points. It is not a Latin-only web subset. Inspection with FontTools 4.63.0 confirmed the Chinese annotation sample, Latin letters, digits, common Chinese punctuation, ASCII punctuation, and all 94 fullwidth ASCII forms. See `font-verification.json` for the inspection result. Newer or rare characters outside this upstream font's coverage may use the browser's normal fallback fonts.

Use the CSS alias `Stepwise Sans` consistently in canvas drawing and the input field:

```css
@font-face {
  font-family: 'Stepwise Sans';
  src: url('fonts/NotoSansCJKsc-Regular.otf') format('opentype');
  font-weight: 400;
  font-style: normal;
  font-display: block;
}
```

Wait for `document.fonts.load('28px "Stepwise Sans"')` and verify that a font face was returned before measuring text or exporting annotated images. This avoids storing fallback-font metrics before the bundled font finishes loading.

To restore these files from their pinned official source, run `tools/Get-AnnotationFont.ps1`. It verifies both SHA-256 hashes before replacing the files.
