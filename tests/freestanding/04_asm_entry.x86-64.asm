; The long-mode stub: a static written RIP-relative with a trailing
; immediate, the stack aligned the way the System V ABI wants it at a call.
.bits 64
.global _start
.extern corc_start
.extern s_Program_FromAsm
.extern s_Program_StubStack

_start:
    and rsp, -16
    mov qword [rip + s_Program_FromAsm], 42
    mov [rip + s_Program_StubStack], rsp
    call corc_start
    mov edi, eax
    mov eax, 60
    syscall
