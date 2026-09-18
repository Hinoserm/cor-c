#nullable enable
using System.Buffers.Binary;

namespace Corsac;

/// <summary>
/// The on-disk form of an assembled program.
///
/// A committed image is an encoding contract, not just a build artefact: if a
/// field moves or an opcode is renumbered, an old image stops running, which is
/// exactly the signal we want. Regenerating the golden image is therefore a
/// deliberate act, never a side effect.
/// </summary>
public static class ImageFile
{
    private static ReadOnlySpan<byte> Magic => "CORSACIM"u8;

    /// <summary>Set in the flags word for an image that is a library.</summary>
    public const uint FlagLibrary = 1;

    // ---- the sectioned image format --------------------------------------
    //
    // See docs/image-format.md. The table is what lets generic templates,
    // runtime metadata and per-architecture code be added without changing the
    // position of every existing field. Fixed-layout predecessors describe a
    // different machine and are deliberately refused, never translated.

    public const ushort VersionMajor = 5;

    /// <summary>
    /// Bumped when a section is ADDED. Minor never gates -- an old reader loads
    /// a newer minor -- which is what makes adding one safe, and which is why
    /// it is worth moving rather than leaving at zero for ever.
    ///
    /// 1: ARCH, saying which processor the code is for.
    /// 2: META, the type table behind reflection.
    /// </summary>
    public const ushort VersionMinor = 2;

    /// <summary>
    /// Header of a version-5 image, before the section table.
    ///
    /// It carries the image-wide SCALARS -- where it starts, where it was
    /// linked, whether it is a library -- because those are facts about the
    /// image rather than blobs, and a section holding five numbers would be a
    /// section for the sake of having one. Everything of any size is a section.
    ///
    ///    0  magic "CORSACIM"        24  flags
    ///    8  version major, minor    28  library slot
    ///   12  header length           32  entry
    ///   14  section count           40  link base
    ///   16  section table offset    48  library statics
    ///   20  checksum                52  reserved
    /// </summary>
    private const int V5HeaderBytes = 64;
    private const int V5FlagsAt = 24;
    private const int V5LibSlotAt = 28;
    private const int V5EntryAt = 32;
    private const int V5BaseAt = 40;
    private const int V5LibDataAt = 48;

    /// <summary>
    /// One row of the section table: what it is, which architecture it is for,
    /// where it lives and how long it is.
    /// </summary>
    public readonly record struct Section(string Tag, ushort Arch, ushort Flags, int Offset, int Length, uint Checksum);

    private const int SectionBytes = 24;

    /// <summary>
    /// A loader that does not know this tag must REFUSE the image.
    ///
    /// The safety valve in the compatibility rules: a future section that
    /// changes what the image MEANS marks itself required, so an old loader
    /// stops rather than quietly doing the wrong thing with the rest.
    /// </summary>
    public const ushort SectionRequired = 1;

    /// <summary>A tool may drop this section and rewrite the table.</summary>
    public const ushort SectionStrippable = 2;

    /// <summary>Architecture-neutral: one copy serves every architecture.</summary>
    public const ushort ArchAny = 0;

    // ---- which processor this code is for ---------------------------------
    //
    // The mainframe may hold processors of different architectures at once --
    // an 80386 seated beside a 68010 -- under one kernel, so an image has to
    // say what it was built for and may carry code for more than one.
    //
    // ELF's e_machine numbers, because this project has reused MINIX, the MBR,
    // BSD disklabel letters and POSIX rather than inventing its own, and a
    // third registry of CPU numbers would be the same mistake in a new place.
    // EM_386 is 3, EM_68K is 4, EM_ARM is 40, EM_X86_64 is 62; CORSAC takes a
    // value well above the assigned range, which is what everybody with an
    // unregistered machine does.

    /// <summary>This machine, as an ELF machine number. 'C' and 'O'.</summary>
    public const ushort MachineCorsac = 0x434F;

    /// <summary>
    /// One entry of the ARCH section: what a processor must be to run the code
    /// that carries this same architecture number in the section table.
    ///
    /// Twenty-four bytes, which is the section-table row size -- not a
    /// coincidence worth relying on, but it keeps a dump readable in columns.
    /// </summary>
    public const int ArchEntryBytes = 24;

