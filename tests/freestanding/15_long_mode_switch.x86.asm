; The i386 kernel's entry: the loader's protected mode is the kernel's own,
; so there is no mode to switch to, only a stack to set up.
.bits 32
.global _start
.extern corc_start

_start:
    mov esp, stack_top
    call corc_start
halt:
    hlt
    jmp halt

.bss
.align 16
    .space 16384
stack_top:
