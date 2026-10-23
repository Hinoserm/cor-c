# The Windows target

ONE 32-BIT PROGRAM THAT RUNS, AND LOOKS RIGHT, ON EVERY 32-BIT WINDOWS: 95,
98, ME, NT 4, 2000, XP, Vista, 7, 8, 10 and 11, and under WOW64 on the 64-bit
ones. The same file, not a build per version. What it uses of Windows is
found when it runs, and it takes the best of whatever that Windows has.

This is a third platform beside the two the runtime has today: Linux (which
CORSAC's userland also speaks) and bare metal (the kernel and the ring-1
images). Nothing here changes either of them.

## The rules

1. **THE FLOOR IS WINDOWS 95.** Plain GDI, USER32, the comctl32 that ships
   with Windows 95 (4.00), and the kernel32 calls Windows 95 has. A program
   that only has the floor works fully: nothing may *require* anything newer.
2. **EVERYTHING NEWER IS FOUND AT RUN TIME AND USED WHERE IT IS BETTER.** By
   feature, never by version number: `GetProcAddress` on the module that
   would have it, `LoadLibrary` for a module that may be absent, a version
   resource where a module's version is what decides (comctl32). Version
   numbers lie (8.1 and later report 8 to a program without the manifest
   below) and service packs move features.
3. **WINDOWS DOES THE WORK.** Forms is Windows' own controls, not drawn by
   us; text is Windows' fonts; pictures are Windows' decoders where Windows
   has them. Where .NET draws something itself (ToolStrip, DataGridView,
   owner draw), it draws with GDI, through uxtheme where the system is
   themed. The stdlib's own rasterizer, FontEngine and TrueType reader are
   CORSAC's and are not used here.
4. **.NET's BEHAVIOUR IS THE SPEC**, as it is everywhere else: what a .NET
   program sees on Windows is what this one sees, Windows' differences
   included (paths, newlines, the registry).

## The image (linker)

A PE32 writer beside the ELF one; objects, link-time optimisation and
separate units do not change, only the final image does.

- `.text`, `.rdata`, `.data`, `.bss`, `.idata`, `.reloc`, `.rsrc`; file
  alignment 0x200, section alignment 0x1000; subsystem version 4.0 (the
  loader of 95 and NT 4 refuses higher); console or GUI subsystem.
- IMPORTS ONLY FROM THE FLOOR. The import directory names only what Windows
  95 has; each import gets a `jmp [IAT]` thunk so a call site stays an
  ordinary call. Anything newer is a run-time lookup (rule 2), never an
  import a missing export would stop the program loading over.
- WHAT MODERN LOADERS ASK FOR, which older ones ignore: always relocatable
  (`.reloc`, so ASLR may move it), NX-compatible (no code is written into
  data; any stub the runtime makes goes in memory it maps executable), a
  SafeSEH table naming the runtime's handlers (load configuration
  directory), and exception chains SEHOP accepts.
- THE MANIFEST (RT_MANIFEST 1), which 95 through 2000 never read: comctl32
  6 (XP's visual styles), `supportedOS` for Vista through 10 (no version
  lies, no compatibility shims), DPI awareness (`dpiAware` and
  `dpiAwareness`, per-monitor v2 where the system knows it). Icon and
  version information as the program declares them.

## DLLs

Hino, 2026-10-23: COR-C# builds 32-bit Windows DLLs, first of all CORSAC's
own OpenGL and Glide for the Windows 2000 programs its NT personality runs.
A DLL is the same image as a program, with these differences.