    /// <summary>Sixty-four bit. The only class this machine has.</summary>
    public const byte ClassSixtyFour = 2;

    /// <summary>Little-endian, which is what CORSAC is.</summary>
    public const byte EndianLittle = 1;

    /// <summary>
    /// Reads and validates the image section table.
    ///
    /// The compatibility rules live here and nowhere else:
    ///
    ///   1. a major this loader does not know is refused
    ///   2. a minor it does not know is NOT -- that is what makes additions safe
    ///   3. an unknown tag is skipped when optional and refused when required
    ///   4. every section carries its own length, so a truncated one is caught
    ///
    /// Rule 4 matters more than it looks: a stripped or damaged section that is
    /// silently misparsed is the failure that costs a day, where one that is
    /// detected is a message.
    /// </summary>
    public static IReadOnlyList<Section> Sections(ReadOnlySpan<byte> bytes)
    {
        if (!Looks(bytes))
        {
            throw new AsmException(0, "not a CORSAC image: bad magic");
        }

        if (bytes.Length < V5HeaderBytes)
        {
            throw new AsmException(0, "truncated image: header is incomplete");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);

        if (major != VersionMajor)
        {
            throw new AsmException(0,
                $"image is format version {major}, and this toolchain reads {VersionMajor}");
        }

        int header = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        int at = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);

        if (header < V5HeaderBytes || header > bytes.Length)
        {
            throw new AsmException(0, $"corrupt image: header length {header} is invalid");
        }

        if (at < header || (at & 7) != 0 || (long)at + (long)count * SectionBytes > bytes.Length)
        {
            throw new AsmException(0, "corrupt image: the section table runs past the end of the file");
        }

