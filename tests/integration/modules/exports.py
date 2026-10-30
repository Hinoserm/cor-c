#!/usr/bin/env python3
# The names a kernel's .corsac.exports holds, "name address" a line, the
# address in hex as readelf prints one: the section is read from the file
# as the kernel's loader reads it, by its header, its offsets its own.
# With --identity: the link's identity made again from the file -- the
# SHA-256 of its PT_LOAD bytes, __corsac_link_id's own 32 nought -- and
# "identity ok" where the kernel's copy and the table's are both it.
import hashlib
import struct
import sys

data = open(sys.argv[1], 'rb').read()
wide = data[4] == 2
if wide:
    shoff, = struct.unpack_from('<Q', data, 0x28)
    shentsize, shnum, shstrndx = struct.unpack_from('<HHH', data, 0x3A)
else:
    shoff, = struct.unpack_from('<I', data, 0x20)
    shentsize, shnum, shstrndx = struct.unpack_from('<HHH', data, 0x2E)


def header(i):
    at = shoff + i * shentsize
    if wide:
        name, _, _, _, offset, size = struct.unpack_from('<IIQQQQ', data, at)
    else:
        name, _, _, _, offset, size = struct.unpack_from('<IIIIII', data, at)
    return name, offset, size


_, names, _ = header(shstrndx)
table = None
sections = {}
for i in range(shnum):
    name, offset, size = header(i)
    text = data[names + name:data.index(b'\0', names + name)]
    sections[text] = (i, offset, size)
    if text == b'.corsac.exports':
        table = data[offset:offset + size]
if table is None:
    sys.exit(sys.argv[1] + ': no .corsac.exports')

word = 8 if wide else 4
tags = {}
at = 0
while True:
    tag, value = struct.unpack_from('<qq' if wide else '<ii', table, at)
    at += 2 * word
    if tag == 0:
        break
    tags[tag] = value
if len(sys.argv) > 2 and sys.argv[2] == '--identity':
    # The kernel's copy: __corsac_link_id in .symtab, its address in a segment.
    _, symoff, symsize = sections[b'.symtab']
    _, stroff, _ = sections[b'.strtab']
    size = 24 if wide else 16
    where = None
    for at in range(symoff, symoff + symsize, size):
        if wide:
            name, _, _, _, value, _ = struct.unpack_from('<IBBHQQ', data, at)
        else:
            name, value, _, _, _, _ = struct.unpack_from('<IIIBBH', data, at)
        if data[stroff + name:data.index(b'\0', stroff + name)] == b'__corsac_link_id':
            where = value
    if wide:
        phoff, = struct.unpack_from('<Q', data, 0x20)
        phentsize, phnum = struct.unpack_from('<HH', data, 0x36)
    else:
        phoff, = struct.unpack_from('<I', data, 0x1C)
        phentsize, phnum = struct.unpack_from('<HH', data, 0x2A)
    image = bytearray(data)
    loads = []
    for p in range(phnum):
        at = phoff + p * phentsize
        if wide:
            kind, _, offset, address, _, filesz = struct.unpack_from('<IIQQQQ', data, at)
        else:
            kind, offset, address, _, filesz = struct.unpack_from('<IIIII', data, at)
        if kind == 1:
            loads.append((offset, address, filesz))
    file_at = [o + where - a for o, a, n in loads if a <= where and where + 32 <= a + n][0]
    held = bytes(image[file_at:file_at + 32])
    image[file_at:file_at + 32] = bytes(32)
    made = hashlib.sha256(b''.join(bytes(image[o:o + n]) for o, a, n in loads)).digest()
    said = table[tags[0x60000C07]:tags[0x60000C07] + 32]
    if made != held or made != said:
        sys.exit('identity: made ' + made.hex() + ', kernel ' + held.hex() + ', table ' + said.hex())
    print('identity ok')
    sys.exit(0)

count, = struct.unpack_from('<I', table, tags[4] + 4)

