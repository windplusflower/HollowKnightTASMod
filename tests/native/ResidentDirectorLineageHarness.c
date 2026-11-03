#define wmain director_base_fixture
#include "ResidentDirectorProducerHarness.c"
#undef wmain

typedef struct {DECLSPEC_ALIGN(16) BYTE group[96],nodes[2][48],queue_node[48];HANDLE thread;unsigned count,ordinal;} fixture_group;
static fixture_group fixtures[4];
static DECLSPEC_ALIGN(16) BYTE queue_bytes[256],free_groups[64],free_jobs[64],process_group[40],fences[4][16];
static HANDLE allow_root,root_waiting,all_published;
static LONG allocations,group_allocs,job_allocs,parent_calls,parent_done,child_calls,continuation_calls,child_done;
static void *batch_group;
static unsigned root_value=73,child_value=82,continuation_value=91;
static const wchar_t *test_name;
static void __cdecl lineage_fault(LONG type)
{
    CHECK(type==68 && ((!wcscmp(test_name,L"pending") && g_dn_fault==11) || (!wcscmp(test_name,L"substitute") && g_dn_fault==40)
        || (!wcscmp(test_name,L"wrong-generation") && g_dn_fault==40) || (!wcscmp(test_name,L"duplicate") && g_dn_fault==40)));
    fprintf(stderr,"PASS expected lineage first fault %ld\n",(long)g_dn_fault);fflush(stderr);
}
static void* __cdecl get_job_queue(void) {return queue_bytes;}
static void* __cdecl job_alloc(size_t size,size_t align,int label,int flags,const char *source,int line)
{
    (void)label;(void)flags;(void)source;CHECK(align==16);if(line==0x609) return HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,size);
    if(size==32) {LONG i=InterlockedIncrement(&allocations)-1;CHECK(i>=0 && i<4);fixtures[i].ordinal=(unsigned)i;return fixtures[i].queue_node;}
    if(size==96) {LONG i=InterlockedIncrement(&group_allocs)-1;CHECK(i>=0 && i<4);return fixtures[i].group;}
    CHECK(size==40);LONG n=InterlockedIncrement(&job_allocs)-1;CHECK(n>=0 && n<8);return fixtures[n/2].nodes[n%2];
}
static int invoke_job(void *job,uint64_t tag)
{
    typedef int (__cdecl *execute)(void*,void*,uint64_t,int,BYTE);
    return ((execute)(void*)(image+hktas_de_entries[HKTAS_DE_job_Exec].rva))(queue_bytes,job,tag,1,0);
}
static DWORD WINAPI run_group(void *param)
{
    fixture_group *f=(fixture_group*)param;
    BYTE *group=(BYTE*)hktas_dn_ptr(f->queue_node+8);CHECK(group);BYTE *first=(BYTE*)hktas_dn_ptr(group);
    uint64_t version=hktas_dn_u64(group+8)+2;
    if(!f->ordinal) {
        SetEvent(root_waiting);CHECK(WaitForSingleObject(allow_root,4000)==WAIT_OBJECT_0);
        if(!wcscmp(test_name,L"substitute")) pointer(first+16,&child_value);
        if(!wcscmp(test_name,L"wrong-generation")) invoke_job(first,version-1);
        if(!wcscmp(test_name,L"duplicate")) {
            pointer(group,NULL);u32(group+8,(DWORD)(version-1));invoke_job(first,version);invoke_job(first,version);CHECK(0);
        }
    } else CHECK(WaitForSingleObject(all_published,4000)==WAIT_OBJECT_0);
    ((void(__cdecl *)(void*,void*,BYTE))(void*)(image+0x726f00))(queue_bytes,group,0);
    return 0;
}
static void __cdecl enqueue_group(void *queue,void *node_arg)
{
    CHECK(queue==queue_bytes+240);
    for(unsigned i=0;i<4;++i) if(fixtures[i].queue_node==node_arg) {
        CHECK(!fixtures[i].thread);fixtures[i].thread=CreateThread(NULL,0,run_group,fixtures+i,0,NULL);CHECK(fixtures[i].thread);if(i==2) SetEvent(all_published);return;
    }
    CHECK(0);
}
static void __cdecl enqueue_all(void *queue,void *first,void *last)
{
    CHECK(first==last);enqueue_group(queue,first);
}
static void __cdecl wake_group(void *queue,unsigned count,BYTE inline_mode)
{CHECK(queue==queue_bytes && count>=1 && count<=2 && !inline_mode);}
static void submit(void *fence,hktas_dn_job_fn fn,void *data,int count,hktas_dn_done_fn finish)
{
    typedef void (__cdecl *schedule)(void*,hktas_dn_job_fn,void*,int,hktas_dn_done_fn,int);
    ((schedule)(void*)(image+hktas_de_entries[HKTAS_DE_job_Schedule].rva))(fence,fn,data,count,finish,0);
}
static void __cdecl nested_finish(void *arg) {CHECK(arg==&child_value);InterlockedIncrement(&child_done);}
static void __cdecl nested_job(void *arg,unsigned index) {CHECK(arg==&child_value && !index);InterlockedIncrement(&child_calls);}
static void __cdecl continuation_job(void *arg,unsigned index) {CHECK(arg==&continuation_value && !index);InterlockedIncrement(&continuation_calls);}
static void __cdecl process_job(void *arg,unsigned index)
{
    CHECK(arg==&root_value && index<2);InterlockedIncrement(&parent_calls);
    if(index==1) submit(fences[1],nested_job,&child_value,1,nested_finish);
}
static void __cdecl process_done(void *arg)
{
    CHECK(arg==&root_value);InterlockedIncrement(&parent_done);
    submit(fences[2],continuation_job,&continuation_value,1,NULL);
}
static void __cdecl process_callback(void *outputs)
{
    CHECK(outputs==process_group+8 && !hktas_dn_u64(process_group+24));
    if(!wcscmp(test_name,L"batch")) {
        BYTE dep[16]={0};typedef void*(__cdecl *make_batch)(void*,hktas_dn_job_fn,void*,unsigned,hktas_dn_done_fn,void*,void*);
        batch_group=((make_batch)(void*)(image+0x726770))(queue_bytes,process_job,&root_value,2,process_done,dep,NULL);CHECK(batch_group);return;
    }
    if(!wcscmp(test_name,L"multi")) {
        BYTE dep[16]={0};typedef void*(__cdecl *multi)(void*,void*,hktas_dn_done_fn,void*,void*,int,int);
        queue_bytes[0xea]=1;CHECK(((multi)(void*)(image+0x727a90))(queue_bytes,fences[0],nested_finish,&child_value,dep,1,4)==fences[0]);
        queue_bytes[0xea]=0;return;
    }
    submit(fences[0],process_job,&root_value,2,process_done);
}
static DWORD WINAPI open_after_close(void *unused)
{
    (void)unused;uint64_t start=GetTickCount64();
    for(;;) {hktas_dn_status s=status();if(s.closed && !s.held) break;CHECK(GetTickCount64()-start<3000);Sleep(1);}
    if(!wcscmp(test_name,L"batch")) {
        ((void(__cdecl*)(void*,void*,void*))(void*)(image+hktas_de_entries[HKTAS_DE_job_ScheduleGroups].rva))(queue_bytes,batch_group,batch_group);
        CHECK(WaitForSingleObject(root_waiting,3000)==WAIT_OBJECT_0);
    }
    SetEvent(allow_root);return 0;
}
int wmain(int argc,wchar_t **argv)
{
    CHECK(argc==3);test_name=argv[2];setup(argv[1]);install();g_dn_fault_sink=lineage_fault;
    jump(image+0x7271d0,(void*)get_job_queue);jump(image+0x56b6a0,(void*)job_alloc);
    jump(image+0x81a8e0,(void*)enqueue_group);jump(image+0x81a900,(void*)enqueue_all);jump(image+0x7287d0,(void*)wake_group);
    pointer(image+0x726eb8+0x12f14a8,free_groups);pointer(image+0x726ecc+0x12f149c,free_jobs);
    pointer(queue_bytes+8,queue_bytes+240);CHECK(FlushInstructionCache(GetCurrentProcess(),image,HKTAS_DN_IMAGE));
    all_published=CreateEventW(NULL,TRUE,FALSE,NULL);CHECK(all_published);allow_root=CreateEventW(NULL,TRUE,FALSE,NULL);root_waiting=CreateEventW(NULL,TRUE,FALSE,NULL);CHECK(allow_root && root_waiting);
    pointer(process_group,(void*)process_callback);pointer(manager+592,process_group);
    pointer(manager+600,process_group+40);pointer(manager+608,process_group+40);
    /* Actual ExecuteStage dispatches its dynamic original callback; that
     * callback publishes work and RETURNS before any JobInfo executes. */
    ((void(__cdecl *)(void*,int))(void*)(image+hktas_de_entries[HKTAS_DE_manager_ExecuteStage].rva))(manager,0);
    if(!wcscmp(test_name,L"multi")) {
        CHECK(child_done==1 && g_dn_lineage_issued==1 && g_dn_lineage_retired==1 && g_dn_lineage_entered==1 && g_dn_lineage_returned==1);
        CHECK(HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()) && HktasClockBridge_EndResidentDirectorFence(1,1));
        hktas_dn_status receipt=status();
        CHECK(receipt.version==4 && receipt.bytes==200 && receipt.lineage_issued==1 && receipt.lineage_retired==1 && receipt.lineage_entered==1 && receipt.lineage_returned==1 && !receipt.lineage_nested && !receipt.outstanding_lineages && !receipt.outstanding_lineage_rows);
        puts("PASS native multi-dependency publication bypasses ScheduleGroupInternal; real factory, physical JobInfo and completion callback retained");return 0;
    }
    if(!wcscmp(test_name,L"batch")) CHECK(batch_group && !fixtures[0].thread && WaitForSingleObject(root_waiting,0)==WAIT_TIMEOUT);
    else CHECK(WaitForSingleObject(root_waiting,3000)==WAIT_OBJECT_0);
    CHECK(!manager[987] && !status().active && g_dn_lineage_count==1 && g_dn_lineage_rows==2 && !parent_calls);
    if(!wcscmp(test_name,L"pending")) {
        CHECK(!HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()-HKTAS_DN_WALL+120));
        CHECK(status().fault==11 && !status().held && g_dn_lineage_count==1 && !parent_calls);return 0;
    }
    HANDLE helper=CreateThread(NULL,0,open_after_close,NULL,0,NULL);CHECK(helper && HktasClockBridge_BeginResidentDirectorFence(1,GetTickCount64()));
    check_thread(helper);CHECK(allocations==3);
    for(unsigned i=0;i<3;++i) check_thread(fixtures[i].thread);
    CHECK(parent_calls==2 && parent_done==1 && child_calls==1 && child_done==1 && continuation_calls==1);
    CHECK(!g_dn_lineages && !g_dn_lineage_rows && g_dn_lineage_issued==3 && g_dn_lineage_retired==3
        && g_dn_lineage_entered==4 && g_dn_lineage_returned==4 && g_dn_lineage_nested==2);
    hktas_dn_status receipt=status();
    CHECK(receipt.version==4 && receipt.bytes==200 && receipt.functions==51 && receipt.edges==153 && receipt.required_gaps==7 && !receipt.complete
        && receipt.lineage_issued==3 && receipt.lineage_retired==3 && receipt.lineage_entered==4 && receipt.lineage_returned==4 && receipt.lineage_nested==2
        && !receipt.outstanding_lineages && !receipt.outstanding_lineage_rows);
    CHECK(HktasClockBridge_EndResidentDirectorFence(1,1));
    puts("PASS original dynamic stage callback -> native async group, child from worker and child from completion callback -> exact 3 groups / 4 physical JobInfo executions drained");return 0;
}
