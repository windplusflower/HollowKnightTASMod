#ifndef HKTAS_RESIDENT_AUDIO_FENCE_H
#define HKTAS_RESIDENT_AUDIO_FENCE_H
#include "resident_fatal_evidence.h"
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <limits.h>

/* Pinned FMOD admission only. This is NOT a complete audio-state adapter.
 * The original Thread::callback is copied instruction-for-instruction, with
 * balanced admission calls around startup, each whole iteration and shutdown.
 * Semaphore waits remain outside admission. No audio state/tick is changed.
 * Installation is restricted to the early-APC, no-audio/no-FMOD-system state.
 * Unknown workers outside FMOD::Thread remain an explicit coverage gap.
 */
#define HKTAS_AUDIO_THREAD_RVA 0x10d1210u
#define HKTAS_AUDIO_THREAD_SIZE 283u
#define HKTAS_AUDIO_LEA_RVA 0x10d158fu
#define HKTAS_AUDIO_UNWIND_RVA 0x1833214u
#define HKTAS_AUDIO_IMAGE_MINIMUM 0x1a58a38u
#define HKTAS_AUDIO_PAGE_SIZE 4096u
#define HKTAS_AUDIO_MAX_WORKERS 256u
#define HKTAS_AUDIO_COUNT_MASK UINT64_C(0xffffffff)
#define HKTAS_AUDIO_CLOSED UINT64_C(0x4000000000000000)
#define HKTAS_AUDIO_FAILED UINT64_C(0x8000000000000000)
#define HKTAS_AUDIO_REQUIRED_GAP_NON_THREAD_WORKERS 1u
#define HKTAS_AUDIO_GAP_UNKNOWN_CALLBACK 2u
#define HKTAS_AUDIO_GAP_PENDING_STARTUP 4u
#define HKTAS_AUDIO_WALL_LIMIT 360000u

typedef struct {
    uint16_t offset;
    BYTE size, kind, condition, action;
    uint32_t target;
} hktas_audio_instruction;

