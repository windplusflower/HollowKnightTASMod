#ifndef HKTAS_RESIDENT_CLIP_CURVE_LEDGER_H
#define HKTAS_RESIDENT_CLIP_CURVE_LEDGER_H
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "resident_deadline.h"
#include "resident_fatal_evidence.h"
#include "resident_director_entry_hooks.h"
#include "resident_clip_curve_entry_layout.h"

/* Three actual native authored-curve entries, below public marshalling.
 * Events/settings are D.
 * The fixed Player's generated/clone clips need separate serialized-origin
 * qualification. No curve program is enumerated by this ledger. */
typedef void (__cdecl *hktas_cl_one_fn)(void *);
typedef void (__cdecl *hktas_cl_eight_fn)(void *,const void *,const void *,int32_t,const void *,const void *,unsigned char,unsigned char);
typedef void (__cdecl *hktas_cl_sink)(LONG);
#define HKTAS_CL_THUNK(id, rva, length, hash, cut, bytes, unwind) \
    extern BYTE hktas_cl_resume_##id[]; \
    extern const BYTE hktas_cl_original_##id[]; \
    __asm__(".section .hktascl,\"xr\"\n.p2align 4\n.globl hktas_cl_original_" #id "\nhktas_cl_original_" #id ":\n" \
        ".byte " bytes "\n.byte 0x4c,0x8d,0x1d,4,0,0,0,0x41,0xff,0x63,0\nhktas_cl_end_" #id ":\n" \
        ".globl hktas_cl_resume_" #id "\nhktas_cl_resume_" #id ":\n.quad 0\n" \
        ".section .xdata,\"dr\"\n.p2align 2\nhktas_cl_unwind_" #id ":\n.byte " unwind "\n.p2align 2\n" \
        ".section .pdata,\"dr\"\n.p2align 2\n.rva hktas_cl_original_" #id ",hktas_cl_end_" #id ",hktas_cl_unwind_" #id "\n.text\n");
HKTAS_CL_ENTRIES(HKTAS_CL_THUNK)
#undef HKTAS_CL_THUNK
#define HKTAS_CL_ROW(id,rva,length,hash,cut,bytes,unwind) {rva,length,hash,cut,hktas_cl_original_##id,hktas_cl_resume_##id},
static const hktas_de_entry g_cl_entries[]={HKTAS_CL_ENTRIES(HKTAS_CL_ROW)};
#undef HKTAS_CL_ROW
static BYTE *g_cl_relay;
#define HKTAS_CL_CAPACITY 8192u
typedef struct { uintptr_t pointer; int32_t id; } hktas_cl_pair;
static hktas_cl_pair g_cl_pairs[HKTAS_CL_CAPACITY];
static SRWLOCK g_cl_lock = SRWLOCK_INIT;
static CONDITION_VARIABLE g_cl_changed = CONDITION_VARIABLE_INIT;
static BYTE *g_cl_base;
static void *g_cl_original[3];
static DWORD g_cl_main, g_cl_tls = FLS_OUT_OF_INDEXES;
static BOOL g_cl_installed, g_cl_closed, g_cl_held;
static LONG g_cl_fault, g_cl_unknown, g_cl_attempted_install;
static uint32_t g_cl_active, g_cl_used;
static uint64_t g_cl_previous_epoch;
static uint64_t g_cl_generation, g_cl_epoch, g_cl_started, g_cl_attempted, g_cl_completed;
static hktas_cl_sink g_cl_sink;
static BOOL hktas_cl_image(BOOL installed);
static void hktas_cl_fault_locked(LONG reason)
{
    if (!g_cl_fault) {
        g_cl_fault=reason;
        if (g_cl_sink) g_cl_sink(69);
    }
    WakeAllConditionVariable(&g_cl_changed);
}
typedef struct { uint32_t depth; uint64_t epoch; BOOL counted; } hktas_cl_thread;
static hktas_cl_thread *hktas_cl_thread_current(void)
{ return g_cl_tls==FLS_OUT_OF_INDEXES ? NULL : (hktas_cl_thread *)FlsGetValue(g_cl_tls); }
static BOOL hktas_cl_thread_inherited_locked(hktas_cl_thread *thread)
{
    return thread && thread->counted && thread->depth && g_cl_active>=thread->depth
        && g_cl_closed && !g_cl_held
        && (thread->epoch==g_cl_epoch || thread->epoch==g_cl_previous_epoch);
}
static VOID CALLBACK hktas_cl_thread_exit(void *storage)
{
    hktas_cl_thread *thread=storage;
    if(!thread) return;
    AcquireSRWLockExclusive(&g_cl_lock);
    if(thread->depth) hktas_cl_fault_locked(13);
    ReleaseSRWLockExclusive(&g_cl_lock);
    HeapFree(GetProcessHeap(),0,thread);
}
static void hktas_cl_forbidden(void)
{
    hktas_resident_record_fatal("clip-curve",g_cl_fault);
    TerminateProcess(GetCurrentProcess(),ERROR_INVALID_STATE);
    for (;;) Sleep(1000);
}
static BOOL hktas_cl_read(uintptr_t address,void *value,SIZE_T bytes)
{
    SIZE_T got=0;
    return address && ReadProcessMemory(GetCurrentProcess(),(const void *)address,value,bytes,&got) && got==bytes;
}
static BOOL hktas_cl_read_pair(uintptr_t pointer,int32_t expected,hktas_cl_pair *out)
{
    uintptr_t vt=0; int32_t id=0;
    if (!g_cl_base || !hktas_cl_read(pointer,&vt,8) || vt!=(uintptr_t)(g_cl_base+0x171d808u)
        || !hktas_cl_read(pointer+8,&id,4) || !id || (expected && expected!=id)) return FALSE;
    out->pointer=pointer; out->id=id; return TRUE;
}
static uint32_t hktas_cl_slot(hktas_cl_pair pair)
{
    uint64_t value=((uint64_t)pair.pointer>>3)^((uint64_t)(uint32_t)pair.id*UINT64_C(0x9e3779b97f4a7c15));
    value^=value>>32; return (uint32_t)value&(HKTAS_CL_CAPACITY-1u);
}
static BOOL hktas_cl_dirty_locked(hktas_cl_pair pair,BOOL insert)
{
    uint32_t slot=hktas_cl_slot(pair);
    for (uint32_t count=0;count<HKTAS_CL_CAPACITY;count++,slot=(slot+1u)&(HKTAS_CL_CAPACITY-1u)) {
        hktas_cl_pair *entry=&g_cl_pairs[slot];
        if (!entry->pointer) {
            if (!insert) return FALSE;
            if (g_cl_used==HKTAS_CL_CAPACITY) break;
            *entry=pair; ++g_cl_used; return TRUE;
        }
        if (entry->pointer==pair.pointer && entry->id==pair.id) return TRUE;
    }
    if (insert && !g_cl_unknown) g_cl_unknown=3;
    return FALSE;
}
/* This gate is below generated icall marshalling. Original wrappers own
 * every managed root and raise parameter exceptions before this entry.
 * Fixed native mutation/notification callees return normally; managed log
 * callback exceptions are captured by ScriptingInvocation(out exception). */
static BOOL hktas_cl_enter(void *native_self)
{
#ifdef HKTAS_CL_PARENT_LOCK
    HKTAS_CL_PARENT_LOCK();
#endif
    AcquireSRWLockExclusive(&g_cl_lock);
    hktas_cl_thread *thread=hktas_cl_thread_current();
    BOOL inherited=hktas_cl_thread_inherited_locked(thread);
#ifdef HKTAS_CL_PARENT_COUNTED
    inherited=inherited || (g_cl_closed && !g_cl_held && HKTAS_CL_PARENT_COUNTED());
    HKTAS_CL_PARENT_UNLOCK();
#endif
    /* A timed-out/faulted closed gate NEVER passes an original call. Other
     * workers remain parked until joint release; poison retains closed. */
    while (g_cl_closed && !inherited) {
        if (GetCurrentThreadId()==g_cl_main) {
            hktas_cl_fault_locked(4); ReleaseSRWLockExclusive(&g_cl_lock); hktas_cl_forbidden();
        }
        uint64_t elapsed=GetTickCount64()-g_cl_started;
        if (elapsed>=g_resident_wall_limit) hktas_cl_fault_locked(5);
        DWORD wait=elapsed>=g_resident_wall_limit ? 100u : (DWORD)(g_resident_wall_limit-elapsed);
        if (!SleepConditionVariableSRW(&g_cl_changed,&g_cl_lock,wait,0) && GetLastError()!=ERROR_TIMEOUT)
            hktas_cl_fault_locked(6);
    }
    if (!g_cl_installed || g_cl_fault) { ReleaseSRWLockExclusive(&g_cl_lock); return FALSE; }
    hktas_cl_pair pair={0};
    BOOL known=hktas_cl_read_pair((uintptr_t)native_self,0,&pair);
    if (!known && !g_cl_unknown) g_cl_unknown=1;
    if(!thread) {
        thread=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(*thread));
        if(!thread || !FlsSetValue(g_cl_tls,thread)) {
            if(thread) HeapFree(GetProcessHeap(),0,thread);
            hktas_cl_fault_locked(2);ReleaseSRWLockExclusive(&g_cl_lock);return FALSE;
        }
    }
    if (g_cl_active==UINT32_MAX || thread->depth==UINT32_MAX) {
        hktas_cl_fault_locked(2); ReleaseSRWLockExclusive(&g_cl_lock); return FALSE;
    }
    if (known) hktas_cl_dirty_locked(pair,TRUE);
    if(!thread->depth) {thread->epoch=g_cl_epoch;thread->counted=TRUE;}
    ++thread->depth;
    ++g_cl_active; ++g_cl_attempted;
    ReleaseSRWLockExclusive(&g_cl_lock); return TRUE;
}
static void hktas_cl_leave(BOOL entered)
{
    if (!entered) return;
    AcquireSRWLockExclusive(&g_cl_lock);
    hktas_cl_thread *thread=hktas_cl_thread_current();
    if (!g_cl_active || !thread || !thread->depth || !thread->counted) hktas_cl_fault_locked(7);
    else {
        --g_cl_active;--thread->depth;++g_cl_completed;
        if(!thread->depth) thread->counted=FALSE;
    }
    WakeAllConditionVariable(&g_cl_changed); ReleaseSRWLockExclusive(&g_cl_lock);
#ifdef HKTAS_CL_WAKE_PARENT
    HKTAS_CL_WAKE_PARENT();
#endif
}
static void __cdecl hktas_cl_set_curve(void *self,const void *path,const void *type,int32_t script,const void *property,const void *curve,unsigned char combine,unsigned char update)
{
    BOOL entered=hktas_cl_enter(self);
    ((hktas_cl_eight_fn)g_cl_original[0])(self,path,type,script,property,curve,combine,update);
    hktas_cl_leave(entered);
}
static void __cdecl hktas_cl_ensure(void *self)
{
    BOOL entered=hktas_cl_enter(self);
    ((hktas_cl_one_fn)g_cl_original[1])(self); hktas_cl_leave(entered);
}
static void __cdecl hktas_cl_clear(void *self)
{
    BOOL entered=hktas_cl_enter(self);
    ((hktas_cl_one_fn)g_cl_original[2])(self); hktas_cl_leave(entered);
}
static void *g_cl_wrappers[3]={(void *)hktas_cl_set_curve,(void *)hktas_cl_ensure,(void *)hktas_cl_clear};
static BOOL hktas_cl_image(BOOL installed)
{
    if (!g_cl_base) return FALSE;
    IMAGE_DOS_HEADER *dos=(IMAGE_DOS_HEADER *)g_cl_base;
    if (dos->e_magic!=IMAGE_DOS_SIGNATURE || dos->e_lfanew<64 || dos->e_lfanew>4096) return FALSE;
    IMAGE_NT_HEADERS64 *nt=(IMAGE_NT_HEADERS64 *)(g_cl_base+dos->e_lfanew);
    if (nt->Signature!=IMAGE_NT_SIGNATURE || nt->FileHeader.Machine!=IMAGE_FILE_MACHINE_AMD64
        || nt->FileHeader.TimeDateStamp!=0x6008c66eu || nt->OptionalHeader.Magic!=IMAGE_NT_OPTIONAL_HDR64_MAGIC
        || nt->OptionalHeader.SizeOfImage!=0x1bc7000u) return FALSE;
    IMAGE_DATA_DIRECTORY debug=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG];
    if (!debug.VirtualAddress || debug.Size<sizeof(IMAGE_DEBUG_DIRECTORY) || debug.Size>4096u
        || debug.VirtualAddress>0x1bc7000u-debug.Size) return FALSE;
    const BYTE guid[]={0x90,0xbe,0x34,0x03,0xdd,0x63,0x44,0x46,0xbe,0xe1,0xfc,0x8d,0x56,0xc8,0x40,0xc2};
    BOOL matched=FALSE;
    for (DWORD at=0;at+sizeof(IMAGE_DEBUG_DIRECTORY)<=debug.Size;at+=sizeof(IMAGE_DEBUG_DIRECTORY)) {
        IMAGE_DEBUG_DIRECTORY *row=(IMAGE_DEBUG_DIRECTORY *)(g_cl_base+debug.VirtualAddress+at);
        if (row->Type!=IMAGE_DEBUG_TYPE_CODEVIEW || row->SizeOfData<24 || row->AddressOfRawData>0x1bc7000u-24) continue;
        BYTE *cv=g_cl_base+row->AddressOfRawData; DWORD age; memcpy(&age,cv+20,4);
        if (!memcmp(cv,"RSDS",4) && !memcmp(cv+4,guid,16) && age==1) matched=TRUE;
    }
    if (!matched) return FALSE;
    return hktas_de_verify_table(g_cl_base,g_cl_relay,(const void *const *)g_cl_wrappers,installed,g_cl_entries,3);
}
static BOOL install_resident_clip_curve_ledger(BYTE *base,DWORD main_thread,hktas_cl_sink sink)
{
    if (InterlockedCompareExchange(&g_cl_attempted_install,1,0) || !base || !main_thread
        || GetCurrentThreadId()!=main_thread || GetModuleHandleW(L"mono-2.0-bdwgc.dll") || !sink) return FALSE;
    g_cl_base=base;
    if (!hktas_cl_image(FALSE)) return FALSE;
    g_cl_tls=FlsAlloc(hktas_cl_thread_exit); if (g_cl_tls==FLS_OUT_OF_INDEXES) return FALSE;
    g_cl_main=main_thread; g_cl_sink=sink;
    uintptr_t relay_start=((uintptr_t)base+0x1bc7000u+65535u)&~(uintptr_t)65535u;
    for (uintptr_t offset=0;offset<0x60000000u && !g_cl_relay;offset+=65536u)
        g_cl_relay=VirtualAlloc((void *)(relay_start+offset),4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
    if (!g_cl_relay) return FALSE;
    for (unsigned i=0;i<3;i++) g_cl_original[i]=(void *)g_cl_entries[i].original;
    if (!hktas_de_install_table(base,g_cl_relay,(const void *const *)g_cl_wrappers,g_cl_entries,3) || !hktas_cl_image(TRUE)) return FALSE;
    g_cl_generation=((uint64_t)GetCurrentProcessId()<<32)^GetTickCount64();
    if (!g_cl_generation) g_cl_generation=1;
    g_cl_installed=TRUE; return TRUE;
}
static BOOL hktas_cl_held_locked(uint64_t token)
{
    return g_cl_installed && !g_cl_fault && g_cl_closed && g_cl_held && token && token==g_cl_epoch
        && !g_cl_active && GetCurrentThreadId()==g_cl_main
        && GetTickCount64()-g_cl_started<g_resident_wall_limit && hktas_cl_image(TRUE);
}
static BOOL hktas_cl_begin(uint64_t token,uint64_t started,BOOL drain)
{
    AcquireSRWLockExclusive(&g_cl_lock);
    if (!g_cl_installed || g_cl_fault || g_cl_closed || !token || token<=g_cl_epoch
        || GetCurrentThreadId()!=g_cl_main || (hktas_cl_thread_current() && hktas_cl_thread_current()->depth) || !hktas_cl_image(TRUE)) {
        hktas_cl_fault_locked(8); ReleaseSRWLockExclusive(&g_cl_lock); return FALSE;
    }
    g_cl_closed=TRUE;g_cl_held=FALSE;g_cl_previous_epoch=g_cl_epoch; g_cl_epoch=token; g_cl_started=started;
    if(!drain) {ReleaseSRWLockExclusive(&g_cl_lock);return TRUE;}
    while (g_cl_active && !g_cl_fault) {
        uint64_t elapsed=GetTickCount64()-started;
        if (elapsed>=g_resident_wall_limit) break;
        if (!SleepConditionVariableSRW(&g_cl_changed,&g_cl_lock,(DWORD)(g_resident_wall_limit-elapsed),0)
            && GetLastError()!=ERROR_TIMEOUT) break;
    }
    g_cl_held=!g_cl_active && !g_cl_fault;
    BOOL ok=hktas_cl_held_locked(token);
    if (!ok) hktas_cl_fault_locked(9);
    ReleaseSRWLockExclusive(&g_cl_lock); return ok;
}
static BOOL hktas_cl_require(uint64_t token)
{
    AcquireSRWLockExclusive(&g_cl_lock); BOOL ok=hktas_cl_held_locked(token);
    if (!ok && g_cl_closed) hktas_cl_fault_locked(10);
    ReleaseSRWLockExclusive(&g_cl_lock); return ok;
}
typedef struct {
    uint32_t magic,version,bytes,installed_mask;
    uint64_t generation,epoch,attempted,completed;
    int32_t fault; uint32_t active,closed,abi_valid;
} hktas_cl_receipt;
typedef char hktas_cl_receipt_size[(sizeof(hktas_cl_receipt)==64 && offsetof(hktas_cl_receipt,fault)==48)?1:-1];
static hktas_cl_receipt hktas_cl_receipt_locked(void)
{
    hktas_cl_receipt receipt={0x314c4343u,1u,64u,g_cl_installed ? 7u : 0u,g_cl_generation,g_cl_epoch,
        g_cl_attempted,g_cl_completed,g_cl_fault ? g_cl_fault : g_cl_unknown ? 100+g_cl_unknown : 0,g_cl_active,(uint32_t)g_cl_closed,(uint32_t)hktas_cl_image(TRUE)};
    return receipt;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_CopyResidentClipCurveLedgerStatus(BYTE *output,uint32_t bytes)
{
    if (!output || bytes!=64u) return 0;
    AcquireSRWLockExclusive(&g_cl_lock); hktas_cl_receipt receipt=hktas_cl_receipt_locked();
    memcpy(output,&receipt,64); ReleaseSRWLockExclusive(&g_cl_lock); return 64;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_QueryResidentClipCurveEligibility(uint64_t token,uintptr_t pointer,int32_t id,BYTE *output,uint32_t bytes)
{
    if (!output || bytes!=64u) return 0;
    AcquireSRWLockExclusive(&g_cl_lock); LONG result=0; hktas_cl_pair pair={0};
    if (hktas_cl_held_locked(token) && !g_cl_unknown && id && hktas_cl_read_pair(pointer,id,&pair)) result=hktas_cl_dirty_locked(pair,FALSE) ? 2 : 1;
    hktas_cl_receipt receipt=hktas_cl_receipt_locked(); memcpy(output,&receipt,64);
    ReleaseSRWLockExclusive(&g_cl_lock); return result;
}
#endif
