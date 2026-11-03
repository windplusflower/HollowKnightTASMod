/* Private, pinned PE image from the byte/queue fixture. The alternative
 * candidates are exercised in separate processes so neither can inherit
 * the other publisher's registry, bytes or ownership state. */
#define wmain hktas_de_callsite_fixture
#include "ResidentDirectorProducerHarness.c"
#undef wmain
#include "../../native/HollowKnightTAS.ClockBridge/resident_director_entry_hooks.h"

static unsigned entry_hits[HKTAS_DE_COUNT];
static BYTE *entry_relays;
static BOOL job_parked;
static HANDLE job_entered,job_release;
static DWORD job_frame_kind,job_flags,job_context;
static double job_dt;
static uint64_t job_frame;
static void __cdecl unexpected(void) {CHECK(0);}
static void __cdecl reset_time(void *owner)
{
    ++entry_hits[HKTAS_DE_manager_ResetFrameTiming];
    ((void(__cdecl *)(void*))(void*)hktas_de_original_manager_ResetFrameTiming)(owner);
}
static void *__cdecl graph_handle(void *owner,void *out)
{
    ++entry_hits[HKTAS_DE_graph_Handle];
    return ((void*(__cdecl *)(void*,void*))(void*)hktas_de_original_graph_Handle)(owner,out);
}
static void __cdecl free_node(void *owner,void *item)
{
    ++entry_hits[HKTAS_DE_manager_FreeNode];
    ((void(__cdecl *)(void*,void*))(void*)hktas_de_original_manager_FreeNode)(owner,item);
}
static void __cdecl worker_prepare(void *jobs,unsigned index)
{
    ++entry_hits[HKTAS_DE_job_PrepareFrame];
    if(job_parked) {SetEvent(job_entered);CHECK(WaitForSingleObject(job_release,4000)==WAIT_OBJECT_0);}
    ((void(__cdecl *)(void*,unsigned))(void*)hktas_de_original_job_PrepareFrame)(jobs,index);
}
static void __cdecl observe_prepare(void *owner,int kind,double dt,uint64_t frame,unsigned flags,int context)
{
    CHECK(owner==graph);++entry_hits[HKTAS_DE_graph_PrepareFrame];
    job_frame_kind=(DWORD)kind;job_dt=dt;job_frame=frame;job_flags=flags;job_context=(DWORD)context;
}
static void compare_unwind(void)
{
    IMAGE_NT_HEADERS64 *nt=(IMAGE_NT_HEADERS64*)(image+((IMAGE_DOS_HEADER*)image)->e_lfanew);
    IMAGE_DATA_DIRECTORY dir=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
    RUNTIME_FUNCTION *rows=(RUNTIME_FUNCTION*)(image+dir.VirtualAddress);
    unsigned total=0;
    for(unsigned i=0;i<HKTAS_DE_COUNT;++i) {
        const hktas_de_entry *e=&hktas_de_entries[i];PRUNTIME_FUNCTION original=NULL;
        for(unsigned k=0;k<dir.Size/sizeof(*rows);++k) if(rows[k].BeginAddress==e->rva) {original=rows+k;break;}
        for(unsigned off=0;off<e->prefix+11;off++) {
            DECLSPEC_ALIGN(16) DWORD64 stack[256];for(unsigned j=0;j<256;j++) stack[j]=UINT64_C(0x55000000)+j;
            CONTEXT a,b;memset(&a,0,sizeof(a));a.ContextFlags=CONTEXT_FULL;
            a.Rsp=(DWORD64)(uintptr_t)&stack[96];a.Rbp=(DWORD64)(uintptr_t)&stack[104];
            a.Rbx=0x1111;a.Rsi=0x2222;a.Rdi=0x3333;a.R12=0x4444;a.R13=0x5555;a.R14=0x6666;a.R15=0x7777;
            b=a;
            a.Rip=(DWORD64)(uintptr_t)(image+e->rva+(off<e->prefix?off:e->prefix));
            b.Rip=(DWORD64)(uintptr_t)(e->original+off);
            PVOID handler_a=NULL,handler_b=NULL;DWORD64 estab_a=0,estab_b=0,module=0;
            if(original) RtlVirtualUnwind(UNW_FLAG_NHANDLER,(DWORD64)(uintptr_t)image,a.Rip,original,&a,&handler_a,&estab_a,NULL);
            else {a.Rip=*(DWORD64*)(uintptr_t)a.Rsp;estab_a=a.Rsp;a.Rsp+=8;}
            PRUNTIME_FUNCTION wrapper=RtlLookupFunctionEntry(b.Rip,&module,NULL);
            CHECK(wrapper && module+wrapper->BeginAddress==(DWORD64)(uintptr_t)e->original);
            RtlVirtualUnwind(UNW_FLAG_NHANDLER,module,b.Rip,wrapper,&b,&handler_b,&estab_b,NULL);
            if(a.Rsp!=b.Rsp || a.Rip!=b.Rip || a.Rbp!=b.Rbp || a.Rbx!=b.Rbx || a.Rsi!=b.Rsi || a.Rdi!=b.Rdi
                || a.R12!=b.R12 || a.R13!=b.R13 || a.R14!=b.R14 || a.R15!=b.R15 || estab_a!=estab_b) {
                fprintf(stderr,"unwind mismatch rva=%lx offset=%u rsp=%lld/%lld rip=%llx/%llx estab=%lld/%lld rbx=%llx/%llx\n",
                    (unsigned long)e->rva,off,(long long)(a.Rsp-(uintptr_t)stack),(long long)(b.Rsp-(uintptr_t)stack),
                    (unsigned long long)a.Rip,(unsigned long long)b.Rip,(long long)(estab_a-(uintptr_t)stack),(long long)(estab_b-(uintptr_t)stack),
                    (unsigned long long)a.Rbx,(unsigned long long)b.Rbx);CHECK(0);
            }
            ++total;
        }
    }
    printf("PASS %u statically linked exact x64 prefix thunks, %u original/relocated Win64 unwind PC comparisons\n",HKTAS_DE_COUNT,total);
}
static void * __cdecl marker(void *unused) {(void)unused;return NULL;}
static void install_entries(const void **wrappers)
{
    for(unsigned i=0;i<HKTAS_DE_COUNT;i++) wrappers[i]=(void*)unexpected;
    wrappers[HKTAS_DE_manager_ResetFrameTiming]=(void*)reset_time;wrappers[HKTAS_DE_graph_Handle]=(void*)graph_handle;
    wrappers[HKTAS_DE_manager_FreeNode]=(void*)free_node;wrappers[HKTAS_DE_job_PrepareFrame]=(void*)worker_prepare;
    wrappers[HKTAS_DE_graph_PrepareFrame]=(void*)observe_prepare;
    uintptr_t start=((uintptr_t)image+HKTAS_DN_IMAGE+65535u)&~(uintptr_t)65535u;
    for(uintptr_t off=0;off<0x60000000u && !entry_relays;off+=65536u)
        entry_relays=(BYTE*)VirtualAlloc((void*)(start+off),4096,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
    CHECK(entry_relays && hktas_de_install(image,entry_relays,wrappers));
    CHECK(hktas_de_verify(image,entry_relays,wrappers,TRUE));
}
static DWORD WINAPI dispatch_job(void *jobs)
{
    BYTE *site=image+0x8978a9;int32_t displacement;memcpy(&displacement,site+3,4);
    CHECK(!memcmp(site,"\x48\x8d\x15",3) && site+7+displacement==image+0x899090);
    ((void(__cdecl *)(void*,unsigned))(void*)(site+7+displacement))(jobs,1);return 0;
}
static void callsite_control(void)
{
    uintptr_t start=((uintptr_t)image+HKTAS_DN_IMAGE+65535u)&~(uintptr_t)65535u;
    BYTE *stub=NULL;
    for(uintptr_t off=0;off<0x60000000u && !stub;off+=65536u) stub=(BYTE*)VirtualAlloc((void*)(start+off),4096,MEM_COMMIT|MEM_RESERVE,PAGE_EXECUTE_READWRITE);
    CHECK(stub);jump(stub,(void*)unexpected);
    for(unsigned i=0;i<DN_EDGE_COUNT;++i) {
        hktas_dn_edge e=g_dn_edges[i];BYTE *site=image+e.rva;int32_t before,after;
        CHECK(site[0]==e.opcode && hktas_dn_rel(site,image+hktas_de_entries[e.id].rva,&before) && !memcmp(site+1,&before,4)
            && hktas_dn_rel(site,stub,&after));memcpy(site+1,&after,4);
    }
    CHECK(FlushInstructionCache(GetCurrentProcess(),image,HKTAS_DN_IMAGE));
    ((void(__cdecl *)(void*))(void*)(image+0x899d80))(manager);
    CHECK(manager[988]);
}
int wmain(int argc,wchar_t **argv)
{
    CHECK(argc==3);setup(argv[1]);
    if(!wcscmp(argv[2],L"v1-bypass")) {
        callsite_control();puts("PASS counterexample: a raw original entry changed Director state with all 153 v1 callsites trapped");return 0;
    }
    CHECK(!wcscmp(argv[2],L"entries"));compare_unwind();const void *wrappers[HKTAS_DE_COUNT];install_entries(wrappers);
    pointer(manager+1088,stacks[6]);pointer(graph+16,node);u32(node+32,64);pointer(node+40,graph);
    ((void(__cdecl *)(void*))(void*)(image+0x899d80))(manager);CHECK(manager[988] && entry_hits[HKTAS_DE_manager_ResetFrameTiming]==1);
    memset(handle,0,sizeof(handle));
    CHECK(((void*(__cdecl *)(void*,void*))(void*)(image+0x898780))(graph,handle)==handle
        && hktas_dn_ptr(handle)==(uintptr_t)node && hktas_dn_u32(handle+8)==64 && entry_hits[HKTAS_DE_graph_Handle]==1);
    ((void(__cdecl *)(void*,void*))(void*)(image+0x898290))(manager,node);
    CHECK(hktas_dn_ptr(stacks[6])==(uintptr_t)node && hktas_dn_u32(node+32)==66 && !hktas_dn_ptr(node+40) && entry_hits[HKTAS_DE_manager_FreeNode]==1);
    BYTE jobs[160]={0};float dt=0.125f;memcpy(jobs+80+8,&dt,4);pointer(jobs+80+72,graph);
    uint64_t sample=UINT64_C(0x1234567890abc);memcpy(jobs+80+16,&sample,8);u32(jobs+80+56,0x13579);
    job_entered=CreateEventW(NULL,TRUE,FALSE,NULL);job_release=CreateEventW(NULL,TRUE,FALSE,NULL);CHECK(job_entered && job_release);
    job_parked=TRUE;HANDLE t=CreateThread(NULL,0,dispatch_job,jobs,0,NULL);CHECK(t && WaitForSingleObject(job_entered,3000)==WAIT_OBJECT_0);
    CHECK(!entry_hits[HKTAS_DE_graph_PrepareFrame]);SetEvent(job_release);check_thread(t);
    CHECK(entry_hits[HKTAS_DE_job_PrepareFrame]==1 && entry_hits[HKTAS_DE_graph_PrepareFrame]==1
        && job_frame_kind==0 && job_dt==0.125 && job_frame==sample && job_flags==0x13579 && job_context==1);
    printf("PASS direct/indirect entries and real RIP-published indexed worker callback, stage=%lu dt=%g frame=%llu flags=%lu context=%lu\n",
        (unsigned long)job_frame_kind,job_dt,(unsigned long long)job_frame,(unsigned long)job_flags,(unsigned long)job_context);
    wrappers[HKTAS_DE_graph_Handle]=(void*)marker;CHECK(!hktas_de_verify(image,entry_relays,wrappers,TRUE));
    return 0;
}
