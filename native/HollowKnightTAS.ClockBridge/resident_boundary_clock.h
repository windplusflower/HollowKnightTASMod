#ifndef HKTAS_RESIDENT_BOUNDARY_CLOCK_H
#define HKTAS_RESIDENT_BOUNDARY_CLOCK_H

/* Every ordinary, recording, replay and restore boundary uses one epoch.
 * A failed or leaked epoch permanently faults the process. The old experiment
 * entry points retain their original once-per-process admission policy. */
static uint64_t g_resident_boundary_serial, g_resident_boundary_token;
static uint64_t g_resident_boundary_frame, g_resident_boundary_started;
static LONGLONG g_resident_boundary_command;
static LONG g_resident_boundary_mode, g_resident_boundary_phase;
static BOOL g_resident_boundary_failed, g_resident_boundary_imported;
static BOOL g_resident_boundary_read_only;
/* >0 counts external clock writers, -1 is a read-only frame lease. CAS
 * prevents a writer passing a flag check just before Begin closes the frame.
 * Holding across the whole public operation covers the QPC read as well. */
static volatile LONG g_resident_frame_writers;
static BOOL hktas_enter_clock_writer(void)
{
    for (;;) {
        LONG writers = InterlockedCompareExchange(&g_resident_frame_writers, 0, 0);
        if (writers < 0 || writers == LONG_MAX) return FALSE;
        if (InterlockedCompareExchange(&g_resident_frame_writers, writers + 1, writers) == writers) return TRUE;
    }
}
static void hktas_leave_clock_writer(void) { InterlockedDecrement(&g_resident_frame_writers); }

static BOOL hktas_resident_boundary_released(void)
{ return !g_resident_boundary_failed && g_resident_boundary_token == 0; }

static BOOL hktas_resident_boundary_valid(uint64_t token, uint64_t frame)
{
    return token != 0 && token == g_resident_boundary_token && !g_resident_boundary_failed
        && (!g_resident_boundary_read_only || InterlockedCompareExchange(&g_resident_frame_writers, 0, 0) == -1)
        && hktas_clock_engine_context(g_resident_boundary_read_only ? g_resident_boundary_phase : 0) && save_write_guard_is_armed() && g_guard_install_status == 1
        && g_v2_boundary_callback_phase == g_resident_boundary_phase
        && g_v2_state->mode == g_resident_boundary_mode
        && frame == g_resident_boundary_frame && hktas_get_completed_player_loops() == frame
        && g_v2_state->command_sequence == g_resident_boundary_command
        && g_v2_state->ack_sequence == g_resident_boundary_command
        && HktasClockBridge_RequireResidentAudioFence(token) == 1
        && HktasClockBridge_RequireResidentParticleFence(token) == 1
        /* QPC is virtualized for gameplay; use the unvirtualized wall clock. */
        && GetTickCount64() - g_resident_boundary_started < 9000u;
}

__declspec(dllexport) uint32_t __cdecl HktasClockBridge_GetResidentBoundaryCapability(void)
{ return 1u; }

static uint64_t hktas_begin_resident_epoch(uint64_t expected_completed, LONG observation_phase)
{
    if (observation_phase && InterlockedCompareExchange(&g_resident_frame_writers, -1, 0) != 0) return 0;
    if (!hktas_resident_boundary_released() || g_resident_boundary_serial == UINT64_MAX
        || !hktas_clock_engine_context(observation_phase) || !save_write_guard_is_armed() || g_guard_install_status != 1
        || (observation_phase ? (observation_phase != 1 && observation_phase != 3) || observation_phase != g_v2_boundary_callback_phase
                             : (g_v2_boundary_callback_phase != 1 && g_v2_boundary_callback_phase != 2))
        || g_v2_state->mode < HKTAS_V2_MODE_PAUSED || g_v2_state->mode > HKTAS_V2_MODE_RUN
        || hktas_get_completed_player_loops() != expected_completed
        || g_v2_state->command_sequence != g_v2_state->ack_sequence) {
        if (observation_phase) InterlockedExchange(&g_resident_frame_writers, 0);
        return 0;
    }
    g_resident_boundary_frame = expected_completed;
    g_resident_boundary_started = GetTickCount64();
    g_resident_boundary_command = g_v2_state->command_sequence;
    g_resident_boundary_mode = g_v2_state->mode;
    g_resident_boundary_phase = g_v2_boundary_callback_phase;
    g_resident_boundary_imported = FALSE;
    g_resident_boundary_read_only = observation_phase != 0;
    g_resident_boundary_token = ++g_resident_boundary_serial;
    if (HktasClockBridge_BeginResidentAudioFence(g_resident_boundary_token, g_resident_boundary_started) != 1) {
        g_resident_boundary_failed = TRUE; hktas_v2_fault(66); return 0;
    }
    if (HktasClockBridge_BeginResidentParticleFence(g_resident_boundary_token, g_resident_boundary_started) != 1) {
        g_resident_boundary_failed = TRUE; hktas_v2_fault(67);
        HktasClockBridge_EndResidentAudioFence(g_resident_boundary_token, 0); return 0;
    }
    return g_resident_boundary_token;
}