static const BYTE g_audio_original_thread[HKTAS_AUDIO_THREAD_SIZE] = {
    0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x74,0x24,0x10,0x57,0x48,0x83,0xec,0x20,0x48,
    0x8b,0xb1,0x48,0x01,0x00,0x00,0x33,0xff,0xc6,0x81,0x18,0x01,0x00,0x00,0x01,0x48,
    0x8b,0xd9,0x48,0x85,0xf6,0x74,0x1c,0x48,0x8b,0xbe,0x90,0x97,0x00,0x00,0x48,0x85,
    0xff,0x74,0x10,0x45,0x33,0xc9,0x4c,0x8d,0x41,0x09,0x48,0x8b,0xce,0x41,0x8d,0x51,
    0x08,0xff,0xd7,0x48,0x8b,0x8b,0x38,0x01,0x00,0x00,0x33,0xd2,0xe8,0xdf,0xe2,0xff,
    0xff,0x0f,0xb6,0x83,0x18,0x01,0x00,0x00,0x84,0xc0,0x0f,0x84,0x7f,0x00,0x00,0x00,
    0x48,0x8b,0x8b,0x30,0x01,0x00,0x00,0x48,0x85,0xc9,0x74,0x05,0xe8,0xef,0xe2,0xff,
    0xff,0x0f,0xb6,0x83,0x18,0x01,0x00,0x00,0x84,0xc0,0x74,0x58,0x48,0x85,0xff,0x74,
    0x10,0x45,0x33,0xc9,0x4c,0x8d,0x43,0x09,0x48,0x8b,0xce,0x41,0x8d,0x51,0x0a,0xff,
    0xd7,0x48,0x8b,0x83,0x40,0x01,0x00,0x00,0x48,0x85,0xc0,0x74,0x0b,0x48,0x8b,0x8b,
    0x20,0x01,0x00,0x00,0xff,0xd0,0xeb,0x08,0x48,0x8b,0x03,0x48,0x8b,0xcb,0xff,0x10,
    0x48,0x85,0xff,0x74,0x10,0x45,0x33,0xc9,0x4c,0x8d,0x43,0x09,0x48,0x8b,0xce,0x41,
    0x8d,0x51,0x0b,0xff,0xd7,0x8b,0x8b,0x50,0x01,0x00,0x00,0x85,0xc9,0x74,0x05,0xe8,
    0x4c,0xe4,0xff,0xff,0x0f,0xb6,0x83,0x18,0x01,0x00,0x00,0x84,0xc0,0x75,0x81,0x48,
    0x8b,0x8b,0x38,0x01,0x00,0x00,0x33,0xd2,0xe8,0x43,0xe2,0xff,0xff,0x48,0x85,0xff,
    0x74,0x10,0x45,0x33,0xc9,0x4c,0x8d,0x43,0x09,0x48,0x8b,0xce,0x41,0x8d,0x51,0x09,
    0xff,0xd7,0x33,0xc9,0xe8,0x47,0x51,0x4c,0x00,0x48,0x8b,0x5c,0x24,0x30,0x33,0xc0,
    0x48,0x8b,0x74,0x24,0x38,0x48,0x83,0xc4,0x20,0x5f,0xc3,
};
static const BYTE g_audio_original_lea[7] = {0x48,0x8d,0x15,0x7a,0xfc,0xff,0xff};
static const BYTE g_audio_original_unwind[16] = {0x01,0x0f,0x06,0x00,0x0f,0x64,0x07,0x00,0x0f,0x34,0x06,0x00,0x0f,0x32,0x0b,0x70};
static const hktas_audio_instruction g_audio_instructions[] = {
    {0u,5u,0u,0u,0u,0x0u},
    {5u,5u,0u,0u,0u,0x0u},
    {10u,1u,0u,0u,0u,0x0u},
    {11u,4u,0u,0u,0u,0x0u},
    {15u,7u,0u,0u,1u,0x0u},
    {22u,2u,0u,0u,0u,0x0u},
    {24u,7u,0u,0u,0u,0x0u},
    {31u,3u,0u,0u,0u,0x0u},
    {34u,3u,0u,0u,0u,0x0u},
    {37u,2u,3u,4u,0u,0x10d1253u},
    {39u,7u,0u,0u,0u,0x0u},
    {46u,3u,0u,0u,0u,0x0u},
    {49u,2u,3u,4u,0u,0x10d1253u},
    {51u,3u,0u,0u,0u,0x0u},
    {54u,4u,0u,0u,0u,0x0u},
    {58u,3u,0u,0u,0u,0x0u},
    {61u,4u,0u,0u,0u,0x0u},
    {65u,2u,0u,0u,0u,0x0u},
    {67u,7u,0u,0u,0u,0x0u},
    {74u,2u,0u,0u,0u,0x0u},
    {76u,5u,1u,0u,0u,0x10cf540u},
    {81u,7u,0u,0u,2u,0x0u},
    {88u,2u,0u,0u,0u,0x0u},
    {90u,6u,3u,4u,0u,0x10d12efu},
    {96u,7u,0u,0u,0u,0x0u},
    {103u,3u,0u,0u,0u,0x0u},
    {106u,2u,3u,4u,0u,0x10d1281u},
    {108u,5u,1u,0u,0u,0x10cf570u},
    {113u,7u,0u,0u,3u,0x0u},
    {120u,2u,0u,0u,0u,0x0u},
    {122u,2u,3u,4u,0u,0x10d12e4u},
    {124u,3u,0u,0u,0u,0x0u},
    {127u,2u,3u,4u,0u,0x10d12a1u},
    {129u,3u,0u,0u,0u,0x0u},
    {132u,4u,0u,0u,0u,0x0u},
    {136u,3u,0u,0u,0u,0x0u},
    {139u,4u,0u,0u,0u,0x0u},
    {143u,2u,0u,0u,0u,0x0u},
    {145u,7u,0u,0u,0u,0x0u},
    {152u,3u,0u,0u,0u,0x0u},
    {155u,2u,3u,4u,0u,0x10d12b8u},
    {157u,7u,0u,0u,0u,0x0u},
    {164u,2u,0u,0u,0u,0x0u},
    {166u,2u,2u,0u,0u,0x10d12c0u},
    {168u,3u,0u,0u,0u,0x0u},
    {171u,3u,0u,0u,0u,0x0u},
    {174u,2u,0u,0u,0u,0x0u},
    {176u,3u,0u,0u,0u,0x0u},
    {179u,2u,3u,4u,0u,0x10d12d5u},
    {181u,3u,0u,0u,0u,0x0u},
    {184u,4u,0u,0u,0u,0x0u},
    {188u,3u,0u,0u,0u,0x0u},
    {191u,4u,0u,0u,0u,0x0u},
    {195u,2u,0u,0u,0u,0x0u},
    {197u,6u,0u,0u,0u,0x0u},
    {203u,2u,0u,0u,0u,0x0u},
    {205u,2u,3u,4u,0u,0x10d12e4u},
    {207u,5u,1u,0u,0u,0x10cf730u},
    {212u,7u,0u,0u,4u,0x0u},
    {219u,2u,0u,0u,0u,0x0u},
    {221u,2u,3u,5u,0u,0x10d1270u},
    {223u,7u,0u,0u,5u,0x0u},
    {230u,2u,0u,0u,0u,0x0u},
    {232u,5u,1u,0u,0u,0x10cf540u},
    {237u,3u,0u,0u,0u,0x0u},
    {240u,2u,3u,4u,0u,0x10d1312u},
    {242u,3u,0u,0u,0u,0x0u},
    {245u,4u,0u,0u,0u,0x0u},
    {249u,3u,0u,0u,0u,0x0u},
    {252u,4u,0u,0u,0u,0x0u},
    {256u,2u,0u,0u,0u,0x0u},
    {258u,2u,0u,0u,6u,0x0u},
    {260u,5u,1u,0u,0u,0x1596460u},
    {265u,5u,0u,0u,0u,0x0u},
    {270u,2u,0u,0u,0u,0x0u},
    {272u,5u,0u,0u,0u,0x0u},
    {277u,4u,0u,0u,0u,0x0u},
    {281u,1u,0u,0u,0u,0x0u},
    {282u,1u,0u,0u,0u,0x0u},
};

typedef void (__cdecl *hktas_audio_fault_sink)(LONG);
typedef struct {
    volatile LONG used;
    DWORD thread_id, kind, phase, depth;
    BYTE *thread, *system;
    uintptr_t callback, system_callback, user_data;
    uint64_t last_epoch;
} hktas_audio_worker;
typedef struct {
    uintptr_t thread, system, callback, system_callback, user_data;
    uint64_t last_epoch;
    DWORD thread_id, kind, phase, depth;
} hktas_audio_worker_row;
typedef struct { DWORD start, pop_flags, release_stack, ret, end; } hktas_audio_thunk;

static DECLSPEC_ALIGN(8) volatile LONG64 g_audio_state;
static DECLSPEC_ALIGN(8) volatile LONG64 g_audio_epoch;
static DECLSPEC_ALIGN(8) volatile LONG64 g_audio_started;
static volatile LONG g_audio_fault_reason, g_audio_unknown_callback, g_audio_record_version, g_audio_pending_startups;
static hktas_audio_worker g_audio_workers[HKTAS_AUDIO_MAX_WORKERS];
static DWORD g_audio_tls = TLS_OUT_OF_INDEXES, g_audio_main_thread;
static HANDLE g_audio_open_event, g_audio_drained_event;
static hktas_audio_fault_sink g_audio_fault_sink;
static BYTE *g_audio_base, *g_audio_relay;
static BYTE g_audio_patched_lea[7];
static BYTE g_audio_relay_expected[HKTAS_AUDIO_PAGE_SIZE];
static BOOL g_audio_installed, g_audio_install_attempted, g_audio_unwind_registered;
static RUNTIME_FUNCTION g_audio_runtime_functions[25];
static DWORD g_audio_runtime_function_count, g_audio_thread_copy_size;
static hktas_audio_thunk g_audio_thunks[6];

