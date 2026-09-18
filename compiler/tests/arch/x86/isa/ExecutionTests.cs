using System.Diagnostics;
using Corsac.Asm;
using Corsac.Lang.X86;

namespace Corsac.Tests.Isa;

public static class ExecutionTests
{
    public static int Run(string work)
    {
        // A private boot sector executes exactly the bytes produced by our assembler.
        // Debug-exit carries the failing case number; 1 means all checks completed.
        string source = """
            .base 0x7c00
            cli
            xor ax, ax
            mov ds, ax
            mov es, ax
            mov ss, ax
            mov sp, 0x7c00
            mov eax, cr0
            and eax, 0xfffffff3
            mov cr0, eax
            finit
            mov bx, 1
            fld1
            fld1
            faddp st1, st0
            fistp dword [0x600]
            cmp dword [0x600], 2
            jne failed
            mov bx, 2
            mov eax, 0x7fff0080
            movd mm0, eax
            mov eax, 0x00010080
            movd mm1, eax
            paddsw mm0, mm1
            movd eax, mm0
            cmp eax, 0x7fff0100
            jne failed
            mov bx, 3
            pcmpeqd mm2, mm2
            psllq mm2, 32
            movq qword [0x608], mm2
            cmp dword [0x608], 0
            jne failed
            cmp dword [0x60c], -1
            jne failed
            mov bx, 4
            emms
            fld1
            fistp dword [0x600]
            cmp dword [0x600], 1
            jne failed
            mov bx, 5
            mov eax, 3
            movd mm0, eax
            pi2fd mm0, mm0
            pfadd mm0, mm0
            pf2id mm0, mm0
            movd eax, mm0
            cmp eax, 6
            jne failed
            mov bx, 6
            mov dword [0x608], 7
            mov dword [0x60c], 11
            movq mm0, qword [0x608]
            pswapd mm0, mm0
            movd eax, mm0
            cmp eax, 11
            jne failed
            psrlq mm0, 32
            movd eax, mm0
            cmp eax, 7
            jne failed
            mov bx, 7
            prefetch [0x608]
            prefetchw [0x608]
            femms
            fld1
            fistp dword [0x600]
            cmp dword [0x600], 1
            jne failed
            xor bx, bx
            failed:
            mov ax, bx
            mov dx, 0xf4
            out dx, ax
            halt:
            hlt
            jmp halt
            """;
        File.WriteAllText(Path.Combine(work, "execution.asm"), source);
        byte[] bytes = X86Assembler.Assemble(source, "execution.asm", bits: 16,
            cpu: X86Cpu.Parse(["--cpu=k6-3+"])).Bytes;
        if (bytes.Length > 510) throw new Exception("Execution fixture exceeds boot-sector payload");
        byte[] disk = new byte[1440 * 1024];
        bytes.CopyTo(disk, 0); disk[510] = 0x55; disk[511] = 0xaa;
        string image = Path.Combine(work, "execution.img"); File.WriteAllBytes(image, disk);
        ProcessStartInfo start = new("qemu-system-i386")
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-machine", "pc", "-accel", "tcg", "-cpu", "athlon",
            "-m", "16", "-display", "none", "-monitor", "none", "-serial", "none", "-nic", "none",
            "-no-reboot", "-boot", "a", "-drive", "file=" + image + ",format=raw,if=floppy,readonly=on",
            "-device", "isa-debug-exit,iobase=0xf4,iosize=0x04" }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true); process.WaitForExit();
            throw new Exception("TCG instruction execution timed out; " + errors.Result);
        }
        File.WriteAllText(Path.Combine(work, "execution.log"), output.Result + errors.Result);
        if (process.ExitCode != 1)
            throw new Exception("TCG instruction execution failed, debug-exit=" + process.ExitCode + "; " + errors.Result);
        Console.WriteLine("7 x87/MMX/3DNow execution checks passed under QEMU TCG (Athlon model; not a K6 timing model)");
        return 7;
    }
}