__declspec(dllexport) uint64_t __cdecl HktasClockBridge_BeginResidentBoundary(uint64_t expected_completed)
{ return hktas_begin_resident_epoch(expected_completed, 0); }

__declspec(dllexport) uint64_t __cdecl HktasClockBridge_BeginResidentFrameObservation(uint64_t expected_completed, LONG phase)
{ return phase == 1 || phase == 3 ? hktas_begin_resident_epoch(expected_completed, phase) : 0; }

__declspec(dllexport) LONG __cdecl HktasClockBridge_RequireResidentBoundary(uint64_t token, uint64_t expected_completed)
{ return hktas_resident_boundary_valid(token, expected_completed) ? 1 : 0; }

__declspec(dllexport) LONG __cdecl HktasClockBridge_RequireResidentWritableBoundary(uint64_t token, uint64_t expected_completed)
{
    return !g_resident_boundary_read_only && g_resident_boundary_mode == HKTAS_V2_MODE_PAUSED && g_resident_boundary_phase == 2
        && hktas_resident_boundary_valid(token, expected_completed) ? 1 : 0;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_RequireResidentFrameObservation(uint64_t token, uint64_t expected_completed)
{
    return g_resident_boundary_read_only && (g_resident_boundary_phase == 1 || g_resident_boundary_phase == 3)
        && hktas_resident_boundary_valid(token, expected_completed) ? 1 : 0;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_ExportResidentContinuation(uint64_t token, BYTE *output, uint32_t capacity)
{
    if (g_resident_boundary_read_only || !hktas_resident_boundary_valid(token, g_resident_boundary_frame)) return 0;
    LONG result = hktas_export_continuation(output, capacity);
    return result == 1 && hktas_resident_boundary_valid(token, g_resident_boundary_frame) ? 1 : 0;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_ImportResidentContinuation(
    uint64_t token, const BYTE *input, uint32_t length, uint64_t expected_completed)
{
    if (!HktasClockBridge_RequireResidentWritableBoundary(token, expected_completed) || g_resident_boundary_imported) return 0;
    /* Consume before a write attempt; malformed input and readback failures
     * cannot turn into another attempt in the same partially restored world. */
    g_resident_boundary_imported = TRUE;
    LONG result = hktas_import_continuation(input, length, expected_completed);
    if (result != 1) { g_resident_boundary_failed = TRUE; hktas_v2_fault(65); return 0; }
    g_resident_boundary_frame = hktas_get_completed_player_loops();
    if (!hktas_resident_boundary_valid(token, g_resident_boundary_frame)) {
        g_resident_boundary_failed = TRUE; hktas_v2_fault(65); return 0;
    }
    return 1;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_EndResidentBoundary(uint64_t token, LONG complete)
{
    if (complete != 1 || !hktas_resident_boundary_valid(token, g_resident_boundary_frame)) {
        HktasClockBridge_EndResidentAudioFence(token, 0);
        HktasClockBridge_EndResidentParticleFence(token, 0);
        g_resident_boundary_failed = TRUE; hktas_v2_fault(65); return 0;
    }
    if (HktasClockBridge_EndResidentParticleFence(token, 1) != 1) {
        g_resident_boundary_failed = TRUE; hktas_v2_fault(67);
        HktasClockBridge_EndResidentAudioFence(token, 0); return 0;
    }
    if (HktasClockBridge_EndResidentAudioFence(token, 1) != 1) {
        g_resident_boundary_failed = TRUE; hktas_v2_fault(66); return 0;
    }
    if (g_resident_boundary_read_only && InterlockedCompareExchange(&g_resident_frame_writers, 0, -1) != -1) {
        g_resident_boundary_failed = TRUE; hktas_v2_fault(65); return 0;
    }
    g_resident_boundary_token = 0; return 1;
}
#endif
