/* Fills a DLL's statics, makes collections, and checks what the statics hold. */
#include <windows.h>
#include <stdio.h>
typedef int (__stdcall *one_t)(int);
typedef int (__stdcall *none_t)(void);
int main(int argc, char **argv)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    HMODULE m = LoadLibraryA(argv[1]);
    if (!m) { printf("load failed %lu\n", GetLastError()); return 1; }
    one_t fill = (one_t)GetProcAddress(m, "Fill"), churn = (one_t)GetProcAddress(m, "Churn");
    none_t check = (none_t)GetProcAddress(m, "Check"), collections = (none_t)GetProcAddress(m, "Collections");
    printf("filled %d\n", fill(5000));
    churn(400);
    printf("collected %s\n", collections() > 2 ? "yes" : "no");
    printf("check %d\n", check());
    return 0;
}
