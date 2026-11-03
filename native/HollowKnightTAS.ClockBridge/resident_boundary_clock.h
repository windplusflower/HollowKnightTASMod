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

static BOOL hktas_resident_clock_valid(uint64_t token, uint64_t frame)
{
    return token != 0 && token == g_resident_boundary_token && !g_resident_boundary_failed
        && (!g_resident_boundary_read_only || InterlockedCompareExchange(&g_resident_frame_writers, 0, 0) == -1)
        && hktas_clock_engine_context(g_resident_boundary_read_only ? g_resident_boundary_phase : 0) && save_write_guard_is_armed() && g_guard_install_status == 1
        && g_v2_boundary_callback_phase == g_resident_boundary_phase
        && g_v2_state->mode == g_resident_boundary_mode
        && frame == g_resident_boundary_frame && hktas_get_completed_player_loops() == frame
        && g_v2_state->command_sequence == g_resident_boundary_command
        && g_v2_state->ack_sequence == g_resident_boundary_command
        /* QPC is virtualized for gameplay; use the unvirtualized wall clock. */
        && GetTickCount64() - g_resident_boundary_started < 9000u;
}

static BOOL hktas_resident_boundary_valid(uint64_t token, uint64_t frame)
{
    return hktas_resident_clock_valid(token,frame)
        && HktasClockBridge_RequireResidentAudioFence(token)
        && HktasClockBridge_RequireResidentParticleFence(token)
        && HktasClockBridge_RequireResidentDirectorFence(token);
}
/* All paths needing both locks use particle -> Director. The per-domain
 * failure sinks only publish a fault and never recursively take these locks. */
static LONG hktas_joint_poison_locked(LONG reason)
{
    g_resident_boundary_failed = TRUE;
    hktas_v2_fault(reason);
    hktas_audio_fail(37);
    g_ps_closed = TRUE; hktas_ps_fault_locked(22);
    hktas_dn_fault_locked(14);
    return 0;
}
static LONG hktas_joint_poison(LONG reason)
{
    AcquireSRWLockExclusive(&g_ps_lock); AcquireSRWLockExclusive(&g_dn_lock);
    hktas_joint_poison_locked(reason);
    ReleaseSRWLockExclusive(&g_dn_lock); ReleaseSRWLockExclusive(&g_ps_lock);
    return 0;
}
static LONG hktas_reject_individual_end(void) { return hktas_joint_poison(65); }

__declspec(dllexport) uint32_t __cdecl HktasClockBridge_GetResidentBoundaryCapability(void)
{ return 2u; }

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
        return hktas_joint_poison(66);
    }
    if (HktasClockBridge_BeginResidentParticleFence(g_resident_boundary_token, g_resident_boundary_started) != 1) {
        return hktas_joint_poison(67);
    }
    if (!HktasClockBridge_BeginResidentDirectorFence(g_resident_boundary_token, g_resident_boundary_started))
        return hktas_joint_poison(68);
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
    if (result != 1) return hktas_joint_poison(65);
    g_resident_boundary_frame = hktas_get_completed_player_loops();
    if (!hktas_resident_boundary_valid(token, g_resident_boundary_frame)) {
        return hktas_joint_poison(65);
    }
    return 1;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_EndResidentBoundary(uint64_t token, LONG complete)
{
    if (complete != 1 || !hktas_resident_clock_valid(token,g_resident_boundary_frame)) return hktas_joint_poison(65);
    AcquireSRWLockExclusive(&g_ps_lock); AcquireSRWLockExclusive(&g_dn_lock);
    LONG failed = 0;
    if (!hktas_ps_held_locked(token)) failed=67;
    else if (!hktas_dn_held_locked(token) || !hktas_dn_unchanged_locked() || !hktas_dn_verify(TRUE)) failed=68;
    else if (!HktasClockBridge_RequireResidentAudioFence(token)) failed=66;
    /* The event is only a wake hint. Prepare it while audio admission is
     * still CLOSED. After the admission CAS succeeds nothing may fail. */
    else if (!SetEvent(g_audio_open_event)) { hktas_audio_fail(39); failed=66; }
    else if (!hktas_resident_clock_valid(token,g_resident_boundary_frame)) failed=65;
    else if ((uint64_t)InterlockedCompareExchange64(&g_audio_state,0,(LONG64)HKTAS_AUDIO_CLOSED)!=HKTAS_AUDIO_CLOSED) failed=66;
    if (failed) hktas_joint_poison_locked(failed);
    else {
        g_ps_closed=FALSE; g_dn_closed=FALSE; g_dn_held=FALSE;
        g_resident_boundary_token=0;
        if (g_resident_boundary_read_only) InterlockedExchange(&g_resident_frame_writers,0);
        WakeAllConditionVariable(&g_dn_cv); WakeAllConditionVariable(&g_ps_changed);
    }
    ReleaseSRWLockExclusive(&g_dn_lock); ReleaseSRWLockExclusive(&g_ps_lock);
    return failed ? 0 : 1;
}
#endif
