#!/usr/bin/env python3
# Every export of a shared image found as a loader finds it: through the GNU
# hash -- its bloom word, bucket and chain -- and, beside the symbol, its
# name's FNV-1a 64-bit hash and value in .corsac.names64 (DT_CORSAC_NAMES64),
# the same tables a kernel's .corsac.exports holds. Prints "lookup ok N"
# where every one of the N exports is found at its own value, and what went
# wrong otherwise.
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
sections = {}
for i in range(shnum):
    name, offset, size = header(i)
    text = data[names + name:data.index(b'\0', names + name)]
    sections[text] = data[offset:offset + size]
for wanted in (b'.dynsym', b'.dynstr', b'.gnu.hash', b'.corsac.names64'):
    if wanted not in sections:
        sys.exit('no ' + wanted.decode())
dynsym, dynstr, gnu, names64 = sections[b'.dynsym'], sections[b'.dynstr'], sections[b'.gnu.hash'], sections[b'.corsac.names64']

entry = 24 if wide else 16
symbols = []
for at in range(0, len(dynsym), entry):
    if wide:
        name, info, other, shndx, value, size = struct.unpack_from('<IBBHQQ', dynsym, at)
    else:
        name, value, size, info, other, shndx = struct.unpack_from('<IIIBBH', dynsym, at)
    text = dynstr[name:dynstr.index(b'\0', name)]
    symbols.append((text, value, shndx))


def gnu_hash(name):
    h = 5381
    for c in name:
        h = (h * 33 + c) & 0xffffffff
    return h


def fnv(name):
    h = 0xcbf29ce484222325
    for c in name:
        h = ((h ^ c) * 0x100000001b3) & 0xffffffffffffffff
    return h


buckets, first, bloom_words, shift = struct.unpack_from('<IIII', gnu, 0)
word = 8 if wide else 4
bits = word * 8
bloom = [struct.unpack_from('<Q' if wide else '<I', gnu, 16 + i * word)[0] for i in range(bloom_words)]
bucket_at = 16 + bloom_words * word
bucket = [struct.unpack_from('<I', gnu, bucket_at + i * 4)[0] for i in range(buckets)]
chain_at = bucket_at + buckets * 4

# The imports come first and are never in the hash; every export after them.
for i, (text, value, shndx) in enumerate(symbols[1:], 1):
    if (shndx == 0) != (i < first):
        sys.exit('symbol %d %s is %s but the hash starts at %d' % (i, text.decode(), 'an import' if shndx == 0 else 'an export', first))


def lookup(name):
    h = gnu_hash(name)
    w = bloom[(h // bits) % bloom_words]
    if not (w >> (h % bits)) & 1 or not (w >> ((h >> shift) % bits)) & 1:
        return None
    i = bucket[h % buckets]
    if i == 0:
        return None
    strong = fnv(name)
    while True:
        c, = struct.unpack_from('<I', gnu, chain_at + (i - first) * 4)
        if (c | 1) == (h | 1):
            n_hash, n_value = struct.unpack_from('<QQ', names64, (i - first) * 16)
            if n_hash == strong and symbols[i][0] == name:
                return n_value, i
        if c & 1:
            return None
        i += 1


found = 0
for i, (text, value, shndx) in enumerate(symbols[first:], first):
    got = lookup(text)
    if got is None:
        sys.exit('not found through the GNU hash: ' + text.decode())
    if got != (value, i):
        sys.exit('%s found as symbol %d at %#x, not %d at %#x' % (text.decode(), got[1], got[0], i, value))
    found += 1
if lookup(b'm_no_such_name_ever') is not None:
    sys.exit('a name no image defines was found')
print('lookup ok %d' % found)