        List<Section> found = new(count);

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> row = bytes.Slice(at + i * SectionBytes, SectionBytes);
            string tag = System.Text.Encoding.ASCII.GetString(row[..4]);
            ushort arch = BinaryPrimitives.ReadUInt16LittleEndian(row[4..]);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(row[6..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(row[8..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(row[12..]);
            uint sum = BinaryPrimitives.ReadUInt32LittleEndian(row[16..]);

            if (offset < 0 || length < 0 || (long)offset + length > bytes.Length)
            {
                throw new AsmException(0, $"corrupt image: section '{tag}' runs past the end of the file");
            }

            if (!Known(tag) && (flags & SectionRequired) != 0)
            {
                throw new AsmException(0,
                    $"image needs section '{tag}', which this toolchain does not understand");
            }

            found.Add(new Section(tag, arch, flags, offset, length, sum));
        }

        return found;
    }

    /// <summary>Reads a version-5 image, given its already-validated table.</summary>
    private static Image ReadV5(ReadOnlySpan<byte> bytes, IReadOnlyList<Section> table)
    {
        // Copied once, because a local function cannot capture a span and the
        // alternative is threading the whole buffer through five helpers.
        byte[] all = bytes.ToArray();

        // CHOSEN BY ARCHITECTURE, not by being first.
        //
        // An image may carry code for several processors, so a section is
        // wanted only if it is for THIS one or for none in particular. With one
        // architecture every row matches and this is the same as taking the
        // first -- which is exactly why it is written now, while it is easy to
        // check, rather than on the day a second architecture makes it matter.
        byte[] Body(string tag)
        {
            foreach (Section s in table)
            {
                if (s.Tag == tag && (s.Arch == ArchAny || s.Arch == MachineCorsac))
                {
                    return all[s.Offset..(s.Offset + s.Length)];
                }
            }
            return Array.Empty<byte>();
        }

        // WHAT THIS IMAGE WAS BUILT FOR, refused when it is not us.
        //
        // The message names both sides, because "wrong architecture" without
        // saying which two is a message that sends somebody to read a hex dump.
        // ARCH is mandatory. An image that omits it does not identify executable
        // code for this processor and is not a current CORSAC image.
        bool foundArch = false;

        foreach (Section s in table)
        {
            if (s.Tag != "ARCH")
            {
                continue;
            }

            foundArch = true;

            if (s.Length < ArchEntryBytes || s.Length % ArchEntryBytes != 0)
            {
                throw new AsmException(0, "corrupt image: the ARCH section is not whole architecture records");
            }

            bool ours = false;

            for (int at = 0; at + 2 <= s.Length; at += ArchEntryBytes)
            {
                ours |= BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(s.Offset + at)) == MachineCorsac;
            }

            if (!ours)
            {
                ushort was = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(s.Offset));

                throw new AsmException(0,
                    $"image is for machine {was}, and this is machine {MachineCorsac} (CORSAC)");
            }
            break;
        }

        if (!foundArch)
        {
            throw new AsmException(0, "corrupt image: it has no 'ARCH' section");
        }

        // WHAT MUST BE THERE, asked before anything is read out of it.
        //
        // The writer marks these required and the compatibility rules say a
        // reader refuses an image missing something required -- but Sections
        // only enforces that for tags it does not KNOW, so a CODE section that
        // had simply gone would come back as an empty array and load as a
        // program with no instructions in it.
        foreach (string must in new[] { "ARCH", "CODE", "DATA", "RELO", "SYMS" })
        {
            if (!table.Any(s => s.Tag == must))
            {
                throw new AsmException(0, $"corrupt image: it has no '{must}' section");
            }
        }

        byte[] codeBytes = Body("CODE");
        byte[] strs = Body("STRS");

        // A CODE SECTION IS WHOLE WORDS. A length that is not a multiple of
        // eight means the section was truncated or is not what it says it is,
        // and rounding down would load all but the last instruction and run it.
        if (codeBytes.Length % 8 != 0)
        {
            throw new AsmException(0,
                $"corrupt image: the code section is {codeBytes.Length} bytes, which is not whole words");
        }

        ulong[] code = new ulong[codeBytes.Length / 8];

        for (int i = 0; i < code.Length; i++)
        {
            code[i] = BinaryPrimitives.ReadUInt64LittleEndian(codeBytes.AsSpan()[(i * 8)..]);
        }

        byte[] relo = Body("RELO");
        List<int> cr = new();
        List<int> dr = new();

        if (relo.Length >= 8)
        {
            int nCode = BinaryPrimitives.ReadInt32LittleEndian(relo.AsSpan());
            int nData = BinaryPrimitives.ReadInt32LittleEndian(relo.AsSpan()[4..]);

            // A COUNT IS CHECKED AGAINST THE SECTION THAT CARRIES IT.
            //
            // The whole point of a section knowing its own length is that a
            // count inside it can be disagreed with. Without this a damaged
            // count asks for entries past the end and comes out as an index
            // out of range with a stack trace through the reader, which says
            // nothing about which file on the disc is broken.
            if (nCode < 0 || nData < 0 || 8L + ((long)nCode + nData) * 4 > relo.Length)
            {
                throw new AsmException(0,
                    $"corrupt image: the relocation section claims {nCode}+{nData} entries "
                  + $"and holds {(relo.Length - 8) / 4}");
            }

            for (int i = 0; i < nCode; i++)
            {
                cr.Add(BinaryPrimitives.ReadInt32LittleEndian(relo.AsSpan()[(8 + i * 4)..]));
            }

            for (int i = 0; i < nData; i++)
            {
                dr.Add(BinaryPrimitives.ReadInt32LittleEndian(relo.AsSpan()[(8 + (nCode + i) * 4)..]));
            }
        }

        string NameAt(int at)
        {
            // AN OFFSET FROM THE FILE IS NOT TRUSTED. It indexes a byte array
            // directly, so a damaged one is an exception out of the middle of
            // the reader rather than a sentence about the image.
            if (at < 0 || at >= strs.Length)
            {
                throw new AsmException(0,
                    $"corrupt image: a symbol name is at offset {at}, and the name section is {strs.Length} bytes");
            }

            int end = at;

            while (end < strs.Length && strs[end] != 0)
            {
                end++;
            }
            return System.Text.Encoding.UTF8.GetString(strs, at, end - at);
        }

        byte[] syms = Body("SYMS");
        List<(string, int)> exports = new();
        List<(string, int)> imports = new();

        if (syms.Length >= 8)
        {
            int nExp = BinaryPrimitives.ReadInt32LittleEndian(syms.AsSpan());
            int nImp = BinaryPrimitives.ReadInt32LittleEndian(syms.AsSpan()[4..]);

            // The same check as RELO, for the same reason. A symbol is eight
            // bytes: an offset into the names and the code word it concerns.
            if (nExp < 0 || nImp < 0 || 8L + ((long)nExp + nImp) * 8 > syms.Length)
            {
                throw new AsmException(0,
                    $"corrupt image: the symbol section claims {nExp}+{nImp} symbols "
                  + $"and holds {(syms.Length - 8) / 8}");
            }

            for (int i = 0; i < nExp; i++)
            {
                exports.Add((NameAt(BinaryPrimitives.ReadInt32LittleEndian(syms.AsSpan()[(8 + i * 8)..])),
                             BinaryPrimitives.ReadInt32LittleEndian(syms.AsSpan()[(12 + i * 8)..])));
            }

            for (int i = 0; i < nImp; i++)
            {
                int at = 8 + (nExp + i) * 8;
                imports.Add((NameAt(BinaryPrimitives.ReadInt32LittleEndian(syms.AsSpan()[at..])),
                             BinaryPrimitives.ReadInt32LittleEndian(syms.AsSpan()[(at + 4)..])));
            }
        }

        byte[] sigs = Body("SIGS");
        long entry = BinaryPrimitives.ReadInt64LittleEndian(bytes[V5EntryAt..]);


        // WHERE IT STARTS, checked against what there is to start.
        //
        // This becomes the processor's reset vector, so a damaged entry does
        // not fail here -- it fails as a machine executing whatever happens to
        // be at an address nobody chose, a long way from the image that caused
        // it. A library has no entry point and says so with zero.
        if (entry < 0 || entry > code.Length)
        {
            throw new AsmException(0,
                $"corrupt image: it starts at word {entry} and has {code.Length} words of code");
        }

        return new Image
        {
            Code = code,
            Data = Body("DATA"),
            Base = BinaryPrimitives.ReadInt64LittleEndian(bytes[V5BaseAt..]),
            Entry = (int)entry,
            Exports = exports,
            Imports = imports,
            Header = sigs.Length == 0 ? "" : System.Text.Encoding.UTF8.GetString(sigs),
            Gir = Body("GIR "),
            Meta = Body("META"),
            LibSlot = BinaryPrimitives.ReadInt32LittleEndian(bytes[V5LibSlotAt..]),
            LibData = BinaryPrimitives.ReadInt32LittleEndian(bytes[V5LibDataAt..]),
            IsLibrary = (BinaryPrimitives.ReadUInt32LittleEndian(bytes[V5FlagsAt..]) & FlagLibrary) != 0,
            LineOf = new int[code.Length],
            CodeLabels = new Dictionary<string, long>(),
            DataLabels = new Dictionary<string, long>(),
            CodeRelocs = cr,
            DataRelocs = dr,
        };
    }

