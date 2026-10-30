; The i386 stub: absolute addresses, as the kernel's entry.asm writes them.
.bits 32
.global _start
.extern corc_start
.extern s_Program_FromAsm_t$V$I64
_start:
    mov dword [s_Program_FromAsm_t$V$I64], 42
    mov eax, s_Program_FromAsm_t$V$I64
    call corc_start
    hlt
