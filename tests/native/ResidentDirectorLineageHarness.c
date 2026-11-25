#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>

#define HKTAS_DN_LIMIT 100000u

enum {
    DN_job_Schedule = 1,
    DN_job_PrepareFrame = 2,
    DN_job_Group = 3,
    DN_job_Exec = 4,
    DN_job_Complete = 5,
    DN_job_CreateGroup = 6,
    DN_job_ScheduleGroups = 7,
    HKTAS_DE_job_PrepareFrame = 0
};

typedef struct {
    DWORD rva;
} hktas_de_entry_stub;

typedef struct {
    unsigned depth, director_depth, tickets;
    uint64_t lineage, counted_epoch;
    BOOL counted;
    void *publishing, *executing;
    BYTE frames[64];
} hktas_dn_thread;

static SRWLOCK g_dn_lock = SRWLOCK_INIT;
static CONDITION_VARIABLE g_dn_cv = CONDITION_VARIABLE_INIT;
static DWORD g_dn_fls = FLS_OUT_OF_INDEXES;
static BYTE g_fake_image[0x800000];
static BYTE *g_dn_base = g_fake_image;
static LONG g_dn_fault;
static BOOL g_dn_closed, g_dn_held;
static DWORD g_dn_active;
static const hktas_de_entry_stub hktas_de_entries[] = { { 0x899090u } };

static uintptr_t hktas_dn_ptr(const BYTE *bytes) { uintptr_t v; memcpy(&v, bytes, 8); return v; }
static uint64_t hktas_dn_u64(const BYTE *bytes) { uint64_t v; memcpy(&v, bytes, 8); return v; }
static uint32_t hktas_dn_u32(const BYTE *bytes) { uint32_t v; memcpy(&v, bytes, 4); return v; }
static BOOL hktas_dn_read(uintptr_t address, void *bytes, size_t count)
{
    if (!count) return TRUE;
    if (!address || count > 32u * 1024u * 1024u) return FALSE;
    memcpy(bytes, (const void *)address, count);
    return TRUE;
}

static void hktas_dn_reject_locked(LONG reason)
{
    if (!g_dn_fault) g_dn_fault = reason;
    g_dn_closed = TRUE;
    ReleaseSRWLockExclusive(&g_dn_lock);
    WakeAllConditionVariable(&g_dn_cv);
}

static BOOL hktas_dn_enter_locked(unsigned site, BOOL inherited)
{
    (void)inherited;
    hktas_dn_thread *t = (hktas_dn_thread *)FlsGetValue(g_dn_fls);
    if (!t || g_dn_held || g_dn_fault || t->depth >= sizeof(t->frames)) {
        hktas_dn_reject_locked(2);
        return FALSE;
    }
    if (!t->depth) ++g_dn_active;
    t->frames[t->depth++] = (BYTE)site;
    ++t->director_depth;
    ReleaseSRWLockExclusive(&g_dn_lock);
    return TRUE;
}

static BOOL hktas_dn_enter(unsigned site)
{
    AcquireSRWLockExclusive(&g_dn_lock);
    return hktas_dn_enter_locked(site, FALSE);
}

static void hktas_dn_leave(unsigned site)
{
    AcquireSRWLockExclusive(&g_dn_lock);
    hktas_dn_thread *t = (hktas_dn_thread *)FlsGetValue(g_dn_fls);
    if (!t || !t->depth || t->frames[t->depth - 1] != site || !t->director_depth) {
        hktas_dn_reject_locked(5);
        return;
    }
    --t->director_depth;
    --t->depth;
    if (!t->depth && g_dn_active) --g_dn_active;
    ReleaseSRWLockExclusive(&g_dn_lock);
}

static void (__cdecl *mock_orig_Schedule)(void*, void (*)(void*, unsigned), void*, int, void (*)(void*), int);
static void (__cdecl *mock_orig_PrepareFrame)(void*, unsigned);
static void* (__cdecl *mock_orig_Group)(void*, void*, void*, int, BYTE);
static int (__cdecl *mock_orig_Exec)(void*, void*, uint64_t, int, BYTE);
static void (__cdecl *mock_orig_Complete)(void*, int);
static void* (__cdecl *mock_orig_CreateGroup)(void*, unsigned, void*);
static void (__cdecl *mock_orig_ScheduleGroups)(void*, void*, void*);