static uint64_t hktas_audio_atomic(volatile LONG64 *value)
{ return (uint64_t)InterlockedCompareExchange64(value, 0, 0); }

static void hktas_audio_fail(LONG reason)
{
    uint64_t previous;
    do {
        previous = hktas_audio_atomic(&g_audio_state);
    } while ((uint64_t)InterlockedCompareExchange64(&g_audio_state,
        (LONG64)(previous | HKTAS_AUDIO_CLOSED | HKTAS_AUDIO_FAILED), (LONG64)previous) != previous);
    if (InterlockedCompareExchange(&g_audio_fault_reason, reason, 0) == 0 && g_audio_fault_sink)
        g_audio_fault_sink(66);
    /* Wake waiters so they observe the permanent fault. This never opens the
     * gate, and no original callback is allowed to run after a failed entry. */
    if (g_audio_open_event) SetEvent(g_audio_open_event);
    if (g_audio_drained_event) SetEvent(g_audio_drained_event);
}

static void hktas_audio_fatal(LONG reason)
{
    hktas_audio_fail(reason);
    /* A callback cannot return fabricated FMOD success, or park Unity's main
     * thread indefinitely. This is an owned, permanently failed process. */
    hktas_resident_record_fatal("audio",g_audio_fault_reason);
    TerminateProcess(GetCurrentProcess(), ERROR_INVALID_STATE);
    for (;;) Sleep(1000);
}

static BOOL hktas_audio_deadline(void)
{ return GetTickCount64() - hktas_audio_atomic(&g_audio_started) < HKTAS_AUDIO_WALL_LIMIT; }

static DWORD hktas_audio_kind_at(BYTE *image, uintptr_t callback)
{
    if (callback == (uintptr_t)(image + 0x1143ee0u)) return 1; /* WASAPI feeder */
    if (callback == (uintptr_t)(image + 0x11453f0u)) return 2; /* WASAPI mixer */
    if (callback == (uintptr_t)(image + 0x11464a0u)) return 3; /* WASAPI record */
    if (callback == (uintptr_t)(image + 0x10b8540u)) return 4; /* stream */
    if (callback == (uintptr_t)(image + 0x10d5a10u)) return 5; /* async decoder/file */
    /* fileThreadFunc tail-calls FileThread::threadFunc. This is distinct
     * from the async decoder. Recognition does not cover queued file I/O. */
    if (callback == (uintptr_t)(image + 0x10d2560u)) {
        static const BYTE tail[] = {0xe9,0xfb,0x13,0,0};
        static const BYTE target[] = {0x41,0x56,0x48,0x83,0xec,0x60,0x80,0xb9,0x70,0x01,0,0};
        if (!memcmp(image + 0x10d2560u,tail,sizeof(tail)) &&
            !memcmp(image + 0x10d3960u,target,sizeof(target))) return 6;
    }
    return 0;
}

static uintptr_t hktas_audio_pointer(const BYTE *address)
{ uintptr_t value; memcpy(&value, address, sizeof(value)); return value; }

static void hktas_audio_admit(void)
{
    for (;;) {
        uint64_t state = hktas_audio_atomic(&g_audio_state);
        if (state & HKTAS_AUDIO_FAILED) hktas_audio_fatal(5);
        if (state & HKTAS_AUDIO_CLOSED) {
            if (GetCurrentThreadId() == g_audio_main_thread) hktas_audio_fatal(6);
            uint64_t elapsed = GetTickCount64() - hktas_audio_atomic(&g_audio_started);
            if (elapsed >= HKTAS_AUDIO_WALL_LIMIT) hktas_audio_fatal(7);
            DWORD remaining = (DWORD)(HKTAS_AUDIO_WALL_LIMIT - elapsed);
            DWORD wait = WaitForSingleObject(g_audio_open_event, remaining < 20u ? remaining : 20u);
            if (wait != WAIT_OBJECT_0 && wait != WAIT_TIMEOUT) hktas_audio_fatal(8);
            continue;
        }
        if ((state & HKTAS_AUDIO_COUNT_MASK) >= HKTAS_AUDIO_MAX_WORKERS
            || (state & ~(HKTAS_AUDIO_COUNT_MASK | HKTAS_AUDIO_CLOSED | HKTAS_AUDIO_FAILED)) != 0) hktas_audio_fatal(9);
        /* Admission and closing share one CAS word. No check/increment window
         * can admit an original callback after the closing CAS succeeds. */
        if ((uint64_t)InterlockedCompareExchange64(&g_audio_state, (LONG64)(state + 1u), (LONG64)state) != state) continue;
        return;
    }
}

static hktas_audio_worker *hktas_audio_register(BYTE *thread)
{
    if (!thread || TlsGetValue(g_audio_tls) != NULL) hktas_audio_fatal(1);
    /* Even diagnostic native reads and directory publication belong to the
     * startup admission. A pending thread cannot mutate the frozen directory. */
    InterlockedIncrement(&g_audio_pending_startups);
    hktas_audio_admit();
    for (DWORD i = 0; i < HKTAS_AUDIO_MAX_WORKERS; ++i) {
        hktas_audio_worker *row = &g_audio_workers[i];
        if (InterlockedCompareExchange(&row->used, 1, 0) != 0) continue;
        row->thread = thread; row->thread_id = GetCurrentThreadId();
        row->system = (BYTE *)hktas_audio_pointer(thread + 328);
        row->callback = hktas_audio_pointer(thread + 320);
        row->user_data = hktas_audio_pointer(thread + 288);
        row->system_callback = row->system ? hktas_audio_pointer(row->system + 38800) : 0;
        row->kind = hktas_audio_kind_at(g_audio_base,row->callback); row->phase = 1; row->depth = 1;
        row->last_epoch = hktas_audio_atomic(&g_audio_epoch);
        if (!row->kind) InterlockedExchange(&g_audio_unknown_callback, 1);
        if (!TlsSetValue(g_audio_tls, row)) hktas_audio_fatal(2);
        InterlockedExchange(&row->used, 2);
        InterlockedIncrement(&g_audio_record_version);
        InterlockedDecrement(&g_audio_pending_startups);
        return row;
    }
    hktas_audio_fatal(3); return NULL;
}

