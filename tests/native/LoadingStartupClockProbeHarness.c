#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef struct hktas_v2_state_stub
{
    volatile LONG runtime_input_ready;
} hktas_v2_state_stub;

static BOOL g_v2_gate_enabled = TRUE;
static hktas_v2_state_stub *g_v2_state;
static SRWLOCK g_clock_lock = SRWLOCK_INIT;
static LARGE_INTEGER g_deterministic_clock_anchor;
static uint64_t hktas_get_completed_player_loops(void) { return 0u; }

#include "../../native/HollowKnightTAS.ClockBridge/loading_startup_clock_probe.h"

static int failed;

static void check(BOOL ok, const char *name)
{
    printf("%s %s\n", ok ? "PASS" : "FAIL", name);
    if (!ok) failed++;
}

static void set_startup_preload_active_sim(BOOL active)
{
    if (active != FALSE)
    {
        InterlockedExchange(&g_loading_startup_preload_active, 1);
    }
    else
    {
        InterlockedExchange(&g_loading_startup_preload_active, 0);
        InterlockedExchange(&g_loading_startup_preload_reset_pending, 1);
    }
}

static void dirty_time_fields(BYTE *manager, double fixed_dt)
{
    memcpy(manager + 0x48, &fixed_dt, sizeof(fixed_dt));
    uint64_t dirty_u64 = 0x123456789abcdef0ULL;
    LONGLONG dirty_frame = 777;
    memcpy(manager + 0x30, &dirty_u64, sizeof(dirty_u64));
    memcpy(manager + 0x90, &dirty_u64, sizeof(dirty_u64));
    memcpy(manager + 0xc8, &dirty_frame, sizeof(dirty_frame));
    manager[0xc2] = 1;
}

static uint64_t read_u64(const BYTE *manager, size_t offset)
{
    uint64_t value = 0;
    memcpy(&value, manager + offset, sizeof(value));
    return value;
}

static double read_double(const BYTE *manager, size_t offset)
{
    double value = 0.0;
    memcpy(&value, manager + offset, sizeof(value));
    return value;
}

