; The long-mode stub: a static written RIP-relative with a trailing
; immediate, the stack aligned the way the System V ABI wants it at a call.
.bits 64
.global _start
.extern corc_start
.extern s_Program_FromAsm_t$V$I64
.extern s_Program_StubStack_t$V$I64

_start:
    and rsp, -16
    mov qword [rip + s_Program_FromAsm_t$V$I64], 42
    mov [rip + s_Program_StubStack_t$V$I64], rsp
    call corc_start
    mov edi, eax
    mov eax, 60
    syscall