#define hktas_de_original_job_Schedule ((const BYTE*)(void*)mock_orig_Schedule)
#define hktas_de_original_job_PrepareFrame ((const BYTE*)(void*)mock_orig_PrepareFrame)
#define hktas_de_original_job_Group ((const BYTE*)(void*)mock_orig_Group)
#define hktas_de_original_job_Exec ((const BYTE*)(void*)mock_orig_Exec)
#define hktas_de_original_job_Complete ((const BYTE*)(void*)mock_orig_Complete)
#define hktas_de_original_job_CreateGroup ((const BYTE*)(void*)mock_orig_CreateGroup)
#define hktas_de_original_job_ScheduleGroups ((const BYTE*)(void*)mock_orig_ScheduleGroups)

#include "../../native/HollowKnightTAS.ClockBridge/resident_director_job_tickets.h"

#define CHECK(cond) do { if (!(cond)) { fprintf(stderr, "FAIL line %d: %s (fault=%ld)\n", __LINE__, #cond, (long)g_dn_fault); return 1; } } while (0)

typedef struct mock_job_info {
    struct mock_job_info *next;
    uintptr_t callback;
    uintptr_t data;
    DWORD flags;
    DWORD pad;
    void *group;
} mock_job_info;

typedef struct mock_job_group {
    mock_job_info *first;
    uint64_t version;
    DWORD count;
    DWORD pad1;
    mock_job_info *last;
    uintptr_t completion;
    uintptr_t completion_data;
    void *pool_node;
    void *dep;
    void *batch_link;
} mock_job_group;

typedef struct mock_batch_node {
    struct mock_batch_node *next;
    void *group;
} mock_batch_node;

typedef struct mock_fence {
    void *group;
    DWORD version;
    DWORD pad;
} mock_fence;

static mock_job_group g_pool_group;
static mock_job_info g_pool_jobs[4];
static void *g_queue = (void *)0x11110000ULL;
static hktas_dn_thread g_main_thread;
static hktas_dn_thread g_worker_thread;
static BOOL g_in_flight_reused;
static HANDLE g_exec_entered_evt;
static HANDLE g_exec_release_evt;

static void reset_state(void)
{
    while (g_dn_lineages) {
        hktas_dn_lineage *n = g_dn_lineages->next;
        HeapFree(GetProcessHeap(), 0, g_dn_lineages);
        g_dn_lineages = n;
    }
    g_dn_lineage_count = 0;
    g_dn_lineage_rows = 0;
    g_dn_lineage_issued = g_dn_lineage_retired = 0;
    g_dn_lineage_entered = g_dn_lineage_returned = 0;
    g_dn_lineage_nested = 0;
    g_dn_fault = 0;
    g_dn_closed = FALSE;
    g_dn_held = FALSE;
    g_dn_active = 0;
    memset(&g_pool_group, 0, sizeof(g_pool_group));
    memset(g_pool_jobs, 0, sizeof(g_pool_jobs));
    memset(&g_main_thread, 0, sizeof(g_main_thread));
    memset(&g_worker_thread, 0, sizeof(g_worker_thread));
    FlsSetValue(g_dn_fls, &g_main_thread);
    g_in_flight_reused = FALSE;
}

static void* __cdecl impl_create_group(void *queue, unsigned count, void *dep)
{
    (void)queue;
    (void)dep;
    g_pool_group.count = count;
    g_pool_group.completion = 0;
    g_pool_group.completion_data = 0;
    if (count == 0) {
        g_pool_group.first = NULL;
        g_pool_group.last = NULL;
    } else {
        for (unsigned i = 0; i < count; ++i) {
            memset(&g_pool_jobs[i], 0, sizeof(g_pool_jobs[i]));
            g_pool_jobs[i].next = (i + 1 < count) ? &g_pool_jobs[i + 1] : NULL;
            g_pool_jobs[i].callback = (uintptr_t)0x12345678ULL;
            g_pool_jobs[i].data = (uintptr_t)(0x2000u + i);
            g_pool_jobs[i].flags = i;
            g_pool_jobs[i].group = &g_pool_group;
        }
        g_pool_group.first = &g_pool_jobs[0];
        g_pool_group.last = &g_pool_jobs[count - 1];
    }
    return &g_pool_group;
}

