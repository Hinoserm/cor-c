/* Takes seventy TLS slots before loading a COR-C# DLL, so the DLL's own is
   past the TEB's sixty-four, in the expansion array; then checks a call in
   leaves the thread's last error alone, and that a thread of the host's,
   which never touched the DLL's slot, can call in. */
#include <windows.h>
#include <stdio.h>
typedef int (__stdcall *add_t)(int, int);
static add_t add;
static DWORD WINAPI other(LPVOID unused)
{
    (void)unused;
    SetLastError(777);
    int r = add(3, 4);
    printf("foreign %d lasterror %lu\n", r > 0, GetLastError());
    return 0;
}
int main(int argc, char **argv)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    DWORD last = 0;
    for (int i = 0; i < 70; i++) last = TlsAlloc();
    printf("slots taken past 64 %d\n", last != TLS_OUT_OF_INDEXES && last >= 64);
    HMODULE m = LoadLibraryA(argc > 1 ? argv[1] : "exports.dll");
    if (!m) { printf("load failed %lu\n", GetLastError()); return 1; }
    add = (add_t)GetProcAddress(m, "Add");
    if (!add) { printf("export missing\n"); return 2; }
    SetLastError(4321);
    int r = add(1, 2);
    printf("call %d lasterror %lu\n", r > 0, GetLastError());
    HANDLE t = CreateThread(NULL, 0, other, NULL, 0, NULL);
    WaitForSingleObject(t, INFINITE);
    CloseHandle(t);
    return 0;
}
