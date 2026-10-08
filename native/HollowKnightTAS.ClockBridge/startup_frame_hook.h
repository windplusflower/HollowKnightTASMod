#ifndef HKTAS_STARTUP_FRAME_HOOK_H
#define HKTAS_STARTUP_FRAME_HOOK_H
#include <string.h>

/* This prototype is explicitly opt-in. Addresses were resolved from the
 * matching Unity PDB, not inferred from repeated clock calls. The injector
 * also verifies the full UnityPlayer SHA-256 against its build whitelist. */
static BOOL g_boot_frame_hook_enabled;
static void (__cdecl *g_boot_original_player_loop)(void);
static void (__cdecl *g_boot_original_time_update)(void);
static BOOL g_boot_frame_prepared;
static BOOL g_boot_player_loop_active;
static HANDLE g_boot_step;
static HANDLE g_boot_state_mapping;
static BOOL g_boot_loop_active;
static void (__cdecl *g_boot_original_main_loop)(void);

/* Unity's title-bar timer can reenter PerformMainLoop from the nested window
 * message pump while PlayerLoop is parked. Skipping just PlayerLoop is too
 * late: the outer loop still waits for a presentation that never happened.
 * Preserve normal timer updates whenever no controlled PlayerLoop is active. */
static void __cdecl boot_title_bar_main_loop(void)
{
    if (!g_boot_loop_active) g_boot_original_main_loop();
}

