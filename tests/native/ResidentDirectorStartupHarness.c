/* Exercise the actual parking predicate under the injector's early primary
 * APC / loader-lock sequencing, using only this test executable and DLL. */
#include <windows.h>
#include <stdio.h>
#include <wchar.h>
#ifdef HKTAS_DIRECTOR_STARTUP_DLL
#include "../../native/HollowKnightTAS.ClockBridge/resident_director_producers.h"
static LONG result=-1;
__declspec(dllexport) LONG __cdecl SampleDirectorStartup(void) {return result;}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID reserved)
{
    (void)instance;(void)reserved;
    if(reason==DLL_PROCESS_ATTACH) {
        if(GetModuleHandleW(L"UnityPlayer.dll") || GetModuleHandleW(L"mono-2.0-bdwgc.dll")
            || install_resident_director_producers(NULL,GetCurrentThreadId(),NULL)) return FALSE;
        hktas_dn_parked held={0};hktas_dn_startup_abi abi={0};
        BOOL ok=hktas_dn_startup_exports(&abi) && hktas_dn_park(&held,FALSE,&abi) && hktas_dn_park(&held,TRUE,&abi);
        unsigned workers=held.count;
        if(hktas_dn_resume(&held) && ok) result=(LONG)workers;
    }
    return TRUE;
}
#else
int wmain(int argc,wchar_t **argv)
{
    if(argc==2 && !wcscmp(argv[1],L"--child")) {
        HMODULE dll=GetModuleHandleW(L"director-startup-probe.dll");
        LONG (__cdecl *sample)(void)=dll ? (LONG(__cdecl *)(void))(void*)GetProcAddress(dll,"SampleDirectorStartup") : NULL;
        if(!sample) return 201;
        LONG count=sample();
        printf("startup APC parked and resumed %ld proven idle OS workers\n",(long)count);
        return count>=0 ? 0 : 202;
    }
    if(argc!=2) return 203;
    WCHAR exe[2048],command[2100],dll[2048];
    DWORD length=GetModuleFileNameW(NULL,exe,2048),path=GetFullPathNameW(argv[1],2048,dll,NULL);
    if(!length || length>=2048 || !path || path>=2048 || swprintf(command,2100,L"\"%ls\" --child",exe)<0) return 204;
    STARTUPINFOW start={0};start.cb=sizeof(start);PROCESS_INFORMATION child={0};
    if(!CreateProcessW(exe,command,NULL,NULL,FALSE,CREATE_SUSPENDED,NULL,NULL,&start,&child)) return 205;
    DWORD code=206;SIZE_T bytes=(path+1u)*sizeof(WCHAR),written=0;
    void *remote=VirtualAllocEx(child.hProcess,NULL,bytes,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
    PAPCFUNC load=(PAPCFUNC)(void*)GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"LoadLibraryW");
    if(!remote || !load || !WriteProcessMemory(child.hProcess,remote,dll,bytes,&written) || written!=bytes
        || !QueueUserAPC(load,child.hThread,(ULONG_PTR)remote) || ResumeThread(child.hThread)!=(DWORD)1
        || WaitForSingleObject(child.hProcess,4000)!=WAIT_OBJECT_0 || !GetExitCodeProcess(child.hProcess,&code))
        TerminateProcess(child.hProcess,code);
    CloseHandle(child.hThread);CloseHandle(child.hProcess);return (int)code;
}
#endif
