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
strings, entry = tags[5], tags[11]
for i in range(1, count):
    at = tags[6] + i * entry
    if wide:
        name, _, _, _, value, _ = struct.unpack_from('<IBBHQQ', table, at)
    else:
        name, value, _, _, _, _ = struct.unpack_from('<IIIBBH', table, at)
    text = table[strings + name:table.index(b'\0', strings + name)].decode()
    print(text, format(value, '016x' if wide else '08x'))