if len(sys.argv) > 2 and sys.argv[2] == '--gnu':
    # Every name found again through DT_GNU_HASH as a loader finds it: the
    # bloom words, the bucket, the chain's hashes, then the name.
    at = tags[0x6ffffef5]
    nbuckets, symoffset, bloom_size, shift = struct.unpack_from('<IIII', table, at)
    bits = word * 8
    bloom = struct.unpack_from(('<%dQ' if wide else '<%dI') % bloom_size, table, at + 16)
    buckets = struct.unpack_from('<%dI' % nbuckets, table, at + 16 + bloom_size * word)
    chains = at + 16 + bloom_size * word + nbuckets * 4
    strings, entry = tags[5], tags[11]
    def name_of(i):
        at_sym = tags[6] + i * entry
        n, = struct.unpack_from('<I', table, at_sym)
        return table[strings + n:table.index(b'\0', strings + n)].decode()
    names64 = tags[0x60000C08]
    def fnv(text):
        h = 0xcbf29ce484222325
        for c in text.encode():
            h = ((h ^ c) * 0x100000001b3) & 0xffffffffffffffff
        return h
    def lookup(text):
        # As a loader binds: no symbol and no string read, the names as numbers.
        strong = fnv(text)
        h = 5381
        for c in text.encode():
            h = (h * 33 + c) & 0xffffffff
        w = bloom[(h // bits) % bloom_size]
        if not (w >> (h % bits)) & 1 or not (w >> ((h >> shift) % bits)) & 1:
            return None
        i = buckets[h % nbuckets]
        if i == 0:
            return None
        while True:
            ch, = struct.unpack_from('<I', table, chains + (i - symoffset) * 4)
            if (ch | 1) == (h | 1):
                held64, value = struct.unpack_from('<QQ', table, names64 + (i - 1) * 16)
                if held64 == strong:
                    return i
            if ch & 1:
                return None
            i += 1
    # As a glibc reader takes the table for granted.
    if symoffset != 1 or nbuckets % 2 == 0 or bloom_size & (bloom_size - 1) or shift != (6 if wide else 5):
        sys.exit('gnu hash header: nbuckets %d symoffset %d bloom %d shift %d' % (nbuckets, symoffset, bloom_size, shift))
    if count > 1 and not struct.unpack_from('<I', table, chains + (count - 1 - symoffset) * 4)[0] & 1:
        sys.exit('gnu hash: the last chain does not end')
    if tags[0x60000C08] % 8 or tags[0x60000C08] + 16 * (count - 1) > tags[6]:
        sys.exit('names64 is not 8-aligned before .dynsym')
    missed = [name_of(i) for i in range(1, count) if lookup(name_of(i)) != i]
    # And each value the same as the symbol's.
    for i in range(1, count):
        at_sym = tags[6] + i * entry
        sym_value = struct.unpack_from('<Q', table, at_sym + 8)[0] if wide else struct.unpack_from('<I', table, at_sym + 4)[0]
        if struct.unpack_from('<QQ', table, names64 + (i - 1) * 16)[1] != sym_value:
            sys.exit('names64 value of ' + name_of(i) + ' is not its symbol\'s')
    if missed:
        sys.exit('gnu hash misses %d of %d: %s' % (len(missed), count - 1, ', '.join(missed[:5])))
    held = {name_of(i) for i in range(1, count)}
    for k in range(500):
        absent = 'absent_%d_%x' % (k, k * 2654435761 & 0xffffffff)
        if absent not in held and lookup(absent) is not None:
            sys.exit('gnu hash finds a name it does not hold: ' + absent)
    print('gnu ok', count - 1)
    sys.exit(0)
strings, entry = tags[5], tags[11]
for i in range(1, count):
    at = tags[6] + i * entry
    if wide:
        name, _, _, _, value, _ = struct.unpack_from('<IBBHQQ', table, at)
    else:
        name, value, _, _, _, _ = struct.unpack_from('<IIIBBH', table, at)
    text = table[strings + name:table.index(b'\0', strings + name)].decode()
    print(text, format(value, '016x' if wide else '08x'))