static void* __cdecl impl_group(void *queue, void *out, void *group, int priority, BYTE inline_mode)
{
    (void)queue; (void)priority; (void)inline_mode;
    mock_job_group *g = (mock_job_group *)group;
    mock_fence *f = (mock_fence *)out;
    f->group = group;
    f->version = (DWORD)(g->version + 2u);
    return out;
}

static int __cdecl impl_exec_simple(void *queue, void *job, uint64_t generation, int decrement, BYTE inline_mode)
{
    (void)queue; (void)job; (void)generation; (void)decrement; (void)inline_mode;
    return 1;
}

static int __cdecl impl_exec_in_flight(void *queue, void *job, uint64_t generation, int decrement, BYTE inline_mode)
{
    (void)queue; (void)job; (void)decrement; (void)inline_mode;
    if (generation == 10 && !g_in_flight_reused) {
        g_in_flight_reused = TRUE;
        /* Simulate Unity completing the group and returning it to free_groups
         * with bumped version BEFORE JobQueue::Exec returns to dn_job_Exec. */
        g_pool_group.version = 9; /* next CreateGroup generation = 11 */
        SetEvent(g_exec_entered_evt);
        WaitForSingleObject(g_exec_release_evt, 5000);
    }
    return 1;
}

static void __cdecl impl_schedule_groups(void *queue, void *first, void *last)
{
    (void)queue; (void)first; (void)last;
}

static void __cdecl impl_complete_clear(void *fence, int mode)
{
    (void)mode;
    mock_fence *f = (mock_fence *)fence;
    f->group = NULL;
    f->version = 0;
}

/* Call dn_job_Exec through a tiny executable trampoline whose return PC is
 * g_dn_base + 0x726f76 (an authorized native dispatch site) or invalid PC. */
typedef int (__cdecl *exec_call_fn)(void*, void*, uint64_t, int, BYTE);
static int call_dn_exec_from_site(DWORD site_rva, void *queue, void *job, uint64_t generation)
{
    BYTE *site = g_dn_base + site_rva;
    DWORD old = 0;
    VirtualProtect(site - 16, 32, PAGE_EXECUTE_READWRITE, &old);
    /* Write at site - 5: E8 <rel32 to dn_job_Exec> C3 (ret), so return PC == site! */
    BYTE *call_at = site - 5;
    intptr_t delta = (intptr_t)(uintptr_t)(void *)dn_job_Exec - (intptr_t)(uintptr_t)site;
    call_at[0] = 0xE8;
    memcpy(call_at + 1, &delta, 4);
    call_at[5] = 0xC3;
    FlushInstructionCache(GetCurrentProcess(), call_at, 6);
    exec_call_fn fn = (exec_call_fn)(void *)call_at;
    return fn(queue, job, generation, 1, 0);
}

static DWORD WINAPI worker_in_flight_exec(LPVOID param)
{
    (void)param;
    FlsSetValue(g_dn_fls, &g_worker_thread);
    int r = call_dn_exec_from_site(0x726f76u, g_queue, &g_pool_jobs[0], 10);
    return (DWORD)r;
}

