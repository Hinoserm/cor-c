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
| `string`                           | a NUL-terminated UTF-8 copy (`const char *`) |
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

## Tests

`tests/language/700_native_calls.cor`, `701_native_callbacks.cor` and
`702_native_threads.cor`, on both targets.
