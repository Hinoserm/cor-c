; A long-mode stub reaching a higher-half static three ways.
.bits 64
.global _start
.extern corc_start
.extern s_Program_FromAsm
_start:
    mov qword [s_Program_FromAsm], 42
    mov rax, s_Program_FromAsm
    lea rbx, [rip + s_Program_FromAsm]
    call corc_start
    hlt
