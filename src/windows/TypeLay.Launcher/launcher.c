/* TypeLay's small native Windows x64 launcher. No CRT or external DLL payload.
   Compile with eng/windows/build-launcher.py. Windows APIs use explicit UTF-16. */
typedef unsigned short WCHAR;
typedef unsigned long DWORD;
typedef int BOOL;
typedef void *HANDLE;
typedef struct {
    DWORD cb; WCHAR *reserved, *desktop, *title;
    DWORD x, y, width, height, columns, rows, fill, flags;
    unsigned short show, reservedSize; unsigned char *reservedBytes;
    HANDLE input, output, error;
} STARTUPINFOW;
typedef struct { HANDLE process, thread; DWORD processId, threadId; } PROCESS_INFORMATION;
#define API __declspec(dllimport)
API DWORD __stdcall GetModuleFileNameW(HANDLE, WCHAR *, DWORD);
API unsigned int __stdcall GetSystemDirectoryW(WCHAR *, unsigned int);
API BOOL __stdcall CreateProcessW(const WCHAR *, WCHAR *, void *, void *, BOOL, DWORD, void *, const WCHAR *, STARTUPINFOW *, PROCESS_INFORMATION *);
API BOOL __stdcall CloseHandle(HANDLE);
API DWORD __stdcall GetFileAttributesW(const WCHAR *);
API int __stdcall MessageBoxW(HANDLE, const WCHAR *, const WCHAR *, unsigned int);
API void __stdcall ExitProcess(unsigned int);
static WCHAR root[32768], exe[32768], script[32768], command[32768];
static STARTUPINFOW si;
static PROCESS_INFORMATION pi;
static unsigned int length(const WCHAR *s) { unsigned int n=0; while(s[n]) n++; return n; }
static BOOL append(WCHAR *dest, const WCHAR *part) {
    unsigned int a=length(dest), b=length(part);
    if (a+b >= 32767) return 0;
    for (unsigned int i=0; i<=b; i++) dest[a+i]=part[i];
    return 1;
}
static void fail(const WCHAR *message) {
    MessageBoxW(0,message,(const WCHAR*)L"TypeLay",0x10);
    ExitProcess(2);
}
void __stdcall mainCRTStartup(void) {
    DWORD count=GetModuleFileNameW(0,root,32768);
    if (!count || count>=32767) fail((const WCHAR*)L"Cannot locate TypeLay. Extract the complete ZIP into a shorter path.");
    while(count && root[count-1] != '\\') count--;
    if (!count) fail((const WCHAR*)L"Cannot locate the TypeLay directory.");
    root[count-1]=0;
    if (!GetSystemDirectoryW(exe,32768) || !append(exe,(const WCHAR*)L"\\WindowsPowerShell\\v1.0\\powershell.exe"))
        fail((const WCHAR*)L"Windows PowerShell could not be located.");
    if (!append(script,root) || !append(script,(const WCHAR*)L"\\bootstrap\\start.ps1") || GetFileAttributesW(script)==0xffffffff)
        fail((const WCHAR*)L"The package is incomplete. Extract the entire ZIP before starting TypeLay.exe.");
    if (!append(command,(const WCHAR*)L"\"") || !append(command,exe) || !append(command,(const WCHAR*)L"\" -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File \"") || !append(command,script) || !append(command,(const WCHAR*)L"\""))
        fail((const WCHAR*)L"The TypeLay folder path is too long.");
    si.cb=sizeof(si);
    if (!CreateProcessW(exe,command,0,0,0,0x08000000,0,root,&si,&pi))
        fail((const WCHAR*)L"Windows could not start TypeLay setup. Open Start-TypeLay.cmd for diagnostics. Organization policy may block scripts.");
    CloseHandle(pi.thread); CloseHandle(pi.process); ExitProcess(0);
}
