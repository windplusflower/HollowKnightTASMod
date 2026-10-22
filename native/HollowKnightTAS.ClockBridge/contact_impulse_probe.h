#ifndef HKTAS_CONTACT_IMPULSE_PROBE_H
#define HKTAS_CONTACT_IMPULSE_PROBE_H
/* Pinned canonical reporting rule plus an independent finite diagnostic.
 * The original 5-byte WorldContactListener2D tail jump needs no relocation.
 * Records intentionally retain callback bytes even outside impulse.count. */
#define HKTAS_IMPULSE_RECORDS 512
typedef struct {
    LONG published, movie_frame;
    uint64_t native_frame;
    uintptr_t contact;
    LONG manifold_count, impulse_count;
    BYTE impulse[16], report_before[16], report_after[16];
    LONG input_unchanged, report_present;
    uintptr_t fixture_a, fixture_b;
    LONG child_a, child_b;
    BYTE passed_impulse[16];
    LONG canonical_policy;
} hktas_impulse_record;
typedef char hktas_impulse_record_abi[(sizeof(hktas_impulse_record) == 136) ? 1 : -1];
static hktas_impulse_record g_impulse_records[HKTAS_IMPULSE_RECORDS];
static volatile LONG g_impulse_count, g_impulse_overflow;
static volatile LONG g_impulse_movie = -1;
static BYTE *g_impulse_site;
static BYTE g_impulse_patch[5];
static BOOL g_impulse_installed;
static BOOL g_impulse_canonical;
static volatile LONG g_impulse_policy_fault;
static volatile LONG g_impulse_policy_callbacks;
static void (__cdecl *g_impulse_original)(void *, BYTE *, const BYTE *);
static const BYTE g_impulse_expected[5] = {0xe9,0x07,0x1a,0xff,0xff};

static BOOL prepare_canonical_contact_impulse(const BYTE *input, BYTE *local)
{
    LONG count; memcpy(&count, input + 16, 4);
    if (count < 0 || count > 2) return FALSE;
    memcpy(local, input, 20);
    for (LONG i = count; i < 2; ++i) {
        memset(local + i * 4, 0, 4);
        memset(local + 8 + i * 4, 0, 4);
    }
    return TRUE;
}

static void __cdecl hktas_probe_post_solve(void *self, BYTE *contact, const BYTE *impulse)
{
    LONG movie = InterlockedCompareExchange(&g_impulse_movie, 0, 0);
    hktas_impulse_record *row = NULL;
    BYTE original_input[20];
    BYTE initialized_input[20];
    const BYTE *passed = impulse;
    BYTE *report = NULL;
    if (g_impulse_canonical) {
        if (!prepare_canonical_contact_impulse(impulse, initialized_input)) {
            InterlockedExchange(&g_impulse_policy_fault, 1);
            hktas_v2_fault(64); return; /* Never clamp or call with an invalid ABI. */
        }
        passed = initialized_input;
        InterlockedIncrement(&g_impulse_policy_callbacks);
    }
    if (movie >= 1695 && movie <= 1705) {
        LONG index = InterlockedIncrement(&g_impulse_count) - 1;
        if (index < HKTAS_IMPULSE_RECORDS) {
            row = &g_impulse_records[index];
            row->movie_frame = movie;
            row->native_frame = hktas_get_completed_player_loops();
            row->contact = (uintptr_t)contact;
            memcpy(&row->fixture_a, contact + 96, 8);
            memcpy(&row->fixture_b, contact + 104, 8);
            memcpy(&row->child_a, contact + 112, 4);
            memcpy(&row->child_b, contact + 116, 4);
            memcpy(&row->manifold_count, contact + 188, 4);
            memcpy(&row->impulse_count, impulse + 16, 4);
            memcpy(original_input, impulse, 20);
            memcpy(row->impulse, impulse, 16);
            memcpy(row->passed_impulse, passed, 16);
            row->canonical_policy = g_impulse_canonical;
            LONG user_index; BYTE *collision;
            memcpy(&user_index, contact + 296, 4);
            memcpy(&collision, contact + 304, sizeof(collision));
            if (user_index >= 0 && user_index < 4096 && collision != NULL) {
                BYTE *storage; memcpy(&storage, collision, sizeof(storage));
                if (storage != NULL) {
                    report = storage + (size_t)user_index * 112 + 96;
                    memcpy(row->report_before, report, 16);
                    row->report_present = 1;
                }
            }
        } else InterlockedExchange(&g_impulse_overflow, 1);
    }
    /* Compiler-generated Win64 ABI supplies shadow space/alignment and saves
     * nonvolatile registers. No managed call, allocation, lock or file I/O. */
    g_impulse_original(self, contact, passed);
    if (row != NULL) {
        if (report != NULL) memcpy(row->report_after, report, 16);
        row->input_unchanged = memcmp(original_input, impulse, 20) == 0;
        InterlockedExchange(&row->published, 1);
    }
}

