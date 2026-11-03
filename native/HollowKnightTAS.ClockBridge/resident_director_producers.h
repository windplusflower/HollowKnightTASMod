#ifndef HKTAS_RESIDENT_DIRECTOR_PRODUCERS_H
#define HKTAS_RESIDENT_DIRECTOR_PRODUCERS_H
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>

#include "resident_director_entry_hooks.h"
/* Guard the native method entries, including indirect calls and the actual
 * PrepareFrameJobFunc callback. The retained measured call sites provide
 * an independent consistency check. Inline writers, outstanding worker
 * ownership and other mutator domains still block complete admission. */
#define HKTAS_DN_GAP_ADDRESS_OR_INLINE 1u
#define HKTAS_DN_GAP_WORKER_DISPATCH 2u
#define HKTAS_DN_GAP_UNENROLLED_MUTATORS 4u
#define HKTAS_DN_GAPS 7u
#define HKTAS_DN_WALL 9000u
#define HKTAS_DN_LIMIT 100000u
#define HKTAS_DN_MANAGER 0x196f0c0u
#define HKTAS_DN_IMAGE 0x1bc7000u
/* Generated for UnityPlayer RSDS 0334be90-63dd-4644-bee1-fc8d56c840c2/1. */
#define HKTAS_DIR_EDGES(X) \
 X(0xd94e7u, 0xe9u, graph_Play) \
 X(0xd94f3u, 0xe9u, graph_Play) \
 X(0xdb43cu, 0xe8u, graph_DestroyPlayableDeferred) \
 X(0xdb4e6u, 0xe8u, manager_ScheduleGraphDestroy) \
 X(0xdb633u, 0xe8u, graph_Stop) \
 X(0xdbc23u, 0xe8u, graph_DisableMultithread) \
 X(0xdbcdau, 0xe8u, graph_Play) \
 X(0xdbd65u, 0xe8u, manager_ConstructPlayableGraph) \
 X(0xdbedbu, 0xe8u, graph_SetTimeUpdateMode) \
 X(0xe144du, 0xe8u, graph_Stop) \
 X(0xe4629u, 0xe8u, graph_Stop) \
 X(0xe4661u, 0xe8u, graph_SetTimeUpdateMode) \
 X(0xe4693u, 0xe8u, graph_Play) \
 X(0xe8147u, 0xe8u, graph_PrepareFrame) \
 X(0x10a3d2u, 0xe8u, graph_DisableMultithread) \
 X(0x10fc6du, 0xe8u, graph_DestroySubGraphDeferred) \
 X(0x110132u, 0xe8u, graph_DisableMultithread) \
 X(0x1103a9u, 0xe8u, graph_DisableMultithread) \
 X(0x110463u, 0xe8u, graph_DisableMultithread) \
 X(0x1104d2u, 0xe8u, graph_DisableMultithread) \
 X(0x110542u, 0xe8u, graph_DisableMultithread) \
 X(0x11066bu, 0xe8u, graph_DisableMultithread) \
 X(0x110711u, 0xe8u, graph_DisableMultithread) \
 X(0x15116du, 0xe8u, graph_DisableMultithread) \
 X(0x151274u, 0xe8u, graph_DisableMultithread) \
 X(0x151354u, 0xe8u, graph_DisableMultithread) \
 X(0x151aa9u, 0xe8u, graph_DestroyOutput) \
 X(0x151b6bu, 0xe8u, graph_SetTimeUpdateMode) \
 X(0x1526b4u, 0xe8u, graph_DisableMultithread) \
 X(0x152794u, 0xe8u, graph_DisableMultithread) \
 X(0x152880u, 0xe8u, graph_DisableMultithread) \
 X(0x152984u, 0xe8u, graph_DisableMultithread) \
 X(0x152a97u, 0xe8u, graph_DisableMultithread) \
 X(0x152b83u, 0xe8u, graph_DisableMultithread) \
 X(0x15be3cu, 0xe8u, manager_ScheduleGraphDestroy) \
 X(0x15beeau, 0xe8u, manager_QueueAndEvaluateGraph) \
 X(0x15bf82u, 0xe8u, graph_EvaluateDeferred) \
 X(0x15c2b5u, 0xe8u, manager_ConstructPlayableGraph) \
 X(0x15c2ccu, 0xe8u, graph_SetTimeUpdateMode) \
 X(0x15c5f5u, 0xe8u, manager_ScheduleGraphDestroy) \
 X(0x15c7bbu, 0xe8u, graph_Play) \
 X(0x15cacau, 0xe8u, graph_Stop) \
 X(0x15cb90u, 0xe9u, manager_ProcessPlayStateChanges) \
 X(0x15d0b4u, 0xe8u, manager_ProcessPlayStateChanges) \
 X(0x15d147u, 0xe8u, manager_ProcessPlayStateChanges) \
 X(0x15d1cbu, 0xe8u, graph_Play) \
 X(0x15d52fu, 0xe8u, graph_Stop) \
 X(0x15d927u, 0xe9u, graph_SetTimeUpdateMode) \
 X(0x328d60u, 0xe9u, manager_ResetFrameTiming) \
 X(0x76a862u, 0xe8u, manager_SetApplicationPause) \
 X(0x891ad5u, 0xe8u, pool_AllocateInBucket) \
 X(0x891c3au, 0xe8u, pool_Free) \
 X(0x891c5au, 0xe8u, manager_FreeNode) \
 X(0x891ca0u, 0xe8u, pool_Free) \
 X(0x891cc0u, 0xe8u, manager_FreeNode) \
 X(0x891edfu, 0xe8u, manager_AcquireOutputNode) \
 X(0x891fd0u, 0xe8u, manager_AcquireNode) \
 X(0x89235au, 0xe8u, manager_AcquireNode) \
 X(0x892491u, 0xe8u, manager_FreeOutputNode) \
 X(0x8930f6u, 0xe8u, manager_CacheScriptingMethodsForClass) \
 X(0x89329fu, 0xe8u, manager_AcquireNode) \
 X(0x8932efu, 0xe8u, manager_AcquireOutputNode) \
 X(0x8935c3u, 0xe8u, manager_AcquireNode) \
 X(0x8936a2u, 0xe8u, manager_AcquireNode) \
 X(0x893874u, 0xe8u, manager_AcquireNode) \
 X(0x893a7du, 0xe8u, graph_DisableMultithread) \
 X(0x893b52u, 0xe8u, manager_AcquireNode) \
 X(0x893c60u, 0xe8u, manager_AcquireNode) \
 X(0x893da1u, 0xe8u, manager_AcquireNode) \
 X(0x893e4au, 0xe8u, manager_AcquireNode) \
 X(0x893ef3u, 0xe8u, manager_AcquireNode) \
 X(0x894064u, 0xe9u, graph_SetHasPendingNotifications) \
 X(0x894678u, 0xe8u, pool_SwitchBucket) \
 X(0x894bc4u, 0xe8u, manager_AcquireNode) \
 X(0x894c30u, 0xe8u, graph_DisableMultithread) \
 X(0x894d32u, 0xe8u, manager_AcquireNode) \
 X(0x894f9eu, 0xe8u, manager_AcquireNode) \
 X(0x895ca6u, 0xe8u, pool_Destruct) \
 X(0x895ddeu, 0xe8u, pool_Destruct) \
 X(0x89608cu, 0xe8u, pool_CreateItem) \
 X(0x8960d0u, 0xe8u, manager_RebuildPrepareFrameJobs) \
 X(0x896ce1u, 0xe8u, graph_SchedulePlayableDestruction) \
 X(0x896d82u, 0xe8u, manager_ScheduleGraphCommand) \
 X(0x896da9u, 0xe8u, graph_SchedulePlayableDestruction) \
 X(0x896db6u, 0xe9u, graph_DestroyPendingPlayables) \
 X(0x896e26u, 0xe8u, graph_SchedulePlayableDestruction) \
 X(0x896e51u, 0xe8u, graph_ScheduleSubgraphDestruction) \
 X(0x896ef2u, 0xe8u, manager_ScheduleGraphCommand) \
 X(0x896f19u, 0xe8u, graph_ScheduleSubgraphDestruction) \
 X(0x896f26u, 0xe9u, graph_DestroyPendingPlayables) \
 X(0x8970b8u, 0xe8u, manager_ScheduleGraphCommand) \
 X(0x89731du, 0xe8u, graph_PrepareFrame) \
 X(0x89735du, 0xe8u, graph_FireConnectionHashChanged) \
 X(0x897637u, 0xe8u, graph_DispatchOutputNotifications) \
 X(0x897646u, 0xe8u, graph_DestroyPendingPlayables) \
 X(0x8977e2u, 0xe9u, graph_Evaluate) \
 X(0x89780au, 0xe8u, manager_RebuildPrepareFrameJobs) \
 X(0x89797bu, 0xe8u, graph_PrepareFrame) \
 X(0x897b14u, 0xe8u, manager_ProcessPlayStateChanges) \
 X(0x897b1fu, 0xe8u, manager_ExecutePrepareFrames) \
 X(0x897b96u, 0xe8u, manager_RegisterProcessJobs) \
 X(0x897c4cu, 0xe8u, manager_ProcessPlayStateChanges) \
 X(0x897e2du, 0xe8u, manager_BumpFrameIDs) \
 X(0x897e3fu, 0xe9u, manager_BumpFrameIDs) \
 X(0x897e5cu, 0xe9u, manager_ExecuteStage) \
 X(0x897e79u, 0xe9u, manager_ExecuteStage) \
 X(0x897ef5u, 0xe8u, pool_CreateItem) \
 X(0x897f89u, 0xe8u, manager_BumpFrameIDs) \
 X(0x897facu, 0xe9u, manager_ExecuteStage) \
 X(0x897ff2u, 0xe9u, manager_ExecuteStage) \
 X(0x89810du, 0xe8u, manager_ProcessPlayStateChanges) \
 X(0x89814fu, 0xe8u, graph_DispatchOutputNotifications) \
 X(0x8981c8u, 0xe9u, manager_ProcessPlayStateChanges) \
 X(0x8981dcu, 0xe9u, manager_ExecuteStage) \
 X(0x8981fcu, 0xe9u, manager_ExecuteStage) \
 X(0x89821cu, 0xe9u, manager_ExecuteStage) \
 X(0x898267u, 0xe9u, manager_ProcessAndClearForShutdown) \
 X(0x898648u, 0xe8u, graph_UpdateRootPlayableCache) \
 X(0x8986a9u, 0xe8u, graph_UpdateRootPlayableCache) \
 X(0x898ceeu, 0xe8u, manager_ScheduleGraphCommand) \
 X(0x898eefu, 0xe8u, manager_ScheduleGraphCommand) \
 X(0x899109u, 0xe8u, graph_PrepareFrame) \
 X(0x899138u, 0xe8u, manager_ProcessPlayStateChanges) \
 X(0x89936bu, 0xe8u, graph_PrepareFrame) \
 X(0x8993f8u, 0xe8u, graph_PrepareFrame) \
 X(0x899400u, 0xe8u, graph_DispatchOutputNotifications) \
 X(0x89944bu, 0xe8u, graph_PrepareFrame) \
 X(0x899453u, 0xe8u, graph_DispatchOutputNotifications) \
 X(0x89948au, 0xe8u, graph_SchedulePlayableDestruction) \
 X(0x89949bu, 0xe8u, graph_DestroyPendingPlayables) \
 X(0x8996b3u, 0xe8u, graph_DestroyPendingPlayables) \
 X(0x8996ddu, 0xe8u, graph_Evaluate) \
 X(0x8998b7u, 0xe8u, manager_ScheduleGraphCommand) \
 X(0x8998d6u, 0xe9u, manager_ProcessPlayStateChanges) \
 X(0x899e8fu, 0xe8u, manager_ScheduleGraphCommand) \
 X(0x899fe6u, 0xe8u, graph_SchedulePlayableDestruction) \
 X(0x89a63au, 0xe8u, manager_ScheduleGraphCommand) \
 X(0x89a82eu, 0xe8u, manager_ScheduleGraphCommand) \
 X(0x89a8d2u, 0xe8u, pool_AllocateInBucket) \
 X(0x89c768u, 0xe8u, graph_DisableMultithread) \
 X(0x89ca29u, 0xe8u, graph_DisableMultithread) \
 X(0x89cb38u, 0xe9u, manager_ScheduleGraphDestroy) \
 X(0x89cbc9u, 0xe9u, graph_DestroyOutput) \
 X(0x89d08cu, 0xe9u, manager_EvaluateGraph) \
 X(0x89e8e7u, 0xe8u, graph_DisableMultithread) \
 X(0x89eeb9u, 0xe9u, graph_Play) \
 X(0x89ff99u, 0xe9u, graph_SetTimeUpdateMode) \
 X(0x8a01a9u, 0xe9u, graph_Stop) \
 X(0x8a06d8u, 0xe8u, graph_DisableMultithread) \
 X(0xab7f98u, 0xe8u, graph_DisableMultithread) \
 X(0xab88a8u, 0xe8u, graph_DisableMultithread) \
 X(0xcdf665u, 0xe8u, graph_EvaluateDeferred) \
 X(0xcdfea8u, 0xe8u, graph_DisableMultithread)