static void hktas_audio_enter(hktas_audio_worker *row, DWORD phase)
{
    /* The three injection regions are disjoint in the copied control-flow
     * graph. Recursion is therefore invalid, not an unbounded bypass. */
    if (!row || row->depth != 0 || row->thread_id != GetCurrentThreadId()) hktas_audio_fatal(4);
    hktas_audio_admit();
    row->last_epoch = hktas_audio_atomic(&g_audio_epoch); row->phase = phase; row->depth = 1;
}

static void hktas_audio_leave(hktas_audio_worker *row, DWORD expected_phase, BOOL retire)
{
    if (!row || row->depth != 1 || row->phase != expected_phase || row->thread_id != GetCurrentThreadId()) hktas_audio_fatal(10);
    row->depth = 0; row->phase = 2;
    if (retire) {
        if (!TlsSetValue(g_audio_tls, NULL)) hktas_audio_fatal(15);
        InterlockedExchange(&row->used, 0);
        InterlockedIncrement(&g_audio_record_version);
        /* Do not touch row again: another admitted startup may reuse it. */
    }
    for (;;) {
        uint64_t state = hktas_audio_atomic(&g_audio_state);
        if (!(state & HKTAS_AUDIO_COUNT_MASK)) hktas_audio_fatal(11);
        uint64_t next = state - 1u;
        if ((uint64_t)InterlockedCompareExchange64(&g_audio_state, (LONG64)next, (LONG64)state) != state) continue;
        if (!(next & HKTAS_AUDIO_COUNT_MASK)) SetEvent(g_audio_drained_event);
        return;
    }
}

static void __cdecl hktas_audio_transition(BYTE *thread, DWORD action)
{
    DWORD last_error = GetLastError();
    hktas_audio_worker *row = (hktas_audio_worker *)TlsGetValue(g_audio_tls);
    if (action == 1) {
        row = hktas_audio_register(thread);
    } else {
        if (!row || row->thread != thread || row->used != 2) hktas_audio_fatal(12);
        if (action == 2) hktas_audio_leave(row, 1, FALSE);
        else if (action == 3) {
            if (row->phase != 2) hktas_audio_fatal(13);
            hktas_audio_enter(row, 3);
        } else if (action == 4) hktas_audio_leave(row, 3, FALSE);
        else if (action == 5) {
            if (row->phase != 2) hktas_audio_fatal(14);
            hktas_audio_enter(row, 5);
        } else if (action == 6) {
            hktas_audio_leave(row, 5, TRUE);
        } else hktas_audio_fatal(16);
    }
    SetLastError(last_error);
}

static BOOL hktas_audio_rel32(BYTE *where, BYTE *next, const BYTE *target)
{
    int64_t distance = (intptr_t)target - (intptr_t)next;
    if (distance < INT32_MIN || distance > INT32_MAX) return FALSE;
    int32_t relative = (int32_t)distance; memcpy(where, &relative, 4); return TRUE;
}

static BOOL hktas_audio_image(BYTE *base, BOOL startup)
{
    if (!base || sizeof(void *) != 8) return FALSE;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew < 64 || dos->e_lfanew > 4096) return FALSE;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC
        || nt->OptionalHeader.SizeOfImage < HKTAS_AUDIO_IMAGE_MINIMUM) return FALSE;
    DWORD size = nt->OptionalHeader.SizeOfImage;
    IMAGE_DATA_DIRECTORY debug = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG];
    if (debug.VirtualAddress > size || debug.Size > size - debug.VirtualAddress
        || debug.Size % sizeof(IMAGE_DEBUG_DIRECTORY)) return FALSE;
    static const BYTE guid[16] = {0x90,0xbe,0x34,0x03,0xdd,0x63,0x44,0x46,0xbe,0xe1,0xfc,0x8d,0x56,0xc8,0x40,0xc2};
    BOOL matched = FALSE;
    for (DWORD i = 0; i < debug.Size / sizeof(IMAGE_DEBUG_DIRECTORY); ++i) {
        IMAGE_DEBUG_DIRECTORY *row = (IMAGE_DEBUG_DIRECTORY *)(base + debug.VirtualAddress) + i;
        if (row->Type != IMAGE_DEBUG_TYPE_CODEVIEW || row->SizeOfData < 24 || row->AddressOfRawData > size
            || row->SizeOfData > size - row->AddressOfRawData) continue;
        BYTE *cv = base + row->AddressOfRawData; DWORD age; memcpy(&age, cv + 20, 4);
        if (!memcmp(cv, "RSDS", 4) && !memcmp(cv + 4, guid, 16) && age == 1) matched = TRUE;
    }
    if (!matched || memcmp(base + HKTAS_AUDIO_THREAD_RVA, g_audio_original_thread, sizeof(g_audio_original_thread))
        || memcmp(base + HKTAS_AUDIO_UNWIND_RVA, g_audio_original_unwind, sizeof(g_audio_original_unwind))) return FALSE;
    if (memcmp(base + HKTAS_AUDIO_LEA_RVA, startup ? g_audio_original_lea : g_audio_patched_lea, 7)) return FALSE;
    if (!startup) return g_audio_relay && !memcmp(g_audio_relay, g_audio_relay_expected, HKTAS_AUDIO_PAGE_SIZE);
    /* The APC ordering is also enforced by the injector. These live checks
     * reject a late attach, including an FMOD system created but not yet init. */
    if (hktas_audio_pointer(base + 0x1a28450u) || hktas_audio_pointer(base + 0x19d7a48u)) return FALSE;
    LONG initialized; memcpy(&initialized, base + 0x1a4e450u, 4); if (initialized != 0) return FALSE;
    for (DWORD i = 0; i < 5; ++i) if (hktas_audio_pointer(base + 0x1a58a10u + i * 8u)) return FALSE;
    uintptr_t global = hktas_audio_pointer(base + 0x198ede0u);
    if (global) {
        if (global != (uintptr_t)(base + 0x198edf0u)) return FALSE;
        uintptr_t head = hktas_audio_pointer((BYTE *)global);
        if (head && (head != (uintptr_t)(base + 0x1a4e4c0u)
            || hktas_audio_pointer((BYTE *)head + 8) != head + 8
            || hktas_audio_pointer((BYTE *)head + 16) != head + 8)) return FALSE;
        /* File/async work may precede an initialized SystemI. It cannot be
         * inherited by a freshly installed admission mechanism. */
        LONG busy; memcpy(&busy, (BYTE *)global + 16, 4); if (busy) return FALSE;
        uintptr_t next = hktas_audio_pointer((BYTE *)global + 40), previous = hktas_audio_pointer((BYTE *)global + 48);
        if ((next || previous) && (next != global + 40 || previous != global + 40)) return FALSE;
    }
    return TRUE;
}

