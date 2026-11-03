#include "../../native/HollowKnightTAS.ClockBridge/resident_director_producers.h"
#include <stdio.h>
#include <stdlib.h>
/* Synthetic process image only: no LoadLibrary, DLL entry point, game,
 * asset, save or engine worker. Execute fixed native methods on fixtures;
 * only allocation, release and externally observed test latches are supplied
 * by this harness. Imports in the image are never resolved. */
#define CHECK(x) do { if (!(x)) { fprintf(stderr,"FAIL %d: %s (error=%lu)\n",__LINE__,#x,(unsigned long)GetLastError());exit(1); } } while(0)
static BYTE *image;
static DECLSPEC_ALIGN(16) BYTE manager[1120], graph[232], node[48], stacks[9][64], connection[104];
static BYTE handle[16]; static HANDLE entered,resume_call;
static volatile LONG faults,allocs,frees,latch,consumer,worker_done;
static void pointer(void *at,void *value) { memcpy(at,&value,8); }
static void u32(void *at,uint32_t value) { memcpy(at,&value,4); }
static void __cdecl fault_sink(LONG family) { if (family !=68) ExitProcess(2); InterlockedIncrement(&faults); }
static void jump(BYTE *at,void *dest) { memcpy(at,"\xff\x25\0\0\0\0",6);memcpy(at+6,&dest,8); }
static hktas_dn_status status(void) { hktas_dn_status s;CHECK(HktasClockBridge_CopyResidentDirectorFenceStatus((BYTE*)&s,sizeof(s))==sizeof(s));return s; }
static void *relay(unsigned id)
{
    for(unsigned i=0;i<DN_EDGE_COUNT;++i) if(g_dn_edges[i].id==id) {
        BYTE *site=image+g_dn_edges[i].rva;int32_t delta;memcpy(&delta,site+1,4);return site+5+delta;
    }
    return NULL;
}
static void * __cdecl allocate(size_t n,int label,unsigned align,const char *file,int line)
{
    (void)label;(void)align;(void)file;(void)line;
    CHECK(n==32 && status().active);InterlockedIncrement(&allocs);return HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,n);
}
static void __cdecl release(void *p,int label,const char *file,int line)
{
    (void)label;(void)file;(void)line;CHECK(status().active && HeapFree(GetProcessHeap(),0,p));InterlockedIncrement(&frees);
}
static void __cdecl grow(BYTE *array)
{
    CHECK(status().active);size_t before=(size_t)hktas_dn_u64(array+16);CHECK(before<8);
    BYTE *p=(BYTE *)HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,8*24); CHECK(p);
    if(before) {memcpy(p,(void*)hktas_dn_ptr(array),before*24);CHECK(HeapFree(GetProcessHeap(),0,(void*)hktas_dn_ptr(array)));}
    pointer(array,p);u32(array+24,16);
}
static void __cdecl release_resolver(void *ref)
{
    CHECK(ref==graph+160 && status().active==1);
    if(InterlockedCompareExchange(&latch,0,0)==2) ExitThread(0);
    if(InterlockedCompareExchange(&latch,0,0)) {SetEvent(entered);CHECK(WaitForSingleObject(resume_call,4000)==WAIT_OBJECT_0);}
}
static void setup(const wchar_t *path)
{
    HANDLE f=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,0,NULL);CHECK(f!=INVALID_HANDLE_VALUE);
    DWORD size=GetFileSize(f,NULL),got=0;BYTE *raw=(BYTE*)malloc(size);CHECK(raw && ReadFile(f,raw,size,&got,NULL) && got==size);CloseHandle(f);
    CHECK(size>1024 && ((IMAGE_DOS_HEADER*)raw)->e_magic==IMAGE_DOS_SIGNATURE);
    LONG ofs=((IMAGE_DOS_HEADER*)raw)->e_lfanew;CHECK(ofs>0 && (DWORD)ofs<size-sizeof(IMAGE_NT_HEADERS64));
    IMAGE_NT_HEADERS64 *nt=(IMAGE_NT_HEADERS64*)(raw+ofs);CHECK(nt->OptionalHeader.SizeOfImage==HKTAS_DN_IMAGE);
    image=(BYTE*)VirtualAlloc(NULL,HKTAS_DN_IMAGE,MEM_COMMIT|MEM_RESERVE,PAGE_EXECUTE_READWRITE);CHECK(image && nt->OptionalHeader.SizeOfHeaders<size);
    memcpy(image,raw,nt->OptionalHeader.SizeOfHeaders);
    IMAGE_SECTION_HEADER *sections=IMAGE_FIRST_SECTION(nt);CHECK((BYTE*)(sections+nt->FileHeader.NumberOfSections)<=raw+size);
    for(unsigned i=0;i<nt->FileHeader.NumberOfSections;i++) {
        IMAGE_SECTION_HEADER s=sections[i]; CHECK(s.PointerToRawData<=size && s.SizeOfRawData<=size-s.PointerToRawData
            && s.VirtualAddress<=HKTAS_DN_IMAGE && s.SizeOfRawData<=HKTAS_DN_IMAGE-s.VirtualAddress);
        memcpy(image+s.VirtualAddress,raw+s.PointerToRawData,s.SizeOfRawData);
    }
    free(raw);CHECK(hktas_dn_signature(image));CHECK(!GetModuleHandleW(L"UnityPlayer.dll"));
    jump(image+0x5680a0,(void*)allocate);jump(image+0x56b640,(void*)release);jump(image+0xbceb0,(void*)grow);jump(image+0x8bbe30,(void*)release_resolver);
    CHECK(FlushInstructionCache(GetCurrentProcess(),image,HKTAS_DN_IMAGE));
}
static void install(void)
{
    /* Only this harness owns the private PE copy. No fixture thread exists
     * until install returns. The real install correctly rejects this image. */
    CHECK(!install_resident_director_producers(image,GetCurrentThreadId(),fault_sink));
    CHECK(hktas_dn_install_in_exclusive_image(image,GetCurrentThreadId(),fault_sink));
    pointer(image+HKTAS_DN_MANAGER,manager);
    for(unsigned i=0;i<2;++i) { BYTE *head=manager+808+i*16;pointer(head,head);pointer(head+8,head);}
    for(unsigned i=0;i<9;++i) pointer(manager+1040+i*8,stacks[i]);
    pointer(graph+16,node);pointer(node+40,graph);u32(node+32,8);pointer(handle,node);u32(handle+8,8);
    entered=CreateEventW(NULL,TRUE,FALSE,NULL);resume_call=CreateEventW(NULL,TRUE,FALSE,NULL);CHECK(entered && resume_call);
}
static DWORD WINAPI destroy_thread(void *unused)
{
    (void)unused;void (__cdecl *call)(void*,void*)=(void(__cdecl *)(void*,void*))relay(DN_manager_ScheduleGraphDestroy);
    CHECK(call);call(manager,handle);return 0;
}
static DWORD WINAPI unblock(void *unused)
{
    (void)unused;uint64_t start=GetTickCount64();
    for(;;) {hktas_dn_status s=status();if(s.closed && !s.held && s.active==1)break;CHECK(GetTickCount64()-start<3000);Sleep(1);}
    SetEvent(resume_call);return 0;
}
static DWORD WINAPI consume_thread(void *unused)
{
    (void)unused;InterlockedIncrement(&consumer);
    ((void(__cdecl *)(void*))relay(DN_manager_ProcessPlayStateChanges))(manager);InterlockedIncrement(&worker_done);return 0;
}
static void check_thread(HANDLE t)
{DWORD code;CHECK(t && WaitForSingleObject(t,4000)==WAIT_OBJECT_0 && GetExitCodeThread(t,&code) && !code);CloseHandle(t);}
static DWORD WINAPI application_worker(void *unused)
{
    (void)unused;SetEvent(entered);
    CHECK(WaitForSingleObject(resume_call,4000)==WAIT_OBJECT_0);
    InterlockedIncrement(&worker_done);return 0;
}
static void drain_and_resume(void)
{
    latch=1;HANDLE worker=CreateThread(NULL,0,destroy_thread,NULL,0,NULL);CHECK(worker && WaitForSingleObject(entered,3000)==WAIT_OBJECT_0);
    HANDLE helper=CreateThread(NULL,0,unblock,NULL,0,NULL);CHECK(helper);
    CHECK(HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64())==1);
    check_thread(worker);check_thread(helper);
    hktas_dn_status s=status();CHECK(s.held && s.closed && !s.active && s.begun==s.ended && s.max_depth>=2 && s.required_gaps && !s.complete);
    CHECK(hktas_dn_u32(node+32)==10 && hktas_dn_ptr(node+40)==(uintptr_t)graph && hktas_dn_ptr(stacks[5]) && hktas_dn_u64(stacks[5]+8)==1);
    BYTE *cmd=(BYTE*)hktas_dn_ptr(stacks[5]);CHECK(hktas_dn_ptr(cmd+8)==(uintptr_t)node && hktas_dn_u32(cmd+16)==10 && hktas_dn_u32(cmd+24)==2);
    CHECK(HktasClockBridge_VerifyResidentDirectorQueues(1)==1);
    CHECK(HktasClockBridge_EndResidentDirectorFence(1,1)==1);
    /* A normally invalidated older command still has to be consumed.
     * Drive the real FlushGraphCommands / ProcessPlayStateChanges and the
     * installed wrapper while the graph generation is already newer. */
    u32(node+32,12);
    CHECK(HktasClockBridge_BeginResidentDirectorFence(2,GetTickCount64())==1);
    worker=CreateThread(NULL,0,consume_thread,NULL,0,NULL);CHECK(worker);
    uint64_t until=GetTickCount64()+3000;while(!status().waiting) {CHECK(GetTickCount64()<until);Sleep(1);}
    CHECK(consumer==1 && !worker_done && !frees && hktas_dn_ptr(stacks[5])==(uintptr_t)cmd && !manager[986]);
    CHECK(HktasClockBridge_EndResidentDirectorFence(2,1)==1);check_thread(worker);
    CHECK(worker_done==1 && !hktas_dn_ptr(stacks[5]) && frees==2 && !manager[986]);
}
static void pool_cycle(void)
{
    /* Exactly one genuine ConnectionList cycles through Free->Pop.
     * Re-using the same pointer changes the atomic tag and in-flight count. */
    u32(manager+1000,1);u32(manager+1020,1);u32(connection+96,0);
    ((void (__cdecl *)(void*,void*))relay(DN_pool_Free))(manager+992,connection);
    CHECK(!hktas_dn_u32(manager+1020) && hktas_dn_ptr(stacks[0])==(uintptr_t)connection);
    CHECK(HktasClockBridge_BeginResidentDirectorFence(3,GetTickCount64()) && HktasClockBridge_EndResidentDirectorFence(3,1));
    void *taken=((void *(__cdecl *)(void*,int))relay(DN_pool_AllocateInBucket))(manager+992,0);
    CHECK(taken==connection && hktas_dn_u32(manager+1020)==1 && hktas_dn_u64(stacks[0]+8)==2);
}
int wmain(int argc,wchar_t **argv)
{
    CHECK(argc>=2);setup(argv[1]);install();
    if(argc>2 && !wcscmp(argv[2],L"startup-worker")) {
        HANDLE worker=CreateThread(NULL,0,application_worker,NULL,0,NULL);CHECK(worker && WaitForSingleObject(entered,3000)==WAIT_OBJECT_0);
        hktas_dn_parked held={0};hktas_dn_startup_abi abi={0};
        CHECK(hktas_dn_startup_exports(&abi) && !hktas_dn_park(&held,FALSE,&abi) && hktas_dn_resume(&held));
        SetEvent(resume_call);check_thread(worker);CHECK(worker_done==1);
        puts("PASS engine/application threads do not qualify as idle OS loader workers");return 0;
    }
    if(argc>2 && !wcscmp(argv[2],L"timeout")) {
        latch=1;
        HANDLE t=CreateThread(NULL,0,destroy_thread,NULL,0,NULL);CHECK(t && WaitForSingleObject(entered,3000)==WAIT_OBJECT_0);
        uint64_t now=GetTickCount64();
        CHECK(!HktasClockBridge_BeginResidentDirectorFence(1,now-HKTAS_DN_WALL+120));
        hktas_dn_status s=status();
        CHECK(GetTickCount64()-now>=100 && s.closed && !s.held && s.active && s.fault==11 && faults==1);
        puts("PASS original 9 s transaction deadline stops drain while genuine graph destroy is still active");return 0;
    }
    if(argc>2 && !wcscmp(argv[2],L"orphan")) {
        latch=2; check_thread(CreateThread(NULL,0,destroy_thread,NULL,0,NULL));
        hktas_dn_status s=status();CHECK(s.fault==1 && !s.active && s.closed && !s.held && s.begun>s.ended && faults==1);
        CHECK(!HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()));
        puts("PASS worker exiting inside original function permanently retires admission");return 0;
    }
    if(argc>2 && !wcscmp(argv[2],L"abort")) {
        CHECK(HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()));
        CHECK(!HktasClockBridge_EndResidentDirectorFence(1,0) && !HktasClockBridge_BeginResidentDirectorFence(2,GetTickCount64()));
        CHECK(status().closed && status().fault==14 && faults==1);
        puts("PASS explicit abort retires this process and later epochs");return 0;
    }
    if(argc>2 && !wcscmp(argv[2],L"registration")) {
        BYTE *head=manager+824;
        pointer(head,graph);pointer(head+8,graph);pointer(graph,head);pointer(graph+8,head);
        pointer(graph+56,graph+56);pointer(graph+64,graph+56);
        pointer(graph+104,graph+104);pointer(graph+112,graph+104);
        CHECK(HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()));
        CHECK(HktasClockBridge_VerifyResidentDirectorQueues(1));u32(node+32,10);
        CHECK(!HktasClockBridge_VerifyResidentDirectorQueues(1) && status().fault==13 && faults==1);
        puts("PASS registered graph generation drift refused with stable addresses and list links");return 0;
    }
    if(argc>2 && !wcscmp(argv[2],L"tamper")) {
        CHECK(HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()));
        image[g_dn_edges[0].rva+1]^=1;
        CHECK(!HktasClockBridge_VerifyResidentDirectorQueues(1) && status().fault==13 && faults==1);
        puts("PASS patched call destination independently rechecked");return 0;
    }
    if(argc>2 && !wcscmp(argv[2],L"drift")) {
        CHECK(HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()));
        ++stacks[3][8];CHECK(!HktasClockBridge_VerifyResidentDirectorQueues(1) && !HktasClockBridge_EndResidentDirectorFence(1,1));
        CHECK(status().closed && status().fault && faults==1);puts("PASS untracked ABA drift leaves permanent closed fault");return 0;
    }
    if(argc>2 && !wcscmp(argv[2],L"owner-entry")) {
        CHECK(HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()));consume_thread(NULL);return 1;
    }
    CHECK(argc==2 || (argc==3 && !wcscmp(argv[2],L"normal")));
    drain_and_resume();pool_cycle();
    CHECK(!HktasClockBridge_BeginResidentDirectorFence(3,GetTickCount64()) && status().fault && status().closed && faults==1);
    printf("PASS %u patched edges/%u methods, nested drain, actual graph generation/queue consumer, blocked worker, bucket ABA, stale epoch; required gaps=%u\n",
        DN_EDGE_COUNT,(unsigned)DN_FUNCTION_COUNT,HKTAS_DN_GAPS);return 0;
}
