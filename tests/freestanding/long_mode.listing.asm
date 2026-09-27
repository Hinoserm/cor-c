; Long mode, instruction by instruction: what the assembler writes, as
; objdump reads it back (long_mode.listing.expected). Registers 8-15 in every
; field, SIB with r12/r13, RIP-relative addressing, control registers, the
; system instructions, and long mode's own.
.bits 64
start:
    mov rax, 0x123456789abcdef0
    mov r9, 5
    mov eax, 7
    mov r10d, ebx
    mov rbx, [rsp + 8]
    mov [r12 + r13*4 + 16], rdi
    mov qword [rbp], 42
    mov r15, [rip + data]
    lea rsi, [rip + data]
    add rax, r8
    sub r11, 0x1000
    and ecx, 0xff
    xor r14d, r14d
    cmp qword [rax], 0
    test r9, r9
    inc r12
    dec dword [rbx]
    shl rdx, 3
    push rbp
    push r15
    pop r12
    push 0x10
    call target
    call rax
    call qword [r11 + 8]
    jmp target
    jne start
    mov cr3, rax
    mov rbx, cr0
    mov cr8, r9
    lgdt [rdi]
    lidt [rip + data]
    ltr ax
    wrmsr
    rdmsr
    swapgs
    iretq
    sysretq
    syscall
    pushfq
    popfq
    cdqe
    cqo
    rep stosq
    movsxd rax, dword [rcx]
    movzx r8d, byte [rsi]
    xchg rax, r13
    mov sil, 1
    mov byte [rdi], r10b
    int 0x83
    hlt
    retfq
    ret
target:
    ret
data:
    .dq target
