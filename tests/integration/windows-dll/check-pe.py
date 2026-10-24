#!/usr/bin/env python3
# check-pe.py A.dll B.dll EXPORT...: the same DLL linked at two bases. Reads
# both with nothing but the standard library and checks what Windows' loader
# relies on: the headers, the export directory (these names, sorted, each a
# function in .text), the import directory and each thunk's jump through its
# IAT slot, the checksum, and that .reloc lists every word that moves with
# the base and no other.
import struct, sys

def u16(b, o): return struct.unpack_from('<H', b, o)[0]
def u32(b, o): return struct.unpack_from('<I', b, o)[0]

def fail(why):
    print('FAIL', why, file=sys.stderr)
    sys.exit(1)

def read(path):
    b = open(path, 'rb').read()
    if b[:2] != b'MZ': fail(path + ': no MZ')
    pe = u32(b, 0x3C)
    if b[pe:pe + 4] != b'PE\0\0': fail(path + ': no PE signature')
    coff = pe + 4
    if u16(b, coff) != 0x14C: fail(path + ': not i386')
    if not u16(b, coff + 18) & 0x2000: fail(path + ': not a DLL')
    sections, opt = u16(b, coff + 2), coff + 20
    if u16(b, opt) != 0x10B: fail(path + ': not PE32')
    img = dict(bytes=b, base=u32(b, opt + 28), entry=u32(b, opt + 16), size=u32(b, opt + 56),
               headers=u32(b, opt + 60), checksum_at=opt + 64, dirs=[(u32(b, opt + 96 + 8 * i), u32(b, opt + 100 + 8 * i)) for i in range(16)])
    img['sections'] = []
    for i in range(sections):
        s = opt + 224 + 40 * i
        img['sections'].append(dict(name=b[s:s + 8].rstrip(b'\0').decode(), vsize=u32(b, s + 8), rva=u32(b, s + 12),
                                    rawsize=u32(b, s + 16), raw=u32(b, s + 20), flags=u32(b, s + 36)))
    mem = bytearray(img['size'])
    mem[:img['headers']] = b[:img['headers']]
    for s in img['sections']:
        mem[s['rva']:s['rva'] + min(s['rawsize'], s['vsize'])] = b[s['raw']:s['raw'] + min(s['rawsize'], s['vsize'])]
    img['mem'] = bytes(mem)
    return img

def cstr(m, rva): return m[rva:m.index(b'\0', rva)].decode()

def section_of(img, rva):
    for s in img['sections']:
        if s['rva'] <= rva < s['rva'] + s['vsize']: return s
    return None

def checksum(b, at):
    total = 0
    for i in range(0, len(b), 2):
        if i in (at, at + 2): continue
        total += b[i] | (b[i + 1] << 8 if i + 1 < len(b) else 0)
        total = (total & 0xFFFF) + (total >> 16)
    return ((total & 0xFFFF) + (total >> 16)) + len(b)

a, b2 = read(sys.argv[1]), read(sys.argv[2])
wanted = sys.argv[3:]
m = a['mem']
if checksum(a['bytes'], a['checksum_at']) != u32(a['bytes'], a['checksum_at']): fail('checksum')
text = next(s for s in a['sections'] if s['name'] == '.text')
if not text['flags'] & 0x20000000: fail('.text is not executable')
if section_of(a, a['entry']) is not text: fail('DllMain is not in .text')

erva, esize = a['dirs'][0]
if not erva: fail('no export directory')
n, names, funcs, ords = u32(m, erva + 24), u32(m, erva + 32), u32(m, erva + 28), u32(m, erva + 36)
got = [cstr(m, u32(m, names + 4 * i)) for i in range(n)]
if got != sorted(wanted): fail('exports %r, wanted %r' % (got, sorted(wanted)))
for i in range(n):
    f = u32(m, funcs + 4 * u16(m, ords + 2 * i))
    if section_of(a, f) is not text: fail('export %s at 0x%x is not in .text' % (got[i], f))

irva, isize = a['dirs'][1]
iat, iatsize = a['dirs'][12]
imports = {}
d = irva
while u32(m, d + 12):
    dll, lookup, first = cstr(m, u32(m, d + 12)), u32(m, d), u32(m, d + 16)
    if not iat <= first < iat + iatsize: fail(dll + "'s address table is outside the IAT")
    j = 0
    while u32(m, lookup + 4 * j):
        entry = u32(m, lookup + 4 * j)
        if u32(m, first + 4 * j) != entry: fail('IAT and lookup table differ for ' + dll)
        name = cstr(m, entry + 2)
        slot = a['base'] + first + 4 * j
        if m[text['rva']:text['rva'] + text['vsize']].find(b'\xff\x25' + struct.pack('<I', slot)) < 0: fail('no thunk jumps through ' + name)
        imports.setdefault(dll, []).append(name)
        j += 1
    d += 20
# The program's GetTickCount, and the platform library's kernel32 calls:
# only kernel32, and only what Windows 95 and NT 3.51 already exported.
win95 = {'ExitProcess', 'GetStdHandle', 'GetTickCount', 'QueryPerformanceCounter', 'QueryPerformanceFrequency', 'Sleep', 'GetModuleFileNameA', 'LoadLibraryA', 'GetVersion',
         'TlsAlloc', 'TlsFree', 'VirtualAlloc', 'VirtualFree', 'VirtualQuery', 'WriteFile',
         'CreateSemaphoreA', 'ReleaseSemaphore', 'WaitForSingleObject'}
if set(imports) != {'kernel32.dll'} or 'GetTickCount' not in imports['kernel32.dll'] or not set(imports['kernel32.dll']) <= win95:
    fail('imports %r' % imports)

def fixups(img):
    rva, size = img['dirs'][5]
    out, at = set(), rva
    while at < rva + size:
        page, block = u32(img['mem'], at), u32(img['mem'], at + 4)
        for k in range(8, block, 2):
            e = u16(img['mem'], at + k)
            if e >> 12 == 3: out.add(page + (e & 0xFFF))
            elif e >> 12 != 0: fail('fix-up type %d' % (e >> 12))
        at += block
    return out

fa, fb = fixups(a), fixups(b2)
if not fa or fa != fb: fail('the two links list different fix-ups')
delta = b2['base'] - a['base']
for r in fa:
    if (u32(b2['mem'], r) - u32(m, r)) & 0xFFFFFFFF != delta: fail('fix-up at 0x%x does not move with the base' % r)
covered = set(i for r in fa for i in range(r, r + 4))
moved = [i for i in range(a['headers'], len(m)) if m[i] != b2['mem'][i] and i not in covered]
if moved: fail('%d bytes move with the base and have no fix-up, first at 0x%x' % (len(moved), moved[0]))
print('ok: %d exports, %d imports, %d fix-ups' % (n, sum(len(v) for v in imports.values()), len(fa)))
