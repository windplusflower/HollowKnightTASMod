#ifndef HKTAS_FULL_RUN_CONTINUATION_H
#define HKTAS_FULL_RUN_CONTINUATION_H

/* PDB 0334be90-63dd-4644-bee1-fc8d56c840c2 age 1, pinned UnityPlayer.
 * The Object base is excluded. The three TimeHolders, counters, flags,
 * settings and double[100] presentation ring contain no process pointers.
 * Padding is canonical zero, never copied out of the engine. */
#define HKTAS_CONTINUATION_MAGIC 0x31435448u /* HTC1 */
typedef struct hktas_clock_continuation {
    uint32_t magic, version, size, reserved;
    int64_t frequency, anchor, counter_origin, scripting_origin_bits;
    int64_t step, whole;
    uint64_t fraction, phase, completed;
    int32_t advance_sequence, advance_count, numerator, denominator;
    BYTE engine[0x400];
} hktas_clock_continuation;
typedef char hktas_clock_continuation_size_check[(sizeof(hktas_clock_continuation) == 1128u) ? 1 : -1];
static LONGLONG *g_continuation_counter_origin;
static double *g_continuation_scripting_origin;
static BOOL g_continuation_imported;

static BOOL hktas_continuation_context(void)
{
    wchar_t enabled[4];
    if (GetEnvironmentVariableW(L"HKTAS_SCENE_CHECKPOINT_EXPERIMENT", enabled, 4) != 1
        || enabled[0] != L'1' || !g_v2_gate_enabled || !hktas_v2_bootstrap_valid()
        || !g_loading_startup_clock_initialized || !g_loading_startup_clock_released
        || g_loading_startup_clock_frozen_frame || g_boot_frame_prepared || g_boot_player_loop_active
        || GetCurrentThreadId() != g_virtual_clock_main_thread_id
        || !loading_startup_writable(g_loading_startup_time_manager, 0x430)) return FALSE;
    if (!g_continuation_counter_origin || !g_continuation_scripting_origin) {
        BYTE *engine = (BYTE *)GetModuleHandleW(L"UnityPlayer.dll");
        if (!engine) return FALSE;
        g_continuation_counter_origin = (LONGLONG *)(engine + 0x1a41260);
        g_continuation_scripting_origin = *(double **)(engine + 0x1967800);
    }
    return loading_startup_writable(g_continuation_counter_origin, 8)
        && loading_startup_writable(g_continuation_scripting_origin, 8);
}

static void hktas_continuation_engine_copy(BYTE *destination, const BYTE *source)
{
    const unsigned spans[][2] = {{0x30,44}, {0x60,44}, {0x90,44}, {0xc0,3},
        {0xc8,20}, {0xe0,24}, {0xf8,2}, {0xfc,12}, {0x108,808}};
    for (unsigned i = 0; i < sizeof(spans)/sizeof(spans[0]); ++i)
        memcpy(destination + spans[i][0] - 0x30, source + spans[i][0] - 0x30, spans[i][1]);
}

__declspec(dllexport) uint32_t __cdecl HktasClockBridge_GetContinuationBytes(void)
{ return sizeof(hktas_clock_continuation); }