static DWORD hktas_audio_instruction_size(const hktas_audio_instruction *row)
{ return (row->action ? 5u : 0u) + (row->kind == 3 ? 6u : row->kind == 2 ? 5u : row->size); }

static BOOL hktas_audio_add_function(DWORD start, DWORD end, DWORD unwind)
{
    if (start >= end || g_audio_runtime_function_count >= 25u) return FALSE;
    RUNTIME_FUNCTION *row = &g_audio_runtime_functions[g_audio_runtime_function_count];
    if (g_audio_runtime_function_count && start < g_audio_runtime_functions[g_audio_runtime_function_count - 1u].EndAddress) return FALSE;
    row->BeginAddress = start; row->EndAddress = end; row->UnwindData = unwind;
    ++g_audio_runtime_function_count; return TRUE;
}

static DWORD hktas_audio_emit_thunk(BYTE *buffer, DWORD action, hktas_audio_thunk *layout)
{
    DWORD at = 0;
    const BYTE prologue[] = {0x9c,0x48,0x81,0xec,0x60,0x02,0x00,0x00}; /* pushfq; sub rsp,608 */
    memcpy(buffer + at, prologue, sizeof(prologue)); at += sizeof(prologue);
    /* Seven volatile integer registers. FXSAVE64 also preserves all XMM,
     * x87 and MXCSR state. RSP+96 is 16-byte aligned at every thunk call. */
    const BYTE save[][5] = {
        {0x48,0x89,0x44,0x24,0x20},{0x48,0x89,0x4c,0x24,0x28},{0x48,0x89,0x54,0x24,0x30},
        {0x4c,0x89,0x44,0x24,0x38},{0x4c,0x89,0x4c,0x24,0x40},{0x4c,0x89,0x54,0x24,0x48},{0x4c,0x89,0x5c,0x24,0x50}
    };
    for (DWORD i = 0; i < 7; ++i) { memcpy(buffer + at, save[i], 5); at += 5; }
    const BYTE fxsave[] = {0x48,0x0f,0xae,0x84,0x24,0x60,0x00,0x00,0x00};
    memcpy(buffer + at, fxsave, sizeof(fxsave)); at += sizeof(fxsave);
    if (action == 1) { memset(buffer + at, 0x90, 3); } /* incoming RCX is Thread* */
    else { const BYTE self[] = {0x48,0x89,0xd9}; memcpy(buffer + at, self, 3); } /* RBX is Thread* */
    at += 3; buffer[at++] = 0xba; memcpy(buffer + at, &action, 4); at += 4;
    buffer[at++] = 0x48; buffer[at++] = 0xb8;
    void (__cdecl *target)(BYTE *, DWORD) = hktas_audio_transition;
    memcpy(buffer + at, &target, 8); at += 8; buffer[at++] = 0xff; buffer[at++] = 0xd0;
    const BYTE fxrestore[] = {0x48,0x0f,0xae,0x8c,0x24,0x60,0x00,0x00,0x00};
    memcpy(buffer + at, fxrestore, sizeof(fxrestore)); at += sizeof(fxrestore);
    for (int i = 6; i >= 0; --i) {
        BYTE restore[5]; memcpy(restore, save[i], 5); restore[1] = 0x8b;
        memcpy(buffer + at, restore, 5); at += 5;
    }
    const BYTE duplicate_flags[] = {0xff,0xb4,0x24,0x60,0x02,0x00,0x00};
    memcpy(buffer + at, duplicate_flags, sizeof(duplicate_flags)); at += sizeof(duplicate_flags);
    layout->pop_flags = at; buffer[at++] = 0x9d;
    /* LEA, rather than ADD, leaves the restored flags unchanged. The one-byte
     * POPFQ and stack-release ranges have their own exact unwind records. */
    layout->release_stack = at;
    const BYTE release[] = {0x48,0x8d,0xa4,0x24,0x68,0x02,0x00,0x00};
    memcpy(buffer + at, release, sizeof(release)); at += sizeof(release);
    layout->ret = at; buffer[at++] = 0xc3; layout->end = at; return at;
}

