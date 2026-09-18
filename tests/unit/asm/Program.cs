#nullable enable
using System.Diagnostics;
using System.Text;
using Corsac.Asm;
using AsmError = Corsac.Asm.AsmException;

// The x86-16 assembler's encodings, byte for byte.
//
// Every expected byte string below was written from the Intel manual's opcode
// tables and then checked by disassembling it with `objdump -D -b binary -m
// i8086`, which is the point: if the expected bytes came out of this assembler
// the test would only prove that it is consistent with itself, which it would
// be even if it were wrong. objdump is somebody else's decoder and it does not
// share a single line of code or a single assumption with this one.
//
// The harness runs objdump again at the end, over the whole corpus, and fails
// on any byte sequence it calls "(bad)" -- the cheap catch for an encoding
// nobody wrote an expectation for.
//
// Run from anywhere: `dotnet run --project compiler/tests/asm`.
// Exit code is the number of failed checks.

namespace Corsac.Tests.Asm;

internal static class Program
{
    private static int _failures;
    private static int _checks;

    private static int Main()
    {
        Sixteen();
        Addressing();
        Jumps();
        ThirtyTwo();
        _collect = false;
        Directives();
        Expressions();
        Sector();
        _collect = true;
        Errors();
        ObjdumpSweep();

        Console.WriteLine(_failures == 0
            ? $"asm: {_checks} checks passed"
            : $"asm: {_failures} of {_checks} checks FAILED");
        return _failures;
    }

    // ---- the corpus --------------------------------------------------------

    /// <summary>Every case that has run, kept so objdump can be pointed at the lot.</summary>
    private static readonly List<(string Source, byte[] Bytes, int Bits)> Corpus = new();

    /// <summary>
    /// Whether the cases being run are instructions. Data directives are not,
    /// and handing bytes that were never code to a disassembler only proves
    /// that a disassembler will decode anything.
    /// </summary>
    private static bool _collect = true;

