/* Loads a DLL COR-C# built and calls its exports, printing each result. */
#include <windows.h>
#include <stdio.h>
typedef int (__stdcall *add_t)(int, int);
typedef float (__stdcall *vertex_t)(float, float, float);
typedef void (__stdcall *tri_t)(int, int, int);
typedef int (*calls_t)(void);
int main(int argc, char **argv)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    HMODULE m = LoadLibraryA(argc > 1 ? argv[1] : "exports.dll");
    if (!m) { printf("load failed %lu\n", GetLastError()); return 1; }
    printf("loaded at %p\n", (void *)m);
    add_t add = (add_t)GetProcAddress(m, "Add");
    vertex_t vertex = (vertex_t)GetProcAddress(m, "glVertex3f");
    tri_t tri = (tri_t)GetProcAddress(m, "_grDrawTriangle@12");
    calls_t calls = (calls_t)GetProcAddress(m, "Calls");
    if (!add || !vertex || !tri || !calls) { printf("export missing\n"); return 2; }
    int total = 0;
    for (int i = 0; i < 5; i++) total += add(i, 100);
    printf("add sum %d\n", total);
    printf("vertex %.3f\n", vertex(2.0f, 3.5f, 0.25f));
    tri(1, 2, 3);
    printf("calls>0 %d\n", calls() > 0);
    printf("free %d\n", FreeLibrary(m));
    return 0;
}
