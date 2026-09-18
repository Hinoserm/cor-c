# x86 target permissions

`X86Cpu` is the single CPU/profile parser used by both compiler and linker.
The compiler references this model through the linker project. Instruction
selection, register allocation and encoders remain under `compiler/src/arch/x86`.