    private static void Is(string source, string expected, int bits = 16)
    {
        _checks++;
        byte[] want = Hex(expected);
        try
        {
            string text = bits == 32 ? ".bits 32\n" + source : source;
            X86Assembler.Result got = X86Assembler.Assemble(".base 0\n" + text + "\n", "case.asm");
            if (_collect)
            {
                Corpus.Add((source, got.Bytes, bits));
            }

            if (got.Bytes.AsSpan().SequenceEqual(want))
            {
                return;
            }
            _failures++;
            Console.Error.WriteLine($"FAIL {Show(source)}\n  want {Hex(want)}\n  got  {Hex(got.Bytes)}");
        }
        catch (AsmError e)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL {Show(source)}\n  {e}");
        }
    }

    /// <summary>A source that has to be rejected, and the words the message has to contain.</summary>
    private static void Rejects(string source, string because)
    {
        _checks++;
        try
        {
            X86Assembler.Assemble(".base 0\n" + source + "\n", "case.asm");
        }
        catch (AsmError e)
        {
            if (e.Message.Contains(because, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _failures++;
            Console.Error.WriteLine($"FAIL {Show(source)}\n  rejected, but for the wrong reason: {e}");
            return;
        }
        _failures++;
        Console.Error.WriteLine($"FAIL {Show(source)}\n  was accepted; it should have been rejected ({because})");
    }

    private static string Show(string source) => source.Replace("\n", " | ");

    private static byte[] Hex(string text)
    {
        List<byte> bytes = new();
        foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bytes.Add(Convert.ToByte(part, 16));
        }
        return bytes.ToArray();
    }

    private static string Hex(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("x2")));

    // ---- 16-bit real mode --------------------------------------------------

    private static void Sixteen()
    {
        // mov, in every shape a loader needs.
        Is("mov ax, 0x1234",        "b8 34 12");
        Is("mov al, 0x55",          "b0 55");
        Is("mov bx, ax",            "89 c3");
        Is("mov ah, bl",            "88 dc");
        Is("mov ds, ax",            "8e d8");
        Is("mov ax, es",            "8c c0");
        Is("mov ss, bx",            "8e d3");
        Is("mov word [0x500], 0x10","c7 06 00 05 10 00");
        Is("mov byte [bx+si+4], 7", "c6 40 04 07");
        Is("mov dx, [bp-2]",        "8b 56 fe");
        Is("mov [es:di], al",       "26 88 05");

        // The control registers: always 32 bits wide, and so encoded with no
        // operand-size prefix even here in 16-bit code.
        Is("mov eax, cr0",          "0f 20 c0");
        Is("mov cr0, eax",          "0f 22 c0");
        Is("mov ebx, cr3",          "0f 20 db");
        Is("mov cr3, ebx",          "0f 22 db");

        Is("lea si, [bx+di+0x20]",  "8d 71 20");

        // The arithmetic group, including the sign-extended 0x83 short form.
        Is("add ax, 5",             "83 c0 05");
        Is("add ax, 0x1234",        "05 34 12");
        Is("add al, 3",             "04 03");
        Is("add [bx], cx",          "01 0f");
        Is("add cx, [bx]",          "03 0f");
        Is("or byte [si], 0x80",    "80 0c 80");
        Is("adc cx, dx",            "11 d1");
        Is("sbb ah, bl",            "18 dc");
        Is("and eax, -16",          "66 83 e0 f0");
        Is("sub bx, cx",            "29 cb");
        Is("xor ax, ax",            "31 c0");
        Is("cmp dl, 0x2a",          "80 fa 2a");
        Is("cmp ax, 0x1234",        "3d 34 12");

        Is("test al, 1",            "a8 01");
        Is("test ax, bx",           "85 d8");
        Is("test byte [bx], 0x40",  "f6 07 40");

        Is("inc ax",                "40");
        Is("dec si",                "4e");
        Is("inc byte [bx]",         "fe 07");
        Is("dec word [si]",         "ff 0c");

        Is("shl ax, 1",             "d1 e0");
        Is("shr bx, 4",             "c1 eb 04");
        Is("sar dx, cl",            "d3 fa");
        Is("rol al, 1",             "d0 c0");
        Is("ror word [si], 3",      "c1 0c 03");
        Is("shl bx, cl",            "d3 e3");

        Is("mul bx",                "f7 e3");
        Is("div byte [bx]",         "f6 37");
        Is("imul cx",               "f7 e9");
        Is("idiv si",               "f7 fe");
        Is("neg ax",                "f7 d8");
        Is("not cx",                "f7 d1");

        Is("push ax",               "50");
        Is("push ds",               "1e");
        Is("push cs",               "0e");
        Is("push es",               "06");
        Is("push ss",               "16");
        Is("push 0x10",             "6a 10");
        Is("push word 0x1234",      "68 34 12");
        Is("push word [bx]",        "ff 37");
        Is("pop bx",                "5b");
        Is("pop es",                "07");
        Is("pop ds",                "1f");
        Is("pop word [si]",         "8f 04");

        Is("xchg ax, bx",           "93");
        Is("xchg al, [bx]",         "86 07");
        Is("xchg cx, dx",           "87 d1");

        Is("cbw",                   "98");
        Is("cwd",                   "99");
        Is("cld",                   "fc");
        Is("std",                   "fd");
        Is("cli",                   "fa");
        Is("sti",                   "fb");
        Is("hlt",                   "f4");
        Is("nop",                   "90");
        Is("clc",                   "f8");
        Is("stc",                   "f9");

        Is("in al, 0x92",           "e4 92");
        Is("in ax, dx",             "ed");
        Is("in al, dx",             "ec");
        Is("out 0xf4, al",          "e6 f4");
        Is("out dx, al",            "ee");
        Is("out dx, ax",            "ef");

        Is("lodsb",                 "ac");
        Is("lodsw",                 "ad");
        Is("stosb",                 "aa");
        Is("stosw",                 "ab");
        Is("movsb",                 "a4");
        Is("movsw",                 "a5");
        Is("rep movsb",             "f3 a4");
        Is("rep stosw",             "f3 ab");
        Is("insb",                  "6c");
        Is("rep insw",              "f3 6d");
        Is("insd",                  "66 6d");
        Is("outsb",                 "6e");
        Is("rep outsw",             "f3 6f");
        Is("repne scasb",           "f2 ae");

        Is("int 0x13",              "cd 13");
        Is("int3",                  "cc");
        Is("iret",                  "cf");
        Is("ret",                   "c3");
        Is("ret 4",                 "c2 04 00");
        Is("retf",                  "cb");
        Is("retf 8",                "ca 08 00");

        // lgdt and lidt take a six-byte limit-and-base pair and carry no
        // operand-size prefix in 16-bit code.
        Is("lgdt [0x800]",          "0f 01 16 00 08");
        Is("lidt [0x806]",          "0f 01 1e 06 08");
        Is("lgdt [bx]",             "0f 01 17");
    }

    // ---- ModRM and SIB -----------------------------------------------------

    private static void Addressing()
    {
        // All four 16-bit base+index forms, then each single register, then the
        // displacement-only case that has to borrow r/m 110.
        Is("mov al, [bx+si]",       "8a 00");
        Is("mov al, [bx+di]",       "8a 01");
        Is("mov al, [bp+si]",       "8a 02");
        Is("mov al, [bp+di]",       "8a 03");
        Is("mov al, [si]",          "8a 04");
        Is("mov al, [di]",          "8a 05");
        Is("mov al, [bx]",          "8a 07");
        Is("mov al, [0x1234]",      "8a 06 34 12");
        Is("mov al, [dword 0x12345678]", "67 8a 05 78 56 34 12");

        // [bp] alone cannot be spelled with no displacement, so it becomes
        // [bp+0] with a one-byte zero.
        Is("mov al, [bp]",          "8a 46 00");
        Is("mov al, [bp+0x10]",     "8a 46 10");
        Is("mov al, [bp+0x100]",    "8a 86 00 01");
        Is("mov al, [bx+si-1]",     "8a 40 ff");

        // Segment overrides.
        Is("mov al, [es:bx]",       "26 8a 07");
        Is("mov al, [cs:si]",       "2e 8a 04");
        Is("mov al, [ss:di]",       "36 8a 05");
        Is("mov al, [ds:bx]",       "3e 8a 07");
        Is("mov al, [fs:bx]",       "64 8a 07");
        Is("mov al, [gs:bx]",       "65 8a 07");

        // 32-bit addressing inside 16-bit code: the 0x67 prefix, SIB, scales,
        // and esp's enforced SIB byte.
        Is("mov al, [eax]",         "67 8a 00");
        Is("mov al, [esp]",         "67 8a 04 24");
        Is("mov al, [ebp]",         "67 8a 45 00");
        Is("mov al, [esi+edi]",     "67 8a 04 3e");
        Is("mov al, [esi+edi*8]",   "67 8a 04 fe");
        Is("mov al, [ebx+ecx*4+0x40]", "67 8a 44 8b 40");
        Is("mov al, [edx*2+0x1000]", "67 8a 04 55 00 10 00 00");
    }

    // ---- jumps and calls ---------------------------------------------------

    private static void Jumps()
    {
        // A backward jump is short when it reaches; a forward one is near,
        // because pass one sized it without knowing where the target is.
        Is("here:\njmp here",       "eb fe");
        Is("here:\njmp short here", "eb fe");
        Is("here:\njmp near here",  "e9 fd ff");
        Is("jmp there\nthere:",     "e9 00 00");
        Is("jmp short there\nthere:", "eb 00");

        Is("here:\nje here",        "74 fe");
        Is("here:\njne here",       "75 fe");
        Is("here:\njz here",        "74 fe");
        Is("here:\njnz here",       "75 fe");
        Is("here:\njc here",        "72 fe");
        Is("here:\njnc here",       "73 fe");
        Is("here:\njb here",        "72 fe");
        Is("here:\njae here",       "73 fe");
        Is("here:\njbe here",       "76 fe");
        Is("here:\nja here",        "77 fe");
        Is("here:\njs here",        "78 fe");
        Is("here:\njns here",       "79 fe");
        Is("here:\njp here",        "7a fe");
        Is("here:\njnp here",       "7b fe");
        Is("here:\njl here",        "7c fe");
        Is("here:\njge here",       "7d fe");
        Is("here:\njle here",       "7e fe");
        Is("here:\njg here",        "7f fe");
        Is("here:\njo here",        "70 fe");
        Is("here:\njno here",       "71 fe");
        Is("je there\nthere:",      "0f 84 00 00");
        Is("here:\nje near here",   "0f 84 fc ff");

        Is("here:\nloop here",      "e2 fe");
        Is("here:\nloope here",     "e1 fe");
        Is("here:\nloopne here",    "e0 fe");
        Is("here:\njcxz here",      "e3 fe");

        Is("call there\nthere:",    "e8 00 00");
        Is("call ax",               "ff d0");
        Is("call word [bx]",        "ff 17");
        Is("jmp ax",                "ff e0");
        Is("jmp word [bx+si]",      "ff 20");

        // The far jump that leaves real mode. The `dword` says the offset is
        // 32 bits, which in 16-bit code needs the 0x66 in front.
        Is("jmp 0x07c0:0x0005",     "ea 05 00 c0 07");
        Is("jmp dword 0x08:0x10000", "66 ea 00 00 01 00 08 00");
        Is("call 0x07c0:0x0005",    "9a 05 00 c0 07");
    }

    // ---- .bits 32 ----------------------------------------------------------

    private static void ThirtyTwo()
    {
        // The same table, with the prefixes the other way round: a 16-bit
        // operand now needs 0x66 and a 16-bit address now needs 0x67.
        Is("mov ax, 0x10",          "66 b8 10 00", 32);
        Is("mov esp, 0xfff0",       "bc f0 ff 00 00", 32);
        Is("mov ds, ax",            "8e d8", 32);
        Is("mov eax, [ebx+esi*4+0x100]", "8b 84 b3 00 01 00 00", 32);
        Is("mov [ebp-8], ecx",      "89 4d f8", 32);
        Is("mov edx, [0x400]",      "8b 15 00 04 00 00", 32);
        Is("mov bx, [si]",          "66 67 8b 1c", 32);
        Is("mov eax, [bx+si]",      "67 8b 00", 32);
        Is("add esp, 16",           "83 c4 10", 32);
        Is("and esp, -16",          "83 e4 f0", 32);
        Is("sub eax, eax",          "29 c0", 32);
        Is("or edx, ecx",           "09 ca", 32);
        Is("cmp eax, 0x1234",       "3d 34 12 00 00", 32);
        Is("shl eax, 2",            "c1 e0 02", 32);
        Is("lea edi, [eax+eax*2]",  "8d 3c 40", 32);
        Is("mov dx, 0x3f8",         "66 ba f8 03", 32);
        Is("out dx, al",            "ee", 32);
        Is("push eax",              "50", 32);
        Is("pop ebx",               "5b", 32);
        Is("cwde",                  "98", 32);
        Is("cdq",                   "99", 32);
        Is("movsd",                 "a5", 32);
        Is("stosd",                 "ab", 32);
        Is("movsw",                 "66 a5", 32);
        Is("here:\njmp here",       "eb fe", 32);
        Is("jmp there\nthere:",     "e9 00 00 00 00", 32);
        Is("call there\nthere:",    "e8 00 00 00 00", 32);
        Is("je there\nthere:",      "0f 84 00 00 00 00", 32);
        Is("cli",                   "fa", 32);
        Is("hlt",                   "f4", 32);
    }

    // ---- directives --------------------------------------------------------

    private static void Directives()
    {
        Is(".db 1, 2, 3",                 "01 02 03");
        Is(".db \"AB\", 0",               "41 42 00");
        Is(".dw 0x1234, 0x5678",          "34 12 78 56");
        Is(".dd 0x11223344",              "44 33 22 11");
        Is(".ascii \"S2 OK\"",            "53 32 20 4f 4b");
        Is(".asciz \"hi\"",               "68 69 00");
        Is(".db 0\n.align 4",             "00 00 00 00");
        Is(".db 1\n.align 4, 0x90",       "01 90 90 90");
        Is(".fill 3, 0xcc",               "cc cc cc");
        Is(".fill 2",                     "00 00");
        Is(".times 4 .db 0x5a",           "5a 5a 5a 5a");
        Is(".times 2 nop",                "90 90");
        Is(".times 3 .dw 0x0102",         "02 01 02 01 02 01");
        Is(".equ PORT, 0x3f8\nmov dx, PORT", "ba f8 03");
        Is(".equ N, 2\n.times N .db 7",   "07 07");

        // .base moves the addresses labels get without moving the bytes.
        _checks++;
        X86Assembler.Result based = X86Assembler.Assemble(".base 0x7c00\n.entry start\nstart:\nhlt\n", "case.asm");
        if (based.Base != 0x7C00 || based.Entry != 0x7C00 || based.Bytes.Length != 1 || based.Bytes[0] != 0xF4)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL .base/.entry: base {based.Base:x} entry {based.Entry:x} {Hex(based.Bytes)}");
        }

        // An included file is spliced in where it is named, and its labels are
        // the same labels.
        _checks++;
        string dir = Path.Combine(Path.GetTempPath(), "corsac-asmtests");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "inc.asm"), ".equ MAGIC, 0xaa55\n");
        File.WriteAllText(Path.Combine(dir, "main.asm"), ".base 0\n.include \"inc.asm\"\n.dw MAGIC\n");
        X86Assembler.Result inc = X86Assembler.AssembleFile(Path.Combine(dir, "main.asm"));
        if (!inc.Bytes.AsSpan().SequenceEqual(Hex("55 aa")))
        {
            _failures++;
            Console.Error.WriteLine($"FAIL .include: {Hex(inc.Bytes)}");
        }
    }

    // ---- expressions -------------------------------------------------------

    private static void Expressions()
    {
        Is(".dw 1 + 2 * 3",         "07 00");
        Is(".dw (1 + 2) * 3",       "09 00");
        Is(".dw 0x10 | 0x01",       "11 00");
        Is(".dw 0xff & 0x0f",       "0f 00");
        Is(".dw 0xf0 ^ 0x0f",       "ff 00");
        Is(".dw 1 << 8",            "00 01");
        Is(".dw 0x100 >> 4",        "10 00");
        Is(".dw ~0",                "ff ff");
        Is(".dw -1",                "ff ff");
        Is(".dw 0b1010",            "0a 00");
        Is(".db 'A'",               "41");
        Is(".db '\\n'",             "0a");
        Is(".dw 100 / 7",           "0e 00");
        Is(".dw 100 % 7",           "02 00");

        // $ is where this statement begins and $$ where the section does, which
        // is what makes the padding idioms readable rather than a byte count.
        Is(".dw $",                 "00 00");
        Is("nop\n.dw $",            "90 01 00");
        Is("nop\n.dw $ - $$",       "90 01 00");
        Is("nop\n.fill 4 - ($ - $$), 0xcc", "90 cc cc cc");
    }

    // ---- a whole sector ----------------------------------------------------

    private static void Sector()
    {
        // The boot-sector round trip: a 512-byte image whose tail is computed
        // by the assembler rather than counted by hand, with the signature in
        // the last two bytes. This is the shape every MBR has, and it exercises
        // $, $$, .fill and .base together.
        string source = """
            .base 0x7c00
            .entry start
            start:
                    cli
                    xor     ax, ax
                    mov     ds, ax
                    mov     es, ax
                    mov     ss, ax
                    mov     sp, 0x7c00
                    sti
                    mov     si, message
                    call    print
            stop:
                    hlt
                    jmp     stop
            print:
                    lodsb
                    test    al, al
                    jz      done
                    mov     ah, 0x0e
                    mov     bx, 7
                    int     0x10
                    jmp     print
            done:
                    ret
            message:
                    .asciz  "boot"
                    .fill   510 - ($ - $$), 0
                    .dw     0xaa55
            """;

        _checks++;
        X86Assembler.Result r = X86Assembler.Assemble(source, "sector.asm");

        if (r.Bytes.Length != 512)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL sector: {r.Bytes.Length} bytes, not 512");
        }
        else if (r.Bytes[510] != 0x55 || r.Bytes[511] != 0xAA)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL sector: signature is {r.Bytes[510]:x2} {r.Bytes[511]:x2}");
        }
        else if (r.Entry != 0x7C00)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL sector: entry is {r.Entry:x}, not 7c00");
        }

        // Assembling it twice has to produce the same bytes: the whole point of
        // replaying pass one's encoding choices rather than making them again.
        _checks++;
        X86Assembler.Result again = X86Assembler.Assemble(source, "sector.asm");
        if (!again.Bytes.AsSpan().SequenceEqual(r.Bytes))
        {
            _failures++;
            Console.Error.WriteLine("FAIL sector: two assemblies of the same source disagree");
        }
    }

    // ---- what has to be refused --------------------------------------------

    private static void Errors()
    {
        // A short jump that does not reach is the error this assembler exists
        // to report clearly, because the alternative is a boot sector that
        // jumps into the middle of itself.
        Rejects(".times 200 nop\nhere:\n.times 200 nop\njmp short here", "short jump reaches");
        Rejects("jmp short there\n.times 200 nop\nthere:", "short jump reaches");

        Rejects("mov [bx], 1", "operand size");
        Rejects("mov ax, bl", "bits");
        Rejects("frobnicate ax", "unknown instruction");
        Rejects(".frob 1", "unknown directive");
        Rejects("mov al, [ax]", "16-bit addressing allows only");
        Rejects("mov al, [bx+bp]", "base+index");
        Rejects("mov al, [si*2]", "scales an index");
        Rejects("pop cs", "cs cannot be popped");
        Rejects("mov ax, 0x12345", "does not fit");
        Rejects(".dw 1\n.fill 0 - ($ - $$), 0", "too long");
        Rejects("here:\nhere:", "already defined");
        Rejects("mov ax, nowhere", "not a number, an equate, or a known label");
        Rejects(".bits 64", "16-bit and 32-bit");
        Rejects("mov eax, [eax+ebx*3]", "only 1, 2, 4 and 8");
    }

    // ---- the independent decoder -------------------------------------------

    /// <summary>
    /// Hands the whole corpus back to objdump and fails on anything it cannot
    /// decode. The per-case expectations above are the real oracle; this is the
    /// net underneath them, and it costs one process.
    /// </summary>
    private static void ObjdumpSweep()
    {
        string dir = Path.Combine(Path.GetTempPath(), "corsac-asmtests");
        Directory.CreateDirectory(dir);

        foreach (int bits in new[] { 16, 32 })
        {
            List<byte> blob = new();
            foreach ((_, byte[] bytes, int b) in Corpus)
            {
                if (b != bits)
                {
                    continue;
                }
                blob.AddRange(bytes);
                // A one-byte instruction between cases stops a truncated
                // decode in one case from running into the next.
                blob.Add(0x90);
            }

            string path = Path.Combine(dir, $"corpus{bits}.bin");
            File.WriteAllBytes(path, blob.ToArray());

            _checks++;
            string? output = Objdump(path, bits);
            if (output is null)
            {
                Console.Error.WriteLine("asm: objdump is not installed; the independent decode was skipped");
                return;
            }

            int bad = output.Split('\n').Count(l => l.Contains("(bad)"));
            if (bad != 0)
            {
                _failures++;
                Console.Error.WriteLine($"FAIL objdump: {bad} byte sequences in the {bits}-bit corpus do not decode");
                foreach (string line in output.Split('\n').Where(l => l.Contains("(bad)")).Take(10))
                {
                    Console.Error.WriteLine("  " + line.TrimEnd());
                }
            }
        }
    }

    private static string? Objdump(string path, int bits)
    {
        try
        {
            ProcessStartInfo info = new("objdump")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("-D");
            info.ArgumentList.Add("-b");
            info.ArgumentList.Add("binary");
            info.ArgumentList.Add("-m");
            info.ArgumentList.Add(bits == 16 ? "i8086" : "i386");
            info.ArgumentList.Add(path);

            using Process? p = Process.Start(info);
            if (p is null)
            {
                return null;
            }
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            // objdump not being installed is not a test failure; it is a
            // weaker test run, and the line above says so.
            return null;
        }
    }
}