    /// <summary>The tags this toolchain understands. See the spec for what each holds.</summary>
    private static bool Known(string tag) => tag is
        "ARCH" or "CODE" or "DATA" or "RDAT" or "RELO" or
        "SYMS" or "SIGS" or "GIR " or "META" or "LINE" or "STRS";

    /// <summary>
    /// Writes a version-5 image: a header, a section table, and the sections.
    ///
    /// RELO and SYMS carry their own counts rather than leaving them in the
    /// header, so a section is self-describing -- which is what lets a reader
    /// skip one it does not understand instead of losing its place in the file.
    /// </summary>
    public static byte[] Write(Image image)
    {
        return WriteV5(image);
    }

    public static byte[] WriteV5(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte[] names = NameBlob(image, out int[] exportAt, out int[] importAt);
        byte[] sigs = System.Text.Encoding.UTF8.GetBytes(image.Header);

        byte[] code = new byte[image.Code.Length * 8];

        for (int i = 0; i < image.Code.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(code.AsSpan(i * 8), image.Code[i]);
        }

        byte[] relo = new byte[8 + (image.CodeRelocs.Count + image.DataRelocs.Count) * 4];

        BinaryPrimitives.WriteInt32LittleEndian(relo, image.CodeRelocs.Count);
        BinaryPrimitives.WriteInt32LittleEndian(relo.AsSpan(4), image.DataRelocs.Count);

        int r = 8;

        foreach (int one in image.CodeRelocs)
        {
            BinaryPrimitives.WriteInt32LittleEndian(relo.AsSpan(r), one);
            r += 4;
        }

        foreach (int one in image.DataRelocs)
        {
            BinaryPrimitives.WriteInt32LittleEndian(relo.AsSpan(r), one);
            r += 4;
        }

        byte[] syms = new byte[8 + (image.Exports.Count + image.Imports.Count) * 8];

        BinaryPrimitives.WriteInt32LittleEndian(syms, image.Exports.Count);
        BinaryPrimitives.WriteInt32LittleEndian(syms.AsSpan(4), image.Imports.Count);

        int y = 8;

        for (int i = 0; i < image.Exports.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(syms.AsSpan(y), exportAt[i]);
            BinaryPrimitives.WriteInt32LittleEndian(syms.AsSpan(y + 4), image.Exports[i].Item2);
            y += 8;
        }

        for (int i = 0; i < image.Imports.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(syms.AsSpan(y), importAt[i]);
            BinaryPrimitives.WriteInt32LittleEndian(syms.AsSpan(y + 4), image.Imports[i].Item2);
            y += 8;
        }

        // Only sections with something in them. An empty section is a row in
        // the table that means nothing, and a reader that has to tell "absent"
        // from "present but empty" has one more state than it needs.
        List<(string Tag, ushort Flags, byte[] Body)> parts = new()
        {
            ("CODE", SectionRequired, code),
            ("DATA", SectionRequired, image.Data),
            ("RELO", SectionRequired, relo),
            ("SYMS", SectionRequired, syms),
        };

        if (names.Length > 0)
        {
            parts.Add(("STRS", SectionRequired, names));
        }

        if (sigs.Length > 0)
        {
            parts.Add(("SIGS", SectionStrippable, sigs));
        }

        // WHAT PROCESSOR THIS IS FOR, on every image.
        //
        // Written even though there is one architecture, and that is the point
        // of writing it now: the field, the writer, both readers and the
        // kernel's refusal all exist and are exercised while there is still
        // only one answer. A mechanism that first runs on the day a second
        // architecture arrives is a mechanism nobody has ever seen work.
        //
        // Required, so a loader that does not understand architectures cannot
        // quietly run code built for a processor it is not.
        byte[] arch = new byte[ArchEntryBytes];

        BinaryPrimitives.WriteUInt16LittleEndian(arch, MachineCorsac);
        arch[2] = ClassSixtyFour;
        arch[3] = EndianLittle;
        arch[4] = 0;                            // ABI: this system's own
        arch[5] = 0;                            // and its version

        // WAS THE DESCRIPTOR DEPTH IN MASK WORDS, part of the ABI because
        // shared code read a descriptor at a fixed distance in front of the
        // vtable and two images disagreeing about that distance read each
        // other's wrong words. A descriptor is 48 bytes in every image now, so
        // there is no depth to record and nothing to disagree about.
        //
        // Written as zero rather than removed from the header: the field has a
        // fixed position and everything after it would shift, which would make
        // every image already built unreadable to say something no longer worth
        // saying. Zero is what an image from before the field existed says.
        BinaryPrimitives.WriteUInt16LittleEndian(arch.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(arch.AsSpan(8), 0);     // features required
        BinaryPrimitives.WriteInt64LittleEndian(arch.AsSpan(12), image.Entry);
        BinaryPrimitives.WriteUInt32LittleEndian(arch.AsSpan(20), 0);    // flags

        parts.Add(("ARCH", SectionRequired, arch));

        // THE GENERIC TEMPLATES, and they are STRIPPABLE.
        //
        // Briefly marked required, which was wrong and against the rule the
        // spec states: a 300 MB core store is roomy and a small machine is not,
        // so metadata has to be droppable -- GIR, META, SIGS and LINE all are.
        //
        // What the rule demands instead is that using a stripped one fails
        // LOUDLY, and it does: a library with no templates cannot instantiate a
        // generic, and the consumer is told 'List' is not a known type at
        // compile time rather than getting something that runs and is wrong.
        if (image.Gir.Length > 0)
        {
            parts.Add(("GIR ", SectionStrippable, image.Gir));
        }

        // THE TYPE TABLE, so a program can ask what types exist rather than
        // only what type an object it already holds is.
        //
        // Reflection tier 1 works without this -- the descriptor sits in front
        // of every vtable -- but nothing can ENUMERATE types without it, and
        // tiers 2 and 3 add rows to this section rather than inventing another.
        // Strippable for the same reason as the templates.
        if (image.Meta.Length > 0)
        {
            parts.Add(("META", SectionStrippable, image.Meta));
        }

        int tableAt = V5HeaderBytes;
        int body = tableAt + parts.Count * SectionBytes;
        int[] offsets = new int[parts.Count];

        for (int i = 0; i < parts.Count; i++)
        {
            body = (body + 7) & ~7;             // eight-byte aligned, so words load in place
            offsets[i] = body;
            body += parts[i].Body.Length;
        }

        byte[] buf5 = new byte[body];
        Span<byte> h = buf5;

        Magic.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..],  VersionMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], VersionMinor);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], V5HeaderBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], (ushort)parts.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..],  tableAt);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[V5FlagsAt..], image.IsLibrary ? FlagLibrary : 0);
        BinaryPrimitives.WriteInt32LittleEndian(h[V5LibSlotAt..], image.LibSlot);
        BinaryPrimitives.WriteInt64LittleEndian(h[V5EntryAt..],  image.Entry);
        BinaryPrimitives.WriteInt64LittleEndian(h[V5BaseAt..],   image.Base);
        BinaryPrimitives.WriteInt32LittleEndian(h[V5LibDataAt..], image.LibData);

        for (int i = 0; i < parts.Count; i++)
        {
            Span<byte> row = h.Slice(tableAt + i * SectionBytes, SectionBytes);

            System.Text.Encoding.ASCII.GetBytes(parts[i].Tag).CopyTo(row);

            // ONLY THE CODE IS PER-ARCHITECTURE. Strings, data, symbols and
            // generic templates are the same whatever executes them, so they
            // are not duplicated per architecture -- which is the whole reason
            // the architecture is a field on a SECTION rather than a container
            // of whole images.
            BinaryPrimitives.WriteUInt16LittleEndian(row[4..],
                parts[i].Tag == "CODE" ? MachineCorsac : ArchAny);
            BinaryPrimitives.WriteUInt16LittleEndian(row[6..], parts[i].Flags);
            BinaryPrimitives.WriteInt32LittleEndian(row[8..],  offsets[i]);
            BinaryPrimitives.WriteInt32LittleEndian(row[12..], parts[i].Body.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(row[16..], 0);

            parts[i].Body.CopyTo(buf5, offsets[i]);
        }

        return buf5;
    }

    /// <summary>
    /// One blob of NUL-terminated names, and where each entry's name landed in
    /// it. Identical names share a slot, which matters because a program that
    /// calls one library method thirty times imports the same name thirty
    /// times.
    /// </summary>
    private static byte[] NameBlob(Image image, out int[] exportAt, out int[] importAt)
    {
        List<byte> blob = new();
        Dictionary<string, int> seen = new(StringComparer.Ordinal);

        int Intern(string name)
        {
            if (seen.TryGetValue(name, out int found))
            {
                return found;
            }

            int at = blob.Count;

            blob.AddRange(System.Text.Encoding.UTF8.GetBytes(name));
            blob.Add(0);
            seen[name] = at;
            return at;
        }

        exportAt = new int[image.Exports.Count];
        importAt = new int[image.Imports.Count];

        for (int i = 0; i < image.Exports.Count; i++)
        {
            exportAt[i] = Intern(image.Exports[i].Name);
        }

        for (int i = 0; i < image.Imports.Count; i++)
        {
            importAt[i] = Intern(image.Imports[i].Name);
        }

        return blob.ToArray();
    }

    /// <summary>True when the bytes begin with the image magic.</summary>
    public static bool Looks(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 8 && bytes[..8].SequenceEqual(Magic);

    public static Image Read(ReadOnlySpan<byte> bytes)
    {
        return ReadV5(bytes, Sections(bytes));
    }
}