static int test_fence_reuse_non_director_direct_exec(void)
{
    reset_state();
    mock_orig_CreateGroup = impl_create_group;
    mock_orig_Exec = impl_exec_simple;

    /* 1. Inside Director: create empty (count=0) group, never Group/ScheduleGroups/Exec. */
    g_main_thread.director_depth = 1;
    g_main_thread.depth = 1;
    g_pool_group.version = 0;
    void *g1 = dn_job_CreateGroup(g_queue, 0, NULL);
    CHECK(g1 == &g_pool_group && g_dn_fault == 0 && g_dn_lineage_count == 1 && g_dn_lineage_issued == 1);

    /* Unity frees g1 back to free_groups with bumped version (next gen = 3). */
    g_pool_group.version = 1;

    /* 2. Outside Director: CreateGroup pops g1 and executes directly via Exec (no Group/ScheduleGroups). */
    g_main_thread.director_depth = 0;
    g_main_thread.depth = 0;
    void *g2 = dn_job_CreateGroup(g_queue, 1, NULL);
    CHECK(g2 == &g_pool_group && g_dn_fault == 0);
    CHECK(g_dn_lineage_count == 0 && g_dn_lineages == NULL && g_dn_lineage_retired == 1);

    int r = call_dn_exec_from_site(0x726f76u, g_queue, &g_pool_jobs[0], 3);
    CHECK(r == 1 && g_dn_fault == 0);
    CHECK(g_dn_lineage_issued == g_dn_lineage_retired && g_dn_lineage_entered == g_dn_lineage_returned);

    /* 3. Also test 1-job fence group freed WITHOUT version bump (next gen == old gen == 2). */
    reset_state();
    g_main_thread.director_depth = 1;
    g_main_thread.depth = 1;
    g_pool_group.version = 0;
    CHECK(dn_job_CreateGroup(g_queue, 1, NULL) == &g_pool_group && g_dn_lineage_count == 1);

    g_main_thread.director_depth = 0;
    g_main_thread.depth = 0;
    CHECK(dn_job_CreateGroup(g_queue, 1, NULL) == &g_pool_group && g_dn_fault == 0);
    CHECK(g_dn_lineage_count == 0 && g_dn_lineage_retired == 1);
    CHECK(call_dn_exec_from_site(0x726f76u, g_queue, &g_pool_jobs[0], 2) == 1 && g_dn_fault == 0);

    puts("PASS test_fence_reuse_non_director_direct_exec");
    return 0;
}

static int test_complete_retires_fence_and_completion_only_group(void)
{
    reset_state();
    mock_orig_CreateGroup = impl_create_group;
    mock_orig_Group = impl_group;
    mock_orig_Complete = impl_complete_clear;

    /* 1. Fence group (count=1, never Group/Exec) completed via dn_job_Complete. */
    g_main_thread.director_depth = 1;
    g_main_thread.depth = 1;
    g_pool_group.version = 4; /* generation = 6 */
    CHECK(dn_job_CreateGroup(g_queue, 1, NULL) == &g_pool_group && g_dn_lineage_count == 1);
    mock_fence fence = { &g_pool_group, 6, 0 };
    dn_job_Complete(&fence, 0);
    CHECK(g_dn_fault == 0 && fence.group == NULL && fence.version == 0);
    CHECK(g_dn_lineage_count == 0 && g_dn_lineages == NULL && g_dn_lineage_retired == g_dn_lineage_issued);

    /* 2. Completion-only count=0 group (published stays FALSE after Group) completed via dn_job_Complete. */
    reset_state();
    g_main_thread.director_depth = 1;
    g_main_thread.depth = 1;
    g_pool_group.version = 10; /* generation = 12 */
    CHECK(dn_job_CreateGroup(g_queue, 0, NULL) == &g_pool_group && g_dn_lineage_count == 1);
    g_pool_group.count = 1; /* 0 jobs + 1 completion */
    g_pool_group.completion = (uintptr_t)0xdeadbeefULL;
    g_pool_group.completion_data = (uintptr_t)0xcafebabeULL;
    mock_fence out = { 0 };
    CHECK(dn_job_Group(g_queue, &out, &g_pool_group, 0, 0) == &out && g_dn_fault == 0);
    CHECK(g_dn_lineage_count == 1 && out.group == &g_pool_group && out.version == 12);

    /* Stale handle with wrong version must NOT touch live lineage or fault. */
    mock_fence stale = { &g_pool_group, 8, 0 };
    dn_job_Complete(&stale, 0);
    CHECK(g_dn_fault == 0 && g_dn_lineage_count == 1);

    /* Completing the actual handle retires the completion-only lineage. */
    g_main_thread.director_depth = 0;
    g_main_thread.depth = 0;
    dn_job_Complete(&out, 0);
    CHECK(g_dn_fault == 0 && g_dn_lineage_count == 0 && g_dn_lineages == NULL);
    CHECK(g_dn_lineage_retired == g_dn_lineage_issued && !g_dn_active);

    puts("PASS test_complete_retires_fence_and_completion_only_group");
    return 0;
}

static int test_in_flight_exec_tail_superseded_not_freed(void)
{
    reset_state();
    mock_orig_CreateGroup = impl_create_group;
    mock_orig_Group = impl_group;
    mock_orig_Exec = impl_exec_in_flight;

    g_exec_entered_evt = CreateEventW(NULL, TRUE, FALSE, NULL);
    g_exec_release_evt = CreateEventW(NULL, TRUE, FALSE, NULL);
    CHECK(g_exec_entered_evt && g_exec_release_evt);

    g_main_thread.director_depth = 1;
    g_main_thread.depth = 1;
    g_pool_group.version = 8; /* gen = 10 */
    CHECK(dn_job_CreateGroup(g_queue, 1, NULL) == &g_pool_group);
    mock_fence out = { 0 };
    CHECK(dn_job_Group(g_queue, &out, &g_pool_group, 0, 0) == &out && g_dn_fault == 0);

    HANDLE worker = CreateThread(NULL, 0, worker_in_flight_exec, NULL, 0, NULL);
    CHECK(worker != NULL);
    CHECK(WaitForSingleObject(g_exec_entered_evt, 5000) == WAIT_OBJECT_0);

    /* Worker is still inside Exec(10), while pool_group was recycled with version=9 (next gen=11). */
    g_main_thread.director_depth = 0;
    g_main_thread.depth = 0;
    CHECK(dn_job_CreateGroup(g_queue, 1, NULL) == &g_pool_group && g_dn_fault == 0);
    CHECK(g_dn_lineage_count == 1 && g_dn_lineages && g_dn_lineages->superseded && g_dn_lineages->successor == 11);

    /* New non-Director group (gen=11) executes directly while old Exec(10) tail is still in flight. */
    CHECK(call_dn_exec_from_site(0x726f76u, g_queue, &g_pool_jobs[0], 11) == 1 && g_dn_fault == 0);
    CHECK(g_dn_lineage_count == 1);

    SetEvent(g_exec_release_evt);
    DWORD exit_code = 0;
    CHECK(WaitForSingleObject(worker, 5000) == WAIT_OBJECT_0 && GetExitCodeThread(worker, &exit_code) && exit_code == 1);
    CloseHandle(worker);
    CloseHandle(g_exec_entered_evt);
    CloseHandle(g_exec_release_evt);

    CHECK(g_dn_fault == 0 && g_dn_lineage_count == 0 && g_dn_lineages == NULL);
    CHECK(g_dn_lineage_issued == g_dn_lineage_retired && g_dn_lineage_entered == g_dn_lineage_returned);

    puts("PASS test_in_flight_exec_tail_superseded_not_freed");
    return 0;
}

