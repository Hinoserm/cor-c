#!/usr/bin/env python3
# The names a kernel's .corsac.exports holds, "name address" a line, the
# address in hex as readelf prints one: the section is read from the file
# as the kernel's loader reads it, by its header, its offsets its own.
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
for i in range(shnum):
    name, offset, size = header(i)
    if data[names + name:data.index(b'\0', names + name)] == b'.corsac.exports':
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
