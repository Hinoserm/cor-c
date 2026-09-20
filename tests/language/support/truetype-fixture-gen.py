"""Regenerates the two synthetic TrueType fixtures embedded in
truetype_fixture.cor and truetype_composite_fixture.cor as byte arrays.

Needs fontTools (`pip install fonttools`). Run from anywhere; it writes
fixture.ttf and fixture2.ttf into the current directory. Turning either
into a .cor byte array is a matter of reading the file and formatting each
byte -- see the history of this file's commit for the exact script, or
write a new one; the fixtures themselves are what is checked in.
"""
import array
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
import fontTools.ttLib.tables.ttProgram as ttProgram
from fontTools.ttLib.tables._c_v_t import table__c_v_t
from fontTools.ttLib.tables._f_p_g_m import table__f_p_g_m
from fontTools.ttLib.tables._p_r_e_p import table__p_r_e_p
from fontTools.ttLib.tables._g_l_y_f import GlyphComponent, Glyph
from fontTools.ttLib.tables._k_e_r_n import KernTable_format_0

UPM = 1000


def rect_glyph(x0, y0, x1, y1):
    pen = TTGlyphPen(None)
    pen.moveTo((x0, y0)); pen.lineTo((x1, y0)); pen.lineTo((x1, y1)); pen.lineTo((x0, y1))
    pen.closePath()
    return pen.glyph()


def space_glyph():
    return TTGlyphPen(None).glyph()


def letter_H():
    # Two vertical stems and a crossbar: enough for the hinting virtual
    # machine to have real stems to round, and simple enough to check by
    # eye against the rendered bitmap.
    pen = TTGlyphPen(None)
    pen.moveTo((80, 0)); pen.lineTo((220, 0)); pen.lineTo((220, 300)); pen.lineTo((460, 300))
    pen.lineTo((460, 0)); pen.lineTo((600, 0)); pen.lineTo((600, 700)); pen.lineTo((460, 700))
    pen.lineTo((460, 400)); pen.lineTo((220, 400)); pen.lineTo((220, 700)); pen.lineTo((80, 700))
    pen.closePath()
    return pen.glyph()


def letter_O():
    # A ring: an outer contour and a counter-wound inner one, to exercise
    # the non-zero winding fill rule.
    pen = TTGlyphPen(None)
    pen.moveTo((80, 0)); pen.lineTo((520, 0)); pen.lineTo((520, 700)); pen.lineTo((80, 700))
    pen.closePath()
    pen.moveTo((160, 80)); pen.lineTo((160, 620)); pen.lineTo((440, 620)); pen.lineTo((440, 80))
    pen.closePath()
    return pen.glyph()


def mark_dot():
    return rect_glyph(-40, 0, 40, 80)


def build(with_composite):
    glyph_order = [".notdef", "space", "H", "I", "O", "one"]
    glyphs = {
        ".notdef": rect_glyph(50, 0, 450, 700),
        "space": space_glyph(),
        "H": letter_H(),
        "I": rect_glyph(200, 0, 340, 700),
        "O": letter_O(),
        "one": rect_glyph(150, 0, 350, 700),
    }
    advance_widths = {".notdef": 500, "space": 300, "H": 680, "I": 440, "O": 600, "one": 500}
    cmap = {ord(' '): 'space', ord('H'): 'H', ord('I'): 'I', ord('O'): 'O', ord('1'): 'one'}

    if with_composite:
        glyph_order += ["dot", "Hdot"]
        glyphs["dot"] = mark_dot()
        advance_widths["dot"] = 0
        advance_widths["Hdot"] = 680

        # H, plus "dot" scaled to half size and placed 800 font units
        # above the baseline with ArgumentsAreOffsets and
        # ROUND_XY_TO_GRID set, ScaledComponentOffset left OFF (the
        # default): the offset (0, 800) must land at y=800 exactly, not
        # at y=400, which is what wrongly scaling it by yy=0.5 would give.
        composite = Glyph()
        composite.numberOfContours = -1
        composite.xMin = 80; composite.xMax = 600; composite.yMin = 0; composite.yMax = 780
        c1 = GlyphComponent()
        c1.glyphName = "H"; c1.x = 0; c1.y = 0
        c1.flags = 0x0004  # ROUND_XY_TO_GRID
        c2 = GlyphComponent()
        c2.glyphName = "dot"; c2.x = 300; c2.y = 800
        c2.flags = 0x0002 | 0x0004 | 0x0008  # ARGS_ARE_XY_VALUES | ROUND_XY_TO_GRID | WE_HAVE_A_SCALE
        c2.transform = ((0.5, 0.0), (0.0, 0.5))
        composite.components = [c1, c2]
        glyphs["Hdot"] = composite
        cmap[0x1E24] = "Hdot"

    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap(cmap)
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics({name: (advance_widths[name], 0) for name in glyph_order})
    fb.setupHorizontalHeader(ascent=800, descent=-200)
    fb.setupNameTable({"familyName": "CorsacTest", "styleName": "Regular"})
    fb.setupOS2(sTypoAscender=800, sTypoDescender=-200, sTypoLineGap=0, usWinAscent=800, usWinDescent=200)
    fb.setupPost()

    font = fb.font

    cvt = table__c_v_t()
    cvt.values = array.array('h', [600])
    font["cvt "] = cvt

    fpgm = table__f_p_g_m()
    fpgm.program = ttProgram.Program()
    fpgm.program.fromAssembly("PUSH[] 0\nFDEF[]\nSVTCA[0]\nENDF[]".splitlines())
    font["fpgm"] = fpgm

    prep = table__p_r_e_p()
    prep.program = ttProgram.Program()
    prep.program.fromAssembly("PUSHB[] 0\nCALL[]".splitlines())
    font["prep"] = prep

    # A format 0 kern table with one pair, so DrawString/MeasureString's
    # use of it is exercised as well as the parser: H followed by I pulls
    # in by a hundred font units, one tenth of an em.
    from fontTools.ttLib.tables._k_e_r_n import table__k_e_r_n
    kern = table__k_e_r_n()
    kern.version = 0
    sub = KernTable_format_0()
    sub.version = 0
    sub.apple = False
    sub.coverage = 0x0001
    sub.format = 0
    sub.kernTable = {("H", "I"): -100}
    kern.kernTables = [sub]
    font["kern"] = kern

    h_prog = ttProgram.Program()
    h_prog.fromAssembly("SVTCA[0]\nPUSHB[] 0\nMDAP[1]".splitlines())
    font["glyf"]["H"].program = h_prog

    font["maxp"].maxZones = 1
    font["maxp"].maxTwilightPoints = 0
    font["maxp"].maxStorage = 1
    font["maxp"].maxFunctionDefs = 1
    font["maxp"].maxInstructionDefs = 0
    font["maxp"].maxStackElements = 32
    font["maxp"].maxSizeOfInstructions = 32
    font["maxp"].maxComponentElements = 2 if with_composite else 0
    font["maxp"].maxComponentDepth = 1 if with_composite else 0

    return font


if __name__ == "__main__":
    build(False).save("fixture.ttf")
    build(True).save("fixture2.ttf")
    print("wrote fixture.ttf and fixture2.ttf")
