# Calling C

A COR-C# program calls into C the way a .NET one does:

```csharp
using System.Runtime.InteropServices;

static class C
{
    [DllImport("libc.so.6")] public static extern nint strlen(string s);
    [DllImport("m", EntryPoint = "pow")] public static extern double Power(double x, double y);
    [DllImport("libc.so.6", SetLastError = true)] public static extern int open(string path, int flags);
}
```

`[LibraryImport]` is read the same way. Both targets' own calling
conventions are C's -- cdecl on i386, System V AMD64 on x86-64 -- so the
call is an ordinary call to the C symbol through the PLT.

## What goes across

| COR-C#                             | C                                          |
|------------------------------------|--------------------------------------------|
| integers, `char`, enums, `float`, `double` | as they are                        |
| `nint`/`IntPtr`, pointers          | as they are                                |
| `bool`                             | a 32-bit int, 0 or 1 (Win32's `BOOL`, .NET's default) |
| `string`                           | a NUL-terminated copy: UTF-16 (`LPWSTR`) for `CharSet.Unicode`, `StringMarshalling.Utf16`, `CharSet.Auto` on Windows and `[MarshalAs(UnmanagedType.LPWStr)]`; Windows-1252 for `CharSet.Ansi` (the default) and `LPStr` on Windows; UTF-8 (`const char *`) otherwise and for `LPUTF8Str` |
| an array of numbers                | the address of its first element           |
| `ref`/`out` of a number            | the variable's address                     |
| a `bool` result                    | nonzero is true; `[return: MarshalAs(UnmanagedType.I1)]` reads a byte |
| a narrow integer result            | narrowed again: C leaves the upper bits undefined |