int main(void)
{
    (void)configure_loading_startup_clock_probe;

    BYTE fake_manager[0x430];
    memset(fake_manager, 0, sizeof(fake_manager));
    LONGLONG fake_counter_origin = 1000LL;
    double fake_scripting_origin = 0.0;

    hktas_v2_state_stub state = {0};
    g_v2_state = &state;

    g_deterministic_clock_anchor.QuadPart = 500000LL;
    g_loading_startup_clock_enabled = TRUE;
    g_loading_startup_clock_initialized = TRUE;
    g_loading_startup_clock_released = FALSE;
    g_loading_startup_time_manager = fake_manager;
    g_loading_startup_counter_origin = &fake_counter_origin;
    g_loading_startup_scripting_origin = &fake_scripting_origin;
    g_loading_startup_clock_wall_begin = GetTickCount();

    /* 1. Ordinary freeze (no preload active, runtime_input_ready == 0) */
    dirty_time_fields(fake_manager, 0.02);
    check(prepare_loading_startup_clock_probe(), "ordinary_freeze_prepare_succeeds");
    check(g_loading_startup_clock_frozen_frame == TRUE, "ordinary_freeze_marks_frozen_frame");
    check(fake_manager[0xc2] == 0, "ordinary_freeze_clears_first_fixed_flag");
    check(read_u64(fake_manager, 0x90) == 0 && read_u64(fake_manager, 0xc8) == 0,
        "ordinary_freeze_zeroes_time_and_frame_fields");
    check(read_double(fake_manager, 0x48) == 0.02, "ordinary_freeze_preserves_fixed_delta_time");
    check(g_loading_startup_clock_frozen_loops == 1u, "ordinary_freeze_increments_frozen_loops");
    dirty_time_fields(fake_manager, 0.02);
    finish_loading_startup_clock_probe();
    check(read_u64(fake_manager, 0x90) == 0 && read_u64(fake_manager, 0xc8) == 0,
        "ordinary_freeze_finish_zeroes_when_frozen");

    /* 2. SetStartupPreloadActive(TRUE): pause zeroing & pause 30s timeout */
    set_startup_preload_active_sim(TRUE);
    g_loading_startup_clock_wall_begin = GetTickCount() - 40000u; /* > 30s ago */
    dirty_time_fields(fake_manager, 0.02);
    check(prepare_loading_startup_clock_probe(), "preload_active_prepare_succeeds");
    check(g_loading_startup_clock_enabled == TRUE, "preload_active_pauses_30s_timeout");
    check(g_loading_startup_clock_frozen_frame == FALSE, "preload_active_unfreezes_frame");
    check(fake_manager[0xc2] == 1, "preload_active_allows_first_fixed_flag");
    check(read_u64(fake_manager, 0x90) != 0 && read_u64(fake_manager, 0xc8) == 777,
        "preload_active_does_not_zero_time_fields_on_prepare");
    check(GetTickCount() - g_loading_startup_clock_wall_begin < 5000u,
        "preload_active_refreshes_wall_begin");
    finish_loading_startup_clock_probe();
    check(read_u64(fake_manager, 0x90) != 0 && read_u64(fake_manager, 0xc8) == 777,
        "preload_active_finish_does_not_zero_when_unfrozen");

    /* 3. SetStartupPreloadActive(FALSE): trigger reset & rebind origins & resume freeze */
    set_startup_preload_active_sim(FALSE);
    g_deterministic_clock_anchor.QuadPart = 987654321LL;
    fake_counter_origin = 111LL;
    fake_scripting_origin = 42.5;
    dirty_time_fields(fake_manager, 0.02);
    g_loading_startup_clock_wall_begin = GetTickCount() - 40000u;
    check(prepare_loading_startup_clock_probe(), "preload_end_prepare_succeeds");
    check(InterlockedCompareExchange(&g_loading_startup_preload_reset_pending, 0, 0) == 0,
        "preload_end_consumes_reset_pending_flag");
    check(fake_counter_origin == 987654321LL, "preload_end_rebinds_counter_origin_to_anchor");
    check(fake_scripting_origin == 0.0, "preload_end_resets_scripting_origin_to_zero");
    check(read_u64(fake_manager, 0x90) == 0 && read_u64(fake_manager, 0xc8) == 0
        && fake_manager[0xc2] == 0, "preload_end_zeroes_time_fields");
    check(read_double(fake_manager, 0x48) == 0.02, "preload_end_preserves_fixed_delta_time");
    check(g_loading_startup_clock_enabled == TRUE, "preload_end_resets_timeout_window");
    check(g_loading_startup_clock_frozen_frame == TRUE, "preload_end_restores_frozen_frame");

    /* 4. runtime_input_ready == 1 releases startup probe */
    InterlockedExchange(&state.runtime_input_ready, 1);
    check(prepare_loading_startup_clock_probe(), "runtime_ready_prepare_succeeds");
    check(g_loading_startup_clock_released == TRUE, "runtime_ready_releases_probe");
    check(g_loading_startup_clock_frozen_frame == FALSE, "runtime_ready_clears_frozen_frame");
    check(fake_manager[0xc2] == 1, "runtime_ready_restores_first_fixed_flag");

    /* 5. Non-preload 30s timeout still triggers normally */
    g_loading_startup_clock_released = FALSE;
    InterlockedExchange(&state.runtime_input_ready, 0);
    g_loading_startup_clock_wall_begin = GetTickCount() - 35000u;
    check(prepare_loading_startup_clock_probe(), "non_preload_timeout_prepare_returns_true");
    check(g_loading_startup_clock_enabled == FALSE, "non_preload_timeout_disables_probe");
    check(g_loading_startup_clock_frozen_frame == FALSE, "non_preload_timeout_clears_frozen_frame");

    return failed == 0 ? 0 : 1;
}
