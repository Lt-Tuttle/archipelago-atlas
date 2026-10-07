r"""Makes the wordmark: "The Archipelago Atlas" in Cormorant SC small capitals (outlined, so no font ships), a scale bar
above it and hairlines beside "Atlas". Writes docs/wordmark/wordmark-light.svg and wordmark-dark.svg; Atlas recolours
them for the theme and accent at load (AP_Atlas.Core.WordmarkSvg), so their own colours are the keys it swaps.

Needs the font and fontTools, neither of which the repository holds:
  pip install fonttools
  CormorantSC-SemiBold.ttf from https://github.com/google/fonts/tree/main/ofl/cormorantsc (SIL OFL 1.1)
Run: python Tools/wordmark.py <path to CormorantSC-SemiBold.ttf>
"""
import os
import sys

from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.ttLib import TTFont

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'docs', 'wordmark')

# The keys WordmarkSvg swaps: text, quiet lines, accent (the bar's filled segments).
PALETTES = {
    'light': {'ink': '#201B33', 'soft': '#6B6480', 'accent': '#8A2BE2'},
    'dark': {'ink': '#F3EFE6', 'soft': '#A9A3B8', 'accent': '#B98AF0'},
}


class Face:
    def __init__(self, path):
        self.font = TTFont(path)
        self.upem = self.font['head'].unitsPerEm
        self.cmap = self.font.getBestCmap()
        self.glyphs = self.font.getGlyphSet()
        self.hmtx = self.font['hmtx']
        self.kern = self._kerning()

    def _kerning(self):
        pairs = {}
        if 'GPOS' not in self.font:
            return pairs
        for lookup in self.font['GPOS'].table.LookupList.Lookup:
            for sub in lookup.SubTable:
                st = getattr(sub, 'ExtSubTable', sub)
                if st.__class__.__name__ != 'PairPos':
                    continue
                if st.Format == 1:
                    first = st.Coverage.glyphs
                    for i, ps in enumerate(st.PairSet):
                        for rec in ps.PairValueRecord:
                            v = getattr(rec.Value1, 'XAdvance', 0) if rec.Value1 else 0
                            if v:
                                pairs.setdefault((first[i], rec.SecondGlyph), v)
                elif st.Format == 2:
                    c1, c2 = st.ClassDef1.classDefs, st.ClassDef2.classDefs
                    by1, by2 = {}, {}
                    for g in st.Coverage.glyphs:
                        by1.setdefault(c1.get(g, 0), []).append(g)
                    for g, c in c2.items():
                        by2.setdefault(c, []).append(g)
                    for i, c1rec in enumerate(st.Class1Record):
                        for j, c2rec in enumerate(c1rec.Class2Record):
                            v = getattr(c2rec.Value1, 'XAdvance', 0) if c2rec.Value1 else 0
                            if not v:
                                continue
                            for g1 in by1.get(i, []):
                                for g2 in by2.get(j, []):
                                    pairs.setdefault((g1, g2), v)
        return pairs

    def text(self, text, size, x, baseline, fill, tracking=0.0, anchor='start'):
        """The text as outlined paths at a size; returns (svg, width)."""
        scale = size / self.upem
        glyphs, advance, prev = [], 0.0, None
        for ch in text:
            if ch == ' ':
                advance += 0.3 * size + tracking * size
                prev = None
                continue
            name = self.cmap[ord(ch)]
            if prev is not None:
                advance += self.kern.get((prev, name), 0) * scale
            pen = SVGPathPen(self.glyphs)
            self.glyphs[name].draw(pen)
            glyphs.append((pen.getCommands(), advance))
            advance += self.hmtx[name][0] * scale + tracking * size
            prev = name
        width = advance - tracking * size
        if anchor == 'middle':
            x -= width / 2
        parts = [f'<g fill="{fill}">']
        for d, dx in glyphs:
            parts.append(f'<path transform="translate({x + dx:.2f} {baseline:.2f}) scale({scale:.5f} {-scale:.5f})" d="{d}"/>')
        parts.append('</g>')
        return '\n'.join(parts), width


def wordmark(face, p):
    W, H = 1300, 370
    cx = W / 2
    body = []
    top, tw = face.text('The Archipelago', 96, cx, 160, p['ink'], tracking=0.08, anchor='middle')
    body.append(top)
    bottom, bw = face.text('Atlas', 150, cx, 310, p['ink'], tracking=0.16, anchor='middle')
    body.append(bottom)
    # Plain double hairlines beside "Atlas".
    for sign in (-1, 1):
        inner, outer, y = cx + sign * (bw / 2 + 44), cx + sign * (tw / 2), 268
        body.append(f'<line x1="{inner:.0f}" y1="{y}" x2="{outer:.0f}" y2="{y}" stroke="{p["soft"]}" stroke-width="1.6"/>')
        body.append(f'<line x1="{inner:.0f}" y1="{y + 7}" x2="{outer:.0f}" y2="{y + 7}" stroke="{p["soft"]}" stroke-width="0.8"/>')
    # The top rule is a scale bar: alternating filled segments with ticks, as wide as the name.
    y, segs, x0 = 52, 10, cx - tw / 2
    seg_w = tw / segs
    for i in range(segs):
        fill = p['accent'] if i % 2 == 0 else 'none'
        body.append(f'<rect x="{x0 + i * seg_w:.1f}" y="{y}" width="{seg_w:.1f}" height="7" fill="{fill}" stroke="{p["soft"]}" stroke-width="1"/>')
    for i in range(segs + 1):
        h = 10 if i % (segs // 2) == 0 else 5
        x = x0 + i * seg_w
        body.append(f'<line x1="{x:.1f}" y1="{y + 7}" x2="{x:.1f}" y2="{y + 7 + h}" stroke="{p["soft"]}" stroke-width="1"/>')
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="The Archipelago Atlas">\n'
            f'<title>The Archipelago Atlas</title>\n' + '\n'.join(body) + '\n</svg>\n')


if __name__ == '__main__':
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    face = Face(sys.argv[1])
    os.makedirs(OUT, exist_ok=True)
    for mode, p in PALETTES.items():
        path = os.path.join(OUT, f'wordmark-{mode}.svg')
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(wordmark(face, p))
        print(path, os.path.getsize(path))
