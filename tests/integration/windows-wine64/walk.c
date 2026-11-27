/* Calls a COR-C# DLL's export, which calls back here; the callback walks the
   stack with RtlCaptureStackBackTrace, which unwinds each frame by the
   image's .pdata and .xdata. The walk must come back through the DLL's
   frames to caller(), the function that made the call. */
#include <windows.h>
#include <stdio.h>
typedef int (*through_t)(int (*)(int), int);
static void *frames[64];
static USHORT count;
static int callback(int v)
{
    count = RtlCaptureStackBackTrace(0, 64, frames, NULL);
    return v * 2;
}
__attribute__((noinline)) static int caller(through_t through)
{
    return through(callback, 20);
}
int main(int argc, char **argv)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    HMODULE m = LoadLibraryA(argc > 1 ? argv[1] : "walk.dll");
    if (!m) { printf("load failed %lu\n", GetLastError()); return 1; }
    through_t through = (through_t)GetProcAddress(m, "Through");
    if (!through) { printf("export missing\n"); return 2; }
    printf("through %d\n", caller(through));
    char *dll = (char *)m;
    int inDll = 0, inCaller = 0;
    for (USHORT i = 0; i < count; i++)
    {
        char *at = (char *)frames[i];
        IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(dll + ((IMAGE_DOS_HEADER *)dll)->e_lfanew);
        if (at >= dll && at < dll + nt->OptionalHeader.SizeOfImage) inDll++;
        /* By the host's own unwind table: the function this frame is in. */
        DWORD64 base = 0;
        RUNTIME_FUNCTION *f = RtlLookupFunctionEntry((DWORD64)at, &base, NULL);
        if (f && (char *)(base + f->BeginAddress) == (char *)caller) inCaller = 1;
    }
    printf("walked dll frames>0 %d caller %d\n", inDll > 0, inCaller);
    return 0;
}