static void wait_boot_frame_command(void)
{
    HANDLE handles[3] = {g_boot_continue, g_boot_owner, g_boot_step};
    for (;;) {
        DWORD result = MsgWaitForMultipleObjectsEx(3, handles, INFINITE,
            QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (result != WAIT_OBJECT_0 + 3) return;
        MSG message;
        /* Keep the native window responsive without executing PlayerLoop.
         * Reentrant loop requests during DispatchMessage are suppressed by
         * g_boot_loop_active below. Bound the batch to avoid input starvation. */
        for (int i = 0; i < 128 && PeekMessageW(&message, NULL, 0, 0, PM_REMOVE); ++i) {
            if (message.message == WM_QUIT) {
                PostQuitMessage((int)message.wParam);
                return;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
}

static BOOL prepare_boot_frame(void)
{
    if (g_guard_required)
    {
        for (int attempt = 0; attempt < 10000 && g_guard_install_status == 0; ++attempt)
            Sleep(1);
        if (g_guard_install_status != 1)
        {
            return FALSE; /* Never advertise frame 0 as ready without the save guard. */
        }
    }
    if (g_v2_gate_enabled)
    {
        if (!hktas_v2_before_frame())
        {
            hktas_v2_wait_fault();
        }
    }
    else if (g_boot_frame_state && WaitForSingleObject(g_boot_continue, 0) != WAIT_OBJECT_0)
    {
        InterlockedExchange(&g_boot_frame_state[2], (LONG)GetCurrentThreadId());
        InterlockedExchange(&g_boot_frame_state[1], 1);
        SetEvent(g_boot_ready);
        wait_boot_frame_command();
        ResetEvent(g_boot_ready);
        InterlockedExchange(&g_boot_frame_state[1], 0);
    }
    /* Start the bootstrap deadline only after the user releases frame zero. */
    if (!prepare_loading_startup_clock_probe()) {
        hktas_v2_fault(61);
        hktas_v2_wait_fault();
    }
    advance_boot_frame_clock();
    return TRUE;
}

static void __cdecl boot_time_update(void)
{
    if (!g_v2_gate_enabled) {
        g_boot_original_time_update();
        return;
    }
    if (g_boot_loop_active) {
        /* Keep legitimate nested loading updates inside gameplay. A window
         * loop while the gate is waiting must not latch another time/frame. */
        if (g_boot_player_loop_active) g_boot_original_time_update();
        return;
    }
    /* ExecuteTimeUpdate is separate from PlayerLoop on this pinned build.
     * Select the Movie frame and advance QPC before Unity latches its delta;
     * waiting at the later PlayerLoop call applies a rate one frame too late. */
    g_boot_loop_active = TRUE;
    if (!prepare_boot_frame()) {
        g_boot_loop_active = FALSE;
        return;
    }
    g_boot_original_time_update();
    g_boot_frame_prepared = TRUE;
}

static void __cdecl boot_player_loop(void)
{
    if (g_boot_player_loop_active) return;
    if (g_v2_gate_enabled) {
        if (!g_boot_frame_prepared) {
            if (g_boot_loop_active) return; /* Window reentry while preparing/waiting. */
            hktas_v2_fault(62);
            hktas_v2_wait_fault();
        }
    } else {
        if (g_boot_loop_active) return;
        g_boot_loop_active = TRUE;
        if (!prepare_boot_frame()) {
            g_boot_loop_active = FALSE;
            return;
        }
    }
    g_boot_player_loop_active = TRUE;
    g_boot_original_player_loop();
    finish_loading_startup_clock_probe();
    if (g_v2_gate_enabled) hktas_v2_after_frame();
    if (g_boot_frame_state) InterlockedIncrement(&g_boot_frame_state[0]);
    g_boot_frame_prepared = FALSE;
    g_boot_player_loop_active = FALSE;
    g_boot_loop_active = FALSE;
}

static BOOL install_boot_frame_hook(void)
{
    wchar_t enabled[4], token[40], name[100];
    if (GetEnvironmentVariableW(L"HKTAS_BOOT_FRAME_GATE", enabled, 4) == 0) return TRUE;
    if (wcscmp(enabled, L"1") != 0 || !g_boot_ready) return FALSE;
    BYTE *base = (BYTE *)GetModuleHandleW(L"UnityPlayer.dll");
    if (!base) return FALSE;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + dos->e_lfanew);
    IMAGE_DATA_DIRECTORY debug = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG];
    const BYTE expected_guid[16] = {0x90,0xbe,0x34,0x03,0xdd,0x63,0x44,0x46,0xbe,0xe1,0xfc,0x8d,0x56,0xc8,0x40,0xc2};
    BOOL matched = FALSE;
    for (DWORD i = 0; i < debug.Size / sizeof(IMAGE_DEBUG_DIRECTORY); ++i) {
        IMAGE_DEBUG_DIRECTORY *entry = (IMAGE_DEBUG_DIRECTORY *)(base + debug.VirtualAddress) + i;
        if (entry->Type != IMAGE_DEBUG_TYPE_CODEVIEW || entry->SizeOfData < 24) continue;
        BYTE *cv = base + entry->AddressOfRawData;
        if (memcmp(cv, "RSDS", 4) == 0 && memcmp(cv + 4, expected_guid, 16) == 0
            && *(DWORD *)(cv + 20) == 1) matched = TRUE;
    }
    if (!matched) return FALSE;
    BYTE *site = base + 0x5231e5;
    const BYTE expected_call[5] = {0xe8,0xd6,0x92,0x23,0x00};
    if (memcmp(site, expected_call, sizeof(expected_call)) != 0) return FALSE;
    /* Matching PDB: the initial MainMessageLoop time update and the trailing
     * PerformMainLoop time update both prepare the following PlayerLoop. */
    BYTE *time_sites[2] = {base + 0x5217c3, base + 0x5232f9};
    const BYTE expected_time_calls[2][5] = {
        {0xe8,0xa8,0x70,0x23,0x00}, {0xe8,0x72,0x55,0x23,0x00}
    };
    for (int i = 0; i < 2; ++i)
        if (memcmp(time_sites[i], expected_time_calls[i], 5) != 0) return FALSE;
    /* Matching PDB: TitleBarTimerUpdateCallback tail-jumps to PerformMainLoop.
     * Validate both patch sites before modifying either one. */
    BYTE *timer_site = base + 0x5253d1;
    const BYTE expected_timer_jump[5] = {0xe9,0x5a,0xdc,0xff,0xff};
    if (memcmp(timer_site, expected_timer_jump, sizeof(expected_timer_jump)) != 0) return FALSE;
    GetEnvironmentVariableW(L"HKTAS_BOOT_GATE_TOKEN", token, 40);
    wsprintfW(name, L"Local\\HKTAS.Boot.%s.Step", token);
    g_boot_step = OpenEventW(SYNCHRONIZE, FALSE, name);
    wsprintfW(name, L"Local\\HKTAS.Boot.%s.State", token);
    g_boot_state_mapping = OpenFileMappingW(FILE_MAP_WRITE, FALSE, name);
    if (!g_boot_step || !g_boot_state_mapping) return FALSE;
    g_boot_frame_state = (volatile LONG *)MapViewOfFile(g_boot_state_mapping, FILE_MAP_WRITE, 0, 0, 16);
    if (!g_boot_frame_state) return FALSE;
    if (!install_full_run_frame_gate()) return FALSE;

    /* Replace one five-byte CALL with a nearby absolute-jump relay. No
     * function prologue relocation, no guessed instruction lengths. This
     * executes before the primary thread enters Unity's main loop. */
    SYSTEM_INFO info;
    GetSystemInfo(&info);
    BYTE *relay = NULL;
    uintptr_t aligned = (uintptr_t)base & ~((uintptr_t)info.dwAllocationGranularity - 1);
    for (uintptr_t delta = info.dwAllocationGranularity; delta < 0x70000000; delta += info.dwAllocationGranularity) {
        relay = VirtualAlloc((void *)(aligned + delta), 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (relay) break;
    }
    if (!relay) return FALSE;
    relay[0] = 0xff; relay[1] = 0x25;
    memset(relay + 2, 0, 4);
    void (*target)(void) = boot_player_loop;
    memcpy(relay + 6, &target, sizeof(target));
    relay[32] = 0xff; relay[33] = 0x25;
    memset(relay + 34, 0, 4);
    void (*timer_target)(void) = boot_title_bar_main_loop;
    memcpy(relay + 38, &timer_target, sizeof(timer_target));
    relay[64] = 0xff; relay[65] = 0x25;
    memset(relay + 66, 0, 4);
    void (*time_target)(void) = boot_time_update;
    memcpy(relay + 70, &time_target, sizeof(time_target));
    DWORD old;
    if (!VirtualProtect(relay, 4096, PAGE_EXECUTE_READ, &old)) return FALSE;
    FlushInstructionCache(GetCurrentProcess(), relay, 78);
    g_boot_original_player_loop = (void (__cdecl *)(void))(base + 0x75c4c0);
    g_boot_original_time_update = (void (__cdecl *)(void))(base + 0x758870);
    g_boot_original_main_loop = (void (__cdecl *)(void))(base + 0x523030);
    if (!VirtualProtect(timer_site, 5, PAGE_EXECUTE_READWRITE, &old)) return FALSE;
    int32_t timer_relative = (int32_t)((intptr_t)(relay + 32) - (intptr_t)(timer_site + 5));
    memcpy(timer_site + 1, &timer_relative, sizeof(timer_relative));
    DWORD timer_ignored;
    if (!VirtualProtect(timer_site, 5, old, &timer_ignored)) return FALSE;
    FlushInstructionCache(GetCurrentProcess(), timer_site, 5);
    if (!VirtualProtect(site, 5, PAGE_EXECUTE_READWRITE, &old)) return FALSE;
    int32_t relative = (int32_t)((intptr_t)relay - (intptr_t)(site + 5));
    memcpy(site + 1, &relative, sizeof(relative));
    DWORD ignored;
    if (!VirtualProtect(site, 5, old, &ignored)) return FALSE;
    FlushInstructionCache(GetCurrentProcess(), site, 5);
    for (int i = 0; i < 2; ++i) {
        if (!VirtualProtect(time_sites[i], 5, PAGE_EXECUTE_READWRITE, &old)) return FALSE;
        int32_t time_relative = (int32_t)((intptr_t)(relay + 64) - (intptr_t)(time_sites[i] + 5));
        memcpy(time_sites[i] + 1, &time_relative, sizeof(time_relative));
        if (!VirtualProtect(time_sites[i], 5, old, &ignored)) return FALSE;
        FlushInstructionCache(GetCurrentProcess(), time_sites[i], 5);
    }
    g_boot_frame_hook_enabled = TRUE;
    InterlockedExchange(&g_boot_frame_state[3], 1);
    return TRUE;
}
#endif
