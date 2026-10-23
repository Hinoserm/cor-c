/* COR-C# DLLs on Windows 9x: loads each, calls its exports, writes C:\TEST\OUT.TXT. kernel32 only, no C runtime. */
#include <windows.h>
typedef int (__stdcall *two_t)(int, int);
typedef int (__stdcall *one_t)(int);
typedef int (__stdcall *none_t)(void);
typedef float (__stdcall *vertex_t)(float, float, float);
static HANDLE out;
static void say(const char *s) { DWORD n; int len = 0; while (s[len]) len++; WriteFile(out, s, len, &n, NULL); }
static void num(long v) { char b[16]; int i = 15; int neg = v < 0; unsigned long u = neg ? -v : v; b[i] = 0; do { b[--i] = '0' + u % 10; u /= 10; } while (u); if (neg) b[--i] = '-'; say(b + i); }
static void hex(unsigned long v) { char b[12]; int i = 11; b[i] = 0; do { b[--i] = "0123456789abcdef"[v & 15]; v >>= 4; } while (v); say(b + i); }
static two_t churn;
static int answers[4];
static DWORD WINAPI run(LPVOID arg) { int i = (int)(INT_PTR)arg; answers[i] = churn(i * 1000, 200); return 0; }
static void exports(const char *name)
{
    HMODULE m = LoadLibraryA(name);
    say(name); say(": ");
    if (!m) { say("load failed "); num(GetLastError()); say("\r\n"); return; }
    two_t add = (two_t)GetProcAddress(m, "Add");
    vertex_t vertex = (vertex_t)GetProcAddress(m, "glVertex3f");
    int total = 0;
    for (int i = 0; i < 5; i++) total += add(i, 100);
    say("add sum "); num(total); say(", vertex*1000 "); num((long)(vertex(2.0f, 3.5f, 0.25f) * 1000.0f)); say("\r\n");
}
static void threads(const char *name)
{
    HMODULE m = LoadLibraryA(name);
    say(name); say(": ");
    if (!m) { say("load failed "); num(GetLastError()); say("\r\n"); return; }
    churn = (two_t)GetProcAddress(m, "Churn");
    HANDLE t[4];
    DWORD id[4]; /* Windows 9x fails CreateThread without somewhere to put the id. */
    for (int i = 0; i < 4; i++) t[i] = CreateThread(NULL, 0, run, (LPVOID)(INT_PTR)i, 0, &id[i]);
    WaitForMultipleObjects(4, t, TRUE, INFINITE);
    int ok = 0;
    for (int i = 0; i < 4; i++) { int want = 0; for (int r = 0; r < 200; r++) for (int k = 0; k < 200; k++) want += (i * 1000 + k) & 7; ok += answers[i] == want; }
    say("threads ok "); num(ok); say("/4, collections "); num(((none_t)GetProcAddress(m, "Collections"))());
    say(", null caught "); num(((one_t)GetProcAddress(m, "NullCaught"))(3)); say("\r\n");
}
void __stdcall start(void)
{
    CreateDirectoryA("C:\\TEST", NULL);
    out = CreateFileA("C:\\TEST\\OUT.TXT", GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    DWORD v = GetVersion();
    say("version "); hex(v); say((v & 0x80000000) ? " (9x)\r\n" : " (NT)\r\n");
    exports("C:\\TEST\\STATIC.DLL");
    threads("C:\\TEST\\THREADS.DLL");
    exports("C:\\TEST\\DYN.DLL");
    say("done\r\n");
    CloseHandle(out);
    ExitProcess(0);
}
