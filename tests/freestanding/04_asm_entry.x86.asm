; The i386 stub: the same, with absolute addresses.
.bits 32
.global _start
.extern corc_start
.extern s_Program_FromAsm_t$V$I64
.extern s_Program_StubStack_t$V$I64

_start:
    and esp, -16
    mov dword [s_Program_FromAsm_t$V$I64], 42
    mov dword [s_Program_FromAsm_t$V$I64 + 4], 0
    mov [s_Program_StubStack_t$V$I64], esp
    mov dword [s_Program_StubStack_t$V$I64 + 4], 0
    call corc_start
    mov ebx, eax
    mov eax, 1
    int 0x80
