#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

static HANDLE g_boot_ready;
static HANDLE g_boot_owner;
static volatile LONG *g_boot_frame_state;
static volatile LONG g_guard_install_status = 1;
static BOOL g_guard_required = TRUE;
static BOOL g_v2_gate_enabled = TRUE;
static BOOL g_fake_guard_armed = TRUE;
static BOOL save_write_guard_is_armed(void) { return g_fake_guard_armed; }
static volatile LONG finished_policy_count;
static DWORD finished_policy_thread;
static BOOL finish_contact_impulse_policy(void) {
    finished_policy_thread = GetCurrentThreadId();
    InterlockedIncrement(&finished_policy_count); return TRUE;
}
static BOOL install_contact_impulse_probe(BYTE *base) { (void)base; return TRUE; }

#include "../../native/HollowKnightTAS.ClockBridge/full_run_frame_gate.h"
static HANDLE g_boot_continue;
static void advance_boot_frame_clock(void) { }
static BOOL prepare_loading_startup_clock_probe(void) { return TRUE; }
static BOOL prepare_early_mod_wall_clock(void) { return TRUE; }
static void finish_loading_startup_clock_probe(void) { }
#include "../../native/HollowKnightTAS.ClockBridge/startup_frame_hook.h"

static int failed;
static volatile LONG before_count;
static volatile LONG completed_count;
static volatile LONG observed_count;
static volatile LONG observed_wrong_boundary;
static DWORD game_thread_id;
static void __cdecl observe_frame(uint64_t completed)
{
    if (GetCurrentThreadId() != game_thread_id || completed != (uint64_t)g_v2_state->completed_frames
        || before_count != completed_count) InterlockedIncrement(&observed_wrong_boundary);
    InterlockedIncrement(&observed_count);
}
static BOOL observe_and_wait(void)
{
    LONG expected = observed_count + 1;
    if (!hktas_v2_request_observation()) return FALSE;
    for (int i = 0; i < 3000 && observed_count < expected; i++) Sleep(1);
    return observed_count == expected;
}
static volatile LONG loading_frames_to_skip;
static volatile LONG timer_callbacks;
static volatile LONG nested_main_loops;
static void __cdecl fake_main_loop(void) { InterlockedIncrement(&nested_main_loops); }
static void __cdecl fake_player_loop(void) { Sleep(2); }
static void CALLBACK title_bar_timer(HWND window, UINT message, UINT_PTR timer, DWORD time)
{
    (void)window; (void)message; (void)timer; (void)time;
    InterlockedIncrement(&timer_callbacks);
    boot_title_bar_main_loop();
}
static void __cdecl before_frame(uint64_t completed)
{
    (void)completed;
    InterlockedIncrement(&before_count);
}
static void __cdecl completed_frame(uint64_t completed)
{
    (void)completed;
    InterlockedIncrement(&completed_count);
    if (g_v2_state->mode == HKTAS_V2_MODE_STEP)
    {
        if (InterlockedCompareExchange(&loading_frames_to_skip, 0, 0) > 0)
            InterlockedDecrement(&loading_frames_to_skip);
        else InterlockedExchange(&g_v2_step_movie_frame_complete, 1);
    }
}
static void check(BOOL okay, const char *name)
{
    printf("%s %s\n", okay ? "PASS" : "FAIL", name);
    if (!okay) failed++;
}

static BOOL wait_for(LONGLONG minimum_frame, LONG mode, LONGLONG ack)
{
    for (int attempt = 0; attempt < 3000; attempt++)
    {
        if (g_v2_state->completed_frames >= minimum_frame
            && g_v2_state->mode == mode && g_v2_state->ack_sequence == ack)
            return TRUE;
        Sleep(1);
    }
    return FALSE;
}

static void send_command(LONGLONG sequence, LONG mode, LONGLONG expected)
{
    g_v2_state->expected_frame = expected;
    g_v2_state->requested_mode = mode;
    MemoryBarrier();
    g_v2_state->command_sequence = sequence;
    SetEvent(g_v2_command_event);
}

static DWORD WINAPI game_loop(LPVOID ignored)
{
    (void)ignored;
    game_thread_id = GetCurrentThreadId();
    UINT_PTR timer = SetTimer(NULL, 0, 10, title_bar_timer);
    for (int i = 0; i < 1000; i++)
    {
        /* Current Unity enters the gate in TimeUpdate before PlayerLoop.
         * Exercise the production gate directly so expected terminal faults
         * return to this harness instead of entering Unity's fault park. */
        g_boot_loop_active = TRUE;
        if (!hktas_v2_before_frame()) {
            g_boot_loop_active = FALSE; KillTimer(NULL, timer); return 0;
        }
        g_boot_frame_prepared = TRUE;
        boot_player_loop();
        if (g_v2_state->fault_code) { KillTimer(NULL, timer); return 0; }
    }
    KillTimer(NULL, timer);
    return 1;
}