- EXPORTS ARE .NET's: `[UnmanagedCallersOnly(EntryPoint = "glVertex3f",
  CallConvs = new[] { typeof(CallConvStdcall) })]`, as native AOT exports
  them. The name is any the DLL must answer to, a decorated one included
  (Glide's `_grDrawTriangle@12`); the export directory lists them sorted,
  ordinals from 1. `corc compile --dll` roots a DLL at its exports, not at a
  Main: the entry it generates, DllMain, takes each export's address the way
  a program hands C a callback, so the optimiser keeps every one and treats
  it as called by code it never sees.
- IMPORTS ARE THE IMPORT DIRECTORY'S, by name, straight from `[DllImport]`:
  no import library. An object names one as `kernel32.dll!GetTickCount`
  (Pe.WindowsImports); the link gives each a `jmp [IAT]` thunk.
  `SetLastError = true` reads GetLastError.
- THE IMAGE MOVES. The code is not position-independent; `.reloc` lists
  every absolute address, so the loader can put the DLL wherever its base is
  taken, which for a DLL is usual. The base is Microsoft's linker's,
  0x10000000, unless `--base` names another.
- DllMain STARTS THE RUNTIME AND NOTHING ELSE: the loader lock is held
  while it runs, so it starts no thread and loads nothing. At
  DLL_PROCESS_ATTACH the platform library (Win32.Attach) installs its
  kernel32 hooks, takes the TLS slot and puts the loading thread's block in
  it; the module initialisers run; DllMain answers FALSE, failing the load,
  when the TEB has no slot left. At DLL_PROCESS_DETACH the slot goes back.
- AN EXPORT IS ITS TYPE'S FIRST CODE: like Main, it touches its own type on
  the way in, so the type's statics are initialised before its body reads
  them.
- THREADS THAT ARE NOT OURS: a game's thread calling an export gets a
  runtime thread block on its first call, and loses it at
  `DLL_THREAD_DETACH`. The block is found through a TLS slot (TlsAlloc) read
  straight from the TEB, `fs:[0xE10 + 4 * slot]`, not through static TLS:
  Windows 2000's and XP's loaders give a DLL loaded by LoadLibrary no static
  TLS, and Quake's engines load opengl32 that way.
- IT STAYS LOADED once loaded (it pins itself, as native AOT's do), so a
  FreeLibrary and LoadLibrary pair -- Quake 2's `vid_restart` -- finds the
  same runtime again rather than leaking a heap each time. GetModuleHandleEx's
  pin is XP's; on 2000 the DLL loads its own name once more, which only counts
  a loaded module again (Win32.Pin).
- FAULTS: Windows 2000 has no vectored handlers, so each entry from Windows
  puts an SEH registration in a slot of its frame and at the head of the
  chain, fs:[0] (Lowering.SehEnter), and takes it off on the way out. Its
  handler (Win32.Fault) sends an access violation at a null address by this
  image's own code on into Runtime.NullFault through a stub on a page of its
  own -- the faulting stack is where Windows put the context it hands over
  -- and the NullReferenceException is caught as .NET catches it. A fault in
  code that is not ours, or anything else, is searched on. An exception that
  escapes an export ends the process, as .NET's does.
- THE CALLER'S FPU CONTROL WORD STAYS: Direct3D sets single precision for
  the whole process and Microsoft's DLLs live with it; conversions already
  save and restore the word around themselves.

STATE (2026-10-23): the PE32 link, exports and imports, `.reloc`, DllMain,
the TLS-slot thread block, threads that are not ours (a block on the first
call) and the platform layer's floor are in. Tested structurally
(tests/integration/windows-dll.sh: headers, export and import directories,
thunks, checksum, and a second link at another base moving exactly the words
`.reloc` lists) and by running (tests/integration/windows-wine.sh: a 32-bit
program loads the DLL under Wine, calls each export and checks its answer).
examples/windows-cards is cards.dll in C#: Windows 2000's own sol.exe and
freecell.exe draw every card through it, and FreeCell's game 1 deals as the
published one. Four Windows threads at once in a DLL, with collections while
they all run and each block freed at DLL_THREAD_DETACH, pass five runs in
five: the collector's stop is cooperative, and a wait gives up the thread's
time slice (Sleep(0)). A read through null in the DLL is caught as
NullReferenceException (Wine's trace shows the access violation and the
handler). It stays loaded once loaded: a FreeLibrary leaves it in place.

## Calling Windows

- `[DllImport]` gains stdcall (WINAPI, Windows' default) on the caller's
  side; the backend already returns with `ret n` for stdcall callees
  (`Function.CalleePops`, used by COM's wrappers).
- STRINGS: the runtime's strings are UTF-16 already, which the W calls take
  as they are, and NT gets them. On 9x, where only a few W calls exist:
  - THE MICROSOFT LAYER FOR UNICODE (unicows.dll) where it is installed:
    each import with both forms is bound at start to unicows' W entry.
  - BELOW IT, THE RUNTIME'S OWN, which is what unicows does inside: the A
    call, the string converted to the system's ANSI code page
    (WideCharToMultiByte) on the way in and back (MultiByteToWideChar) on the
    way out, a character the code page cannot hold a `?` as it is there. The
    W calls 9x does have (TextOutW, ExtTextOutW, GetTextExtentPoint32W,
    MessageBoxW, lstrlenW and the few others) are called as they are, so
    text drawn on 9x stays Unicode wherever the font has the glyph.
  - WINDOW MESSAGES are the part that is more than a call: on 9x every
    window is an ANSI one, so the messages that carry text are converted
    both ways in the window procedure and in the send wrappers --
    WM_SETTEXT, WM_GETTEXT, the EDIT, LISTBOX, COMBOBOX and comctl32 ones,
    WM_CHAR (ANSI bytes, a DBCS pair for CJK, read with the keyboard
    layout's code page) and WM_IME_CHAR.
- CALLBACKS (a window procedure, an enumeration's callback) are
  `[UnmanagedCallersOnly]` stdcall functions, which exist. No exception
  ever unwinds through Windows' frames: the callback's stub catches it and
  hands it to Application.ThreadException, as .NET does.
- STRUCTS: Windows' own structures need what the compiler lacks today:
  `[StructLayout]` (Sequential, Explicit with `[FieldOffset]` for unions,
  `Pack`, `CharSet`), fixed-size buffers (`fixed char lfFaceName[32]`),
  C's field layout guaranteed for them, and `Marshal.SizeOf`.
- COM, which OLE drag and drop, the shell and the common dialogs of later
  systems need, exists: runtime- and COM-callable wrappers, BSTR, VARIANT,
  SAFEARRAY, IDispatch (compiler/src/metadata/ComDeclarations.cs).

## The thread block

The runtime finds a thread's block through GS on Linux. Windows' 32-bit
systems give user code no GS of its own and FS is the thread's Windows block,
which must not change. The block is in a TLS slot the runtime takes with
TlsAlloc, read straight from the TEB: `fs:[0xE10 + 4 * slot]`, one
instruction (Selector, `FsTlsSlot`), written by Sys.SetThreadBlock. Static
TLS (`fs:[0x2C]` and the image's TLS directory) is not used: Windows 2000's
and XP's loaders give a DLL that LoadLibrary loads none. A slot past the
TEB's 64 is refused at DllMain. Calls into C swap nothing.

## The platform layer: runtime/src/platforms/win32

The same classes Linux's layer has (Platform, Os, Native, Scheduler, Clock,
ThreadPool; 5,146 lines there), over kernel32 at the floor, with the better
call where the system has it:

| What | Floor (Windows 95) | Better, where present |
|---|---|---|
| Memory | VirtualAlloc, VirtualFree, VirtualProtect | |
| Threads | CreateThread; stack bounds from the thread's Windows block | |
| Waiting on a word | an event per thread, parked by address | WaitOnAddress (8) |
| Locks the runtime keeps | its own `lock cmpxchg` and the waits above | |
| Clocks | QueryPerformanceCounter, GetSystemTimeAsFileTime | GetSystemTimePreciseAsFileTime (8) |
| Files | CreateFile, ReadFile, WriteFile, FindFirstFile, SetFilePointer | GetFileAttributesEx (98, NT 4), SetFilePointerEx (2000), `\\?\` long paths (NT) |
| Console | WriteFile / WriteConsole A | WriteConsoleW (NT) |
| Faults | an SEH frame at the base of every thread: an access violation at a null address becomes a NullReferenceException, as SIGSEGV does on Linux | |
| Processes | GetCommandLine parsed as .NET parses it, CreateProcess, ExitProcess | |
| Sockets | Winsock (wsock32) | ws2_32 (98, NT 4 with it) |

The collector already stops threads cooperatively (a thread in C counts as
stopped), so nothing suspends threads from outside.

STATE (2026-10-23): runtime/src/platforms/win32/win32.cor is the bare-metal
runtime (baremetal.cor) with its hooks answered by kernel32: memory
(VirtualAlloc, VirtualFree), the time (QueryPerformanceCounter, GetTickCount
without one), the exit (ExitProcess) and standard error. `--windows` links
it in place of `--freestanding`. Every call it makes is in Windows 95's and
NT 3.51's kernel32.

## What .NET shows on Windows

`\` and drive letters in Path, case-insensitive names, `Environment.NewLine`
"\r\n", `OperatingSystem.IsWindows()`, `Environment.OSVersion` from the
real version (RtlGetVersion on NT, GetVersionEx on 9x),
`Is64BitOperatingSystem` from IsWow64Process (XP SP2) and false where that is
absent, special folders from the shell (SHGetFolderPath where shfolder.dll
is, the registry's shell folders below that: to verify on 95),
`Microsoft.Win32.Registry` over advapi32 (CORSAC's registry is CORSAC's).

## Forms

A Control is a window of Windows' own class, created with CreateWindowEx,
its messages through its window procedure into `WndProc(ref Message)` and
from there into .NET's events; `Handle`, `CreateParams`, `NativeWindow`,
`SetStyle`, `DefWndProc` as .NET has them.

| .NET | Windows class | Needs |
|---|---|---|
| Button, CheckBox, RadioButton, GroupBox | BUTTON | floor |
| TextBox | EDIT | floor |
| RichTextBox | RichEdit (riched32 1.0, riched20 2.0/3.0, msftedit 4.1) | floor; best found |
| Label | STATIC | floor |
| ListBox, ComboBox | LISTBOX, COMBOBOX | floor |
| ScrollBars | SCROLLBAR | floor |
| ListView, TreeView, TabControl, ProgressBar, StatusBar, ToolBar, TrackBar, NumericUpDown's arrows, ImageList | comctl32 | 4.00 (floor) |
| DateTimePicker, MonthCalendar | comctl32 | 4.70 (IE 3): what stands in below it is an open decision |
| Menus (MainMenu, ContextMenu) | HMENU | floor |
| MenuStrip, ToolStrip, StatusStrip, DataGridView | .NET draws these: GDI, uxtheme where themed | floor |
| MessageBox, OpenFileDialog, SaveFileDialog, ColorDialog, FontDialog, PrintDialog, PageSetupDialog | USER32, comdlg32 | floor; the newer dialogs (Vista's IFileDialog) where present |
| Clipboard, drag and drop | USER32, OLE | floor |
| Printing | GDI's spooler (StartDoc, StartPage) | floor |

LOOKING RIGHT EVERYWHERE: the manifest gives XP and later their visual
styles; owner-drawn parts draw through uxtheme when the window is themed and
with DrawFrameControl and DrawEdge when it is not; fonts are the system's own
(its non-client metrics and the dialog font each version uses: that is
Control.DefaultFont here, not .NET's fixed Microsoft Sans Serif), so a
program looks like 95 on 95 and like 11 on 11; DPI is followed where the
system has it (per monitor on 10, the system's below that, 96 on the
floor), with layouts scaled as .NET's AutoScaleMode scales them.

## System.Drawing

Graphics over a GDI device context at the floor; GDI is also the faster for
what both do (fills, blits, lines, text), so it stays the path for those
everywhere. What GDI cannot do is taken from what the system has:

| What | Floor | Better, where present |
|---|---|---|
| Lines, fills, polygons, paths, regions, clipping | GDI | |
| Text, fonts | GDI (HFONT, ExtTextOut, DrawText) | ClearType is Windows' own, through the same calls |
| Float coordinates, transforms | rounded to device units; transforms applied to the points | SetWorldTransform (NT); GDI+ |
| Antialiasing | drawn without, as programs of the time were | GDI+ |
| Alpha, gradients | none | AlphaBlend, GradientFill (msimg32: 98, 2000); GDI+ |
| Images | BMP, ICO, WMF, EMF from GDI; PNG (every depth, Adam7), JPEG (baseline and progressive) and GIF (its frames) from the stdlib's own decoders, shared with CORSAC (stdlib/src/System/Drawing/Imaging.cor reads only icons' PNG today) | GDI+ codecs (PNG, JPEG, GIF, TIFF): XP on, and the redistributable on 98 through 2000 |

THE CODECS ARE .NET'S SHAPE, not a private one. Pictures are read and
written through what .NET programs already call: `Image.FromFile`,
`Image.FromStream`, `new Bitmap(stream)`, `Image.Save(stream, ImageFormat)`
with `ImageFormat.Png`, `Jpeg`, `Gif`, `Bmp`, `Tiff`, `Icon`;
`ImageCodecInfo.GetImageDecoders()` and `GetImageEncoders()` with their
format ids, MIME types and extensions; `EncoderParameters` (JPEG quality,
colour depth); `GetFrameCount(FrameDimension.Time)` and
`SelectActiveFrame` for a GIF's frames; `PropertyItems` for what a file
carries (EXIF orientation, a GIF's frame delays). WPF's
`System.Windows.Media.Imaging` shape (`BitmapDecoder.Create`,
`PngBitmapDecoder`, `JpegBitmapEncoder`, `BitmapFrame`) sits on the same
codecs. Our decoders and encoders are what answers those calls wherever
GDI+ is absent, and on CORSAC everywhere; a program cannot tell which one
did.

GDI+ itself is optional everywhere: built into XP and later, a
redistributable on 98, ME, NT 4 and 2000, absent on 95.

## The processor

One program for every machine is compiled for the oldest processor it must
run on: the 486 profile (Windows 95's floor is a 386DX; native 386 code
waits on that profile's audit). Faster code on a newer processor would need
the same program to choose at run time (CPUID, then the function compiled
for that profile): a compiler feature of its own, not part of this target.

## Testing

Wine on the build host first (Hino, 2026-10-23), the Windows host where Wine
is not enough. Fedora's Wine 11 runs 32-bit programs through its WoW64 build
(/usr/lib64/wine-wow64); a prefix it makes has an empty syswow64 when its
first boot is cut short, and a 32-bit program then stops with "could not
load kernel32.dll": copying /usr/lib64/wine-wow64/wine/i386-windows into the
prefix's syswow64 mends it. With no desktop, Xvfb on a TCP display
(`Xvfb :57 -nolisten unix -listen tcp`, DISPLAY=localhost:57) runs the GUI
programs, and ImageMagick's `import -window root` takes their picture.

Each system in the list is its own test: 95, 98, ME, NT 4, 2000, XP, Vista,
7, 8, 10 and 11, and the WOW64 ones. They run on real machines and VMs, and
CORSAC's NT personality, which runs real Windows 2000 programs, is one of
them.

## Open decisions

For the DLLs (asked 2026-10-23):

- Whether DLLs must load on 9x too, or the NT family only (the TEB's TLS
  slots above are NT's).
- One runtime in each DLL (as native AOT) or one shared runtime DLL.
- OpenGL as a replacement opengl32.dll (369 exports in 2000 SP4) or as an
  ICD under Microsoft's (docs/software/GRAPHICS-3D.md 6.3 in the OS
  repository).
- Which Glide: glide2x.dll, glide3x.dll, glide.dll; and when.

For the programs:

- What stands in for DateTimePicker and MonthCalendar on comctl32 4.00
  (Windows 95 without Internet Explorer 3).

Decided: unicows.dll where installed and the runtime's own A/W layer below
it; the system's font as the default; PNG, JPEG and GIF from our own
decoders wherever GDI+ is absent.

## Order of work

0. DLLs first (2026-10-23): the PE32 link with exports, imports and
   `.reloc`, the thread block through a TLS slot, threads that are not
   ours, the platform layer's floor, DllMain, several threads and thread
   detach, faults, pinning (done, and cards.dll runs Solitaire); then
   opengl32 and Glide.
1. The image, stdcall and A/W imports, the thread block, struct layout:
   a console "hello" that runs on 95 and on 11.
2. The platform layer: the language suite passing on Windows.
3. System.Drawing over GDI, then the run-time upgrades.
4. Forms over Windows' controls.
