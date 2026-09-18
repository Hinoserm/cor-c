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
            foreach (int bits in new[] { 16, 32 })
            foreach (string instruction in cases)
            {
                byte[] actual = X86Assembler.Assemble(instruction + "\n", "case.asm", bits: bits, cpu: X86Cpu.Parse(["--cpu=k6-3+"])).Bytes;
                string source = Path.Combine(work, "case.s"), obj = Path.Combine(work, "case.o"), binary = Path.Combine(work, "case.bin");
                File.WriteAllText(source, ".intel_syntax noprefix\n.code" + bits + "\n.text\n" + instruction + "\n");
                Tool("as", "--32", "-o", obj, source); Tool("objcopy", "-O", "binary", "-j", ".text", obj, binary);
                byte[] expected = File.ReadAllBytes(binary);
                Check(actual.SequenceEqual(expected), bits + " " + instruction + ": expected " + Convert.ToHexString(expected) + ", got " + Convert.ToHexString(actual));
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
            bool excluded = false;
            try { X86Assembler.Assemble("paddd mm0, mm1", "bad.asm", bits: 32); }
            catch (Corsac.Asm.AsmException) { excluded = true; }
            Check(excluded, "486 accepted MMX");
            Console.WriteLine(checks + " CPU/profile/encoding checks passed; " + work);
            ExecutionTests.Run(work);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error + "\nEvidence: " + work); return 1; }
    }
}
