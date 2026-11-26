; A long-mode image booted by QEMU's multiboot loader: the first byte jumps
; over the multiboot header (a flat image is entered at its first byte), whose
; address fields tell the loader where the image goes and where its .bss ends
; (the a.out kludge, flag 16: QEMU loads no 64-bit ELF itself), so the loader
; copies the file to 1 MiB, zeroes .bss and enters at start32 in 32-bit
; protected mode with paging off. From there as 15_long_mode_switch: CPUID,
; PAE, EFER.LME and paging, and a far jump into 64-bit code, which moves to
; the higher half and calls COR-C#. The page directory maps the first GiB
; with 2 MiB pages, at its own address for the jump and at KERNEL for the
; image and its heap. Until the jump nothing is mapped, so the 32-bit part
; names each label less KERNEL: where it was loaded, not where it was linked.
.equ KERNEL, 0xffffffff80000000

.bits 32
.global _start
.extern corc_start
.extern _end

_start:
    jmp start32

.align 4
multiboot:
    .dd 0x1badb002                  ; magic
    .dd 0x00010000                  ; flags: the address fields below
    .dd 0xe4514ffe                  ; checksum: -(magic + flags)
    .dd multiboot - KERNEL          ; header_addr
    .dd _start - KERNEL             ; load_addr: the file's first byte
    .dd 0                           ; load_end_addr: the whole file
    .dd _end - KERNEL               ; bss_end_addr: zeroed up to here
    .dd start32 - KERNEL            ; entry_addr

start32:
    mov esp, stack_top - KERNEL
    ; The first GiB in 2 MiB pages: entry k maps k * 2 MiB, present,
    ; writable, large (0x83).
    mov edi, pd - KERNEL
    mov eax, 0x83
    mov ecx, 512
fill:
    mov [edi], eax
    mov dword [edi + 4], 0
    add eax, 0x200000
    add edi, 8
    dec ecx
    jnz fill
    mov eax, 0x80000000
    cpuid
    cmp eax, 0x80000001
    jb no_long_mode
    mov eax, 0x80000001
    cpuid
    test edx, 0x20000000
    jz no_long_mode
    mov eax, pml4 - KERNEL
    mov cr3, eax
    ; CR4: PAE, and SSE's state and exceptions the operating system's
    ; (OSFXSR, OSXMMEXCPT): long mode's floats and copies are SSE2, which
    ; raises #UD without them (AMD 24593, 11.3).
    mov eax, cr4
    or eax, 0x620
    mov cr4, eax
    mov ecx, 0xc0000080
    rdmsr
    or eax, 0x100
    wrmsr
    ; CR0: paging and protection, the FPU present (EM clear, MP set).
    mov eax, cr0
    and eax, 0xfffffffb
    or eax, 0x80000003
    mov cr0, eax
    lgdt [gdtr - KERNEL]
    jmp 0x08:long64 - KERNEL
no_long_mode:
    hlt
    jmp no_long_mode

.bits 64
long64:
    mov ax, 0x10
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov rsp, stack_top
    mov rax, higher_half
    jmp rax
higher_half:
    fninit
    call corc_start
halt:
    hlt
    jmp halt

; The tables: one page directory reached twice, at its own address (PML4
; entry 0, PDPT entry 0) and at KERNEL (PML4 entry 511, PDPT entry 510).
.data
.align 4096
pml4:
    .dq pdpt - KERNEL + 3
    .fill 510 * 8
    .dq pdpt - KERNEL + 3
pdpt:
    .dq pd - KERNEL + 3
    .fill 509 * 8
    .dq pd - KERNEL + 3
    .dq 0
pd:
    .fill 512 * 8
gdt:
    .dq 0
    .dq 0x00209a0000000000
    .dq 0x0000920000000000
gdtr:
    .dw 23
    .dd gdt - KERNEL

.bss
.align 16
    .space 65536
stack_top:
