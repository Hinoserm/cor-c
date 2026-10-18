using System.Diagnostics;
using Corsac.Asm;
using Corsac.Lang.X86;

namespace Corsac.Tests.Isa;

public static class Program
{
    public static int Main()
    {
        string work = Path.Combine(Path.GetTempPath(), "corc-isa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Tool(string tool, params string[] arguments)
        {
            ProcessStartInfo start = new(tool) { UseShellExecute = false, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            string errors = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception(tool + ": " + errors);
        }
        try
        {
            X86Cpu defaults = X86Cpu.Parse([]);
            Check(defaults.Name == "486" && defaults.Fpu == "x87" && !defaults.Mmx, "default profile changed");
            Check(X86Cpu.Parse(["--cpu=386"]).Fpu == "none", "386 default coprocessor");
            Check(X86Cpu.Parse(["--cpu", "386", "--fpu", "387"]).Fpu == "387", "optional 387");
            Check(!X86Cpu.Parse(["--cpu=k6-3+", "--fpu=none"]).Mmx, "no-FPU leaked MMX");
            Check(X86Cpu.Parse(["--cpu=k6"]).Mmx && !X86Cpu.Parse(["--cpu=k6"]).ThreeDNow, "K6 feature matrix");
            Check(X86Cpu.Parse(["--cpu=k6-3+"]).ThreeDNowExtended, "plus extensions missing");
            Check(!X86Cpu.Parse(["--cpu=k6-3+", "--disable-mmx"]).ThreeDNow, "dependent disable");
            Check(!X86Cpu.Parse(["--cpu=486", "--tune=k6-3+"]).Mmx, "tuning enabled instructions");
            foreach (var (alias, model) in new[] { ("i386", "386"), ("i486", "486"), ("586", "pentium"), ("i586", "pentium"),
                ("pentium_mmx", "pentium-mmx"), ("686", "686"), ("i686", "686"), ("pentium-pro", "686"), ("pentium2", "pentium2"),
                ("pentium-ii", "pentium2"), ("pentium3", "pentium3"), ("pentium-iii", "pentium3"), ("pentium4", "pentium4"),
                ("k6-iii", "k6-3"), ("k6-2+", "k6-2+"), ("k6-iii+", "k6-3+"), ("athlon", "athlon"), ("k7", "athlon"),
                ("athlon-tbird", "athlon"), ("athlon-xp", "athlon-xp"), ("athlon-4", "athlon-xp"), ("athlon-mp", "athlon-xp"),
                ("athlon64", "athlon64") })
            {
                X86Cpu parsed = X86Cpu.Parse(["--cpu", alias]);
                Check(parsed.Name == model && parsed.Features == X86Cpu.Model(model), alias + " is not " + model);
                Check(X86Cpu.Parse(parsed.Contract.Arguments()).Features == parsed.Features, alias + " did not round-trip");
            }
            bool longModeRefused = false;
            try { X86Cpu.Parse(["--cpu=k8"]); } catch (ArgumentException) { longModeRefused = true; }
            Check(longModeRefused, "k8 parsed as a 32-bit profile");
            X86Cpu pentiumPro = X86Cpu.Parse(["--cpu=pentium-pro"]), athlon = X86Cpu.Parse(["--cpu=athlon"]);
            Check(pentiumPro.Has(X86Feature.Cmov | X86Feature.FloatCmov) && !pentiumPro.Mmx, "Pentium Pro feature matrix");
            Check(athlon.Has(X86Feature.ThreeDNowExtended | X86Feature.MmxExtensions | X86Feature.Cmov) && !athlon.Has(X86Feature.Sse), "Athlon feature matrix");
            Check(!X86Cpu.Parse(["--cpu=pentium4", "--disable-sse"]).Has(X86Feature.Sse2), "SSE2 outlived SSE");
            Check(!X86Cpu.Parse(["--cpu=athlon", "--disable-cmov"]).Has(X86Feature.FloatCmov), "FCOMI outlived CMOV");
            Check(!X86Cpu.Parse(["--cpu=athlon64", "--disable-mmx"]).Has(X86Feature.MmxExtensions), "MMX extensions outlived MMX");
            Check(X86Cpu.Parse(["--cpu=pentium3", "--fpu=none"]).Features == (X86Feature.I486 | X86Feature.Pentium | X86Feature.Cmov), "no-FPU leaked x87 state");
            Check(!X86Cpu.Parse(["--cpu=k6-2", "--tune=athlon64"]).Has(X86Feature.Cmov), "tuning enabled CMOV");
            Check(X86Cpu.Parse(["--cpu=486", "--enable-sse2"]).Has(X86Feature.Sse | X86Feature.Sse2), "SSE2 enabled without SSE");
            string[] mmx = "packsswb packssdw packuswb paddb paddw paddd paddsb paddsw paddusb paddusw pand pandn por pxor pcmpeqb pcmpeqw pcmpeqd pcmpgtb pcmpgtw pcmpgtd pmaddwd pmulhw pmullw psllw pslld psllq psraw psrad psrlw psrld psrlq psubb psubw psubd psubsb psubsw psubusb psubusw punpckhbw punpckhwd punpckhdq punpcklbw punpcklwd punpckldq".Split(' ');
            string[] now = "pi2fd pf2id pfacc pfadd pfsub pfsubr pfmul pfcmpeq pfcmpge pfcmpgt pfmax pfmin pfrcp pfrcpit1 pfrcpit2 pfrsqrt pfrsqit1 pmulhrw pavgusb pf2iw pi2fw pfnacc pfpnacc pswapd".Split(' ');
            List<string> cases = ["emms", "femms", "cpuid", "rdtsc", "rdmsr", "wrmsr", "rsm", "cmpxchg8b qword ptr [ebx]", "prefetch [ebx]", "prefetchw [ebx]",
                "movd mm0, eax", "movd eax, mm7", "movq mm1, mm7", "movq qword ptr [ebx+ecx*4+16], mm2", "movq mm2, qword ptr [ebx+ecx*4+16]",
                "movd mm3, dword ptr [ebx]", "movd dword ptr [ebx], mm3"];
            foreach (string mnemonic in mmx.Concat(now))
            {
                cases.Add(mnemonic + " mm0, mm7");
                string width = mnemonic is "punpcklbw" or "punpcklwd" or "punpckldq" ? "dword" : "qword";
                cases.Add(mnemonic + " mm5, " + width + " ptr [ebx+ecx*4+16]");
            }
            foreach (string mnemonic in "psllw pslld psllq psraw psrad psrlw psrld psrlq".Split(' ')) cases.Add(mnemonic + " mm3, 255");
            cases.AddRange("f2xm1 fabs fchs fnclex fcompp fcos fdecstp fincstp fninit fld1 fldl2t fldl2e fldpi fldlg2 fldln2 fldz fnop fpatan fprem fprem1 fptan frndint fscale fsin fsincos fsqrt ftst fucompp fxam fxtract fyl2x fyl2xp1 finit fclex fwait".Split(' '));
            foreach (string mnemonic in "fadd fmul fsub fsubr fdiv fdivr".Split(' '))
            {
                cases.Add(mnemonic + " st(0), st(3)");
                cases.Add(mnemonic + " st(3), st(0)");
                cases.Add(mnemonic + "p st(3), st(0)");
                cases.Add(mnemonic + " dword ptr [ebx+ecx*4+16]");
                cases.Add(mnemonic + " qword ptr [ebx]");
            }
            foreach (string mnemonic in "fiadd fimul fisub fisubr fidiv fidivr ficom ficomp".Split(' '))
                foreach (string width in new[] { "word", "dword" }) cases.Add(mnemonic + " " + width + " ptr [ebx]");
            foreach (string mnemonic in "fld fst fstp fxch ffree fcom fcomp fucom fucomp".Split(' ')) cases.Add(mnemonic + " st(4)");
            cases.AddRange(["fld tbyte ptr [ebx]", "fstp tbyte ptr [ebx]", "fild qword ptr [ebx]", "fistp qword ptr [ebx]",
                "fbld tbyte ptr [ebx]", "fbstp tbyte ptr [ebx]", "fldcw word ptr [ebx]", "fstcw word ptr [ebx]",
                "fnstsw ax", "fstsw ax", "fldenv [ebx]", "fnstenv [ebx]", "fsave [ebx]", "frstor [ebx]",
                "mov eax, dr0", "mov dr7, eax"]);
            cases.AddRange(["bswap eax", "invd", "wbinvd", "invlpg [ebx]", "xadd al, bl", "xadd word ptr [ebx], cx",
                "cmpxchg dword ptr [ebx], ecx", "lock xadd dword ptr [ebx], eax", "lock cmpxchg8b qword ptr [ebx]",
                "lock add dword ptr [ebx], 1", "lock xchg eax, dword ptr [ebx]"]);
            foreach (string mnemonic in new[] { "bt", "bts", "btr", "btc" })
            {
                cases.Add(mnemonic + " dword ptr [ebx], ecx");
                cases.Add(mnemonic + " ax, 15");
                if (mnemonic != "bt") cases.Add("lock " + mnemonic + " dword ptr [ebx], 7");
            }
            foreach (string mnemonic in new[] { "bsf", "bsr" }) cases.Add(mnemonic + " eax, dword ptr [ebx]");
            foreach (string mnemonic in new[] { "shld", "shrd" })
            { cases.Add(mnemonic + " eax, ecx, 13"); cases.Add(mnemonic + " word ptr [ebx], cx, cl"); }
            // The sixth generation's additions, assembled for the Athlon, which
            // has every one: CMOVcc under each spelling of each condition at
            // both widths and from a SIB operand, the x87 moves and flag-setting
            // compares in each of their operand forms, and the streaming
            // prefetches and store fence.
            List<string> sixth = ["sfence", "cmovne edi, dword ptr [esp+8]", "cmovl si, di", "prefetchnta [esi]"];
            foreach (string condition in "o no b c nae ae nb nc e z ne nz be na a nbe s ns p pe np po l nge ge nl le ng g nle".Split(' '))
            {
                sixth.Add("cmov" + condition + " eax, ecx");
                sixth.Add("cmov" + condition + " ax, word ptr [ebx+ecx*4+16]");
            }
            foreach (string mnemonic in "fcmovb fcmovnae fcmove fcmovbe fcmovna fcmovu fcmovnb fcmovae fcmovne fcmovnbe fcmova fcmovnu".Split(' '))
                sixth.Add(mnemonic + " st(0), st(3)");
            sixth.Add("fcmovnu st, st(7)");
            foreach (string mnemonic in "fcomi fcomip fucomi fucomip".Split(' '))
            { sixth.Add(mnemonic + " st(0), st(3)"); sixth.Add(mnemonic + " st(5)"); sixth.Add(mnemonic); }
            foreach (string mnemonic in "prefetchnta prefetcht0 prefetcht1 prefetcht2".Split(' '))
                sixth.Add(mnemonic + " byte ptr [ebx+ecx*4+16]");
            foreach (int bits in new[] { 16, 32 })
            foreach ((string instruction, string profile) in cases.Select(c => (c, "k6-3+")).Concat(sixth.Select(c => (c, "athlon"))))
            {
                byte[] actual = X86Assembler.Assemble(instruction + "\n", "case.asm", bits: bits, cpu: X86Cpu.Parse(["--cpu=" + profile])).Bytes;
                string source = Path.Combine(work, "case.s"), obj = Path.Combine(work, "case.o"), binary = Path.Combine(work, "case.bin");
                File.WriteAllText(source, ".intel_syntax noprefix\n.code" + bits + "\n.text\n" + instruction + "\n");
                Tool("as", "--32", "-o", obj, source); Tool("objcopy", "-O", "binary", "-j", ".text", obj, binary);
                byte[] expected = File.ReadAllBytes(binary);
                // LOCK and operand/address-size prefixes commute; GNU and our assembler choose
                // different legal orders when both are required. Preserve all other bytes.
                static byte[] PrefixOrder(byte[] bytes)
                {
                    byte[] normalized = (byte[])bytes.Clone();
                    int count = 0;
                    while (count < normalized.Length && normalized[count] is 0x66 or 0x67 or 0xf0) count++;
                    if (normalized.Take(count).Distinct().Count() != count) throw new Exception("Duplicate instruction prefix in encoding fixture");
                    Array.Sort(normalized, 0, count);
                    return normalized;
                }
                Check(PrefixOrder(actual).SequenceEqual(PrefixOrder(expected)), bits + " " + instruction + ": expected " + Convert.ToHexString(expected) + ", got " + Convert.ToHexString(actual));
            }
            foreach (string invalid in new[] { "paddd eax, ebx", "paddd mm0, dword ptr [ebx]", "movd mm0, mm1", "movq mm0, eax", "psllq mm0, 256", "cmpxchg8b eax", "prefetch eax",
                "lock cmpxchg8b eax", "lock add eax, ebx", "lock mov dword ptr [ebx], eax", "lock nop", "lock lock add dword ptr [ebx], eax",
                "bswap ax", "xadd ax, ebx", "cmpxchg dword ptr [ebx], cx", "invlpg eax" })
            {
                bool rejected = false;
                try { X86Assembler.Assemble(invalid, "bad.asm", bits: 32, cpu: X86Cpu.Parse(["--cpu=k6-3+"])); }
                catch (Corsac.Asm.AsmException) { rejected = true; }
                Check(rejected, "invalid operands accepted: " + invalid);
            }
            foreach (string invalid in new[] { "cmove eax, 5", "cmove al, bl", "cmove eax, cx", "cmove ax, dword ptr [ebx]", "cmove dword ptr [ebx], eax",
                "cmove eax", "fcomi eax", "fcomi st(1), st(2)", "fucomip qword ptr [ebx]", "fcmovb st(3)", "fcmovb st(1), st(0)", "fcmove eax, ebx",
                "prefetchnta eax", "prefetcht0 mm0", "prefetcht2", "sfence eax" })
            {
                bool rejected = false;
                try { X86Assembler.Assemble(invalid, "bad.asm", bits: 32, cpu: athlon); }
                catch (Corsac.Asm.AsmException) { rejected = true; }
                Check(rejected, "invalid operands accepted: " + invalid);
            }
            bool excluded = false;
            try { X86Assembler.Assemble("paddd mm0, mm1", "bad.asm", bits: 32); }
            catch (Corsac.Asm.AsmException) { excluded = true; }
            Check(excluded, "486 accepted MMX");
            // `.runtime mmx`: code reached only after a CPUID check assembles
            // MMX on a plain Pentium, and `.runtime none` ends the allowance.
            byte[] guarded = X86Assembler.Assemble(".runtime mmx\npaddsw mm0, mm1\nemms\n.runtime none\n", "guarded.asm",
                bits: 32, cpu: X86Cpu.Parse(["--cpu=pentium"])).Bytes;
            Check(guarded.Length == 5 && guarded[0] == 0x0F && guarded[1] == 0xED && guarded[3] == 0x0F && guarded[4] == 0x77,
                ".runtime mmx did not assemble paddsw and emms");
            bool ended = false;
            try { X86Assembler.Assemble(".runtime mmx\n.runtime none\npaddsw mm0, mm1\n", "ended.asm", bits: 32, cpu: X86Cpu.Parse(["--cpu=pentium"])); }
            catch (Corsac.Asm.AsmException) { ended = true; }
            Check(ended, ".runtime none did not end the MMX allowance");
            // `.runtime cmov` and `.runtime sse` do the same for a 486 or a
            // Pentium build's checked routines, and a list names several.
            byte[] moved = X86Assembler.Assemble(".runtime cmov\ncmovl eax, ebx\nfcomip st(1)\n.runtime sse\nprefetchnta [ebx]\nsfence\n.runtime none\n",
                "moved.asm", bits: 32).Bytes;
            Check(Convert.ToHexString(moved) == "0F4CC3DFF10F18030FAEF8", ".runtime cmov/sse: " + Convert.ToHexString(moved));
            byte[] both = X86Assembler.Assemble(".runtime mmx, cmov\npaddsw mm0, mm1\ncmove eax, ecx\nemms\n", "both.asm",
                bits: 32, cpu: X86Cpu.Parse(["--cpu=pentium"])).Bytes;
            Check(Convert.ToHexString(both) == "0FEDC10F44C10F77", ".runtime mmx, cmov: " + Convert.ToHexString(both));
            foreach (string replaced in new[] { ".runtime cmov\n.runtime mmx\ncmove eax, ecx\n", ".runtime sse\n.runtime none\nsfence\n",
                ".runtime cmov\nprefetcht0 [ebx]\n", ".runtime avx\n", ".runtime none, cmov\n", ".runtime\n" })
            {
                bool refused = false;
                try { X86Assembler.Assemble(replaced, "replaced.asm", bits: 32, cpu: X86Cpu.Parse(["--cpu=pentium"])); }
                catch (Corsac.Asm.AsmException) { refused = true; }
                Check(refused, ".runtime accepted " + replaced.Replace('\n', ';'));
            }
            // IMUL's three shapes, hand-encoded: one operand (F7 /5), two
            // (0F AF /r), three with a byte or a dword immediate (6B, 69).
            byte[] imul = X86Assembler.Assemble("imul ecx\nimul edx, ecx\nimul eax, [esp + 20]\nimul eax, ebx, 10\nimul eax, 1000\n",
                "imul.asm", bits: 32).Bytes;
            byte[] want = { 0xF7, 0xE9, 0x0F, 0xAF, 0xD1, 0x0F, 0xAF, 0x44, 0x24, 0x14, 0x6B, 0xC3, 0x0A, 0x69, 0xC0, 0xE8, 0x03, 0x00, 0x00 };
            Check(imul.Length == want.Length && imul.AsSpan().SequenceEqual(want), "imul's forms: " + Convert.ToHexString(imul));
            foreach (var forbidden in new[] {
                ("386", "bswap eax"), ("386", "xadd eax, ebx"), ("386", "invlpg [eax]"),
                ("486", "rdtsc"), ("486", "cmpxchg8b qword [eax]"), ("pentium", "emms"),
                ("k6", "femms"), ("k6-2", "pswapd mm0, mm1"), ("686", "emms"), ("pentium4", "femms"),
                ("k6-2", "cmove eax, ecx"), ("k6-3+", "fcomi st(1)"), ("pentium-mmx", "fcmovb st(0), st(1)"), ("486", "cmovl eax, ebx"),
                ("pentium2", "prefetchnta [ebx]"), ("pentium2", "sfence"), ("686", "prefetcht0 [ebx]"), ("k6-3+", "sfence"),
                ("pentium2", "pswapd mm0, mm1"), ("pentium3", "femms"),
                ("6x86mx", "fcmovb st(0), st(1)"), ("6x86mx", "fcomi st(1)"), ("6x86mx", "femms"), ("6x86mx", "sfence") })
            {
                bool rejected = false;
                try { X86Assembler.Assemble(forbidden.Item2, "profile.asm", bits: 32, cpu: X86Cpu.Parse(["--cpu=" + forbidden.Item1])); }
                catch (Corsac.Asm.AsmException) { rejected = true; }
                Check(rejected, forbidden.Item1 + " accepted " + forbidden.Item2);
            }
            foreach (string forbidden in new[] { "fld1", "fadd st0, st1", "emms", "femms" })
            {
                bool rejected = false;
                try { X86Assembler.Assemble(forbidden, "no-fpu.asm", bits: 32, cpu: X86Cpu.Parse(["--cpu=k6-3+", "--fpu=none"])); }
                catch (Corsac.Asm.AsmException) { rejected = true; }
                Check(rejected, "no-FPU accepted " + forbidden);
            }
            foreach (string forbidden in new[] { "fcomi st(1)", "fcmovb st(0), st(1)", "prefetchnta [ebx]", "sfence" })
            {
                bool rejected = false;
                try { X86Assembler.Assemble(forbidden, "no-fpu.asm", bits: 32, cpu: X86Cpu.Parse(["--cpu=athlon", "--fpu=none"])); }
                catch (Corsac.Asm.AsmException) { rejected = true; }
                Check(rejected, "no-FPU Athlon accepted " + forbidden);
            }
            Check(X86Assembler.Assemble("cmove eax, ecx", "no-fpu.asm", bits: 32, cpu: X86Cpu.Parse(["--cpu=athlon", "--fpu=none"])).Bytes.Length == 3,
                "no-FPU Athlon refused CMOV");
            foreach (var allowed in new[] { ("686", "fcmovu st(0), st(2)"), ("pentium2", "cmovg eax, ebx"), ("pentium2", "fucomi st(1)"),
                ("pentium3", "prefetchnta [ebx]"), ("pentium3", "sfence"), ("6x86mx", "cmove eax, ecx"), ("6x86mx", "emms") })
                Check(X86Assembler.Assemble(allowed.Item2, "profile.asm", bits: 32, cpu: X86Cpu.Parse(["--cpu=" + allowed.Item1])).Bytes.Length > 0,
                    allowed.Item1 + " refused " + allowed.Item2);
            // An object carries its profile, and the link refuses one whose
            // instructions the selected target lacks: the Pentium III's prefetch
            // on a Pentium II, the Athlon's 3DNow! on a Pentium III, CMOV on a K6,
            // FCOMI on a Pentium Pro given only a 387, the Pentium III's SSE on
            // an Athlon that has none (the Athlon XP has it).
            foreach (var (built, target, accepted) in new[] { ("pentium3", "pentium2", false), ("athlon", "pentium3", false), ("686", "k6-3+", false),
                ("686", "686 --fpu=387", false), ("pentium3", "athlon", false), ("pentium3", "athlon-xp", true), ("k6-3+", "athlon", true), ("pentium2", "pentium3", true),
                ("686 --fpu=387", "686", true) })
            {
                Corsac.Lang.Ir.ObjectFile carrier = X86Assembler.Assemble("nop", "carrier.asm", bits: 32, asObject: true, cpu: X86Cpu.Parse(("--cpu=" + built).Split(' '))).Object!;
                bool linked = true;
                try { Corsac.Lang.Ir.X86CodeGenerationContract.ValidateTarget([("carrier.o", carrier)], X86Cpu.Parse(("--cpu=" + target).Split(' ')).Contract); }
                catch (Corsac.Lang.Elf.ElfFormatException) { linked = false; }
                Check(linked == accepted, built + " object " + (accepted ? "refused by " : "accepted by ") + target);
            }
            Console.WriteLine(checks + " CPU/profile/encoding checks passed; " + work);
            ExecutionTests.Run(work);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error + "\nEvidence: " + work); return 1; }
    }
}
