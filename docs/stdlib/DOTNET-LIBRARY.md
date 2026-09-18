# The class library is .NET's

Status: the contract for everything under `lib/` that a program sees,
and for every program under `os/bin`. The core rule is stated in
`X86-BACKEND.md`; this file records what exists, what is stubbed and
where the seams are.

## The rule

Programs are ordinary .NET C# programs. Our platform mirrors C# and
.NET 1:1 -- namespaces flattened, but every class, method, overload and
behaviour is the one the .NET reference documents. The goal is that
nearly any existing C# source compiles to native code through COR-C#
and runs, unchanged, on Linux or on CORSAC/OS. Consequences:

- We never invent an API where .NET has one. A class that is not a
  .NET class is rewritten to the .NET one, not wrapped by it.
- Programs make no operating-system calls of their own. `Os` in
  `lib/sys/linux.cor` (and its CORSAC twin) is the platform seam the
  library is built on; a program that names `Os`, `Fd`, `O`, `Stat`,
  `Errno` or `Args` is wrong.
- The compiler is never changed to suit a library or a program. When it
  rejects normal C#, the compiler is fixed.
- Our own classes exist only for what .NET has no API for at all, and
  each is named here.

## What exists

| File | .NET namespace | Contents |
|---|---|---|
| `lib/std.cor` | System, System.Collections.Generic, System.Linq, System.Text | string, StringBuilder, List, Dictionary, Queue, Math, Convert, BitConverter, Random, DateTime, TimeSpan, Path, Array, Enumerable, Char, Rune, the encodings, the exceptions |
| `lib/io.cor` | System.IO | Stream, FileStream, MemoryStream, TextReader/Writer, StreamReader/Writer, File, Directory, FileSystemInfo, FileInfo, DirectoryInfo, FileAttributes, FileMode/Access, SeekOrigin |
| `lib/console.cor` | System | Console (In/Out/Error, Write/WriteLine, ReadLine, ReadKey, KeyAvailable, CancelKeyPress, IsInputRedirected, OpenStandard*) |
| `lib/environment.cor` | System | Environment (GetCommandLineArgs, GetEnvironmentVariable(s), CurrentDirectory, Exit, ExitCode, ProcessId, UserName, NewLine) |
| `lib/net.cor` | System.Net, System.Net.Sockets | IPAddress, IPEndPoint, Dns, Socket, TcpClient, TcpListener, NetworkStream, the enums |
| `lib/security.cor` | System.Security.Cryptography | RandomNumberGenerator |
| `lib/signals.cor` | System.Runtime.InteropServices | PosixSignal, PosixSignalRegistration, PosixSignalContext |
| `lib/process.cor` | System.Diagnostics | Process, ProcessStartInfo |
| `lib/threading.cor`, `lib/threading-linux.cor` | System.Threading, System.Threading.Tasks | Task, the scheduler, Thread, Mutex |
| `lib/regex.cor` | System.Text.RegularExpressions | Regex, Match (in progress) |
| `lib/collections.cor` | System.Collections(.Generic, .ObjectModel) | Stack, LinkedList(+Node), SortedList, SortedDictionary, PriorityQueue, BitArray, ReadOnlyCollection, ReadOnlyDictionary |
| `lib/io-streams.cor` | System.IO | StringReader/Writer, BufferedStream, BinaryReader/Writer, EndOfStreamException, InvalidDataException |
| `lib/time.cor` | System, System.Diagnostics | DateOnly, TimeOnly, Stopwatch |
| `lib/values.cor` | System | Guid, Version, Lazy, Tuple (2-4) and Tuple.Create |
| `lib/numerics.cor` | System.Numerics | BigInteger (the static-method half; C# spells the rest with operators) |

The compiler links these by default for every Linux program
(`compiler/Driver.cs`, `DefaultLibraries`); `tests/lang/run.sh` names
the same list.

## Ours, because .NET has nothing

- `Terminal` (`lib/console.cor`): raw mode and its restoration, wait for
  a byte, window size. .NET's Console does this privately inside
  ReadKey; a remote shell needs it for the whole session.
- `UnixProcess` (`lib/unix.cor`, in progress): fork, execve, dup2, pipe,
  process groups and sessions, the controlling terminal's foreground
  group, waitpid's status, setuid. A shell, init, login and agetty need
  them; every other program uses Process.
- `FileSystemInfo.OwnerId/GroupId/LinkCount/Inode/Blocks`: five
  properties .NET leaves to P/Invoke and `ls -l` cannot do without.
- Everything under `lib/sys/` and `lib/rt/`: the platform seam and the
  runtime. Not for programs.

## Behaviour worth knowing

- Console.Out is line-buffered on a terminal and fully buffered when
  redirected, as in .NET; it is flushed when Main returns and by
  Environment.Exit.
- Signals: the kernel's handler only records which signal arrived. The
  program's PosixSignalRegistration handlers run at the next quiet
  moment -- a poll interrupted by the signal, a console read -- which is
  the discipline .NET keeps (its handlers run off the signal frame). A
  handler that does not set Cancel gets the default action afterwards.
- Dns resolves numeric addresses, localhost and /etc/hosts. A resolver
  that asks a server is the day the network stack grows one.
- Local time is UTC: the machine keeps no zone.
- Signals delivered by the CORSAC kernel, clock_nanosleep and the RTC
  are being added on the kernel side; until then `sleep`, `date` and
  Ctrl+C misbehave there and only there.

## Stubs and gaps

- Stream.ReadAsync/WriteAsync complete synchronously.
- IPv6 is not spoken.
- `String.FromInt`, `FromDouble`, `FromBool`, `FromByte`, `FromChars` and
  `String.Repeat` are OURS: .NET has no such members, and the library was
  written on them before `ToString` existed here. They stay until nothing
  names them.
- `Array.Resize` is declared as .NET declares it and cannot be CALLED: see
  compiler bug 7 below.
- `Enumerable.FirstOrDefault`, `LastOrDefault`, `SingleOrDefault`,
  `ElementAtOrDefault` and `Array.Find`/`FindLast` return the right thing for
  a reference type and RUBBISH for a value type, because of compiler bug 8.
- `String.Format` with a DATE argument and a format after the colon faults:
  compiler bug 10. Every other argument type works.
- `Math` and `Enumerable` cannot be extended from another file: only a type
  declared in the PRELUDE may be reopened, and a second declaration of a type
  the library already has is a duplicate. Anything they lack is added there.
- `Random()` with no seed is xoshiro256**, as .NET's is, but its state cannot
  be unpredictable: nothing below `lib/std.cor` has entropy, so the seed is a
  constant stirred by a counter. `Random(seed)` reproduces .NET's sequence
  number for number. `NextInt64` is the one member whose sequence has not been
  checked against .NET's.
- `DateTime`'s "U" standard format is the same as "u" because local time is
  UTC here; `Uri` does not speak IPv6 hosts or relative resolution; and
  `Encoding.Latin1` has no accessor, because .NET exposes no Latin1Encoding
  class and `Encoding` here is a static class holding concrete types.
- Doubles print fifteen significant digits where .NET prints the shortest run
  that reads back as the same value: the two agree on every number a program
  writes down and differ in the sixteenth-digit tail of a computed one.
- `ExitCode` (`lib/sys/linux.cor`) and `Args` (`os/lib/user.cor`) are
  the CORSAC one-string argument ABI's leftovers and go when the last
  utility stops naming them.

## Compiler gaps the library is waiting on

Each is reported, with a repro that fits in a paragraph. The numbers are the
ones used in the reports.

7. A type argument is not inferred through a by-reference parameter:
   `static void Resize<T>(ref T[] items, int size)` called as
   `Array.Resize(ref ints, 6)` is refused with "a by-reference argument must
   match exactly". A non-generic `ref int[]` works.
8. A generic method declared to return `T?` hands back an address rather than
   the value for a value-type T, with no diagnostic:
   `static T? Pick<T>(T[] items) { return items[0]; }` over an `int[]` prints
   a large negative number. Declared `T`, it is correct.
9. A struct's static method calling another static method of the SAME struct
   that takes an `out long` crashes the compiler itself --
   "ArgumentNullException: Value cannot be null. (Parameter 'key')" in
   ConstantAndCopyPropagation. The same code in a static class is fine, which
   is why the date parser's shared "read hh:mm:ss" helper lives in
   `DateParts` rather than in DateTime.
10. Testing or unboxing a boxed USER-DEFINED struct faults the machine:
    `object o = new Small(5); o is Small` segfaults, where `o is int` is fine.
    String.Format's date branch is what it bites.
