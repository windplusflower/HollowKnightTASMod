#ifndef HKTAS_RESIDENT_PARTICLE_PRODUCER_H
#define HKTAS_RESIDENT_PARTICLE_PRODUCER_H
#include "resident_fatal_evidence.h"
#include <windows.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

/* Pinned Win64 Mono icall registration. No Unity code or unwind record is
 * rewritten. Install before Mono is loaded (early APC). All three public
 * IParticleSystemJobExtensions entry points read the prior dependency,
 * obtain owner+128, schedule, and publish the returned fence, in that order.
 * ParallelFor and ParallelForBatch use the shared JobsUtility defer icall.
 * This fence protects publication, not execution of an already published
 * job, GfxDevice work, another unsafe producer, or a pending GPU command. */
typedef struct { uintptr_t group; uint32_t version, padding; } hktas_ps_handle;
typedef void (__cdecl *hktas_ps_get_handle_fn)(void *, hktas_ps_handle *);
typedef void *(__cdecl *hktas_ps_get_data_fn)(void *);
typedef void (__cdecl *hktas_ps_schedule_fn)(void *, void *, hktas_ps_handle *);
typedef void (__cdecl *hktas_ps_defer_fn)(void *, int, void *, void *, hktas_ps_handle *);
typedef void (__cdecl *hktas_ps_set_fn)(void *, const hktas_ps_handle *);
typedef void (__cdecl *hktas_ps_fault_sink)(LONG);
typedef struct { DWORD slot, code, length; uint64_t hash; } hktas_ps_binding;
static const hktas_ps_binding g_ps_bindings[] = {
    {0x1955090u, 0x355240u, 336u, UINT64_C(0x213522c8ab3b44df)},
    {0x1955088u, 0x354dd0u, 296u, UINT64_C(0xa165bf8dec806c1a)},
    {0x19550a0u, 0x355890u, 89u,  UINT64_C(0x8974ba816778b2d0)},
    {0x1976488u, 0x93d370u, 104u, UINT64_C(0xcbd306aa61f49738)},
    {0x1955098u, 0x355550u, 328u, UINT64_C(0x0f7f0ef6779a224e)}
};
enum { PS_READ = 1, PS_OWNER, PS_DATA, PS_SCHEDULING, PS_PUBLISH, PS_SETTING };
typedef struct hktas_ps_writer {
    struct hktas_ps_writer *next;
    DWORD thread, phase;
    void *data; /* target native owner+128, never a managed GC address */
    hktas_ps_handle handle;
} hktas_ps_writer;
static SRWLOCK g_ps_lock = SRWLOCK_INIT;
static CONDITION_VARIABLE g_ps_changed = CONDITION_VARIABLE_INIT;
static hktas_ps_writer *g_ps_writers;
static DWORD g_ps_main, g_ps_tls = TLS_OUT_OF_INDEXES;
static LONG g_ps_count, g_ps_reason, g_ps_attempted;
static BOOL g_ps_installed, g_ps_closed;
static uint64_t g_ps_epoch, g_ps_started, g_ps_completed;
static BYTE *g_ps_base;
static hktas_ps_fault_sink g_ps_sink;
static void *g_ps_original[5];

/* Sink is the bridge's leaf permanent-fault publisher. It must not call back
 * into this gate. Outside a boundary an unsupported producer keeps the engine
 * call's normal behavior and permanently disables future checkpoint admission. */
static void hktas_ps_fault_locked(LONG reason)
{
    if (!g_ps_reason) { g_ps_reason = reason; if (g_ps_sink) g_ps_sink(67); }
    WakeAllConditionVariable(&g_ps_changed);
}
static void hktas_ps_forbidden(void)
{
    hktas_resident_record_fatal("particle",g_ps_reason);
    TerminateProcess(GetCurrentProcess(), ERROR_INVALID_STATE);
    for (;;) Sleep(1000);
}
static void hktas_ps_reject_locked(LONG reason)
{
    BOOL forbidden = g_ps_closed;
    hktas_ps_fault_locked(reason); ReleaseSRWLockExclusive(&g_ps_lock);
    if (forbidden) hktas_ps_forbidden();
}
static BOOL hktas_ps_wait_open_locked(void)
{
    while (g_ps_closed && !g_ps_reason) {
        uint64_t elapsed = GetTickCount64() - g_ps_started;
        if (GetCurrentThreadId() == g_ps_main || elapsed >= 9000u) break;
        if (!SleepConditionVariableSRW(&g_ps_changed, &g_ps_lock, (DWORD)(9000u - elapsed), 0) && GetLastError() != ERROR_TIMEOUT) break;
    }
    if (g_ps_closed) { hktas_ps_reject_locked(1); return FALSE; }
    if (g_ps_reason || !g_ps_installed) { ReleaseSRWLockExclusive(&g_ps_lock); return FALSE; }
    return TRUE;
}
static BOOL hktas_ps_same(const hktas_ps_handle *a, const hktas_ps_handle *b)
{ return a && b && a->group == b->group && a->version == b->version; }
/* Classify the live owner by its actual native type. Both GetManagedJobData
 * and the constructor establish that data is &ParticleSystem::m_ManagedJobData
 * at +128. The constructor installs the sole ParticleSystem vtable at
 * 0x15e0148 (PDB ??_7ParticleSystem@@6B@). A generic deferred native list
 * can be at the edge of a mapping. Do not access an unrelated guard page,
 * retain old particle addresses or dereference a destroyed Unity wrapper. */
static BOOL hktas_ps_particle_data(const void *data)
{
    uintptr_t value = (uintptr_t)data;
    if (!g_ps_base || value < 128u) return FALSE;
    const void *owner = (const void *)(value - 128u);
    MEMORY_BASIC_INFORMATION info;
    if (!VirtualQuery(owner,&info,sizeof(info)) || info.State != MEM_COMMIT
        || (info.Protect & (PAGE_GUARD|PAGE_NOACCESS)) || (uintptr_t)info.BaseAddress + info.RegionSize - (uintptr_t)owner < sizeof(uintptr_t)) return FALSE;
    uintptr_t table = 0; SIZE_T size = 0;
    return ReadProcessMemory(GetCurrentProcess(),owner,&table,sizeof(table),&size) && size == sizeof(table)
        && table == (uintptr_t)(g_ps_base + 0x15e0148u);
}
static BOOL hktas_ps_enter(void)
{
    AcquireSRWLockExclusive(&g_ps_lock);
    if (!hktas_ps_wait_open_locked()) return FALSE;
    if (TlsGetValue(g_ps_tls)) { hktas_ps_reject_locked(2); return FALSE; }
    hktas_ps_writer *writer = (hktas_ps_writer *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(*writer));
    if (!writer || !TlsSetValue(g_ps_tls, writer)) {
        if (writer) HeapFree(GetProcessHeap(), 0, writer);
        hktas_ps_reject_locked(3); return FALSE;
    }
    writer->phase = PS_READ; writer->thread = GetCurrentThreadId();
    writer->next = g_ps_writers; g_ps_writers = writer; ++g_ps_count;
    ReleaseSRWLockExclusive(&g_ps_lock); return TRUE;
}
static BOOL hktas_ps_transition(DWORD from, DWORD to, void *data, const hktas_ps_handle *handle)
{
    AcquireSRWLockExclusive(&g_ps_lock);
    hktas_ps_writer *writer = (hktas_ps_writer *)TlsGetValue(g_ps_tls);
    if (g_ps_reason || !writer || writer->phase != from || writer->thread != GetCurrentThreadId()) {
        hktas_ps_reject_locked(4); return FALSE;
    }
    if (from == PS_READ) {
        if (!hktas_ps_particle_data(data)) { hktas_ps_reject_locked(5); return FALSE; }
        for (hktas_ps_writer *other = g_ps_writers; other; other = other->next)
            if (other != writer && other->data == data) { hktas_ps_reject_locked(6); return FALSE; }
        writer->data = data;
    } else if (data != writer->data) { hktas_ps_reject_locked(7); return FALSE; }
    if (from == PS_SCHEDULING) {
        if (!handle) { hktas_ps_reject_locked(8); return FALSE; }
        writer->handle = *handle;
    }
    if (from == PS_PUBLISH && !hktas_ps_same(&writer->handle, handle)) { hktas_ps_reject_locked(9); return FALSE; }
    writer->phase = to;
    ReleaseSRWLockExclusive(&g_ps_lock); return TRUE;
}
static void hktas_ps_published(void *data, const hktas_ps_handle *readback)
{
    AcquireSRWLockExclusive(&g_ps_lock);
    hktas_ps_writer *writer = (hktas_ps_writer *)TlsGetValue(g_ps_tls);
    if (!writer || writer->phase != PS_SETTING || writer->data != data || !hktas_ps_same(&writer->handle, readback)
        || !TlsSetValue(g_ps_tls, NULL)) { hktas_ps_reject_locked(10); return; }
    hktas_ps_writer **slot = &g_ps_writers;
    while (*slot && *slot != writer) slot = &(*slot)->next;
    if (!*slot || g_ps_count <= 0) { hktas_ps_reject_locked(11); return; }
    *slot = writer->next; --g_ps_count; ++g_ps_completed;
    HeapFree(GetProcessHeap(), 0, writer);
    WakeAllConditionVariable(&g_ps_changed); ReleaseSRWLockExclusive(&g_ps_lock);
}
static void __cdecl hktas_ps_get_handle(void *self, hktas_ps_handle *out)
{
    BOOL entered = hktas_ps_enter();
    ((hktas_ps_get_handle_fn)g_ps_original[0])(self, out);
    if (entered) {
        /* The original getter at 354dd0 only unwraps the actual owner and
         * returns &m_ManagedJobData (+128); it does not schedule or sync. */
        void *data = ((hktas_ps_get_data_fn)g_ps_original[1])(self);
        hktas_ps_transition(PS_READ, PS_OWNER, data, NULL);
    }
}
static void *__cdecl hktas_ps_get_data(void *self)
{
    /* A standalone or out-of-order export cannot escape during the epoch.
     * A valid admitted chain may finish while Begin drains existing writers. */
    if (!TlsGetValue(g_ps_tls)) {
        AcquireSRWLockExclusive(&g_ps_lock);
        if (hktas_ps_wait_open_locked()) hktas_ps_reject_locked(12);
    }
    void *data = ((hktas_ps_get_data_fn)g_ps_original[1])(self);
    hktas_ps_transition(PS_OWNER, PS_DATA, data, NULL);
    return data;
}
static void __cdecl hktas_ps_schedule(void *parameters, void *data, hktas_ps_handle *out)
{
    BOOL tracked = hktas_ps_transition(PS_DATA, PS_SCHEDULING, data, NULL);
    ((hktas_ps_schedule_fn)g_ps_original[2])(parameters, data, out);
    if (tracked) hktas_ps_transition(PS_SCHEDULING, PS_PUBLISH, data, out);
}
static void __cdecl hktas_ps_defer(void *parameters, int batch, void *data, void *safety, hktas_ps_handle *out)
{
    if (!TlsGetValue(g_ps_tls)) {
        AcquireSRWLockExclusive(&g_ps_lock);
        BOOL escaped = hktas_ps_particle_data(data);
        if (escaped) hktas_ps_reject_locked(13); else ReleaseSRWLockExclusive(&g_ps_lock);
        ((hktas_ps_defer_fn)g_ps_original[3])(parameters, batch, data, safety, out); return;
    }
    BOOL tracked = hktas_ps_transition(PS_DATA, PS_SCHEDULING, data, NULL);
    ((hktas_ps_defer_fn)g_ps_original[3])(parameters, batch, data, safety, out);
    if (tracked) hktas_ps_transition(PS_SCHEDULING, PS_PUBLISH, data, out);
}
static void __cdecl hktas_ps_set(void *self, const hktas_ps_handle *handle)
{
    if (!TlsGetValue(g_ps_tls)) {
        AcquireSRWLockExclusive(&g_ps_lock); hktas_ps_reject_locked(14);
        ((hktas_ps_set_fn)g_ps_original[4])(self, handle); return;
    }
    void *data = ((hktas_ps_get_data_fn)g_ps_original[1])(self);
    BOOL tracked = hktas_ps_transition(PS_PUBLISH, PS_SETTING, data, handle);
    ((hktas_ps_set_fn)g_ps_original[4])(self, handle);
    if (tracked) {
        DECLSPEC_ALIGN(16) hktas_ps_handle actual = {0};
        ((hktas_ps_get_handle_fn)g_ps_original[0])(self, &actual);
        hktas_ps_published(data, &actual);
    }
}
static void *const g_ps_replacements[] = {
    (void *)hktas_ps_get_handle, (void *)hktas_ps_get_data, (void *)hktas_ps_schedule, (void *)hktas_ps_defer, (void *)hktas_ps_set
};
static BOOL hktas_ps_image(BYTE *base, BOOL installed)
{
    if (!base) return FALSE;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew < 64 || dos->e_lfanew > 4096) return FALSE;
    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 || nt->FileHeader.TimeDateStamp != 0x6008c66eu
        || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC || nt->OptionalHeader.SizeOfImage != 0x1bc7000u
        || nt->OptionalHeader.NumberOfRvaAndSizes <= IMAGE_DIRECTORY_ENTRY_DEBUG) return FALSE;
    const DWORD limit = nt->OptionalHeader.SizeOfImage;
    IMAGE_DATA_DIRECTORY debug = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG];
    if (!debug.VirtualAddress || debug.Size < sizeof(IMAGE_DEBUG_DIRECTORY) || debug.Size > 4096u || debug.VirtualAddress > limit - debug.Size) return FALSE;
    IMAGE_DEBUG_DIRECTORY *rows = (IMAGE_DEBUG_DIRECTORY *)(base + debug.VirtualAddress);
    const BYTE guid[] = {0x90,0xbe,0x34,0x03,0xdd,0x63,0x44,0x46,0xbe,0xe1,0xfc,0x8d,0x56,0xc8,0x40,0xc2};
    BOOL matched = FALSE;
    for (DWORD i = 0; i < debug.Size / sizeof(*rows); ++i) if (rows[i].Type == IMAGE_DEBUG_TYPE_CODEVIEW
        && rows[i].SizeOfData >= 24 && rows[i].AddressOfRawData <= limit - 24u) {
        BYTE *cv = base + rows[i].AddressOfRawData; DWORD age; memcpy(&age, cv+20, 4);
        if (!memcmp(cv,"RSDS",4) && !memcmp(cv+4,guid,16) && age == 1) matched = TRUE;
    }
    if (!matched) return FALSE;
    for (size_t i = 0; i < 5; ++i) {
        hktas_ps_binding b = g_ps_bindings[i];
        uint64_t hash = UINT64_C(14695981039346656037);
        for (DWORD j = 0; j < b.length; ++j) hash = (hash ^ base[b.code + j]) * UINT64_C(1099511628211);
        void *value; memcpy(&value, base + b.slot, sizeof(value));
        if (hash != b.hash || value != (installed ? g_ps_replacements[i] : (void *)(base + b.code))) return FALSE;
    }
    return TRUE;
}
static BOOL install_resident_particle_producer(BYTE *base, DWORD main_thread, hktas_ps_fault_sink sink)
{
    if (InterlockedCompareExchange(&g_ps_attempted,1,0) || !main_thread || main_thread != GetCurrentThreadId() || !sink
        || GetModuleHandleW(L"mono-2.0-bdwgc.dll") || !hktas_ps_image(base,FALSE)) return FALSE;
    void *manager; memcpy(&manager,base+0x19b71f0u,sizeof(manager)); if (manager) return FALSE;
    g_ps_tls = TlsAlloc(); if (g_ps_tls == TLS_OUT_OF_INDEXES) return FALSE;
    g_ps_base = base; g_ps_main = main_thread; g_ps_sink = sink;
    /* All slots live in writable .data. Mono has not resolved an icall yet. */
    size_t n = 0;
    for (; n < 5; ++n) {
        g_ps_original[n] = base + g_ps_bindings[n].code;
        if (InterlockedCompareExchangePointer((PVOID volatile *)(base + g_ps_bindings[n].slot), g_ps_replacements[n], g_ps_original[n]) != g_ps_original[n]) break;
    }
    if (n != 5 || !hktas_ps_image(base,TRUE)) {
        while (n) { --n; InterlockedCompareExchangePointer((PVOID volatile *)(base + g_ps_bindings[n].slot), g_ps_original[n], g_ps_replacements[n]); }
        TlsFree(g_ps_tls); g_ps_tls = TLS_OUT_OF_INDEXES; return FALSE;
    }
    g_ps_installed = TRUE; return TRUE;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_BeginResidentParticleFence(uint64_t token, uint64_t started)
{
    AcquireSRWLockExclusive(&g_ps_lock);
    if (!g_ps_installed || g_ps_reason || g_ps_closed || !token || token <= g_ps_epoch || GetCurrentThreadId() != g_ps_main
        || GetTickCount64() - started >= 9000u || TlsGetValue(g_ps_tls) || !hktas_ps_image(g_ps_base,TRUE)) {
        hktas_ps_fault_locked(20); ReleaseSRWLockExclusive(&g_ps_lock); return 0;
    }
    g_ps_epoch = token; g_ps_started = started; g_ps_closed = TRUE;
    while (g_ps_count && !g_ps_reason) {
        uint64_t elapsed = GetTickCount64() - started;
        if (elapsed >= 9000u || (!SleepConditionVariableSRW(&g_ps_changed,&g_ps_lock,(DWORD)(9000u-elapsed),0) && GetLastError()!=ERROR_TIMEOUT)) break;
    }
    if (g_ps_count || g_ps_reason || GetTickCount64() - started >= 9000u) {
        hktas_ps_fault_locked(21); ReleaseSRWLockExclusive(&g_ps_lock); return 0;
    }
    ReleaseSRWLockExclusive(&g_ps_lock); return 1;
}
static BOOL hktas_ps_held_locked(uint64_t token)
{
    return g_ps_installed && !g_ps_reason && token && g_ps_closed && g_ps_epoch == token && GetCurrentThreadId() == g_ps_main
        && !g_ps_count && !g_ps_writers && GetTickCount64()-g_ps_started < 9000u && hktas_ps_image(g_ps_base,TRUE);
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_RequireResidentParticleFence(uint64_t token)
{
    AcquireSRWLockExclusive(&g_ps_lock);
    BOOL ok = hktas_ps_held_locked(token);
    if (!ok && g_ps_installed && g_ps_closed && g_ps_epoch == token && GetCurrentThreadId() == g_ps_main && !g_ps_count)
        hktas_ps_fault_locked(23);
    ReleaseSRWLockExclusive(&g_ps_lock); return ok;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_EndResidentParticleFence(uint64_t token, LONG complete)
{
#ifdef HKTAS_JOINT_RESIDENT_GATE
    (void)token; (void)complete; return hktas_reject_individual_end();
#else
    AcquireSRWLockExclusive(&g_ps_lock);
    if (complete != 1 || !hktas_ps_held_locked(token)) {
        hktas_ps_fault_locked(22); ReleaseSRWLockExclusive(&g_ps_lock); return 0;
    }
    g_ps_closed = FALSE; WakeAllConditionVariable(&g_ps_changed); ReleaseSRWLockExclusive(&g_ps_lock); return 1;
#endif
}
typedef struct {
    uint32_t magic, version, bytes, installed;
    uint64_t epoch, started, completed;
    uint32_t writers, closed;
    LONG fault;
    DWORD main_thread, caller_thread;
    uint32_t abi_valid;
} hktas_ps_status;
typedef char hktas_ps_status_size[(sizeof(hktas_ps_status)==64 && offsetof(hktas_ps_status,writers)==40 && offsetof(hktas_ps_status,abi_valid)==60)?1:-1];
__declspec(dllexport) LONG __cdecl HktasClockBridge_CopyResidentParticleFenceStatus(BYTE *out, uint32_t bytes)
{
    if (!out || bytes != sizeof(hktas_ps_status)) return 0;
    AcquireSRWLockShared(&g_ps_lock);
    hktas_ps_status state = {0x31465052u,1u,sizeof(hktas_ps_status),(uint32_t)g_ps_installed,
        g_ps_epoch,g_ps_started,g_ps_completed,(uint32_t)g_ps_count,(uint32_t)g_ps_closed,g_ps_reason,g_ps_main,GetCurrentThreadId(),0};
    state.abi_valid = g_ps_installed && hktas_ps_image(g_ps_base,TRUE);
    memcpy(out,&state,sizeof(state)); ReleaseSRWLockShared(&g_ps_lock); return sizeof(state);
}
#endif
