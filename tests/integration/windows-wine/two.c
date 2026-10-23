/* Two DLLs over one runtime DLL in one process: each called, collections made, both checked. */
#include <windows.h>
#include <stdio.h>
typedef int (__stdcall *one_t)(int);
typedef int (__stdcall *two_t)(int, int);
typedef int (__stdcall *none_t)(void);
int main(int argc, char **argv)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    HMODULE keep = LoadLibraryA(argv[1]), exports = LoadLibraryA(argv[2]);
    if (!keep || !exports) { printf("load failed %lu\n", GetLastError()); return 1; }
    one_t fill = (one_t)GetProcAddress(keep, "Fill"), churn = (one_t)GetProcAddress(keep, "Churn");
    none_t check = (none_t)GetProcAddress(keep, "Check");
    two_t add = (two_t)GetProcAddress(exports, "Add");
    fill(2000);
    int total = 0;
    for (int i = 0; i < 5; i++) total += add(i, 100);
    churn(200);
    printf("two: add sum %d, check %d\n", total, check());
    return 0;
}