static int test_director_reuse_same_generation_and_strict_checks(void)
{
    reset_state();
    mock_orig_CreateGroup = impl_create_group;
    mock_orig_Group = impl_group;
    mock_orig_Exec = impl_exec_simple;

    /* 1. Director reuses group freed without version bump (next == old gen == 2). */
    g_main_thread.director_depth = 1;
    g_main_thread.depth = 1;
    g_pool_group.version = 0;
    CHECK(dn_job_CreateGroup(g_queue, 0, NULL) == &g_pool_group && g_dn_lineage_count == 1);
    CHECK(dn_job_CreateGroup(g_queue, 1, NULL) == &g_pool_group && g_dn_fault == 0);
    CHECK(g_dn_lineage_count == 1 && g_dn_lineage_issued == 2 && g_dn_lineage_retired == 1);
    mock_fence out = { 0 };
    CHECK(dn_job_Group(g_queue, &out, &g_pool_group, 0, 0) == &out && g_dn_fault == 0);
    CHECK(call_dn_exec_from_site(0x726f76u, g_queue, &g_pool_jobs[0], 2) == 1 && g_dn_fault == 0);
    CHECK(g_dn_lineage_count == 0 && g_dn_lineage_issued == g_dn_lineage_retired);

    /* 2. Strict wrong_version still faults 40 (6840) when not superseded. */
    reset_state();
    g_main_thread.director_depth = 1;
    g_main_thread.depth = 1;
    g_pool_group.version = 8; /* gen = 10 */
    CHECK(dn_job_CreateGroup(g_queue, 1, NULL) == &g_pool_group);
    CHECK(call_dn_exec_from_site(0x726f76u, g_queue, &g_pool_jobs[0], 99) == 0 && g_dn_fault == 40);

    /* 3. Unauthorized dispatch site still faults 40 (6840). */
    reset_state();
    g_main_thread.director_depth = 1;
    g_main_thread.depth = 1;
    g_pool_group.version = 8; /* gen = 10 */
    CHECK(dn_job_CreateGroup(g_queue, 1, NULL) == &g_pool_group);
    CHECK(call_dn_exec_from_site(0x720100u, g_queue, &g_pool_jobs[0], 10) == 0 && g_dn_fault == 40);

    /* 4. Version rollback in CreateGroup still faults 39 (6839). */
    reset_state();
    g_main_thread.director_depth = 1;
    g_main_thread.depth = 1;
    g_pool_group.version = 8; /* gen = 10 */
    CHECK(dn_job_CreateGroup(g_queue, 0, NULL) == &g_pool_group);
    g_main_thread.director_depth = 0;
    g_main_thread.depth = 0;
    g_pool_group.version = 2; /* gen = 4 < 10 */
    CHECK(dn_job_CreateGroup(g_queue, 0, NULL) == NULL && g_dn_fault == 39);

    puts("PASS test_director_reuse_same_generation_and_strict_checks");
    return 0;
}

static int test_schedule_groups_completion_and_reuse(void)
{
    reset_state();
    mock_orig_CreateGroup = impl_create_group;
    mock_orig_ScheduleGroups = impl_schedule_groups;
    mock_orig_Complete = impl_complete_clear;
    mock_orig_Exec = impl_exec_simple;

    mock_batch_node node = { NULL, &g_pool_group };
    g_main_thread.director_depth = 1;
    g_main_thread.depth = 1;
    g_pool_group.version = 6; /* gen = 8 */
    CHECK(dn_job_CreateGroup(g_queue, 0, NULL) == &g_pool_group && g_dn_lineage_count == 1);
    g_pool_group.count = 1; /* 0 jobs + 1 completion */
    g_pool_group.completion = (uintptr_t)0xdeadbeefULL;
    g_pool_group.completion_data = (uintptr_t)0x1234ULL;
    g_pool_group.pool_node = &node;

    dn_job_ScheduleGroups(g_queue, &g_pool_group, &g_pool_group);
    CHECK(g_dn_fault == 0 && g_dn_lineage_count == 1);

    g_main_thread.director_depth = 0;
    g_main_thread.depth = 0;
    mock_fence fence = { &g_pool_group, 8, 0 };
    dn_job_Complete(&fence, 0);
    CHECK(g_dn_fault == 0 && g_dn_lineage_count == 0 && g_dn_lineage_retired == g_dn_lineage_issued);

    puts("PASS test_schedule_groups_completion_and_reuse");
    return 0;
}

int main(void)
{
    (void)dn_job_Schedule;
    (void)dn_job_PrepareFrame;
    g_dn_fls = FlsAlloc(NULL);
    CHECK(g_dn_fls != FLS_OUT_OF_INDEXES);
    if (test_fence_reuse_non_director_direct_exec()) return 1;
    if (test_complete_retires_fence_and_completion_only_group()) return 1;
    if (test_in_flight_exec_tail_superseded_not_freed()) return 1;
    if (test_director_reuse_same_generation_and_strict_checks()) return 1;
    if (test_schedule_groups_completion_and_reuse()) return 1;
    FlsFree(g_dn_fls);
    puts("ALL ResidentDirectorLineageHarness CHECKS PASSED");
    return 0;
}
