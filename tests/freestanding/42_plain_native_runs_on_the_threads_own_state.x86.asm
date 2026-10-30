; The i386 start of a freestanding test whose native uses the x87: it leaves
; the native's address in a COR-C# static, as a kernel's entry.asm leaves its
; natives' for Sys.CallNative, and calls in.
.bits 32
.global _start
.global float_one
.extern corc_start
.extern s_Program_Native_t$V$NUInt

_start:
    and esp, -16
    mov dword [s_Program_Native_t$V$NUInt], float_one
    call corc_start
    mov ebx, eax
    mov eax, 1
    int 0x80
    jmp $                       ; the exit does not come back: nothing falls into float_one

; float_one(at): the float 1.0 written at `at`, through the x87.
float_one:
    mov eax, [esp + 4]
    fld1
    fstp dword [eax]
    xor eax, eax
    ret