int main(void)
{
    (void)install_boot_frame_hook; /* Hook-site identity is checked against the installed Unity binary in smoke. */
    const wchar_t *token = L"0123456789abcdef0123456789abcdef";
    SetEnvironmentVariableW(L"HKTAS_BOOT_GATE_TOKEN", token);
    g_boot_ready = CreateEventW(NULL, TRUE, FALSE, NULL);
    g_boot_owner = OpenProcess(SYNCHRONIZE, FALSE, GetCurrentProcessId());
    g_v2_command_event = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_v2_observation_event = CreateEventW(NULL, FALSE, FALSE, NULL);
    hktas_v2_state state = {0};
    state.magic = HKTAS_V2_MAGIC;
    state.version = HKTAS_V2_VERSION;
    memcpy(state.token, "0123456789abcdef0123456789abcdef", 32);
    state.mode = HKTAS_V2_MODE_PAUSED;
    g_v2_state = &state;
    check(!hktas_v2_request_observation(), "observation_requires_registered_callback");
    g_v2_observation_callback = observe_frame;
    g_v2_before_callback = before_frame;
    g_v2_completed_callback = completed_frame;
    g_boot_original_player_loop = fake_player_loop;
    g_boot_original_main_loop = fake_main_loop;
    boot_title_bar_main_loop();
    check(nested_main_loops == 1, "title_bar_timer_preserves_normal_main_loop");
    nested_main_loops = 0;
    HANDLE game = CreateThread(NULL, 0, game_loop, NULL, 0, NULL);
    check(WaitForSingleObject(g_boot_ready, 3000) == WAIT_OBJECT_0,
        "frame_zero_ready_with_guard");
    Sleep(100);
    check(state.completed_frames == 0, "unarmed_does_not_advance");
    check(timer_callbacks > 0, "paused_window_pump_dispatches_title_bar_timers");
    check(nested_main_loops == 0, "paused_timer_never_enters_presentation_wait");
    check(observe_and_wait() && observe_and_wait(), "paused_frame_zero_observations_complete");
    check(state.completed_frames == 0 && before_count == 0 && completed_count == 0
        && state.callback_sequence == 0 && state.command_sequence == 0,
        "observation_does_not_advance_input_frame_or_command");
    state.descriptor_sha256[0] = 0x5a;
    state.bootstrap_armed = 1;
    send_command(1, HKTAS_V2_MODE_STEP, 0);
    check(wait_for(1, HKTAS_V2_MODE_PAUSED, 1), "step_zero_to_one");
    check(WaitForSingleObject(g_boot_ready, 3000) == WAIT_OBJECT_0,
        "ready_after_one_frame");
    send_command(2, HKTAS_V2_MODE_STEP, 1);
    check(wait_for(2, HKTAS_V2_MODE_PAUSED, 2), "step_one_to_two");
    check(before_count == 2 && completed_count == 2,
        "before_and_completed_callbacks_match_steps");
    loading_frames_to_skip = 3;
    send_command(3, HKTAS_V2_MODE_STEP, 2);
    check(wait_for(6, HKTAS_V2_MODE_PAUSED, 3),
        "step_skips_three_loading_loops_then_consumes_one_movie_frame");
    check(before_count == 6 && completed_count == 6,
        "callbacks_include_skipped_loading_loops");
    send_command(4, HKTAS_V2_MODE_RUN, 6);
    check(wait_for(8, HKTAS_V2_MODE_RUN, 4), "run_advances_frames");
    check(observe_and_wait(), "running_observation_completes");
    LONGLONG pause_request_frame = state.completed_frames;
    send_command(5, HKTAS_V2_MODE_PAUSED, pause_request_frame);
    check(wait_for(pause_request_frame, HKTAS_V2_MODE_PAUSED, 5),
        "run_pause_boundary_ack");
    LONGLONG paused_frame = state.completed_frames;
    Sleep(30);
    check(state.completed_frames == paused_frame, "paused_frame_does_not_advance");
    check(before_count == completed_count, "callbacks_match_at_paused_boundary");
    LONGLONG callback_sequence = state.callback_sequence;
    check(observe_and_wait() && state.completed_frames == paused_frame
        && state.callback_sequence == callback_sequence && state.ack_sequence == 5,
        "paused_observation_preserves_all_counters");
    InterlockedExchange(&state.mode, HKTAS_V2_MODE_FINISHED);
    check(observe_and_wait() && state.completed_frames == paused_frame
        && state.mode == HKTAS_V2_MODE_FINISHED, "finished_observation_remains_finished");
    check(finished_policy_count > 0 && finished_policy_thread == game_thread_id
        && before_count == completed_count, "finished_policy_runs_on_game_thread_after_last_completion");
    check(observed_wrong_boundary == 0, "observations_run_on_game_thread_between_loops");
    InterlockedExchange(&state.mode, HKTAS_V2_MODE_PAUSED);
    state.descriptor_sha256[0] ^= 1;
    send_command(6, HKTAS_V2_MODE_RUN, paused_frame);
    for (int i = 0; i < 3000 && state.fault_code == 0; i++) Sleep(1);
    check(state.fault_code == 2 && state.mode == HKTAS_V2_MODE_FAULT,
        "descriptor_mutation_faults");
    check(WaitForSingleObject(game, 3000) == WAIT_OBJECT_0, "fault_stops_loop");
    check(!hktas_v2_request_observation(), "faulted_observation_is_rejected");
    CloseHandle(game);

    hktas_v2_state missing_guard = {0};
    missing_guard.magic = HKTAS_V2_MAGIC;
    missing_guard.version = HKTAS_V2_VERSION;
    memcpy(missing_guard.token, state.token, 32);
    missing_guard.mode = HKTAS_V2_MODE_PAUSED;
    g_v2_state = &missing_guard;
    g_guard_install_status = -1;
    check(!hktas_v2_before_frame() && missing_guard.fault_code == 1,
        "guard_install_failure_stops_frame_zero");
    check(missing_guard.completed_frames == 0, "guard_failure_has_no_completed_frame");
    CloseHandle(g_v2_command_event);
    CloseHandle(g_v2_observation_event);
    CloseHandle(g_boot_ready);
    CloseHandle(g_boot_owner);
    return failed == 0 ? 0 : 5;
}
