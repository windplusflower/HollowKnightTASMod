#define wmain director_prior_fixture
#include "ResidentDirectorProducerHarness.c"
#undef wmain
static DECLSPEC_ALIGN(16) BYTE jobs[160],graph2[232],node2[48],job_group[96],queue_body[256],native_jobs[2][48],group_pool_node[48],job_pool[64],group_pool[64];
static HANDLE waiting,run_job,job_worker;
static HANDLE native_completed,finish_tail;
static const wchar_t *scenario;
static LONG expected_reason;
static uint32_t previous_version;
static DWORD unrelated_rows;
static void __cdecl other_job(void *data,unsigned index)
{
    CHECK(data==&unrelated_rows && index<2 && !(unrelated_rows&(1u<<index)));unrelated_rows|=1u<<index;
}
static void __cdecl job_fault(LONG code)
{
    CHECK(code==68 && expected_reason && g_dn_fault==expected_reason);
    fprintf(stderr,"PASS rejected job scenario with expected native reason %ld\n",(long)g_dn_fault);fflush(stderr);
}
static void* __cdecl get_queue(void) {return queue_body;}
static void* __cdecl create_group(void *queue,unsigned count,void *dependency)
{
    CHECK(queue==queue_body && count==2 && !hktas_dn_ptr(dependency));
    uint64_t generation=hktas_dn_u64(job_group+8);memset(job_group,0,96);memcpy(job_group+8,&generation,8);
    memset(native_jobs,0,sizeof(native_jobs));pointer(job_group,native_jobs[0]);pointer(job_group+24,native_jobs[1]);
    u32(job_group+16,2);pointer(job_group+48,group_pool_node);pointer(native_jobs[0],native_jobs[1]);
    pointer(native_jobs[0]+32,job_group);pointer(native_jobs[1]+32,job_group);return job_group;
}
static int run_native_exec(void *job,uint64_t version)
{
    return ((int(__cdecl *)(void*,void*,uint64_t,int,BYTE))(void*)(image+hktas_de_entries[HKTAS_DE_job_Exec].rva))(queue_body,job,version,1,0);
}
static DWORD WINAPI execute_rows(void *unused)
{
    (void)unused;
    if(!wcscmp(scenario,L"async")) CHECK(WaitForSingleObject(run_job,4000)==WAIT_OBJECT_0);
    if(!wcscmp(scenario,L"generation")) u32(node2+32,78);
    uint64_t version=hktas_dn_u64(job_group+8)+2;
    if(!wcscmp(scenario,L"raw-live")) ((hktas_dn_job_fn)(void*)(image+hktas_de_entries[HKTAS_DE_job_PrepareFrame].rva))(jobs,1);
    if(!wcscmp(scenario,L"wrong-group")) run_native_exec(native_jobs[0],version-1);
    if(!wcscmp(scenario,L"high-generation")) run_native_exec(native_jobs[0],version+UINT64_C(0x100000000));
    if(!wcscmp(scenario,L"reused") && previous_version) run_native_exec(native_jobs[0],previous_version);
    if(!wcscmp(scenario,L"missing")) {
        pointer(job_group,NULL);uint64_t tag=version-1;memcpy(job_group+8,&tag,8);run_native_exec(native_jobs[0],version);return 0;
    }
    ((void(__cdecl *)(void*,void*,BYTE))(void*)(image+0x726f00))(queue_body,job_group,0);
    if(!wcscmp(scenario,L"duplicate")) run_native_exec(native_jobs[0],version);
    previous_version=(uint32_t)version;return 0;
}
static void __cdecl enqueue(void *q,void *node_arg)
{
    CHECK(q==queue_body+0xf0 && node_arg==group_pool_node);
    job_worker=CreateThread(NULL,0,execute_rows,NULL,0,NULL);CHECK(job_worker);
}
static void __cdecl wake(void *queue,unsigned count,BYTE inline_mode)
{CHECK(queue==queue_body && count==2 && !inline_mode);}
static void __cdecl wake_dependencies(void *queue,void *dependency,BYTE inline_mode)
{
    (void)dependency;(void)inline_mode;CHECK(queue==queue_body);
    if(!wcscmp(scenario,L"native-before-return")) {
        SetEvent(native_completed);CHECK(WaitForSingleObject(finish_tail,4000)==WAIT_OBJECT_0);
    }
}
static void __cdecl wait_group(void *queue,void *id,int mode)
{
    CHECK(queue==queue_body && hktas_dn_ptr(id)==(uintptr_t)job_group && !mode);
    SetEvent(waiting);
    if(!wcscmp(scenario,L"native-before-return")) {
        CHECK(WaitForSingleObject(native_completed,3000)==WAIT_OBJECT_0 && hktas_dn_u64(job_group+8)==hktas_dn_u32((BYTE*)id+8));
    } else if(job_worker) {check_thread(job_worker);job_worker=NULL;}
}
static DWORD WINAPI publish_stage(void *unused)
{
    (void)unused;
    ((void(__cdecl *)(void*,int))(void*)(image+hktas_de_entries[HKTAS_DE_manager_ExecutePrepareFrames].rva))(manager,0);
    return 0;
}
static DWORD WINAPI unblock_descendants(void *unused)
{
    (void)unused;uint64_t start=GetTickCount64();
    for(;;) {
        hktas_dn_status s=status();if(s.closed && !s.held && s.active) break;
        CHECK(GetTickCount64()-start<3000);Sleep(1);
    }
    SetEvent(!wcscmp(scenario,L"native-before-return")?finish_tail:run_job);return 0;
}
int wmain(int argc,wchar_t **argv)
{
    CHECK(argc==3);scenario=argv[2];setup(argv[1]);install();
    g_dn_fault_sink=job_fault;
    jump(image+0x7271d0,(void*)get_queue);jump(image+0x726860,(void*)create_group);jump(image+0x81a8e0,(void*)enqueue);
    jump(image+0x7287d0,(void*)wake);jump(image+0x7275d0,(void*)wake_dependencies);jump(image+0x728340,(void*)wait_group);
    CHECK(FlushInstructionCache(GetCurrentProcess(),image,HKTAS_DN_IMAGE));
    waiting=CreateEventW(NULL,TRUE,FALSE,NULL);run_job=CreateEventW(NULL,TRUE,FALSE,NULL);CHECK(waiting && run_job);
    native_completed=CreateEventW(NULL,TRUE,FALSE,NULL);finish_tail=CreateEventW(NULL,TRUE,FALSE,NULL);CHECK(native_completed && finish_tail);
    pointer(queue_body+8,queue_body+0xf0);u32(job_group+8,40);queue_body[0xea]=(BYTE)(!wcscmp(scenario,L"sync") || !wcscmp(scenario,L"unrelated"));
    pointer(image+0x726eb8+0x12f14a8,group_pool);pointer(image+0x726ecc+0x12f149c,job_pool);
    pointer(manager+144,jobs);u32(manager+160,2);u32(manager+168,4);
    pointer(jobs+72,graph);pointer(jobs+152,graph2);
    pointer(graph+16,node);pointer(node+40,graph);u32(node+32,66);
    pointer(graph2+16,node2);pointer(node2+40,graph2);u32(node2+32,76);
    for(unsigned i=0;i<2;++i) {
        BYTE *g=i?graph2:graph;pointer(g+56,g+56);pointer(g+64,g+56);pointer(g+104,g+104);pointer(g+112,g+104);
    }
    u32(graph2+24,1);float dsp=0.125f,scaled=0.25f;memcpy(manager+80,&dsp,4);memcpy(manager+72,&scaled,4);
    if(!wcscmp(scenario,L"lazy-handle")) {pointer(graph+16,NULL);pointer(graph2+16,NULL);}
    if(!wcscmp(scenario,L"unrelated")) {
        BYTE id[16]={0};
        typedef void (__cdecl *schedule_type)(void*,hktas_dn_job_fn,void*,int,hktas_dn_done_fn,int);
        ((schedule_type)(void*)(image+hktas_de_entries[HKTAS_DE_job_Schedule].rva))(id,other_job,&unrelated_rows,2,NULL,0);
        ((void(__cdecl *)(void*,int))(void*)(image+hktas_de_entries[HKTAS_DE_job_Complete].rva))(id,0);
        CHECK(unrelated_rows==3 && !hktas_dn_ptr(id) && !g_dn_job_published && !g_dn_job_started && !status().begun);
        puts("PASS unrelated real native JobQueue publication, indexed dispatch and completion retain their original arguments and behavior");return 0;
    }
    if(!wcscmp(scenario,L"unissued")) {
        expected_reason=33;((hktas_dn_job_fn)(void*)(image+hktas_de_entries[HKTAS_DE_job_PrepareFrame].rva))(jobs,1);CHECK(0);
    }
    if(!wcscmp(scenario,L"raw-live") || !wcscmp(scenario,L"generation")) expected_reason=33;
    if(!wcscmp(scenario,L"duplicate") || !wcscmp(scenario,L"wrong-group") || !wcscmp(scenario,L"high-generation") || !wcscmp(scenario,L"reused")) expected_reason=38;
    if(!wcscmp(scenario,L"unpublished-group")) {
        expected_reason=37;BYTE id[16]={0},dependency[16]={0};
        typedef void* (__cdecl *schedule_type)(void*,void*,hktas_dn_job_fn,void*,unsigned,hktas_dn_done_fn,void*,int);
        ((schedule_type)(void*)(image+0x727d60))(queue_body,id,(hktas_dn_job_fn)(void*)(image+hktas_de_entries[HKTAS_DE_job_PrepareFrame].rva),jobs,2,NULL,dependency,0);
        CHECK(0);
    }
    if(!wcscmp(scenario,L"missing")) expected_reason=36;
    HANDLE publisher=CreateThread(NULL,0,publish_stage,NULL,0,NULL);CHECK(publisher);
    if(!wcscmp(scenario,L"async")) {
        CHECK(WaitForSingleObject(waiting,3000)==WAIT_OBJECT_0);
        HANDLE helper=CreateThread(NULL,0,unblock_descendants,NULL,0,NULL);CHECK(helper);
        CHECK(HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()));
        check_thread(helper);
    }
    check_thread(publisher);
    if(!wcscmp(scenario,L"reused")) {publish_stage(NULL);CHECK(0);}
    if(!wcscmp(scenario,L"native-before-return")) {
        /* The publishing thread has already completed its real native fence
         * and exited. The last original Exec is still inside its pool tail. */
        hktas_dn_status left=status();
        CHECK(left.outstanding_groups==1 && left.callbacks_returned==2 && !left.groups_retired && left.active);
        HANDLE helper=CreateThread(NULL,0,unblock_descendants,NULL,0,NULL);CHECK(helper);
        CHECK(HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()));
        check_thread(helper);check_thread(job_worker);
    } else if(wcscmp(scenario,L"async")) {
        CHECK((!wcscmp(scenario,L"sync") || !wcscmp(scenario,L"lazy-handle")) && HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()));
    }
    double a=0,b=0;memcpy(&a,graph+48,8);memcpy(&b,graph2+48,8);
    CHECK(a==0.125 && b==0.25 && !graph[184] && !graph2[184]);
    hktas_dn_status s=status();
    CHECK(s.version==3 && s.bytes==152 && s.functions==49 && s.callbacks_started==2 && s.callbacks_returned==2
        && s.groups_published==1 && s.groups_retired==1 && !s.outstanding_groups && !s.outstanding_rows
        && !g_dn_job_tickets && s.begun==s.ended && HktasClockBridge_EndResidentDirectorFence(1,1));
    printf("PASS real ExecutePrepareFrames -> ScheduleForEach -> two real indexed PrepareFrame workers -> native JobGroup/JobInfo dispatch -> original fence clear, %s publication\n",
        !wcscmp(scenario,L"async")?"async drained while closed":!wcscmp(scenario,L"native-before-return")?"native completion before original Exec returns":
        !wcscmp(scenario,L"lazy-handle")?"without materializing a graph handle":"inline");
    return 0;
}
