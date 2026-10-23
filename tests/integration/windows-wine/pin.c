/* Loads a DLL, frees it, and checks it is still there (pinned). */
#include <windows.h>
#include <stdio.h>
int main(int argc, char **argv)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    HMODULE m = LoadLibraryA(argv[1]);
    if (!m) { printf("load failed\n"); return 1; }
    FreeLibrary(m);
    printf("pinned %d\n", GetModuleHandleA(argv[1]) == m);
    return 0;
}