#define DN_ENUM(id,abi,rva,length,hash,cut,bytes,unwind) DN_##id=HKTAS_DE_##id,
enum hktas_dn_site { HKTAS_DE_ENTRIES(DN_ENUM) DN_FUNCTION_COUNT=HKTAS_DE_COUNT };
#undef DN_ENUM
typedef struct { DWORD rva; BYTE opcode, id; } hktas_dn_edge;
#define DN_EDGE(rva,opcode,id) {rva,opcode,DN_##id},
static const hktas_dn_edge g_dn_edges[] = { HKTAS_DIR_EDGES(DN_EDGE) };
#undef DN_EDGE
#define DN_EDGE_COUNT ((unsigned)(sizeof(g_dn_edges)/sizeof(g_dn_edges[0])))
typedef void (__cdecl *hktas_dn_sink)(LONG family);
typedef struct { unsigned depth,tickets; uint64_t lineage; void *publishing,*executing; BYTE frames[64]; } hktas_dn_thread;
typedef struct { BYTE *data; size_t count, capacity; } hktas_dn_image;
static SRWLOCK g_dn_lock = SRWLOCK_INIT;
static CONDITION_VARIABLE g_dn_cv = CONDITION_VARIABLE_INIT;
static BYTE *g_dn_base, *g_dn_relay;
static DWORD g_dn_main, g_dn_fls = FLS_OUT_OF_INDEXES;
static hktas_dn_sink g_dn_fault_sink;
static LONG g_dn_attempt, g_dn_fault;
static BOOL g_dn_installed, g_dn_closed, g_dn_held;
static uint64_t g_dn_epoch, g_dn_started, g_dn_begun, g_dn_ended, g_dn_waiting, g_dn_peak;
static DWORD g_dn_active;
static hktas_dn_image g_dn_witness;

