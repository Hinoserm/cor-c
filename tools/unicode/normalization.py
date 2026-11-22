#!/usr/bin/env python3
# Writes stdlib/src/System/Text/normalization.cor: the Unicode tables
# string.Normalize and IsNormalized work from, from Python's unicodedata,
# which must carry the Unicode version .NET 10's ICU does (16.0 on ICU 77).
#
#   python3 -I tools/unicode/normalization.py > stdlib/src/System/Text/normalization.cor
#
# The algorithm half is normalization-template.cor beside this script, copied in first.
#
# Tables, all over code points (not UTF-16 units):
#   Classes      every code point with a nonzero canonical combining class,
#                as ranges of one class: start, end, class.
#   Canonical    each code point's full canonical decomposition (applied
#                to the end), as keys and offsets into one array.
#   Compatible   the same for the compatibility decomposition, only where
#                it differs from the canonical one.
#   Composites   the primary composites: (first << 21 | second) -> composite,
#                sorted, every pair whose composition NFC keeps.
#   Checks       UAX #15's quick-check properties, as ranges of one value:
#                start, end, bits (1 NFD No, 2 NFKD No, 4 NFC No, 8 NFC Maybe,
#                16 NFKC No, 32 NFKC Maybe). A string whose code points are
#                all Yes, in canonical order, is that form already. Maybe is
#                whatever may compose with a preceding character: a second of
#                a composite, or (to a fixed point) a code point decomposing to
#                start with one -- a little more than can, which only sends
#                those strings the full way; checked against NFC and NFKC
#                before anything is written.
# Hangul syllables are worked out by the algorithm and are in no table.
import sys
import unicodedata as ud

if ud.unidata_version != "16.0.0":
    sys.exit("unicodedata is Unicode %s; .NET 10's ICU is 16.0.0" % ud.unidata_version)

HANGUL_FIRST, HANGUL_LAST = 0xAC00, 0xD7A3
SURROGATES = range(0xD800, 0xE000)


def chars():
    for cp in range(0x110000):
        if cp in SURROGATES or HANGUL_FIRST <= cp <= HANGUL_LAST:
            continue
        yield cp


classes = []
for cp in chars():
    c = ud.combining(chr(cp))
    if c == 0:
        continue
    if classes and classes[-1][1] == cp - 1 and classes[-1][2] == c:
        classes[-1][1] = cp
    else:
        classes.append([cp, cp, c])

canonical, compatible = {}, {}
for cp in chars():
    s = chr(cp)
    d = ud.normalize("NFD", s)
    if d != s:
        canonical[cp] = [ord(x) for x in d]
    k = ud.normalize("NFKD", s)
    if k != d:
        compatible[cp] = [ord(x) for x in k]

composites = {}
for cp in chars():
    raw = ud.decomposition(chr(cp))
    if not raw or raw.startswith("<"):
        continue
    parts = [int(p, 16) for p in raw.split()]
    if len(parts) != 2:
        continue
    # A primary composite: the pair composes back to it under NFC (which
    # leaves out the composition exclusions and non-starter decompositions).
    if ud.normalize("NFC", chr(parts[0]) + chr(parts[1])) == chr(cp):
        composites[(parts[0] << 21) | parts[1]] = cp


seconds = set()
for key in composites:
    seconds.add(key & 0x1FFFFF)
# Hangul: a V after an L, a T after an LV, compose.
seconds.update(range(0x1161, 0x1161 + 21))
seconds.update(range(0x11A8, 0x11A7 + 28))
# AND WHAT DECOMPOSES TO START WITH ONE: such a code point composes with a
# preceding character through its decomposition (Unicode 16's U+16D68 is
# 16D67 16D67, and 16D63 16D67 composes) -- Maybe too, to a fixed point.
while True:
    grew = False
    for cp in chars():
        if cp in seconds:
            continue
        s1 = chr(cp)
        if ud.normalize("NFC", s1) != s1:
            continue
        d = ud.normalize("NFD", s1)
        if d != s1 and ord(d[0]) in seconds:
            seconds.add(cp)
            grew = True
    if not grew:
        break

