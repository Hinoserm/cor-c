.bits 32
.text
.global _start
.extern managed_entry
.extern __bss_start
.extern _end
_start:
    cli
    cld
    mov edi, __bss_start
    mov ecx, _end
    sub ecx, edi
    xor eax, eax
    rep stosb
    mov esp, 0x90000
    call managed_entry
halt:
    hlt
    jmp halt