static void hktas_dn_fault_locked(LONG code)
{
    g_dn_closed = TRUE;
    if (!g_dn_fault) { g_dn_fault = code; if (g_dn_fault_sink) g_dn_fault_sink(68); }
    WakeAllConditionVariable(&g_dn_cv);
}
static void hktas_dn_forbidden(void)
{
    /* The call cannot be skipped or replayed and original execution must
     * never reenter a partially restored world. The boundary remains closed. */
    TerminateProcess(GetCurrentProcess(), ERROR_INVALID_STATE);
    for (;;) Sleep(1000);
}
static void hktas_dn_reject_locked(LONG reason)
{
    hktas_dn_fault_locked(reason); ReleaseSRWLockExclusive(&g_dn_lock);
    hktas_dn_forbidden();
}
static VOID CALLBACK hktas_dn_thread_exit(PVOID storage)
{
    hktas_dn_thread *t = (hktas_dn_thread *)storage;
    if (!t) return;
    AcquireSRWLockExclusive(&g_dn_lock);
    if (t->depth || t->tickets) {
        if (t->depth && g_dn_active) --g_dn_active;
        hktas_dn_fault_locked(1);
    }
    ReleaseSRWLockExclusive(&g_dn_lock);
    if(!t->tickets) HeapFree(GetProcessHeap(),0,t);
}
static BOOL hktas_dn_enter_locked(unsigned site,BOOL inherited)
{
    hktas_dn_thread *t = g_dn_fls == FLS_OUT_OF_INDEXES ? NULL : (hktas_dn_thread *)FlsGetValue(g_dn_fls);
    if (t && t->depth && !g_dn_fault) {
        /* Only a writer already counted before closure may finish its nested
         * work. The boundary owner never gets an implicit reentrancy permit. */
        if (g_dn_held || t->depth >= sizeof(t->frames)) { hktas_dn_reject_locked(2); return FALSE; }
    } else {
        while (g_dn_closed && !g_dn_fault && !inherited) {
            uint64_t elapsed = GetTickCount64() - g_dn_started;
            if (GetCurrentThreadId() == g_dn_main || elapsed >= HKTAS_DN_WALL) break;
            ++g_dn_waiting;
            BOOL woke = SleepConditionVariableSRW(&g_dn_cv,&g_dn_lock,(DWORD)(HKTAS_DN_WALL-elapsed),0);
            --g_dn_waiting;
            if (!woke && GetLastError()!=ERROR_TIMEOUT) break;
        }
        if (g_dn_held || (g_dn_closed && !inherited)) { hktas_dn_reject_locked(3); return FALSE; }
        if (g_dn_fault || !g_dn_installed) { hktas_dn_reject_locked(3); return FALSE; }
        if (!t) {
            t=(hktas_dn_thread *)HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(*t));
            if (!t || !FlsSetValue(g_dn_fls,t)) {
                if (t) HeapFree(GetProcessHeap(),0,t);
                hktas_dn_reject_locked(4); return FALSE;
            }
        }
        ++g_dn_active;
    }
    t->frames[t->depth++]=(BYTE)site;
    if (t->depth>g_dn_peak) g_dn_peak=t->depth;
    ++g_dn_begun; ReleaseSRWLockExclusive(&g_dn_lock); return TRUE;
}
static BOOL hktas_dn_enter(unsigned site)
{
    AcquireSRWLockExclusive(&g_dn_lock);return hktas_dn_enter_locked(site,FALSE);
}
static void hktas_dn_leave(unsigned site)
{
    AcquireSRWLockExclusive(&g_dn_lock);
    hktas_dn_thread *t=(hktas_dn_thread *)FlsGetValue(g_dn_fls);
    if (!t || !t->depth || t->frames[t->depth-1] != site || !g_dn_active) {
        hktas_dn_reject_locked(5); return;
    }
    --t->depth; ++g_dn_ended;
    if (!t->depth) { --g_dn_active; WakeAllConditionVariable(&g_dn_cv); }
    ReleaseSRWLockExclusive(&g_dn_lock);
}
#include "resident_director_job_tickets.h"
#define DN_JOB(id)
#define DN_SCHED(id)
#define DN_JGROUPS(id)
#define DN_JCREATE(id)
#define DN_GROUP(id)
#define DN_EXEC(id)
#define DN_FENCE(id)
/* ScriptingClassPtr and all reference arguments in this selection occupy
 * one native pointer register. C++ bool is one byte. PrepareFrame includes
 * the two stack parameters after RCX/RDX/XMM2/R9. No generated executable
 * wrapper calls C, adjusts RSP, or needs a fabricated UNWIND_INFO. */
