/* Calls a DLL's Churn export on four threads at once and checks each answer. */
#include <windows.h>
#include <stdio.h>
typedef int (__stdcall *churn_t)(int, int);
typedef int (__stdcall *count_t)(void);
static churn_t churn;
static int answers[4];
static DWORD WINAPI run(LPVOID arg)
{
    int i = (int)(INT_PTR)arg;
    answers[i] = churn(i * 1000, 400);
    return 0;
}
int main(int argc, char **argv)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    HMODULE m = LoadLibraryA(argc > 1 ? argv[1] : "threads.dll");
    if (!m) { printf("load failed %lu\n", GetLastError()); return 1; }
    churn = (churn_t)GetProcAddress(m, "Churn");
    if (!churn) { printf("export missing\n"); return 2; }
    HANDLE t[4];
    for (int i = 0; i < 4; i++) t[i] = CreateThread(NULL, 0, run, (LPVOID)(INT_PTR)i, 0, NULL);
    WaitForMultipleObjects(4, t, TRUE, INFINITE);
    for (int i = 0; i < 4; i++)
    {
        int want = 0;
        for (int r = 0; r < 400; r++) for (int k = 0; k < 200; k++) want += (i * 1000 + k) & 7;
        printf("thread %d %s\n", i, answers[i] == want ? "ok" : "wrong");
    }
    count_t collections = (count_t)GetProcAddress(m, "Collections");
    printf("collected %s\n", collections && collections() > 1 ? "yes" : "no");
    churn_t nullCaught = (churn_t)GetProcAddress(m, "NullCaught");
    printf("null caught %d\n", nullCaught ? ((int (__stdcall *)(int))nullCaught)(3) : -1);
    return 0;
}
