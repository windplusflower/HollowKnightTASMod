#ifndef HKTAS_RESIDENT_DIRECTOR_JOB_LINEAGE_H
#define HKTAS_RESIDENT_DIRECTOR_JOB_LINEAGE_H
#include <stdlib.h>

/* A Director stage invokes the actual ProcessCallbackGroup callback selected
 * by its output. Capture scheduler ownership at the publication operation,
 * even when that callback has its own unknown JobInfo function/data types.
 * Original execution, the optional completion callback and nested job
 * publications remain inside the actual JobQueue::Exec call. */
typedef struct {
    uintptr_t node,callback,data;
    DWORD flags;
    BYTE state;
} hktas_dn_lineage_row;
typedef struct hktas_dn_lineage {
    struct hktas_dn_lineage *next;
    void *queue,*group;
    void *batch_cookie;
    uint64_t generation,successor,serial,ancestor;
    uintptr_t completion,completion_data;
    unsigned count,returned;
    unsigned publisher_refs;
    BOOL published,superseded,assembled;
    hktas_dn_lineage_row rows[];
} hktas_dn_lineage;
static hktas_dn_lineage *g_dn_lineages;
static volatile LONG g_dn_lineage_count;
static unsigned g_dn_lineage_rows;
static uint64_t g_dn_lineage_issued,g_dn_lineage_retired,g_dn_lineage_entered,g_dn_lineage_returned,g_dn_lineage_nested;
static int hktas_dn_row_order(const void *a,const void *b)
{
    uintptr_t x=((const hktas_dn_lineage_row*)a)->node,y=((const hktas_dn_lineage_row*)b)->node;
    return x<y ? -1 : x>y;
}
static hktas_dn_lineage_row *hktas_dn_lookup_row(hktas_dn_lineage *group,uintptr_t node)
{
    hktas_dn_lineage_row key={0};key.node=node;
    return (hktas_dn_lineage_row*)bsearch(&key,group->rows,group->count,sizeof(key),hktas_dn_row_order);
}
static void hktas_dn_supersede_group_locked(void *queue,void *group,uint64_t next)
{
    for(hktas_dn_lineage *g=g_dn_lineages;g;g=g->next) if(g->queue==queue && g->group==group) {
        /* Native completion can precede return of the last Exec tail. A
         * real new publication may legally reuse this allocation already. */
        if(next==g->generation) continue;
        if(next<g->generation) {hktas_dn_reject_locked(39);return;}
        g->superseded=TRUE;g->successor=next;
    }
}
static hktas_dn_lineage *hktas_dn_find_cohort(void *queue,void *group,uint64_t generation)
{
    for(hktas_dn_lineage *g=g_dn_lineages;g;g=g->next) if(g->queue==queue && g->group==group && g->generation==generation) return g;
    return NULL;
}
static hktas_dn_lineage *hktas_dn_lineage_bind_locked(void *queue,void *group,const BYTE *body,unsigned count,hktas_dn_thread *owner,BOOL assembled)
{
    if(!owner || !owner->depth || (unsigned)g_dn_lineage_count>=HKTAS_DN_LIMIT || count>HKTAS_DN_LIMIT-g_dn_lineage_rows
        || g_dn_lineage_issued==UINT64_MAX || hktas_dn_u64(body+8)>UINT64_MAX-2u || hktas_dn_find_cohort(queue,group,hktas_dn_u64(body+8)+2)) {
        hktas_dn_reject_locked(39);return NULL;
    }
    hktas_dn_lineage *g=(hktas_dn_lineage*)HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,
        offsetof(hktas_dn_lineage,rows)+(size_t)count*sizeof(hktas_dn_lineage_row));
    if(!g) {hktas_dn_reject_locked(39);return NULL;}
    g->queue=queue;g->group=group;g->generation=hktas_dn_u64(body+8)+2;g->count=count;
    g->assembled=assembled;g->completion=hktas_dn_ptr(body+32);g->completion_data=hktas_dn_ptr(body+40);
    uintptr_t node=hktas_dn_ptr(body),last=hktas_dn_ptr(body+24);
    if(!count && (node || last)) {HeapFree(GetProcessHeap(),0,g);hktas_dn_reject_locked(39);return NULL;}
    for(unsigned i=0;i<count;++i) {
        BYTE info[40];
        if(!node || !hktas_dn_read(node,info,sizeof(info)) || hktas_dn_ptr(info+32)!=(uintptr_t)group || (assembled && !hktas_dn_ptr(info+8))
            || (node==last)!=(i==count-1)) {HeapFree(GetProcessHeap(),0,g);hktas_dn_reject_locked(39);return NULL;}
        g->rows[i].node=node;g->rows[i].callback=hktas_dn_ptr(info+8);g->rows[i].data=hktas_dn_ptr(info+16);
        g->rows[i].flags=hktas_dn_u32(info+24);node=hktas_dn_ptr(info);
    }
    qsort(g->rows,count,sizeof(*g->rows),hktas_dn_row_order);
    for(unsigned i=1;i<count;++i) if(g->rows[i-1].node==g->rows[i].node) {HeapFree(GetProcessHeap(),0,g);hktas_dn_reject_locked(39);return NULL;}
    g->serial=++g_dn_lineage_issued;g->ancestor=owner->lineage;
    if(g->ancestor) ++g_dn_lineage_nested;
    g->next=g_dn_lineages;g_dn_lineages=g;g_dn_lineage_rows+=count;InterlockedIncrement(&g_dn_lineage_count);return g;
}
static void hktas_dn_lineage_retire_locked(hktas_dn_lineage *g)
{
    if(!g->published || g->publisher_refs || g->returned!=g->count) return;
    hktas_dn_lineage **row=&g_dn_lineages;while(*row && *row!=g) row=&(*row)->next;
    if(*row!=g || g_dn_lineage_rows<g->count || g_dn_lineage_count<=0) {hktas_dn_reject_locked(40);return;}
    *row=g->next;g_dn_lineage_rows-=g->count;InterlockedDecrement(&g_dn_lineage_count);
    ++g_dn_lineage_retired;HeapFree(GetProcessHeap(),0,g);WakeAllConditionVariable(&g_dn_cv);
}
typedef int (__cdecl *hktas_dn_native_exec)(void*,void*,uint64_t,int,BYTE);
static BOOL hktas_dn_native_dispatch_site(void *return_pc)
{
    /* Every verified dispatch of JobQueue::Exec in the pinned player. All
     * are original direct calls; the entry jump preserves their return PC. */
    static const DWORD sites[]={0x72655au,0x726f76u,0x726fe8u,0x727367u,0x727596u,0x727713u,0x728223u,0x728260u,0x7284dau};
    for(unsigned i=0;i<sizeof(sites)/sizeof(*sites);++i) if(return_pc==g_dn_base+sites[i]) return TRUE;
    return FALSE;
}
static int hktas_dn_lineage_exec(hktas_dn_native_exec original,void *queue,void *job,uint64_t generation,int decrement,BYTE inline_mode,const BYTE *info,void *return_pc)
{
    if(!InterlockedCompareExchange(&g_dn_lineage_count,0,0)) return original(queue,job,generation,decrement,inline_mode);
    AcquireSRWLockExclusive(&g_dn_lock);
    uintptr_t group=hktas_dn_ptr(info+32);hktas_dn_lineage *match=NULL;BOOL wrong_version=FALSE;
    for(hktas_dn_lineage *g=g_dn_lineages;g;g=g->next) if(g->group==(void*)group && g->queue==queue) {
        if(generation==g->generation) {match=g;break;}
        if(!g->superseded || g->successor!=generation) wrong_version=TRUE;
    }
    if(!match) {
        if(wrong_version) {hktas_dn_reject_locked(40);return 0;}
        ReleaseSRWLockExclusive(&g_dn_lock);return original(queue,job,generation,decrement,inline_mode);
    }
    hktas_dn_lineage_row *row=hktas_dn_lookup_row(match,(uintptr_t)job);BYTE completion[16];
    if(!row || row->state || !hktas_dn_native_dispatch_site(return_pc) || !hktas_dn_ptr(info+8) || !hktas_dn_read(group+32,completion,16)
        || (match->assembled && (row->callback!=hktas_dn_ptr(info+8) || row->data!=hktas_dn_ptr(info+16)
            || row->flags!=hktas_dn_u32(info+24) || hktas_dn_ptr(completion)!=match->completion || hktas_dn_ptr(completion+8)!=match->completion_data))) {
        hktas_dn_reject_locked(40);return 0;
    }
    /* A directly enqueued batch or multi-dependency group may go from
     * factory to Exec without using ScheduleGroupInternal. The exact
     * factory-issued physical job/generation is already ours; the real
     * dispatcher chooses its callback. It stays in scope until Exec returns. */
    match->published=TRUE;
    row->state=1;++g_dn_lineage_entered;hktas_dn_enter_locked(DN_job_Exec,TRUE);
    hktas_dn_thread *owner=(hktas_dn_thread*)FlsGetValue(g_dn_fls);uint64_t previous=owner->lineage;owner->lineage=match->serial;
    int result=original(queue,job,generation,decrement,inline_mode);
    AcquireSRWLockExclusive(&g_dn_lock);
    if(row->state!=1 || owner->lineage!=match->serial) {hktas_dn_reject_locked(40);return 0;}
    owner->lineage=previous;row->state=2;++match->returned;++g_dn_lineage_returned;hktas_dn_lineage_retire_locked(match);
    ReleaseSRWLockExclusive(&g_dn_lock);hktas_dn_leave(DN_job_Exec);return result;
}
static BOOL hktas_dn_assemble_lineage_locked(hktas_dn_lineage *g,const BYTE *body,unsigned count)
{
    if(count!=g->count || g->returned) return FALSE;
    uintptr_t node=hktas_dn_ptr(body),last=hktas_dn_ptr(body+24);
    for(unsigned i=0;i<count;++i) {
        BYTE info[40];hktas_dn_lineage_row *row=hktas_dn_lookup_row(g,node);
        if(!row || row->state || !hktas_dn_read(node,info,40) || !hktas_dn_ptr(info+8) || hktas_dn_ptr(info+32)!=(uintptr_t)g->group
            || (node==last)!=(i==count-1)) return FALSE;
        row->callback=hktas_dn_ptr(info+8);row->data=hktas_dn_ptr(info+16);row->flags=hktas_dn_u32(info+24);node=hktas_dn_ptr(info);
    }
    g->assembled=TRUE;g->completion=hktas_dn_ptr(body+32);g->completion_data=hktas_dn_ptr(body+40);return TRUE;
}
static void* __cdecl dn_job_CreateGroup(void *queue,unsigned count,void *dependency)
{
    typedef void* (__cdecl *factory)(void*,unsigned,void*);
    factory original=(factory)(void*)hktas_de_original_job_CreateGroup;
    hktas_dn_thread *owner=(hktas_dn_thread*)FlsGetValue(g_dn_fls);
    if(!owner || !owner->depth) return original(queue,count,dependency);
    hktas_dn_enter(DN_job_CreateGroup);
    void *group=original(queue,count,dependency);
    AcquireSRWLockExclusive(&g_dn_lock);BYTE body[48];
    if(!group || !hktas_dn_read((uintptr_t)group,body,48) || hktas_dn_u32(body+16)!=count || hktas_dn_ptr(body+32)
        || hktas_dn_u64(body+8)>UINT64_MAX-2u) {hktas_dn_reject_locked(41);return NULL;}
    hktas_dn_supersede_group_locked(queue,group,hktas_dn_u64(body+8)+2);
    hktas_dn_job_ticket *special=(hktas_dn_job_ticket*)owner->publishing;
    if(special && !special->created_group && special->count==count && __builtin_return_address(0)==g_dn_base+0x727da7u)
        special->created_group=group;
    else hktas_dn_lineage_bind_locked(queue,group,body,count,owner,FALSE);
    ReleaseSRWLockExclusive(&g_dn_lock);hktas_dn_leave(DN_job_CreateGroup);return group;
}
static void __cdecl dn_job_ScheduleGroups(void *queue,void *first,void *last)
{
    typedef void (__cdecl *batch_fn)(void*,void*,void*);
    batch_fn original=(batch_fn)(void*)hktas_de_original_job_ScheduleGroups;
    hktas_dn_thread *owner=(hktas_dn_thread*)FlsGetValue(g_dn_fls);
    if((!owner || !owner->depth) && !InterlockedCompareExchange(&g_dn_lineage_count,0,0)) {original(queue,first,last);return;}
    AcquireSRWLockExclusive(&g_dn_lock);
    uintptr_t group=(uintptr_t)first;unsigned n=0,bound=0;char cookie=0;
    while(group) {
        BYTE body[64];uintptr_t node=0,next_node=0;
        if(++n>HKTAS_DN_LIMIT || !hktas_dn_read(group,body,sizeof(body)) || !(node=hktas_dn_ptr(body+48))
            || !hktas_dn_read(node,&next_node,8) || hktas_dn_u64(body+8)>UINT64_MAX-2u) {hktas_dn_reject_locked(42);return;}
        unsigned count=hktas_dn_u32(body+16)&0x7fffffffu;
        if(hktas_dn_ptr(body+32) && count) --count;
        uint64_t generation=hktas_dn_u64(body+8)+2;hktas_dn_supersede_group_locked(queue,(void*)group,generation);
        hktas_dn_lineage *g=hktas_dn_find_cohort(queue,(void*)group,generation);
        if(g) {
            if(g->published || g->publisher_refs || !hktas_dn_assemble_lineage_locked(g,body,count)) {hktas_dn_reject_locked(42);return;}
        } else if(owner && owner->depth) g=hktas_dn_lineage_bind_locked(queue,(void*)group,body,count,owner,TRUE);
        if(g) {g->batch_cookie=&cookie;++g->publisher_refs;++bound;}
        if(!next_node) {if(group!=(uintptr_t)last) {hktas_dn_reject_locked(42);return;}break;}
        if(group==(uintptr_t)last || !hktas_dn_read(next_node+8,&group,8) || !group) {hktas_dn_reject_locked(42);return;}
    }
    if(!bound) {ReleaseSRWLockExclusive(&g_dn_lock);original(queue,first,last);return;}
    hktas_dn_enter_locked(DN_job_ScheduleGroups,TRUE);original(queue,first,last);
    AcquireSRWLockExclusive(&g_dn_lock);
    for(hktas_dn_lineage *g=g_dn_lineages,*next;g;g=next) {
        next=g->next;if(g->batch_cookie!=&cookie) continue;
        if(!g->publisher_refs || !bound) {hktas_dn_reject_locked(42);return;}
        g->batch_cookie=NULL;--g->publisher_refs;--bound;
        g->published=g->count!=0 || !g->completion;hktas_dn_lineage_retire_locked(g);
    }
    if(bound) {hktas_dn_reject_locked(42);return;}
    ReleaseSRWLockExclusive(&g_dn_lock);hktas_dn_leave(DN_job_ScheduleGroups);
}
#endif