Anything else -- a class, a struct by value, a string result -- is refused
with a message. A pointer C returns is read with `Marshal`:
`PtrToStringUTF8`, `ReadInt32` and the rest, `AllocHGlobal`/`FreeHGlobal`
(the C library's `malloc` and `free` when one is loaded),
`StringToHGlobalUTF8`, `Copy`.

`SetLastError = true` reads errno after the call; `Marshal.GetLastPInvokeError`
answers it, per thread.

## Function pointers and callbacks

C# 9's function pointers: `delegate* unmanaged<int, int, int>` is C's,
`delegate*<...>` a managed one, `&Method` a static method's address, and a
call through either is written as a call. A method C calls back is marked
`[UnmanagedCallersOnly]`:

```csharp
[UnmanagedCallersOnly]
static int Compare(nint a, nint b) => Marshal.ReadInt32(a) - Marshal.ReadInt32(b);

[DllImport("libc.so.6")]
static extern void qsort(int[] items, nint count, nint size, delegate* unmanaged<nint, nint, int> compare);

qsort(items, items.Length, 4, &Compare);
```

An exception that escapes such a method ends the process, as it does in
.NET: its handler chain is emptied for the length of the call, so nothing
is thrown across the C frames beneath it.

## What happens around a call

- **The collector.** For the length of the call the thread counts as
  stopped (`Runtime.EnterNative`/`LeaveNative`), as it does when it waits in
  a system call: C does not allocate from this heap, so a collection does
  not wait for it. Its callee-saved registers were copied into its block as
  it said so, and C preserves them; every object C was handed a pointer
  into is kept alive until the call returns.
- **The thread pointer.** On x86-64 the runtime's thread block is in GS and
  the C library's in FS, and nothing moves. On i386 both use GS; the kernel
  keeps one base per thread per GDT slot, so each is a selector that means
  the right thing in every thread, and a native call swaps them.
- **The call itself.** A variadic C function (printf) finds the vector
  argument count in AL on x86-64, and i386 aligns the stack to sixteen,
  which GCC's code may assume and this compiler's own calls do not keep.

## Linking and startup

An object records the C libraries its imports name (`.corsac.native`); the
link resolves each to the name the library calls itself -- `c` and `libc`
find `libc.so.6`, as .NET's probing does -- in the system's library
directories for the image's class, and makes the program a dynamic one.

When a C library is loaded:

- the program is entered through `__libc_start_main`, with the loader's
  finaliser, as every C program is: that is what gives the C library its
  environment, its standard streams and its program name, and what runs the
  libraries' destructors at exit;
- a new thread is made by `pthread_create`, so C code on it has its own
  errno and allocator caches;
- the program ends through `exit(3)`, which flushes what `printf` buffered.

The runtime finds these (and `malloc` for `Marshal`) in the loaded images by
their GNU or SysV hash tables (`Runtime.NativeSymbol`).

## Later: both formats in one CORSAC program

NOT BUILT YET. On a CORSAC machine a native program may be an ELF image or a
PE image, and either may import ELF shared objects, PE DLLs, or both at once:
its container says nothing about what it may call. Today only a PE image
imports a DLL (`--windows`, docs/WIN32-TARGET.md); an ELF image's
`[DllImport("user32.dll")]` is refused at its link, the name looked for as a
C library in the build host's directories (`Linker.ResolveNative`).

What it takes:

- ONE CORSAC NATIVE TARGET, TWO CONTAINERS. The same runtime, the same code,
  the same system calls; ELF or PE is only the file written. A pure ELF
  image stays a Linux program; one naming a DLL is CORSAC's alone.
- AN IMPORT'S ABI IS ITS LIBRARY'S, never the program's. A call into a
  `.dll` is Windows's -- stdcall, fastcall or thiscall on i386, rcx, rdx,
  r8, r9 and 32 bytes of shadow space on x86-64 -- and a call into a `.so`
  the System V ABI's, chosen per `[DllImport]` by the library it names.
  The two differ where data silently goes wrong: `long` is 32 bits on 64-bit
  Windows and 64 on Linux, `wchar_t` 2 bytes and 4, a `double` in a struct
  8-aligned and 4-aligned on i386, `long double` 8 bytes and 12 or 16. The
  marshalling follows the library too.
- FOREIGN IMPORTS IN EITHER CONTAINER: an ELF image's DT_NEEDED naming a
  DLL, a PE image's import directory naming a shared object, neither
  needing the library on the build host. COR-C#'s own libraries alike: a
  PE program may link libcorsacrt.so and the libSystem images, and a
  library's exports are its C# public surface in either format
  (docs/SEPARATE-COMPILATION.md, "Exports").
- NOTHING UNWINDS ACROSS. A COR-C# exception never unwinds through a DLL's
  frames or a C library's, as it does not through a C callback's now; what
  `malloc` gave goes back to `free`, what `HeapAlloc` gave to `HeapFree`.
- THE LOADER (the CORSAC kernel's): both formats mapped into one process,
  each import bound by its library's format, every image's initialisers --
  DllMain, DT_INIT -- run in the order they need each other, and both
  thread blocks set up: the TEB at FS and thread storage at GS on i386,
  swapped on x86-64.

The order: the per-import ABI; foreign imports in both linkers; a contract
for the loader; then a test program for each combination -- ELF calling a
DLL, PE calling a shared object, one calling both -- run on CORSAC.

Already shaped for it: a library's exports are read by its own format, not
the program's (Driver, the providers' exports); an executable's link judges
the imports a COR-C# library must answer, and leaves every [DllImport] name
-- a C library's or a DLL's, each object lists the names it calls as each
call binds it, a symbol or a Windows image's `dll!export`, recorded where
lowering makes the call (`.corsac.nativesymbols`) -- to the loader
(Linker.ExportsAtStart).

Open: what a CORSAC machine's libc is (glibc, musl or its own), which says
what the loader must give a C library a PE image imports.

## Tests

`tests/language/700_native_calls.cor`, `701_native_callbacks.cor` and
`702_native_threads.cor`, on both targets.