__declspec(dllexport) LONG __cdecl HktasClockBridge_ExportContinuation(BYTE *output, uint32_t capacity)
{
    if (!output || capacity != sizeof(hktas_clock_continuation) || !hktas_continuation_context()) return 0;
    hktas_clock_continuation state = {0};
    state.magic = HKTAS_CONTINUATION_MAGIC; state.version = 1; state.size = sizeof(state);
    AcquireSRWLockShared(&g_clock_lock);
    if (!g_deterministic_clock_enabled || !g_fractional_clock_configured
        || g_virtual_clock_paused || g_virtual_clock_resume_pending) {
        ReleaseSRWLockShared(&g_clock_lock); return 0;
    }
    state.frequency = g_deterministic_clock_frequency;
    state.anchor = g_deterministic_clock_anchor.QuadPart;
    state.counter_origin = *g_continuation_counter_origin;
    memcpy(&state.scripting_origin_bits, g_continuation_scripting_origin, 8);
    state.step = g_deterministic_clock_step_ticks;
    state.whole = g_fractional_clock.whole; state.fraction = g_fractional_clock.fraction; state.phase = g_fractional_clock.phase;
    state.completed = hktas_get_completed_player_loops();
    state.advance_sequence = g_deterministic_clock_last_advance_sequence;
    state.advance_count = g_deterministic_clock_frame_advance_count;
    state.numerator = g_fractional_rate_numerator; state.denominator = g_fractional_rate_denominator;
    hktas_continuation_engine_copy(state.engine, g_loading_startup_time_manager + 0x30);
    ReleaseSRWLockShared(&g_clock_lock);
    memcpy(output, &state, sizeof(state)); return 1;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_ImportContinuation(
    const BYTE *input, uint32_t length, uint64_t expected_completed)
{
    if (!input || length != sizeof(hktas_clock_continuation) || !hktas_continuation_context()
        || g_continuation_imported || g_v2_state->mode != HKTAS_V2_MODE_PAUSED
        || hktas_get_completed_player_loops() != expected_completed
        || g_v2_state->command_sequence != g_v2_state->ack_sequence) return 0;
    hktas_clock_continuation state; memcpy(&state, input, sizeof(state));
    hktas_fractional_clock configured = {0};
    if (state.magic != HKTAS_CONTINUATION_MAGIC || state.version != 1 || state.size != sizeof(state) || state.reserved
        || state.frequency != g_deterministic_clock_frequency || state.anchor < state.counter_origin
        || state.counter_origin < 0 || state.completed > INT_MAX
        || state.advance_sequence < 0 || state.advance_sequence == INT_MAX || state.advance_count < 0
        || !hktas_fractional_configure(&configured, state.frequency, state.numerator, state.denominator)
        || configured.whole != state.whole || configured.fraction != state.fraction
        || (state.step != state.whole && state.step != state.whole + 1)
        || state.anchor > INT64_MAX - state.whole - 1) return 0;
    BYTE canonical[0x400] = {0}; hktas_continuation_engine_copy(canonical, state.engine);
    if (memcmp(canonical, state.engine, sizeof(canonical)) != 0) return 0;
    AcquireSRWLockExclusive(&g_clock_lock);
    if (!g_deterministic_clock_enabled || g_virtual_clock_paused || g_virtual_clock_resume_pending) {
        ReleaseSRWLockExclusive(&g_clock_lock); return 0;
    }
    /* All validation precedes writes. Never import pipe command sequences or
     * bootstrap ownership from the source process. Import remains paused. */
    g_deterministic_clock_anchor.QuadPart = state.anchor;
    g_deterministic_clock_step_ticks = state.step;
    g_fractional_clock.whole = state.whole; g_fractional_clock.fraction = state.fraction; g_fractional_clock.phase = state.phase;
    g_fractional_clock_configured = TRUE;
    g_fractional_rate_numerator = state.numerator; g_fractional_rate_denominator = state.denominator;
    g_deterministic_clock_last_advance_sequence = state.advance_sequence;
    g_deterministic_clock_frame_advance_count = state.advance_count;
    *g_continuation_counter_origin = state.counter_origin;
    memcpy(g_continuation_scripting_origin, &state.scripting_origin_bits, 8);
    hktas_continuation_engine_copy(g_loading_startup_time_manager + 0x30, state.engine);
    InterlockedExchange64(&g_v2_state->completed_frames, (LONGLONG)state.completed);
    InterlockedExchange64(&g_v2_state->expected_frame, (LONGLONG)state.completed);
    if (g_boot_frame_state) InterlockedExchange(&g_boot_frame_state[0], (LONG)state.completed);
    InterlockedExchange(&g_v2_step_movie_frame_complete, 0);
    g_continuation_imported = TRUE;
    ReleaseSRWLockExclusive(&g_clock_lock); return 1;
}
#endif
