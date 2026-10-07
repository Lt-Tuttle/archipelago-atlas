# The wordmark

"The Archipelago Atlas" in Cormorant SC small capitals, with a map's scale bar above and hairlines beside "Atlas". The lettering is outlined, so no font ships with Atlas; the design is deliberately typographic, so nothing in it reads as an emblem next to the icon or Archipelago's own logo.

- `wordmark-light.svg` and `wordmark-dark.svg` are the templates. Atlas recolours them at load for the theme and the accent (`AP_Atlas.Core.WordmarkSvg`): the text takes the theme's text colour, the lines its quiet colour, the bar's filled segments the accent.
- They're made by `AP_Atlas_Source/Tools/wordmark.py` from Cormorant SC SemiBold (Catharsis Fonts, SIL Open Font License 1.1; see `THIRD_PARTY_NOTICES.md`). The font isn't in the repository: the script says where to get it.
- To use the wordmark elsewhere (a README, a social preview), take either SVG as it is.
