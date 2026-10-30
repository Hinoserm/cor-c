; A long-mode stub reaching a higher-half static three ways.
.bits 64
.global _start
.extern corc_start
.extern s_Program_FromAsm_t$V$I64
_start:
    mov qword [s_Program_FromAsm_t$V$I64], 42
    mov rax, s_Program_FromAsm_t$V$I64
    lea rbx, [rip + s_Program_FromAsm_t$V$I64]
    call corc_start
    hlt
