; An i386 image booted by QEMU's multiboot loader: the first byte jumps over
; the multiboot header, whose address fields (the a.out kludge, flag 16: a
; flat image is no ELF) tell the loader where the image goes and where its
; .bss ends; it copies the file to 1 MiB, zeroes .bss and enters at start32
; in 32-bit protected mode with paging off -- the kernel's own mode, so only
; a stack is set up before COR-C# is called.
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
    .dd multiboot                   ; header_addr
    .dd _start                      ; load_addr: the file's first byte
    .dd 0                           ; load_end_addr: the whole file
    .dd _end                        ; bss_end_addr: zeroed up to here
    .dd start32                     ; entry_addr

start32:
    mov esp, stack_top
    ; The FPU present and in its initial state (EM and TS clear, MP set): the
    ; loader says nothing of CR0 but protection.
    mov eax, cr0
    and eax, 0xfffffff3
    or eax, 2
    mov cr0, eax
    fninit
    call corc_start
halt:
    hlt
    jmp halt

.bss
.align 16
    .space 65536
stack_top:
