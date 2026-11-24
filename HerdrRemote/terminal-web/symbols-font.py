#!/usr/bin/env python3
# Builds wwwroot/fonts/RemoteShellSymbolsMono-Regular.woff2: the text symbols agents print (Claude Code's ⏺ ⎿ ✻ ✢ ✳ ⏵ ⏸ ◐ ☐ …) that
# Geist Mono and JetBrains Mono lack, taken from the Noto symbol fonts and fitted to one terminal cell (advance 600 of 1000, as
# Geist Mono's), so no platform falls back to a color emoji font (iOS) or draws a missing-glyph box (Android) for them. Characters
# whose default presentation is emoji (Emoji_Presentation=Yes) are left out: they stay emoji, as on a desktop terminal.
#
# Run by hand (needs fontTools and brotli: pip install fonttools brotli), from terminal-web/:
#   python3 symbols-font.py <dir with the sources below>
# Sources (fetched once by hand; the result is committed, the sources are not):
# - NotoSansSymbols2-Regular.ttf  sha256 3ce38effdb615dd929c8b0f52768dfa2cd21f206e3824bc7df61e4074b41ae52
# - NotoSansSymbols-Regular.ttf   sha256 0088617baec0e8ac47e022cc1f38695f772301c9ef6d1f24a785abbef1e05d79
# - NotoSansMath-Regular.ttf      sha256 7283c396e9b22699bb542d9631030dc804a7e5b954f193d8f8f5b5f1162fbc61
#   from https://github.com/notofonts/notofonts.github.io (fonts/<Family>/full/ttf/), SIL OFL 1.1 (licenses/Noto-OFL.txt)
# - emoji-data.txt (Unicode 16.0.0) sha256 f1365a5173eee18e1f98b240cdc492e84a25f1ce7e0c9d1094eb29c41a22696a
#   from https://www.unicode.org/Public/16.0.0/ucd/emoji/emoji-data.txt
import sys
from pathlib import Path

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.recordingPen import DecomposingRecordingPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont

FAMILY = 'Remote Shell Symbols Mono'
ADVANCE = 600
# The widest and tallest a glyph is drawn inside its cell (Geist Mono's ascender to descender is 1005 to -295).
MAX_WIDTH = 560
MAX_HEIGHT = 1100
ASCENT, DESCENT = 1005, -295
# The blocks searched: arrows, misc technical, geometric shapes, misc symbols, dingbats, supplemental arrows and math symbols,
# misc symbols and arrows.
BLOCKS = [(0x2190, 0x21FF), (0x2300, 0x23FF), (0x25A0, 0x25FF), (0x2600, 0x26FF), (0x2700, 0x27BF), (0x2900, 0x29FF),
          (0x2B00, 0x2BFF)]
SOURCES = ['NotoSansSymbols2-Regular.ttf', 'NotoSansSymbols-Regular.ttf', 'NotoSansMath-Regular.ttf']

sources = Path(sys.argv[1])
fonts = Path(__file__).resolve().parent.parent / 'HerdrRemote' / 'Resources' / 'Raw' / 'wwwroot' / 'fonts'

covered = set()
for name in ['GeistMono-Regular.woff2', 'JetBrainsMono-Regular.woff2']:
    covered |= set(TTFont(fonts / name).getBestCmap())

emoji = set()
for line in (sources / 'emoji-data.txt').read_text(encoding='utf-8').splitlines():
    fields = line.split('#')[0].split(';')
    if len(fields) == 2 and fields[1].strip() == 'Emoji_Presentation':
        first, _, last = fields[0].strip().partition('..')
        emoji |= set(range(int(first, 16), int(last or first, 16) + 1))

wanted = [code for start, end in BLOCKS for code in range(start, end + 1) if code not in covered and code not in emoji]
glyphs = {'.notdef': TTGlyphPen(None).glyph()}
cmap = {}
for source in SOURCES:
    font = TTFont(sources / source)
    glyph_set = font.getGlyphSet()
    source_cmap = font.getBestCmap()
    for code in wanted:
        if code in cmap or code not in source_cmap:
            continue
        recording = DecomposingRecordingPen(glyph_set)
        glyph_set[source_cmap[code]].draw(recording)
        bounds = BoundsPen(None)
        recording.replay(bounds)
        pen = TTGlyphPen(None)
        if bounds.bounds is not None:
            x_min, y_min, x_max, y_max = bounds.bounds
            scale = min(1.0, MAX_WIDTH / max(1, x_max - x_min), MAX_HEIGHT / max(1, y_max - y_min))
            # Centered in the cell; scaled about the glyph's own vertical center, so it stays at the height its font drew it.
            middle_y = (y_min + y_max) / 2
            recording.replay(TransformPen(pen, (scale, 0, 0, scale, ADVANCE / 2 - (x_min + x_max) / 2 * scale, middle_y - middle_y * scale)))
        name = f'uni{code:04X}'
        glyphs[name] = pen.glyph()
        cmap[code] = name

builder = FontBuilder(1000, isTTF=True)
builder.setupGlyphOrder(list(glyphs))
builder.setupCharacterMap(cmap)
builder.setupGlyf(glyphs)
builder.setupHorizontalMetrics({name: (ADVANCE, getattr(glyph, 'xMin', 0)) for name, glyph in glyphs.items()})
builder.setupHorizontalHeader(ascent=ASCENT, descent=DESCENT)
builder.setupNameTable({
    'familyName': FAMILY,
    'styleName': 'Regular',
    'copyright': 'Glyphs from Noto Sans Symbols 2, Noto Sans Symbols and Noto Sans Math: Copyright 2022 The Noto Project Authors',
    'licenseDescription': 'SIL Open Font License, Version 1.1',
    'licenseInfoURL': 'https://openfontlicense.org',
})
builder.setupOS2(sTypoAscender=ASCENT, sTypoDescender=DESCENT, sTypoLineGap=0, usWinAscent=ASCENT, usWinDescent=-DESCENT,
                 xAvgCharWidth=ADVANCE)
builder.setupPost(isFixedPitch=1)
builder.font.flavor = 'woff2'
builder.save(fonts / 'RemoteShellSymbolsMono-Regular.woff2')
print(f'{len(cmap)} symbols')
