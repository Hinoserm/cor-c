; The i386 stub: the same, with absolute addresses.
.bits 32
.global _start
.extern corc_start
.extern s_Program_FromAsm
.extern s_Program_StubStack

_start:
    and esp, -16
    mov dword [s_Program_FromAsm], 42
    mov dword [s_Program_FromAsm + 4], 0
    mov [s_Program_StubStack], esp
    mov dword [s_Program_StubStack + 4], 0
    call corc_start
    mov ebx, eax
    mov eax, 1
    int 0x80