static BOOL hktas_audio_build_relocation(BYTE *base, BYTE *relay)
{
    enum { count = sizeof(g_audio_instructions) / sizeof(g_audio_instructions[0]) };
    DWORD map[count], cursor = 0;
    for (DWORD i = 0; i < count; ++i) { map[i] = cursor; cursor += hktas_audio_instruction_size(&g_audio_instructions[i]); }
    g_audio_thread_copy_size = cursor; cursor = (cursor + 15u) & ~15u;
    for (DWORD i = 0; i < 6; ++i) {
        hktas_audio_thunk local = {0}; DWORD start = cursor;
        DWORD length = hktas_audio_emit_thunk(relay + cursor, i + 1u, &local);
        if (cursor > HKTAS_AUDIO_PAGE_SIZE || length > HKTAS_AUDIO_PAGE_SIZE - cursor) return FALSE;
        local.start = start; local.pop_flags += start; local.release_stack += start; local.ret += start; local.end += start;
        g_audio_thunks[i] = local; cursor = (cursor + length + 15u) & ~15u;
    }
    for (DWORD i = 0; i < count; ++i) {
        const hktas_audio_instruction *row = &g_audio_instructions[i]; DWORD at = map[i];
        if (row->action) {
            relay[at] = 0xe8;
            if (!hktas_audio_rel32(relay + at + 1u, relay + at + 5u, relay + g_audio_thunks[row->action - 1u].start)) return FALSE;
            at += 5u;
        }
        if (row->kind == 0) { memcpy(relay + at, g_audio_original_thread + row->offset, row->size); continue; }
        BYTE *target = base + row->target;
        if (row->target >= HKTAS_AUDIO_THREAD_RVA && row->target < HKTAS_AUDIO_THREAD_RVA + HKTAS_AUDIO_THREAD_SIZE) {
            BOOL found = FALSE;
            for (DWORD j = 0; j < count; ++j) if (HKTAS_AUDIO_THREAD_RVA + g_audio_instructions[j].offset == row->target) {
                target = relay + map[j]; found = TRUE; break;
            }
            if (!found) return FALSE;
        }
        if (row->kind == 3) {
            relay[at] = 0x0f; relay[at + 1u] = (BYTE)(0x80u | row->condition);
            if (!hktas_audio_rel32(relay + at + 2u, relay + at + 6u, target)) return FALSE;
        } else {
            relay[at] = row->kind == 1 ? 0xe8 : 0xe9;
            if (!hktas_audio_rel32(relay + at + 1u, relay + at + 5u, target)) return FALSE;
        }
    }
    DWORD original_unwind = cursor;
    memcpy(relay + cursor, g_audio_original_unwind, sizeof(g_audio_original_unwind)); cursor += sizeof(g_audio_original_unwind);
    DWORD thunk_unwind = cursor;
    const BYTE unwind_thunk[] = {1,8,3,0,8,1,76,0,1,2,0,0}; /* sub608 + pushfq8 */
    memcpy(relay + cursor, unwind_thunk, sizeof(unwind_thunk)); cursor += sizeof(unwind_thunk);
    DWORD pop_unwind = cursor;
    const BYTE unwind_pop[] = {1,0,2,0,0,1,78,0}; /* flags duplicate temporarily adds8:624 */
    memcpy(relay + cursor, unwind_pop, sizeof(unwind_pop)); cursor += sizeof(unwind_pop);
    DWORD release_unwind = cursor;
    const BYTE unwind_release[] = {1,0,2,0,0,1,77,0}; /* original thunk frame:616 */
    memcpy(relay + cursor, unwind_release, sizeof(unwind_release)); cursor += sizeof(unwind_release);
    DWORD return_unwind = cursor;
    const BYTE unwind_return[] = {1,0,0,0};
    memcpy(relay + cursor, unwind_return, sizeof(unwind_return)); cursor += sizeof(unwind_return);
    if (cursor > HKTAS_AUDIO_PAGE_SIZE) return FALSE;
    g_audio_runtime_function_count = 0;
    if (!hktas_audio_add_function(0, g_audio_thread_copy_size, original_unwind)) return FALSE;
    for (DWORD i = 0; i < 6; ++i) {
        hktas_audio_thunk *row = &g_audio_thunks[i];
        if (!hktas_audio_add_function(row->start, row->pop_flags, thunk_unwind)
            || !hktas_audio_add_function(row->pop_flags, row->release_stack, pop_unwind)
            || !hktas_audio_add_function(row->release_stack, row->ret, release_unwind)
            || !hktas_audio_add_function(row->ret, row->end, return_unwind)) return FALSE;
    }
    return TRUE;
}

