; From a loader's protected mode into long mode, as one assembly: the
; loader -- multiboot's, or a stage 2 that hands over the same way -- enters
; in 32-bit protected mode with paging off, so the stub checks the
; processor, turns on PAE, EFER.LME and paging, and far-jumps into its own
; 64-bit part, which moves to the higher half and calls COR-C#. Until the
; jump nothing is mapped, so the 32-bit part names each label less KERNEL:
; where it was loaded, not where it was linked.
.equ KERNEL, 0xffffffff80000000

.bits 32
.global _start
.global enable_long_mode
.extern corc_start

_start:
    mov esp, stack_top - KERNEL
    call enable_long_mode
    lgdt [gdtr - KERNEL]
    jmp 0x08:long64 - KERNEL

; CPUID's extended leaf 0x80000001 says whether there is long mode at all
; (EDX bit 29), on a processor whose highest extended leaf (0x80000000's
; EAX) reaches it; then the page tables, CR4.PAE, EFER.LME and CR0.PG, in
; the order the processor requires.
enable_long_mode:
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
    mov eax, cr4
    or eax, 0x20
    mov cr4, eax
    mov ecx, 0xc0000080
    rdmsr
    or eax, 0x100
    wrmsr
    mov eax, cr0
    or eax, 0x80000001
    mov cr0, eax
    ret
no_long_mode:
    hlt
    jmp no_long_mode

; Still at the loaded address, now in long mode: the data segments, then
; the stack and the code where they were linked.
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
    call corc_start
halt:
    hlt
    jmp halt

; One page directory of a 2 MiB page at the bottom of memory, reached twice:
; at its own address for the jump, and at KERNEL (PML4 entry 511, PDPT
; entry 510) for everything after.
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
    .dq 0x83
    .fill 511 * 8
gdt:
    .dq 0
    .dq 0x00209a0000000000
    .dq 0x0000920000000000
gdtr:
    .dw 23
    .dd gdt - KERNEL

.bss
.align 16
    .space 16384
stack_top:
