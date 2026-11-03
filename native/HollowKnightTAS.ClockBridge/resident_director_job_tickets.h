#ifndef HKTAS_RESIDENT_DIRECTOR_JOB_TICKETS_H
#define HKTAS_RESIDENT_DIRECTOR_JOB_TICKETS_H
/* The original PrepareFrame worker reads an indexed 80-byte row before it
 * calls the graph. Bind every index before publishing the callback to the
 * job system. An already published child may finish while admission drains.
 * Its originating fence, version and index cannot be inherited by another
 * publisher or worker. Other engine callback types retain their original
 * scheduler path without attaching a Director ticket. */
typedef void (__cdecl *hktas_dn_job_fn)(void*,unsigned);
typedef void (__cdecl *hktas_dn_done_fn)(void*);
typedef struct {
    uintptr_t graph,node,native_job;
    DWORD version;
    BYTE state;
} hktas_dn_job_row;
typedef struct hktas_dn_job_ticket {
    struct hktas_dn_job_ticket *next;
    void *fence,*data,*group,*queue,*created_group;
    hktas_dn_thread *owner;
    uint64_t generation;
    DWORD version,count,returned,finished;
    BOOL published,completing,completion_seen;
    hktas_dn_job_row rows[];
} hktas_dn_job_ticket;
typedef struct hktas_dn_invocation {
    hktas_dn_job_ticket *ticket;
    unsigned index;
    void *previous;
} hktas_dn_invocation;
static hktas_dn_job_ticket *g_dn_job_tickets;
static volatile LONG g_dn_job_count;
static unsigned g_dn_job_rows;
static uint64_t g_dn_job_published,g_dn_job_retired,g_dn_job_started,g_dn_job_finished;
static BOOL hktas_dn_read(uintptr_t address,void *bytes,size_t count);
static uintptr_t hktas_dn_ptr(const BYTE *bytes);
static uint32_t hktas_dn_u32(const BYTE *bytes);
static uint64_t hktas_dn_u64(const BYTE *bytes);
static BOOL hktas_dn_job_identity(const void *data,unsigned index,hktas_dn_job_row *row)
{
    BYTE node[16];uintptr_t graph=0,handle=0;
    uintptr_t address=(uintptr_t)data,offset=(uintptr_t)index*80u+72u;
    if(address>UINTPTR_MAX-offset || !hktas_dn_read(address+offset,&graph,8) || !graph || graph>UINTPTR_MAX-16u
        || !hktas_dn_read(graph+16u,&handle,8)) return FALSE;
    row->graph=graph;row->node=handle;row->version=0;
    if(!handle) return TRUE;
    if(handle>UINTPTR_MAX-48u || !hktas_dn_read(handle+32u,node,16) || hktas_dn_ptr(node+8)!=graph) return FALSE;
    row->version=hktas_dn_u32(node);return TRUE;
}
static hktas_dn_job_ticket *hktas_dn_job_by_fence(void *fence)
{
    for(hktas_dn_job_ticket *t=g_dn_job_tickets;t;t=t->next) if(!t->completion_seen && t->fence==fence) return t;
    return NULL;
}
static hktas_dn_job_ticket *hktas_dn_job_by_data(void *data)
{
    for(hktas_dn_job_ticket *t=g_dn_job_tickets;t;t=t->next) if(!t->completion_seen && t->data==data) return t;
    return NULL;
}
static BOOL hktas_dn_job_disjoint(void *fence,void *data,unsigned count)
{
    uintptr_t a=(uintptr_t)fence,b=(uintptr_t)data;size_t length=(size_t)count*80u;
    if(a>UINTPTR_MAX-16 || b>UINTPTR_MAX-length || (a<b+length && b<a+16)) return FALSE;
    for(hktas_dn_job_ticket *t=g_dn_job_tickets;t;t=t->next) {
        if(t->completion_seen) continue;
        uintptr_t f=(uintptr_t)t->fence,j=(uintptr_t)t->data,end=j+(size_t)t->count*80u;
        if((a<f+16 && f<a+16) || (a<end && j<a+16) || (b<f+16 && f<b+length) || (b<end && j<b+length)) return FALSE;
    }
    return TRUE;
}
static BOOL hktas_dn_job_retire_locked(hktas_dn_job_ticket *t)
{
    if(!t || !t->published || !t->completion_seen || t->owner || t->returned!=t->count || t->finished!=t->count
        || g_dn_job_count<=0 || g_dn_job_rows<t->count)
        return FALSE;
    hktas_dn_job_ticket **at=&g_dn_job_tickets;
    while(*at && *at!=t) at=&(*at)->next;
    if(*at!=t) return FALSE;
    *at=t->next;g_dn_job_rows-=t->count;
    InterlockedDecrement(&g_dn_job_count);++g_dn_job_retired;
    HeapFree(GetProcessHeap(),0,t);WakeAllConditionVariable(&g_dn_cv);return TRUE;
}
static void __cdecl dn_job_Schedule(void *fence,hktas_dn_job_fn callback,void *data,int count,hktas_dn_done_fn done,int priority)
{
    typedef void (__cdecl *schedule_fn)(void*,hktas_dn_job_fn,void*,int,hktas_dn_done_fn,int);
    schedule_fn original=(schedule_fn)(void*)hktas_de_original_job_Schedule;
    if((BYTE*)(void*)callback!=g_dn_base+hktas_de_entries[HKTAS_DE_job_PrepareFrame].rva) {
        original(fence,callback,data,count,done,priority);return;
    }
    hktas_dn_enter(DN_job_Schedule);
    AcquireSRWLockExclusive(&g_dn_lock);
    hktas_dn_thread *owner=(hktas_dn_thread*)FlsGetValue(g_dn_fls);BYTE before[16];
    if(!owner || !owner->depth || !fence || !data || count<1 || (unsigned)count>HKTAS_DN_LIMIT-g_dn_job_rows
        || done || !hktas_dn_read((uintptr_t)fence,before,16) || hktas_dn_ptr(before) || hktas_dn_u32(before+8)
        || !hktas_dn_job_disjoint(fence,data,(unsigned)count)) {hktas_dn_reject_locked(30);return;}
    size_t size=offsetof(hktas_dn_job_ticket,rows)+(size_t)count*sizeof(hktas_dn_job_row);
    hktas_dn_job_ticket *t=(hktas_dn_job_ticket*)HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,size);
    if(!t) {hktas_dn_reject_locked(30);return;}
    t->fence=fence;t->data=data;t->count=(DWORD)count;t->owner=owner;
    for(unsigned i=0;i<t->count;++i) if(!hktas_dn_job_identity(data,i,&t->rows[i])) {
        HeapFree(GetProcessHeap(),0,t);hktas_dn_reject_locked(31);return;
    }
    void *previous=owner->publishing;owner->publishing=t;
    ++owner->tickets;g_dn_job_rows+=t->count;t->next=g_dn_job_tickets;g_dn_job_tickets=t;
    InterlockedIncrement(&g_dn_job_count);++g_dn_job_published;ReleaseSRWLockExclusive(&g_dn_lock);
    original(fence,callback,data,count,done,priority);
    AcquireSRWLockExclusive(&g_dn_lock);BYTE after[16];
    if(owner->publishing!=t || !hktas_dn_read((uintptr_t)fence,after,16) || hktas_dn_job_by_fence(fence)!=t || t->published
        || !t->group || (void*)hktas_dn_ptr(after)!=t->group || hktas_dn_u32(after+8)!=t->version) {hktas_dn_reject_locked(32);return;}
    owner->publishing=previous;t->published=TRUE;
    ReleaseSRWLockExclusive(&g_dn_lock);hktas_dn_leave(DN_job_Schedule);
}
static void __cdecl dn_job_PrepareFrame(void *data,unsigned index)
{
    typedef void (__cdecl *prepare_fn)(void*,unsigned);
    AcquireSRWLockExclusive(&g_dn_lock);
    hktas_dn_thread *thread=(hktas_dn_thread*)FlsGetValue(g_dn_fls);
    hktas_dn_invocation *scope=thread?(hktas_dn_invocation*)thread->executing:NULL;
    hktas_dn_job_ticket *t=scope?scope->ticket:NULL;hktas_dn_job_row actual={0};
    if(!t || scope->index!=index || t->data!=data || index>=t->count || t->rows[index].state!=1 || !hktas_dn_job_identity(data,index,&actual)
        || t->rows[index].graph!=actual.graph || t->rows[index].node!=actual.node || t->rows[index].version!=actual.version) {
        hktas_dn_reject_locked(33);return;
    }
    t->rows[index].state=2;++g_dn_job_started;
    hktas_dn_enter_locked(DN_job_PrepareFrame,TRUE);
    ((prepare_fn)(void*)hktas_de_original_job_PrepareFrame)(data,index);
    AcquireSRWLockExclusive(&g_dn_lock);
    if(thread->executing!=scope || hktas_dn_job_by_data(data)!=t || t->rows[index].state!=2) {hktas_dn_reject_locked(34);return;}
    t->rows[index].state=3;++t->returned;++g_dn_job_finished;
    ReleaseSRWLockExclusive(&g_dn_lock);hktas_dn_leave(DN_job_PrepareFrame);
}
#include "resident_director_job_lineage.h"
static void* __cdecl dn_job_Group(void *queue,void *out,void *group,int priority,BYTE inline_mode)
{
    typedef void* (__cdecl *group_fn)(void*,void*,void*,int,BYTE);
    group_fn original=(group_fn)(void*)hktas_de_original_job_Group;
    BYTE body[48];
    if(!hktas_dn_read((uintptr_t)group,body,sizeof(body))) {
        AcquireSRWLockExclusive(&g_dn_lock);hktas_dn_reject_locked(37);return NULL;
    }
    unsigned count=hktas_dn_u32(body+16)&0x7fffffffu;
    if(hktas_dn_ptr(body+32) && count) --count;
    uintptr_t cursor=hktas_dn_ptr(body),last=hktas_dn_ptr(body+24),callback=(uintptr_t)g_dn_base+hktas_de_entries[HKTAS_DE_job_PrepareFrame].rva;

    AcquireSRWLockExclusive(&g_dn_lock);
    hktas_dn_thread *owner=(hktas_dn_thread*)FlsGetValue(g_dn_fls);
    hktas_dn_job_ticket *t=owner?(hktas_dn_job_ticket*)owner->publishing:NULL;
    unsigned matches=0;
    for(unsigned n=0;cursor;n++) {
        BYTE job[40];if(n>=HKTAS_DN_LIMIT || !hktas_dn_read(cursor,job,sizeof(job))) {hktas_dn_reject_locked(37);return NULL;}
        if(hktas_dn_ptr(job+8)==callback) {
            unsigned bits=hktas_dn_u32(job+24),index=bits&0x1fffffffu;
            if(!t || t->group || t->created_group!=group || count!=t->count || hktas_dn_ptr(body+32) || !(bits&0x20000000u) || index>=t->count
                || t->rows[index].native_job || hktas_dn_ptr(job+16)!=(uintptr_t)t->data || hktas_dn_ptr(job+32)!=(uintptr_t)group) {
                hktas_dn_reject_locked(37);return NULL;
            }
            t->rows[index].native_job=cursor;++matches;
        }
        if(cursor==last) break;
        cursor=hktas_dn_ptr(job);
    }
    if(hktas_dn_u64(body+8)>UINT64_MAX-2u) {hktas_dn_reject_locked(37);return NULL;}
    hktas_dn_supersede_group_locked(queue,group,hktas_dn_u64(body+8)+2);
    if(!matches) {
        hktas_dn_lineage *family=hktas_dn_find_cohort(queue,group,hktas_dn_u64(body+8)+2);
        if(!family && (!owner || !owner->depth)) {ReleaseSRWLockExclusive(&g_dn_lock);return original(queue,out,group,priority,inline_mode);}
        if(!family) family=hktas_dn_lineage_bind_locked(queue,group,body,count,owner,TRUE);
        else if(family->published || !hktas_dn_assemble_lineage_locked(family,body,count)) {hktas_dn_reject_locked(39);return NULL;}
        ++family->publisher_refs;hktas_dn_enter_locked(DN_job_Group,TRUE);
        void *result=original(queue,out,group,priority,inline_mode);
        AcquireSRWLockExclusive(&g_dn_lock);BYTE issued[16];
        if(result!=out || !hktas_dn_read((uintptr_t)out,issued,16) || hktas_dn_ptr(issued)!=(uintptr_t)group
            || hktas_dn_u32(issued+8)!=(DWORD)family->generation) {hktas_dn_reject_locked(39);return NULL;}
        --family->publisher_refs;family->published=count || (!hktas_dn_ptr(body+32) && !hktas_dn_u32(body+16));hktas_dn_lineage_retire_locked(family);ReleaseSRWLockExclusive(&g_dn_lock);hktas_dn_leave(DN_job_Group);return result;
    }
    if(matches!=t->count || cursor!=last) {hktas_dn_reject_locked(37);return NULL;}
    t->group=group;t->queue=queue;t->generation=hktas_dn_u64(body+8)+2u;t->version=(DWORD)t->generation;
    hktas_dn_enter_locked(DN_job_Group,TRUE);
    void *result=original(queue,out,group,priority,inline_mode);
    AcquireSRWLockExclusive(&g_dn_lock);BYTE issued[16];
    if(result!=out || !hktas_dn_read((uintptr_t)out,issued,16) || hktas_dn_ptr(issued)!=(uintptr_t)group || hktas_dn_u32(issued+8)!=t->version) {
        hktas_dn_reject_locked(37);return NULL;
    }
    ReleaseSRWLockExclusive(&g_dn_lock);hktas_dn_leave(DN_job_Group);return result;
}
static int __cdecl dn_job_Exec(void *queue,void *job,uint64_t generation,int decrement,BYTE inline_mode)
{
    typedef int (__cdecl *exec_fn)(void*,void*,uint64_t,int,BYTE);
    exec_fn original=(exec_fn)(void*)hktas_de_original_job_Exec;
    BYTE info[40];
    if(!hktas_dn_read((uintptr_t)job,info,sizeof(info))) {AcquireSRWLockExclusive(&g_dn_lock);hktas_dn_reject_locked(38);return 0;}
    if(hktas_dn_ptr(info+8)!=(uintptr_t)g_dn_base+hktas_de_entries[HKTAS_DE_job_PrepareFrame].rva)
        return hktas_dn_lineage_exec(original,queue,job,generation,decrement,inline_mode,info,__builtin_return_address(0));
    AcquireSRWLockExclusive(&g_dn_lock);
    unsigned bits=hktas_dn_u32(info+24),index=bits&0x1fffffffu;
    hktas_dn_job_ticket *t=hktas_dn_job_by_data((void*)hktas_dn_ptr(info+16));
    if(!t || t->queue!=queue || t->group!=(void*)hktas_dn_ptr(info+32) || generation!=t->generation
        || !(bits&0x20000000u) || index>=t->count || t->rows[index].state || t->rows[index].native_job!=(uintptr_t)job) {
        hktas_dn_reject_locked(38);return 0;
    }
    t->rows[index].state=1;hktas_dn_enter_locked(DN_job_Exec,TRUE);
    hktas_dn_thread *thread=(hktas_dn_thread*)FlsGetValue(g_dn_fls);
    hktas_dn_invocation scope={t,index,thread->executing};thread->executing=&scope;
    int result=original(queue,job,generation,decrement,inline_mode);
    AcquireSRWLockExclusive(&g_dn_lock);
    if(thread->executing!=&scope || t->rows[index].state!=3) {hktas_dn_reject_locked(38);return 0;}
    thread->executing=scope.previous;t->rows[index].state=4;++t->finished;
    if(t->completion_seen && t->finished==t->count && !hktas_dn_job_retire_locked(t)) {hktas_dn_reject_locked(38);return 0;}
    ReleaseSRWLockExclusive(&g_dn_lock);hktas_dn_leave(DN_job_Exec);return result;
}
static void __cdecl dn_job_Complete(void *fence,int mode)
{
    typedef void (__cdecl *complete_fn)(void*,int);
    complete_fn original=(complete_fn)(void*)hktas_de_original_job_Complete;
    if(!InterlockedCompareExchange(&g_dn_job_count,0,0)) {original(fence,mode);return;}
    AcquireSRWLockExclusive(&g_dn_lock);hktas_dn_job_ticket *t=hktas_dn_job_by_fence(fence);
    if(!t) {ReleaseSRWLockExclusive(&g_dn_lock);original(fence,mode);return;}
    BYTE before[16];
    if(!t->published || t->completing || t->owner!=(hktas_dn_thread*)FlsGetValue(g_dn_fls)
        || !hktas_dn_read((uintptr_t)fence,before,16) || (void*)hktas_dn_ptr(before)!=t->group || hktas_dn_u32(before+8)!=t->version) {
        hktas_dn_reject_locked(35);return;
    }
    t->completing=TRUE;hktas_dn_enter_locked(DN_job_Complete,TRUE);
    original(fence,mode);
    AcquireSRWLockExclusive(&g_dn_lock);BYTE after[16];
    /* CompleteFenceInternal waits on the issued JobGroupID and clears both
     * the group pointer and its version in the original fence object. */
    if(!hktas_dn_read((uintptr_t)fence,after,16) || hktas_dn_ptr(after) || hktas_dn_u32(after+8)
        || t->returned!=t->count || !t->owner->tickets) {hktas_dn_reject_locked(36);return;}
    /* The native group is complete before Exec returns the JobInfo to its
     * pool. Let the publisher resume immediately with its own fence cleared.
     * Only the independent admission ticket lives on until every real Exec
     * tail has returned. Its former fence/data may now legitimately be reused. */
    --t->owner->tickets;t->owner=NULL;t->completion_seen=TRUE;
    if(t->finished==t->count && !hktas_dn_job_retire_locked(t)) {hktas_dn_reject_locked(36);return;}
    ReleaseSRWLockExclusive(&g_dn_lock);hktas_dn_leave(DN_job_Complete);
}
#endif