static inline BOOL install_resident_audio_fence(BYTE *base, DWORD main_thread, hktas_audio_fault_sink fault_sink)
{
    if (g_audio_install_attempted || !fault_sink || !main_thread || main_thread != GetCurrentThreadId()) return FALSE;
    g_audio_install_attempted = TRUE; g_audio_fault_sink = fault_sink; g_audio_main_thread = main_thread;
    if (!hktas_audio_image(base, TRUE)) { hktas_audio_fail(20); return FALSE; }
    g_audio_base = base;
    g_audio_tls = TlsAlloc();
    g_audio_open_event = CreateEventW(NULL, TRUE, TRUE, NULL);
    g_audio_drained_event = CreateEventW(NULL, TRUE, TRUE, NULL);
    if (g_audio_tls == TLS_OUT_OF_INDEXES || !g_audio_open_event || !g_audio_drained_event) { hktas_audio_fail(21); return FALSE; }
    SYSTEM_INFO info; GetSystemInfo(&info);
    uintptr_t aligned = (uintptr_t)base & ~((uintptr_t)info.dwAllocationGranularity - 1u);
    for (uintptr_t delta = info.dwAllocationGranularity; delta < 0x70000000u; delta += info.dwAllocationGranularity) {
        if (aligned > UINTPTR_MAX - delta) break;
        g_audio_relay = (BYTE *)VirtualAlloc((void *)(aligned + delta), HKTAS_AUDIO_PAGE_SIZE, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (g_audio_relay) break;
    }
    if (!g_audio_relay || !hktas_audio_build_relocation(base, g_audio_relay)) { hktas_audio_fail(22); return FALSE; }
    memcpy(g_audio_relay_expected, g_audio_relay, HKTAS_AUDIO_PAGE_SIZE);
    if (!RtlAddFunctionTable(g_audio_runtime_functions, g_audio_runtime_function_count, (DWORD64)g_audio_relay)) { hktas_audio_fail(23); return FALSE; }
    g_audio_unwind_registered = TRUE;
    DWORD old, ignored;
    if (!VirtualProtect(g_audio_relay, HKTAS_AUDIO_PAGE_SIZE, PAGE_EXECUTE_READ, &old)
        || !FlushInstructionCache(GetCurrentProcess(), g_audio_relay, HKTAS_AUDIO_PAGE_SIZE)) { hktas_audio_fail(24); return FALSE; }
    memcpy(g_audio_patched_lea, g_audio_original_lea, sizeof(g_audio_patched_lea));
    if (!hktas_audio_rel32(g_audio_patched_lea + 3, base + HKTAS_AUDIO_LEA_RVA + 7, g_audio_relay)
        || !hktas_audio_image(base, TRUE)) { hktas_audio_fail(25); return FALSE; }
    BYTE *site = base + HKTAS_AUDIO_LEA_RVA;
    if (!VirtualProtect(site, 7, PAGE_EXECUTE_READWRITE, &old)) { hktas_audio_fail(26); return FALSE; }
    memcpy(site + 3, g_audio_patched_lea + 3, 4);
    /* No unload or rollback is safe once a future native thread can point
     * into this DLL/relay. Installation failures retire the owned process. */
    if (!VirtualProtect(site, 7, old, &ignored) || !FlushInstructionCache(GetCurrentProcess(), site, 7)
        || memcmp(site, g_audio_patched_lea, 7)) { hktas_audio_fail(27); return FALSE; }
    g_audio_installed = TRUE; return TRUE;
}

__declspec(dllexport) uint32_t __cdecl HktasClockBridge_GetResidentAudioFenceCapability(void)
{ return g_audio_installed && g_audio_unwind_registered && !(hktas_audio_atomic(&g_audio_state) & HKTAS_AUDIO_FAILED) ? 1u : 0u; }

__declspec(dllexport) LONG __cdecl HktasClockBridge_BeginResidentAudioFence(uint64_t token, uint64_t boundary_started)
{
    if (!g_audio_installed || !token || GetCurrentThreadId() != g_audio_main_thread || TlsGetValue(g_audio_tls) != NULL
        || token <= hktas_audio_atomic(&g_audio_epoch) || GetTickCount64() - boundary_started >= HKTAS_AUDIO_WALL_LIMIT
        || !hktas_audio_image(g_audio_base, FALSE)) { hktas_audio_fail(30); return 0; }
    uint64_t state = hktas_audio_atomic(&g_audio_state);
    if (state & (HKTAS_AUDIO_CLOSED | HKTAS_AUDIO_FAILED)) { hktas_audio_fail(31); return 0; }
    InterlockedExchange64(&g_audio_epoch, (LONG64)token);
    InterlockedExchange64(&g_audio_started, (LONG64)boundary_started);
    if (!ResetEvent(g_audio_open_event) || !ResetEvent(g_audio_drained_event)) { hktas_audio_fail(32); return 0; }
    for (;;) {
        state = hktas_audio_atomic(&g_audio_state);
        if (state & (HKTAS_AUDIO_CLOSED | HKTAS_AUDIO_FAILED)) { hktas_audio_fail(33); return 0; }
        if ((uint64_t)InterlockedCompareExchange64(&g_audio_state, (LONG64)(state | HKTAS_AUDIO_CLOSED), (LONG64)state) == state) break;
    }
    for (;;) {
        state = hktas_audio_atomic(&g_audio_state);
        if (!hktas_audio_deadline() || (state & HKTAS_AUDIO_FAILED)) { hktas_audio_fail(34); return 0; }
        if (!(state & HKTAS_AUDIO_COUNT_MASK)) return 1;
        uint64_t elapsed = GetTickCount64() - boundary_started;
        DWORD remaining = elapsed >= HKTAS_AUDIO_WALL_LIMIT ? 0u : (DWORD)(HKTAS_AUDIO_WALL_LIMIT - elapsed);
        DWORD wait = WaitForSingleObject(g_audio_drained_event, remaining < 10u ? remaining : 10u);
        if (wait != WAIT_OBJECT_0 && wait != WAIT_TIMEOUT) { hktas_audio_fail(35); return 0; }
        /* A prior open-epoch last exit can leave the manual event signalled.
         * Count is authoritative; clear the hint only while work remains. */
        if (hktas_audio_atomic(&g_audio_state) & HKTAS_AUDIO_COUNT_MASK) ResetEvent(g_audio_drained_event);
    }
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_RequireResidentAudioFence(uint64_t token)
{
    if (!g_audio_installed || !token || token != hktas_audio_atomic(&g_audio_epoch)
        || GetCurrentThreadId() != g_audio_main_thread || TlsGetValue(g_audio_tls) != NULL) return 0;
    uint64_t state = hktas_audio_atomic(&g_audio_state);
    if (state != HKTAS_AUDIO_CLOSED) return 0;
    if (!hktas_audio_deadline() || !hktas_audio_image(g_audio_base, FALSE)) { hktas_audio_fail(36); return 0; }
    return 1;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_EndResidentAudioFence(uint64_t token, LONG complete)
{
#ifdef HKTAS_JOINT_RESIDENT_GATE
    (void)token; (void)complete; return hktas_reject_individual_end();
#else
    if (complete != 1 || !HktasClockBridge_RequireResidentAudioFence(token)) { hktas_audio_fail(37); return 0; }
    if ((uint64_t)InterlockedCompareExchange64(&g_audio_state, 0, (LONG64)HKTAS_AUDIO_CLOSED) != HKTAS_AUDIO_CLOSED) { hktas_audio_fail(38); return 0; }
    if (!SetEvent(g_audio_open_event)) { hktas_audio_fail(39); return 0; }
    return 1;
#endif
}

__declspec(dllexport) uint32_t __cdecl HktasClockBridge_GetResidentAudioFenceCoverageGaps(void)
{
    uint32_t gaps = HKTAS_AUDIO_REQUIRED_GAP_NON_THREAD_WORKERS;
    if (InterlockedCompareExchange(&g_audio_unknown_callback, 0, 0)) gaps |= HKTAS_AUDIO_GAP_UNKNOWN_CALLBACK;
    if (InterlockedCompareExchange(&g_audio_pending_startups, 0, 0)) gaps |= HKTAS_AUDIO_GAP_PENDING_STARTUP;
    return gaps;
}

/* Fixed read-only receipt. No wait, event consumption, admission or failure
 * transition occurs here. A mixed concurrent publication is rejected. Raw
 * addresses, epoch and wall time stay in the observer's local witness. */
typedef struct {
    uint32_t magic, version, bytes, worker_bytes, capability, coverage;
    uint64_t epoch, started, observed, state;
    LONG fault;
    uint32_t worker_count;
    LONG record_version, pending_startups;
    DWORD main_thread, caller_thread;
    uint32_t abi_valid, maximum_workers;
    uint64_t reserved;
} hktas_audio_status;
typedef char hktas_audio_status_size[(sizeof(hktas_audio_status) == 96 && offsetof(hktas_audio_status, state) == 48
    && sizeof(hktas_audio_worker_row) == 64) ? 1 : -1];

__declspec(dllexport) LONG __cdecl HktasClockBridge_CopyResidentAudioFenceStatus(BYTE *output, uint32_t capacity)
{
    if (!output || capacity != sizeof(hktas_audio_status)) return 0;
    hktas_audio_status value = {0};
    value.magic = 0x31464152u; value.version = 1; value.bytes = sizeof(value); value.worker_bytes = sizeof(hktas_audio_worker_row);
    value.record_version = InterlockedCompareExchange(&g_audio_record_version, 0, 0);
    value.epoch = hktas_audio_atomic(&g_audio_epoch); value.started = hktas_audio_atomic(&g_audio_started);
    value.state = hktas_audio_atomic(&g_audio_state); value.fault = InterlockedCompareExchange(&g_audio_fault_reason, 0, 0);
    value.capability = g_audio_installed && g_audio_unwind_registered && !(value.state & HKTAS_AUDIO_FAILED) ? 1u : 0u;
    value.pending_startups = InterlockedCompareExchange(&g_audio_pending_startups, 0, 0);
    value.coverage = HKTAS_AUDIO_REQUIRED_GAP_NON_THREAD_WORKERS;
    if (InterlockedCompareExchange(&g_audio_unknown_callback, 0, 0)) value.coverage |= HKTAS_AUDIO_GAP_UNKNOWN_CALLBACK;
    if (value.pending_startups) value.coverage |= HKTAS_AUDIO_GAP_PENDING_STARTUP;
    for (DWORD i = 0; i < HKTAS_AUDIO_MAX_WORKERS; ++i) {
        LONG used = InterlockedCompareExchange(&g_audio_workers[i].used, 0, 0);
        if (used == 2) ++value.worker_count;
        else if (used != 0) return 0;
    }
    value.main_thread = g_audio_main_thread; value.caller_thread = GetCurrentThreadId();
    value.abi_valid = g_audio_installed && hktas_audio_image(g_audio_base, FALSE) ? 1u : 0u;
    value.maximum_workers = HKTAS_AUDIO_MAX_WORKERS; value.observed = GetTickCount64();
    if (value.record_version != InterlockedCompareExchange(&g_audio_record_version, 0, 0)
        || value.epoch != hktas_audio_atomic(&g_audio_epoch) || value.started != hktas_audio_atomic(&g_audio_started)
        || value.state != hktas_audio_atomic(&g_audio_state) || value.fault != InterlockedCompareExchange(&g_audio_fault_reason, 0, 0)
        || value.pending_startups != InterlockedCompareExchange(&g_audio_pending_startups, 0, 0)
        || value.coverage != HktasClockBridge_GetResidentAudioFenceCoverageGaps()) return 0;
    memcpy(output, &value, sizeof(value)); return 1;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_CopyResidentAudioWorkers(uint64_t token, hktas_audio_worker_row *output, uint32_t capacity)
{
    if (!HktasClockBridge_RequireResidentAudioFence(token) || !output || capacity > HKTAS_AUDIO_MAX_WORKERS) return -1;
    LONG version = InterlockedCompareExchange(&g_audio_record_version, 0, 0); uint32_t count = 0;
    for (DWORD i = 0; i < HKTAS_AUDIO_MAX_WORKERS; ++i) {
        hktas_audio_worker *row = &g_audio_workers[i]; LONG used = InterlockedCompareExchange(&row->used, 0, 0);
        if (used == 0) continue;
        if (used != 2 || count >= capacity || row->depth != 0) return -1;
        hktas_audio_worker_row value = {(uintptr_t)row->thread, (uintptr_t)row->system, row->callback, row->system_callback,
            row->user_data, row->last_epoch, row->thread_id, row->kind, row->phase, row->depth};
        output[count++] = value;
    }
    if (version != InterlockedCompareExchange(&g_audio_record_version, 0, 0) || !HktasClockBridge_RequireResidentAudioFence(token)) return -1;
    return (LONG)count;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_GetResidentAudioFenceFault(void)
{ return InterlockedCompareExchange(&g_audio_fault_reason, 0, 0); }

__declspec(dllexport) uint32_t __cdecl HktasClockBridge_GetResidentAudioFenceInFlight(void)
{ return (uint32_t)(hktas_audio_atomic(&g_audio_state) & HKTAS_AUDIO_COUNT_MASK); }
#endif