#define DN_WRAP_VOID(id,params,args) static void __cdecl dn_##id params { \
    typedef void (__cdecl *fn_t) params; BOOL entered=hktas_dn_enter(DN_##id); \
    ((fn_t)(void*)hktas_de_original_##id)args; if (entered) hktas_dn_leave(DN_##id); }
#define DN_WRAP_RET(id,type,params,args) static type __cdecl dn_##id params { \
    typedef type (__cdecl *fn_t) params; BOOL entered=hktas_dn_enter(DN_##id); \
    type result=((fn_t)(void*)hktas_de_original_##id)args; if (entered) hktas_dn_leave(DN_##id); return result; }
#define DN_V0(id) DN_WRAP_VOID(id,(void *p),(p))
#define DN_VI(id) DN_WRAP_VOID(id,(void *p,int i),(p,i))
#define DN_VB(id) DN_WRAP_VOID(id,(void *p,BYTE b),(p,b))
#define DN_VP(id) DN_WRAP_VOID(id,(void *p,void *q),(p,q))
#define DN_VIP(id) DN_WRAP_VOID(id,(void *p,int i,void *q),(p,i,q))
#define DN_VPD(id) DN_WRAP_VOID(id,(void *p,void *q,double dt),(p,q,dt))
#define DN_VEVAL(id) DN_WRAP_VOID(id,(void *p,double dt,uint64_t frame,unsigned flags),(p,dt,frame,flags))
#define DN_VPREP(id) DN_WRAP_VOID(id,(void *p,int phase,double dt,uint64_t frame,unsigned flags,int context),(p,phase,dt,frame,flags,context))
#define DN_P0(id) DN_WRAP_RET(id,void *,(void *p),(p))
#define DN_PP(id) DN_WRAP_RET(id,void *,(void *p,void *q),(p,q))
#define DN_PI(id) DN_WRAP_RET(id,void *,(void *p,int i),(p,i))
#define DN_SP(id) DN_WRAP_RET(id,short,(void *p,void *q),(p,q))
#define DN_DEF(id,abi,rva,length,hash,cut,bytes,unwind) DN_##abi(id)
HKTAS_DE_ENTRIES(DN_DEF)
#undef DN_DEF
#define DN_ADDR(id,abi,rva,length,hash,cut,bytes,unwind) (void *)dn_##id,
static void *const g_dn_wrappers[]={ HKTAS_DE_ENTRIES(DN_ADDR) };
#undef DN_ADDR

static BOOL hktas_dn_signature(BYTE *base)
{
    if (!base || sizeof(void*)!=8) return FALSE;
    IMAGE_DOS_HEADER *dos=(IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew<64 || dos->e_lfanew>4096) return FALSE;
    IMAGE_NT_HEADERS64 *nt=(IMAGE_NT_HEADERS64 *)(base+dos->e_lfanew);
    if (nt->Signature!=IMAGE_NT_SIGNATURE || nt->FileHeader.Machine!=IMAGE_FILE_MACHINE_AMD64
        || nt->FileHeader.TimeDateStamp!=0x6008c66eu || nt->OptionalHeader.SizeOfImage!=HKTAS_DN_IMAGE
        || nt->OptionalHeader.Magic!=IMAGE_NT_OPTIONAL_HDR64_MAGIC || nt->OptionalHeader.NumberOfRvaAndSizes<=IMAGE_DIRECTORY_ENTRY_DEBUG) return FALSE;
    IMAGE_DATA_DIRECTORY debug=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG];
    if (!debug.VirtualAddress || debug.Size<sizeof(IMAGE_DEBUG_DIRECTORY) || debug.Size>4096 || debug.VirtualAddress>HKTAS_DN_IMAGE-debug.Size) return FALSE;
    IMAGE_DEBUG_DIRECTORY *rows=(IMAGE_DEBUG_DIRECTORY *)(base+debug.VirtualAddress);
    static const BYTE cv[]={0x52,0x53,0x44,0x53,0x90,0xbe,0x34,0x03,0xdd,0x63,0x44,0x46,0xbe,0xe1,0xfc,0x8d,0x56,0xc8,0x40,0xc2,1,0,0,0};
    for (unsigned i=0;i<debug.Size/sizeof(*rows);++i) if (rows[i].Type==IMAGE_DEBUG_TYPE_CODEVIEW && rows[i].SizeOfData>=sizeof(cv)
        && rows[i].AddressOfRawData<=HKTAS_DN_IMAGE-sizeof(cv) && !memcmp(base+rows[i].AddressOfRawData,cv,sizeof(cv))) return TRUE;
    return FALSE;
}
static BOOL hktas_dn_rel(BYTE *at, BYTE *target, int32_t *rel)
{
    int64_t delta=(int64_t)(uintptr_t)target-(int64_t)(uintptr_t)(at+5);
    if (delta<INT32_MIN || delta>INT32_MAX) return FALSE;
    *rel=(int32_t)delta;return TRUE;
}
static BOOL hktas_dn_verify(BOOL installed)
{
    if(!hktas_dn_signature(g_dn_base) || !hktas_de_verify(g_dn_base,g_dn_relay,(const void *const*)g_dn_wrappers,installed)) return FALSE;
    for(unsigned i=0;i<DN_EDGE_COUNT;++i) {
        hktas_dn_edge edge=g_dn_edges[i];BYTE *site=g_dn_base+edge.rva;int32_t delta;
        if(site[0]!=edge.opcode || !hktas_dn_rel(site,g_dn_base+hktas_de_entries[edge.id].rva,&delta) || memcmp(site+1,&delta,4)) return FALSE;
    }
    return TRUE;
}
/* Primary startup APC / DLL_PROCESS_ATTACH only. Windows may have already
 * created genuine OS loader-pool workers. Do not patch code concurrently
 * with any other runnable thread. Accept an existing thread solely while
 * we hold a suspend reference and its entire native stack is the idle OS
 * worker syscall -> its actual thread start routine -> OS start thunks. */
typedef struct {HANDLE handles[64];unsigned count;} hktas_dn_parked;
typedef LONG (NTAPI *hktas_dn_query_thread)(HANDLE,ULONG,PVOID,ULONG,PULONG);
typedef struct {
    DWORD64 nt, kernel, init, start;
    BYTE *wait;
    hktas_dn_query_thread query;
} hktas_dn_startup_abi;
static BOOL hktas_dn_startup_exports(hktas_dn_startup_abi *abi)
{
    HMODULE nt=GetModuleHandleW(L"ntdll.dll"),kernel=GetModuleHandleW(L"kernel32.dll");
    if(!nt || !kernel) return FALSE;
    abi->nt=(DWORD64)(uintptr_t)nt;abi->kernel=(DWORD64)(uintptr_t)kernel;
    abi->query=(hktas_dn_query_thread)(void*)GetProcAddress(nt,"NtQueryInformationThread");
    abi->wait=(BYTE *)(void*)GetProcAddress(nt,"NtWaitForWorkViaWorkerFactory");
    abi->init=(DWORD64)(uintptr_t)GetProcAddress(kernel,"BaseThreadInitThunk");
    abi->start=(DWORD64)(uintptr_t)GetProcAddress(nt,"RtlUserThreadStart");
    return abi->wait && abi->query && abi->init && abi->start;
}
static BOOL hktas_dn_same_function(DWORD64 pc,DWORD64 start,DWORD64 module)
{
    DWORD64 frame_base=0,start_base=0;
    PRUNTIME_FUNCTION frame=RtlLookupFunctionEntry(pc,&frame_base,NULL);
    PRUNTIME_FUNCTION entry=RtlLookupFunctionEntry(start,&start_base,NULL);
    return frame && entry && frame_base==module && start_base==module
        && frame->BeginAddress==entry->BeginAddress && frame->EndAddress==entry->EndAddress;
}
static BOOL hktas_dn_unwind_os(CONTEXT *ctx,DWORD64 entry,DWORD64 module)
{
    if(!hktas_dn_same_function(ctx->Rip,entry,module)) return FALSE;
    DWORD64 base=0,establisher=0;PVOID handler_data=NULL;
    PRUNTIME_FUNCTION frame=RtlLookupFunctionEntry(ctx->Rip,&base,NULL);
    if(!frame || base!=module) return FALSE;
    RtlVirtualUnwind(UNW_FLAG_NHANDLER,base,ctx->Rip,frame,ctx,&handler_data,&establisher,NULL);
    return TRUE;
}
static BOOL hktas_dn_idle_os_worker(HANDLE thread,DWORD64 worker,const hktas_dn_startup_abi *abi)
{
    BYTE *wait=abi->wait;
    CONTEXT ctx;memset(&ctx,0,sizeof(ctx));ctx.ContextFlags=CONTEXT_FULL;
    if(!GetThreadContext(thread,&ctx)) return FALSE;
    /* No syscall number or OS RVA is assumed. Restrict the current PC to
     * RET immediately after SYSCALL in this particular exported leaf stub. */
    DWORD64 offset=ctx.Rip-(DWORD64)(uintptr_t)wait;
    if(offset<2 || offset>30 || wait[offset-2]!=0x0f || wait[offset-1]!=0x05 || wait[offset]!=0xc3) return FALSE;
    SIZE_T got=0;DWORD64 caller=0;
    if(!ReadProcessMemory(GetCurrentProcess(),(void*)(uintptr_t)ctx.Rsp,&caller,sizeof(caller),&got) || got!=sizeof(caller)) return FALSE;
    ctx.Rip=caller;ctx.Rsp+=sizeof(caller);
    return hktas_dn_unwind_os(&ctx,worker,abi->nt) && hktas_dn_unwind_os(&ctx,abi->init,abi->kernel)
        && hktas_dn_unwind_os(&ctx,abi->start,abi->nt) && ctx.Rip==0;
}
static BOOL hktas_dn_resume(hktas_dn_parked *held)
{
    BOOL ok=TRUE;
    while(held->count) {
        HANDLE thread=held->handles[--held->count];
        if(ResumeThread(thread)!=1) ok=FALSE;
        CloseHandle(thread);
    }
    return ok;
}
static BOOL hktas_dn_park(hktas_dn_parked *held,BOOL verify,const hktas_dn_startup_abi *abi)
{
    HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
    if(snap==INVALID_HANDLE_VALUE) return FALSE;
    THREADENTRY32 e;memset(&e,0,sizeof(e));e.dwSize=sizeof(e);unsigned observed=0;BOOL main_seen=FALSE;
    BOOL ok=Thread32First(snap,&e);
    if(ok) {
        do {
            if(e.th32OwnerProcessID!=GetCurrentProcessId()) continue;
            if(e.th32ThreadID==GetCurrentThreadId()) {main_seen=TRUE;continue;}
            ++observed;
            if(verify) {
                unsigned i=0;while(i<held->count && GetThreadId(held->handles[i])!=e.th32ThreadID) ++i;
                if(i==held->count) {ok=FALSE;break;}
            } else {
                if(held->count>=sizeof(held->handles)/sizeof(*held->handles)) {ok=FALSE;break;}
                HANDLE t=OpenThread(THREAD_QUERY_INFORMATION|THREAD_GET_CONTEXT|THREAD_SUSPEND_RESUME,FALSE,e.th32ThreadID);
                if(!t) {ok=FALSE;break;}
                DWORD64 worker=0;MEMORY_BASIC_INFORMATION region;
                if(GetProcessIdOfThread(t)!=GetCurrentProcessId() || abi->query(t,9,&worker,sizeof(worker),NULL)<0
                    || !VirtualQuery((void*)(uintptr_t)worker,&region,sizeof(region)) || (DWORD64)(uintptr_t)region.AllocationBase!=abi->nt) {
                    CloseHandle(t);ok=FALSE;break;
                }
                DWORD previous=SuspendThread(t);
                if(previous==(DWORD)-1) {CloseHandle(t);ok=FALSE;break;}
                held->handles[held->count++]=t;
                if(previous || !hktas_dn_idle_os_worker(t,worker,abi)) {ok=FALSE;break;}
            }
        } while(Thread32Next(snap,&e));
        if(ok && GetLastError()!=ERROR_NO_MORE_FILES) ok=FALSE;
    }
    CloseHandle(snap);return ok && main_seen && observed==held->count;
}
static BOOL hktas_dn_install_in_exclusive_image(BYTE *base,DWORD main_thread,hktas_dn_sink sink)
{
    if (InterlockedCompareExchange(&g_dn_attempt,1,0) || !sink || !base || GetCurrentThreadId()!=main_thread
        || GetModuleHandleW(L"mono-2.0-bdwgc.dll") || !hktas_dn_signature(base)) return FALSE;
    void *manager;memcpy(&manager,base+HKTAS_DN_MANAGER,8);if(manager) return FALSE;
    g_dn_base=base;g_dn_main=main_thread;g_dn_fault_sink=sink;
    if (!hktas_dn_verify(FALSE)) return FALSE;
    g_dn_fls=FlsAlloc(hktas_dn_thread_exit);if(g_dn_fls==FLS_OUT_OF_INDEXES) return FALSE;
    uintptr_t start=((uintptr_t)base+HKTAS_DN_IMAGE+65535u)&~(uintptr_t)65535u;
    for (uintptr_t off=0;off<0x60000000u && !g_dn_relay;off+=65536u)
        g_dn_relay=(BYTE *)VirtualAlloc((void *)(start+off),4096,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
    if (!g_dn_relay) { FlsFree(g_dn_fls);g_dn_fls=FLS_OUT_OF_INDEXES;return FALSE; }
    if(!hktas_de_install(base,g_dn_relay,(const void *const*)g_dn_wrappers) || !hktas_dn_verify(TRUE)) {
        /* Publication may already be visible. Keep the static trampolines,
         * relays and FLS alive until the owner terminates this process. */
        hktas_dn_fault_locked(6);return FALSE;
    }
    g_dn_installed=TRUE;return TRUE;
}
static BOOL install_resident_director_producers(BYTE *base,DWORD main_thread,hktas_dn_sink sink)
{
    /* Production is invoked from the pre-main-thread loader callback. Never
     * patch calls in a running player or infer exclusivity from a null
     * DirectorManager alone. */
    if(!base || base!=(BYTE*)GetModuleHandleW(L"UnityPlayer.dll") || GetCurrentThreadId()!=main_thread) return FALSE;
    hktas_dn_parked held={0};hktas_dn_startup_abi abi={0};
    BOOL ok=hktas_dn_startup_exports(&abi) && hktas_dn_park(&held,FALSE,&abi) && hktas_dn_park(&held,TRUE,&abi)
        && hktas_dn_install_in_exclusive_image(base,main_thread,sink);
    BOOL resumed=hktas_dn_resume(&held);
    return ok && resumed;
}
static uintptr_t hktas_dn_ptr(const BYTE *bytes) { uintptr_t v;memcpy(&v,bytes,8);return v; }
static uint64_t hktas_dn_u64(const BYTE *bytes) { uint64_t v;memcpy(&v,bytes,8);return v; }
static uint32_t hktas_dn_u32(const BYTE *bytes) { uint32_t v;memcpy(&v,bytes,4);return v; }
static BOOL hktas_dn_read(uintptr_t address,void *bytes,size_t count)
{
    SIZE_T got=0;
    return !count || (address && count<=32u*1024u*1024u
        && ReadProcessMemory(GetCurrentProcess(),(const void*)address,bytes,count,&got) && got==count);
}
/* This is a local exact-byte witness kept for one epoch; no address, ABA
 * tag, allocator memory or command body is exported as portable game data. */
static BOOL hktas_dn_append(hktas_dn_image *out,uintptr_t address,size_t count)
{
    const size_t limit=32u*1024u*1024u;
    if(count>limit-out->count || GetTickCount64()-g_dn_started>=HKTAS_DN_WALL) return FALSE;
    if(out->count+count>out->capacity) {
        size_t cap=out->capacity ? out->capacity : 2048;
        while(cap<out->count+count) cap*=2;
        BYTE *next=out->data ? (BYTE*)HeapReAlloc(GetProcessHeap(),0,out->data,cap) : (BYTE*)HeapAlloc(GetProcessHeap(),0,cap);
        if(!next) return FALSE;
        out->data=next;out->capacity=cap;
    }
    if(count && !hktas_dn_read(address,out->data+out->count,count)) return FALSE;
    out->count+=count;return TRUE;
}
static BOOL hktas_dn_handle(hktas_dn_image *out,const BYTE *handle)
{
    uintptr_t node=hktas_dn_ptr(handle);
    return !node || hktas_dn_append(out,node+32u,16u);
}
static BOOL hktas_dn_observe_owner_node(hktas_dn_image *out,uintptr_t object,unsigned offset)
{
    uintptr_t node=0;BYTE owner[16];
    if(!object || !hktas_dn_read(object+offset,&node,sizeof(node)) || !hktas_dn_append(out,object+offset,sizeof(node))) return FALSE;
    /* Native handles are lazily issued by PlayableGraph::Handle. Witness a
     * genuinely unissued slot without calling the mutating handle getter. */
    return !node || (hktas_dn_read(node+32,owner,sizeof(owner)) && hktas_dn_ptr(owner+8)==object && hktas_dn_append(out,node+32,sizeof(owner)));
}
static BOOL hktas_dn_array(hktas_dn_image *out,const BYTE *header,size_t stride,int handle_offset)
{
    uint64_t count=hktas_dn_u64(header+16),cap=hktas_dn_u64(header+24)>>1;
    uintptr_t data=hktas_dn_ptr(header);
    if(count>cap || cap>HKTAS_DN_LIMIT || (cap && !data) || !hktas_dn_append(out,data,(size_t)count*stride)) return FALSE;
    if(handle_offset>=0) for(uint64_t i=0;i<count;++i) {
        BYTE handle[16];
        if(!hktas_dn_read(data+(size_t)i*stride+(unsigned)handle_offset,handle,16) || !hktas_dn_handle(out,handle)) return FALSE;
    }
    return TRUE;
}
static BOOL hktas_dn_graph(hktas_dn_image *out,uintptr_t graph)
{
    BYTE g[232];
    if(!hktas_dn_read(graph,g,sizeof(g)) || !hktas_dn_append(out,graph,sizeof(g)) || !hktas_dn_observe_owner_node(out,graph,16)
        || !hktas_dn_array(out,g+72,16,0) || !hktas_dn_array(out,g+120,16,0)) return FALSE;
    for(unsigned lane=0;lane<2;++lane) {
        unsigned headOff=lane?104u:56u;uintptr_t head=graph+headOff,prev=head,cursor=hktas_dn_ptr(g+headOff+8);unsigned count=0;
        while(cursor!=head) {
            BYTE node[208];size_t size=lane?208u:192u;
            if(++count>HKTAS_DN_LIMIT || cursor<8) return FALSE;
            uintptr_t owner=cursor-8;
            if(!hktas_dn_read(owner,node,size) || !hktas_dn_append(out,owner,size) || hktas_dn_ptr(node+8)!=prev
                || hktas_dn_ptr(node+(lane?40:32))!=graph) return FALSE;
            uintptr_t h=hktas_dn_ptr(node+(lane?32:40));BYTE issued[16];
            if(!h || !hktas_dn_read(h+32u,issued,16) || hktas_dn_ptr(issued+8)!=owner || !hktas_dn_append(out,h+32u,16)) return FALSE;
            if(lane) {
                if(!hktas_dn_handle(out,node+48) || !hktas_dn_array(out,node+112,64,0) || !hktas_dn_array(out,node+144,24,-1)
                    || !hktas_dn_array(out,node+176,8,-1)) return FALSE;
            } else {
                uintptr_t ports=hktas_dn_ptr(node+48);BYTE c[104];
                if(ports && (!hktas_dn_read(ports,c,104) || !hktas_dn_append(out,ports,104)
                    || !hktas_dn_array(out,c+32,16,-1) || !hktas_dn_array(out,c+64,8,-1))) return FALSE;
            }
            prev=cursor;cursor=hktas_dn_ptr(node+16);
        }
        if(prev!=hktas_dn_ptr(g+headOff)) return FALSE;
    }
    return TRUE;
}
static BOOL hktas_dn_capture(hktas_dn_image *out)
{
    uintptr_t manager;out->count=0;
    if(!hktas_dn_read((uintptr_t)g_dn_base+HKTAS_DN_MANAGER,&manager,8)
        || !hktas_dn_append(out,(uintptr_t)&manager,8)) return FALSE;
    if(!manager) return TRUE;
    BYTE m[1120];
    if(!hktas_dn_read(manager,m,1120) || !hktas_dn_append(out,manager,1120) || m[986] || m[987]) return FALSE;
    /* Compare the exact command, retirement and notification handles and the
     * generation/target behind each, including already invalidated handles. */
    if(!hktas_dn_array(out,m+840,16,0) || !hktas_dn_array(out,m+872,16,0) || !hktas_dn_array(out,m+776,80,-1)) return FALSE;
    for(unsigned i=0;i<7;++i) {
        for(unsigned lane=0;lane<2;++lane) {
            const BYTE *jobs=m+(lane?368u:144u)+i*32;
            if(!hktas_dn_array(out,jobs,80,-1)) return FALSE;
            for(uint64_t j=0;j<hktas_dn_u64(jobs+16);++j) {
                uintptr_t graph=0;
                if(!hktas_dn_read(hktas_dn_ptr(jobs)+(size_t)j*80+72,&graph,8)
                    || !hktas_dn_observe_owner_node(out,graph,16)) return FALSE;
            }
        }
        const BYTE *vec=m+592+i*24;uintptr_t first=hktas_dn_ptr(vec),last=hktas_dn_ptr(vec+8),cap=hktas_dn_ptr(vec+16);
        if(last<first || cap<last || (!first && cap) || cap-first>HKTAS_DN_LIMIT*40u || (last-first)%40 || (cap-first)%40) return FALSE;
        for(uintptr_t at=first;at<last;at+=40) {
            BYTE g[40];if(!hktas_dn_read(at,g,40) || !hktas_dn_append(out,at,40) || !hktas_dn_array(out,g+8,8,-1)) return FALSE;
            for(uint64_t j=0;j<hktas_dn_u64(g+24);++j) {
                uintptr_t output=0;
                if(!hktas_dn_read(hktas_dn_ptr(g+8)+(size_t)j*8,&output,8) || !hktas_dn_observe_owner_node(out,output,32)) return FALSE;
            }
        }
    }
    for(unsigned i=0;i<2;++i) {
        unsigned off=808u+16u*i;uintptr_t head=manager+off,prev=head,cursor=hktas_dn_ptr(m+off+8);unsigned count=0;
        while(cursor!=head) {
            BYTE links[16];
            if(++count>HKTAS_DN_LIMIT || !hktas_dn_read(cursor,links,16) || hktas_dn_ptr(links)!=prev || !hktas_dn_graph(out,cursor)) return FALSE;
            prev=cursor;cursor=hktas_dn_ptr(links+8);
        }
        if(prev!=hktas_dn_ptr(m+off)) return FALSE;
    }
    for(unsigned i=0;i<9;++i) {
        /* Offsets 1040..1072 buckets, 1080 commands, 1088..1104 handles. */
        uintptr_t stack=hktas_dn_ptr(m+1040u+8u*i);BYTE top[16],after[16];unsigned count=0;
        if(!stack) {
            if(i<5 && (hktas_dn_u32(m+1000+i*4) || hktas_dn_u32(m+1020+i*4))) return FALSE;
            continue;
        }
        for(unsigned j=0;j<i;++j) if(hktas_dn_ptr(m+1040u+8u*j)==stack) return FALSE;
        if(!hktas_dn_read(stack,top,16) || !hktas_dn_append(out,stack,16)) return FALSE;
        uintptr_t node=hktas_dn_ptr(top);
        while(node) {
            BYTE bytes[104];size_t size=i<5 ? 104u : i==5 ? 32u : 48u;
            if(++count>HKTAS_DN_LIMIT || !hktas_dn_read(node,bytes,size) || !hktas_dn_append(out,node,size)) return FALSE;
            if(i<5 && (hktas_dn_u32(bytes+96)!=i || !hktas_dn_array(out,bytes+32,16,-1) || !hktas_dn_array(out,bytes+64,8,-1))) return FALSE;
            if(i==5 && !hktas_dn_handle(out,bytes+8)) return FALSE;
            node=hktas_dn_ptr(bytes);
        }
        if(i<5) {
            uint32_t total=hktas_dn_u32(m+1000+i*4),active=hktas_dn_u32(m+1020+i*4);
            if(total>HKTAS_DN_LIMIT || active>total || count!=total-active) return FALSE;
        }
        if(!hktas_dn_read(stack,after,16) || memcmp(top,after,16)) return FALSE;
    }
    return GetTickCount64()-g_dn_started<HKTAS_DN_WALL;
}
static BOOL hktas_dn_unchanged_locked(void)
{
    hktas_dn_image other={0};
    BOOL ok=hktas_dn_capture(&other) && other.count==g_dn_witness.count && !memcmp(other.data,g_dn_witness.data,other.count);
    if(other.data) HeapFree(GetProcessHeap(),0,other.data);
    return ok;
}
static BOOL hktas_dn_held_locked(uint64_t token)
{
    return g_dn_installed && !g_dn_fault && g_dn_closed && g_dn_held && token && token==g_dn_epoch
        && g_dn_main==GetCurrentThreadId() && !g_dn_active && !g_dn_lineages && !g_dn_lineage_rows && g_dn_lineage_issued==g_dn_lineage_retired && g_dn_lineage_entered==g_dn_lineage_returned && !g_dn_job_tickets && g_dn_job_published==g_dn_job_retired && g_dn_job_started==g_dn_job_finished && !g_dn_job_rows && g_dn_begun==g_dn_ended && GetTickCount64()-g_dn_started<HKTAS_DN_WALL;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_BeginResidentDirectorFence(uint64_t token,uint64_t started)
{
    AcquireSRWLockExclusive(&g_dn_lock);
    hktas_dn_thread *own=g_dn_fls==FLS_OUT_OF_INDEXES ? NULL : (hktas_dn_thread *)FlsGetValue(g_dn_fls);
    if(!g_dn_installed || g_dn_fault || g_dn_closed || !token || token<=g_dn_epoch || GetCurrentThreadId()!=g_dn_main
        || (own && (own->depth || own->tickets)) || GetTickCount64()-started>=HKTAS_DN_WALL || !hktas_dn_verify(TRUE)) {
        hktas_dn_fault_locked(10);ReleaseSRWLockExclusive(&g_dn_lock);return 0;
    }
    g_dn_epoch=token;g_dn_started=started;g_dn_closed=TRUE;
    while((g_dn_active || g_dn_job_tickets || g_dn_lineages) && !g_dn_fault) {
        uint64_t elapsed=GetTickCount64()-started;
        if(elapsed>=HKTAS_DN_WALL) break;
        if(!SleepConditionVariableSRW(&g_dn_cv,&g_dn_lock,(DWORD)(HKTAS_DN_WALL-elapsed),0) && GetLastError()!=ERROR_TIMEOUT) break;
    }
    if(g_dn_active || g_dn_lineages || g_dn_lineage_rows || g_dn_lineage_issued!=g_dn_lineage_retired || g_dn_lineage_entered!=g_dn_lineage_returned || g_dn_job_tickets || g_dn_job_published!=g_dn_job_retired || g_dn_job_started!=g_dn_job_finished || g_dn_job_rows || g_dn_fault || g_dn_begun!=g_dn_ended || !hktas_dn_capture(&g_dn_witness) || !hktas_dn_unchanged_locked()) {
        hktas_dn_fault_locked(11);ReleaseSRWLockExclusive(&g_dn_lock);return 0;
    }
    g_dn_held=TRUE;ReleaseSRWLockExclusive(&g_dn_lock);return 1;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_RequireResidentDirectorFence(uint64_t token)
{
    AcquireSRWLockExclusive(&g_dn_lock);BOOL ok=hktas_dn_held_locked(token);
    if(!ok && g_dn_closed) hktas_dn_fault_locked(12);
    ReleaseSRWLockExclusive(&g_dn_lock);return ok;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_VerifyResidentDirectorQueues(uint64_t token)
{
    AcquireSRWLockExclusive(&g_dn_lock);
    BOOL ok=hktas_dn_held_locked(token) && hktas_dn_unchanged_locked() && hktas_dn_verify(TRUE);
    if(!ok && g_dn_closed) hktas_dn_fault_locked(13);
    ReleaseSRWLockExclusive(&g_dn_lock);return ok;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_EndResidentDirectorFence(uint64_t token,LONG complete)
{
#ifdef HKTAS_JOINT_RESIDENT_GATE
    (void)token; (void)complete; return hktas_reject_individual_end();
#else
    AcquireSRWLockExclusive(&g_dn_lock);
    if(complete!=1 || !hktas_dn_held_locked(token) || !hktas_dn_unchanged_locked() || !hktas_dn_verify(TRUE)) {
        hktas_dn_fault_locked(14);ReleaseSRWLockExclusive(&g_dn_lock);return 0;
    }
    g_dn_closed=FALSE;g_dn_held=FALSE;WakeAllConditionVariable(&g_dn_cv);ReleaseSRWLockExclusive(&g_dn_lock);return 1;
#endif
}
typedef struct {
    uint32_t magic, version, bytes, installed;
    uint64_t epoch, started, begun, ended, waiting, max_depth, witness_bytes;
    uint32_t active, closed, held, fault, main_thread, caller_thread;
    uint32_t edges, functions, required_gaps, complete;
    uint64_t groups_published,groups_retired,callbacks_started,callbacks_returned;
    uint32_t outstanding_groups,outstanding_rows;
    uint64_t lineage_issued,lineage_retired,lineage_entered,lineage_returned,lineage_nested;
    uint32_t outstanding_lineages,outstanding_lineage_rows;
} hktas_dn_status;
typedef char hktas_dn_status_check[(sizeof(hktas_dn_status)==200 && offsetof(hktas_dn_status,required_gaps)==104 && offsetof(hktas_dn_status,outstanding_groups)==144 && offsetof(hktas_dn_status,lineage_issued)==152 && offsetof(hktas_dn_status,lineage_retired)==160 && offsetof(hktas_dn_status,lineage_entered)==168 && offsetof(hktas_dn_status,lineage_returned)==176 && offsetof(hktas_dn_status,lineage_nested)==184 && offsetof(hktas_dn_status,outstanding_lineages)==192 && offsetof(hktas_dn_status,outstanding_lineage_rows)==196)?1:-1];
__declspec(dllexport) LONG __cdecl HktasClockBridge_CopyResidentDirectorFenceStatus(BYTE *buffer,uint32_t length)
{
    if(!buffer || length!=sizeof(hktas_dn_status)) return 0;
    AcquireSRWLockShared(&g_dn_lock);
    hktas_dn_status v={0x31464452u,4u,sizeof(hktas_dn_status),g_dn_installed,g_dn_epoch,g_dn_started,g_dn_begun,g_dn_ended,
        g_dn_waiting,g_dn_peak,g_dn_witness.count,g_dn_active,g_dn_closed,g_dn_held,(uint32_t)g_dn_fault,g_dn_main,GetCurrentThreadId(),
        DN_EDGE_COUNT,DN_FUNCTION_COUNT,HKTAS_DN_GAPS,0u,g_dn_job_published,g_dn_job_retired,g_dn_job_started,g_dn_job_finished,
        (uint32_t)g_dn_job_count,g_dn_job_rows,g_dn_lineage_issued,g_dn_lineage_retired,g_dn_lineage_entered,g_dn_lineage_returned,g_dn_lineage_nested,
        (uint32_t)g_dn_lineage_count,g_dn_lineage_rows};
    memcpy(buffer,&v,sizeof(v));ReleaseSRWLockShared(&g_dn_lock);return sizeof(v);
}
#endif