# CHECKED AGAINST NFC AND NFKC THEMSELVES: each code point that the form
# leaves as it is and that has a decomposition (canonical for NFC,
# compatibility for NFKC), and is not already a Maybe, put after each
# composite's first -- a change the form makes there is one only a Maybe may
# let through. (Maybe grows by NFD alone: a code point whose compatibility
# decomposition starts with a second is NFKC No already.)
firsts = sorted({key >> 21 for key in composites} | set(range(0x1100, 0x1100 + 19)) | {0xAC00})
for form in ("NFC", "NFKC"):
    for cp in chars():
        s1 = chr(cp)
        if cp in seconds or ud.normalize(form, s1) != s1:
            continue
        if ud.normalize("NFD" if form == "NFC" else "NFKD", s1) == s1:
            continue
        for f in firsts:
            pair = chr(f) + s1
            if ud.normalize(form, pair) != ud.normalize(form, chr(f)) + s1:
                sys.exit("U+%04X composes under %s after U+%04X but is not Maybe" % (cp, form, f))


def check_bits(cp):
    s = chr(cp)
    bits = 0
    if HANGUL_FIRST <= cp <= HANGUL_LAST:
        return 1 | 2
    if ud.normalize("NFD", s) != s:
        bits |= 1
    if ud.normalize("NFKD", s) != s:
        bits |= 2
    if ud.normalize("NFC", s) != s:
        bits |= 4
    elif cp in seconds:
        bits |= 8
    if ud.normalize("NFKC", s) != s:
        bits |= 16
    elif cp in seconds:
        bits |= 32
    return bits


checks = []
for cp in range(0x110000):
    if cp in SURROGATES:
        continue
    b = check_bits(cp)
    if b == 0:
        continue
    if checks and checks[-1][1] == cp - 1 and checks[-1][2] == b:
        checks[-1][1] = cp
    else:
        checks.append([cp, cp, b])


def ints(name, values, per=12):
    out = ["    internal static readonly int[] %s = {" % name]
    row = []
    for v in values:
        row.append("0x%X" % v)
        if len(row) == per:
            out.append("        " + ", ".join(row) + ",")
            row = []
    if row:
        out.append("        " + ", ".join(row) + ",")
    out.append("    };")
    return out


def longs(name, values, per=8):
    out = ["    internal static readonly long[] %s = {" % name]
    row = []
    for v in values:
        row.append("0x%XL" % v)
        if len(row) == per:
            out.append("        " + ", ".join(row) + ",")
            row = []
    if row:
        out.append("        " + ", ".join(row) + ",")
    out.append("    };")
    return out


def mapping(prefix, table):
    keys = sorted(table)
    offsets, data = [], []
    for k in keys:
        offsets.append(len(data))
        data.extend(table[k])
    offsets.append(len(data))
    return ints(prefix + "Keys", keys) + ints(prefix + "Offsets", offsets) + ints(prefix + "Data", data)


import os
here = os.path.dirname(os.path.abspath(__file__))
lines = [
    "// GENERATED by tools/unicode/normalization.py (with tools/unicode/normalization-template.cor)",
    "// from Unicode %s, .NET 10's ICU. Do not edit: change the script or the" % ud.unidata_version,
    "// template and run it again.",
    "",
    "using System.Collections.Generic;",
    "using System.Text;",
    "",
]
lines += open(os.path.join(here, "normalization-template.cor")).read().rstrip("\n").split("\n")
lines += ["", "static class UnicodeNormalizationData", "{"]
lines += ints("ClassRanges", [v for r in classes for v in r])
lines += mapping("Canonical", canonical)
lines += mapping("Compatible", compatible)
keys = sorted(composites)
lines += longs("CompositePairs", keys)
lines += ints("CompositeValues", [composites[k] for k in keys])
lines += ints("CheckRanges", [v for r in checks for v in r])
lines.append("}")
sys.stdout.write("\n".join(lines) + "\n")