static BOOL install_contact_impulse_probe(BYTE *base)
{
    wchar_t enabled[4] = {0}, policy[64] = {0}, canonical[4] = {0}, legacy[4] = {0};
    DWORD probe_length = GetEnvironmentVariableW(L"HKTAS_CONTACT_IMPULSE_PROBE", enabled, 4);
    DWORD policy_length = GetEnvironmentVariableW(L"HKTAS_CANONICAL_CONTACT_IMPULSE", policy, 64);
    GetEnvironmentVariableW(L"HKTAS_LOADING_CANONICAL_BOUNDARY", canonical, 4);
    GetEnvironmentVariableW(L"HKTAS_LOADING_CANONICAL_BOUNDARY_LEGACY", legacy, 4);
    if (probe_length && wcscmp(enabled, L"1") != 0) return FALSE;
    if (wcscmp(canonical, L"1") == 0 && wcscmp(legacy, L"1") != 0 && !policy_length) return FALSE;
    if (policy_length && (policy_length >= 64 || wcscmp(policy, L"zero-unused-impulse-slots-v1") != 0
        || wcscmp(canonical, L"1") != 0 || wcscmp(legacy, L"1") == 0)) return FALSE;
    if (!probe_length && !policy_length) return TRUE;
    BYTE *site = base + 0xc134c4;
    const BYTE prefix[4] = {0x48,0x8b,0x49,0x08};
    if (memcmp(site - 4, prefix, 4) || memcmp(site, g_impulse_expected, 5)) return FALSE;
    SYSTEM_INFO info; GetSystemInfo(&info);
    uintptr_t aligned = (uintptr_t)base & ~((uintptr_t)info.dwAllocationGranularity - 1);
    BYTE *relay = NULL;
    for (uintptr_t delta = info.dwAllocationGranularity; delta < 0x70000000; delta += info.dwAllocationGranularity) {
        relay = VirtualAlloc((void *)(aligned + delta), 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (relay) break;
    }
    if (!relay) return FALSE;
    int64_t distance = (intptr_t)relay - (intptr_t)(site + 5);
    if (distance < INT32_MIN || distance > INT32_MAX) return FALSE;
    relay[0] = 0xff; relay[1] = 0x25; memset(relay + 2, 0, 4);
    void (*target)(void *, BYTE *, const BYTE *) = hktas_probe_post_solve;
    memcpy(relay + 6, &target, sizeof(target));
    DWORD old, ignored;
    if (!VirtualProtect(relay, 4096, PAGE_EXECUTE_READ, &old)) return FALSE;
    FlushInstructionCache(GetCurrentProcess(), relay, 14);
    g_impulse_original = (void (__cdecl *)(void *, BYTE *, const BYTE *))(base + 0xc04ed0);
    g_impulse_patch[0] = 0xe9; int32_t relative = (int32_t)distance;
    memcpy(g_impulse_patch + 1, &relative, 4);
    if (!VirtualProtect(site, 5, PAGE_EXECUTE_READWRITE, &old)) return FALSE;
    memcpy(site, g_impulse_patch, 5);
    if (!VirtualProtect(site, 5, old, &ignored)) return FALSE;
    FlushInstructionCache(GetCurrentProcess(), site, 5);
    g_impulse_site = site; g_impulse_installed = TRUE;
    g_impulse_canonical = policy_length != 0;
    return TRUE;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_SetContactImpulseProbeFrame(LONG movie)
{
    if (!g_impulse_installed || movie < -1 || movie > 3150) return 0;
    InterlockedExchange(&g_impulse_movie, movie); return 1;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_CopyContactImpulseProbe(void *output, LONG bytes)
{
    LONG count = InterlockedCompareExchange(&g_impulse_count, 0, 0);
    if (!g_impulse_installed || g_impulse_overflow || count > HKTAS_IMPULSE_RECORDS || bytes != (LONG)sizeof(g_impulse_records)) return -1;
    for (LONG i = 0; i < count; ++i) if (!g_impulse_records[i].published) return -1;
    memcpy(output, g_impulse_records, sizeof(g_impulse_records)); return count;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_StopContactImpulseProbe(void)
{
    InterlockedExchange(&g_impulse_movie, -1);
    if (g_impulse_canonical) return g_impulse_installed ? 1 : 0;
    if (!g_impulse_installed || memcmp(g_impulse_site, g_impulse_patch, 5)) return 0;
    DWORD old, ignored;
    if (!VirtualProtect(g_impulse_site, 5, PAGE_EXECUTE_READWRITE, &old)) return 0;
    memcpy(g_impulse_site, g_impulse_expected, 5);
    if (!VirtualProtect(g_impulse_site, 5, old, &ignored)) return 0;
    FlushInstructionCache(GetCurrentProcess(), g_impulse_site, 5);
    g_impulse_installed = FALSE;
    return memcmp(g_impulse_site, g_impulse_expected, 5) == 0;
}
static BOOL finish_contact_impulse_policy(void)
{
    if (!g_impulse_canonical) return TRUE;
    /* Called by the original gate between PlayerLoops, after physics jobs
     * have joined. Diagnostic sealing must not retire the session policy. */
    g_impulse_canonical = FALSE;
    return HktasClockBridge_StopContactImpulseProbe() == 1;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_GetContactImpulsePolicy(void)
{
    return g_impulse_installed && g_impulse_canonical && !g_impulse_policy_fault ? 1 : 0;
}
__declspec(dllexport) LONG __cdecl HktasClockBridge_GetContactImpulsePolicyCallbacks(void)
{
    return InterlockedCompareExchange(&g_impulse_policy_callbacks, 0, 0);
}
#endif
