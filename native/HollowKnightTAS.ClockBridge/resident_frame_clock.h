#ifndef HKTAS_RESIDENT_FRAME_CLOCK_H
#define HKTAS_RESIDENT_FRAME_CLOCK_H

/* RFC1 is an observation of a single admitted native callback. The HTC1
 * continuation import has a different magic/size and its original pause
 * restrictions. Keep this include after resident_boundary_clock.h. */
#define HKTAS_FRAME_CLOCK_MAGIC 0x31434652u
#define HKTAS_FRAME_CLOCK_BYTES 1192u
enum {
    HKTAS_FRAME_DETERMINISTIC       = 1u << 0,
    HKTAS_FRAME_FRACTIONAL          = 1u << 1,
    HKTAS_FRAME_PAUSED              = 1u << 2,
    HKTAS_FRAME_RESUME_PENDING      = 1u << 3,
    HKTAS_FRAME_QPC_HOOK            = 1u << 4,
    HKTAS_FRAME_STARTUP_LATCH       = 1u << 5,
    HKTAS_FRAME_LOADING_ENABLED     = 1u << 6,
    HKTAS_FRAME_LOADING_INITIALIZED = 1u << 7,
    HKTAS_FRAME_LOADING_RELEASED    = 1u << 8,
    HKTAS_FRAME_LOADING_FROZEN      = 1u << 9,
    HKTAS_FRAME_BOOT_HOOK           = 1u << 10,
    HKTAS_FRAME_OUTER_ACTIVE        = 1u << 11,
    HKTAS_FRAME_PREPARED            = 1u << 12,
    HKTAS_FRAME_PLAYER_LOOP_ACTIVE  = 1u << 13,
    HKTAS_FRAME_GATE_V2             = 1u << 14,
    HKTAS_FRAME_CLOCK_MAIN          = 1u << 15,
    HKTAS_FRAME_STARTUP_THREAD      = 1u << 16,
    HKTAS_FRAME_ORIGINAL_QPC_BOUND  = 1u << 17
};

typedef struct {
    uint32_t magic, version, bytes, callback_phase;
    int64_t frequency, anchor, counter_origin, scripting_origin_bits, step, whole;
    uint64_t fraction, fractional_phase, completed;
    int32_t advance_sequence, advance_count, numerator, denominator;
    BYTE engine[0x400];
    uint32_t flags;
    int32_t gate_mode;
    int64_t pause_started, paused_ticks;
    int32_t pause_count, resume_request_count, resume_count, startup_qpc_count, startup_adopt_count, startup_fault;
    uint32_t startup_wall_begin, startup_frozen_loops;
    int32_t step_movie_complete;
    uint32_t reserved;
} hktas_frame_clock;
typedef char hktas_frame_clock_layout_check[(sizeof(hktas_frame_clock)==HKTAS_FRAME_CLOCK_BYTES
    && offsetof(hktas_frame_clock,engine)==104 && offsetof(hktas_frame_clock,flags)==1128
    && offsetof(hktas_frame_clock,pause_started)==1136 && offsetof(hktas_frame_clock,reserved)==1188)?1:-1];

static uint32_t hktas_frame_clock_flags(void)
{
    const DWORD thread=GetCurrentThreadId();
    return (g_deterministic_clock_enabled ? HKTAS_FRAME_DETERMINISTIC : 0u)
        | (g_fractional_clock_configured ? HKTAS_FRAME_FRACTIONAL : 0u)
        | (g_virtual_clock_paused ? HKTAS_FRAME_PAUSED : 0u)
        | (g_virtual_clock_resume_pending ? HKTAS_FRAME_RESUME_PENDING : 0u)
        | (g_query_performance_counter_hook_installed ? HKTAS_FRAME_QPC_HOOK : 0u)
        | (g_startup_latch_enabled ? HKTAS_FRAME_STARTUP_LATCH : 0u)
        | (g_loading_startup_clock_enabled ? HKTAS_FRAME_LOADING_ENABLED : 0u)
        | (g_loading_startup_clock_initialized ? HKTAS_FRAME_LOADING_INITIALIZED : 0u)
        | (g_loading_startup_clock_released ? HKTAS_FRAME_LOADING_RELEASED : 0u)
        | (g_loading_startup_clock_frozen_frame ? HKTAS_FRAME_LOADING_FROZEN : 0u)
        | (g_boot_frame_hook_enabled ? HKTAS_FRAME_BOOT_HOOK : 0u)
        | (g_boot_loop_active ? HKTAS_FRAME_OUTER_ACTIVE : 0u)
        | (g_boot_frame_prepared ? HKTAS_FRAME_PREPARED : 0u)
        | (g_boot_player_loop_active ? HKTAS_FRAME_PLAYER_LOOP_ACTIVE : 0u)
        | (g_v2_gate_enabled ? HKTAS_FRAME_GATE_V2 : 0u)
        | (thread==g_virtual_clock_main_thread_id ? HKTAS_FRAME_CLOCK_MAIN : 0u)
        | (thread==g_startup_hook_thread_id ? HKTAS_FRAME_STARTUP_THREAD : 0u)
        | (g_query_performance_counter ? HKTAS_FRAME_ORIGINAL_QPC_BOUND : 0u);
}

__declspec(dllexport) uint32_t __cdecl HktasClockBridge_GetResidentFrameClockBytes(void)
{ return sizeof(hktas_frame_clock); }

__declspec(dllexport) LONG __cdecl HktasClockBridge_ExportResidentFrameClock(uint64_t token,BYTE *output,uint32_t capacity)
{
    if (!output || capacity!=sizeof(hktas_frame_clock)) return 0;
    const uint64_t completed=g_resident_boundary_frame;
    if (HktasClockBridge_RequireResidentFrameObservation(token,completed)!=1) return 0;
    hktas_frame_clock value={0};
    value.magic=HKTAS_FRAME_CLOCK_MAGIC; value.version=1; value.bytes=sizeof(value);
    value.callback_phase=(uint32_t)g_v2_boundary_callback_phase;
    AcquireSRWLockShared(&g_clock_lock);
    if (!g_deterministic_clock_enabled || !g_fractional_clock_configured || (g_virtual_clock_resume_pending && !g_virtual_clock_paused)) {
        ReleaseSRWLockShared(&g_clock_lock); return 0;
    }
    value.frequency=g_deterministic_clock_frequency; value.anchor=g_deterministic_clock_anchor.QuadPart;
    value.counter_origin=*g_continuation_counter_origin; memcpy(&value.scripting_origin_bits,g_continuation_scripting_origin,8);
    value.step=g_deterministic_clock_step_ticks; value.whole=g_fractional_clock.whole;
    value.fraction=g_fractional_clock.fraction; value.fractional_phase=g_fractional_clock.phase; value.completed=completed;
    value.advance_sequence=g_deterministic_clock_last_advance_sequence; value.advance_count=g_deterministic_clock_frame_advance_count;
    value.numerator=g_fractional_rate_numerator; value.denominator=g_fractional_rate_denominator;
    /* Read the entire pinned pointer-free TimeManager body. No canonicalization
     * or protected/dirty memory writes occur on K/loading frames. */
    memcpy(value.engine,g_loading_startup_time_manager+0x30,sizeof(value.engine));
    value.flags=hktas_frame_clock_flags(); value.gate_mode=g_v2_state->mode;
    value.pause_started=g_virtual_clock_pause_started.QuadPart; value.paused_ticks=g_virtual_clock_paused_ticks;
    value.pause_count=g_virtual_clock_pause_count; value.resume_request_count=g_virtual_clock_resume_request_count;
    value.resume_count=g_virtual_clock_resume_count; value.startup_qpc_count=g_startup_virtual_qpc_call_count;
    value.startup_adopt_count=g_startup_handoff_adopt_count; value.startup_fault=g_startup_fault_code;
    value.startup_wall_begin=g_loading_startup_clock_wall_begin; value.startup_frozen_loops=g_loading_startup_clock_frozen_loops;
    value.step_movie_complete=g_v2_step_movie_frame_complete;
    ReleaseSRWLockShared(&g_clock_lock);
    /* Never query QPC, advance the fractional phase, attempt a resume or copy
     * into the caller's buffer after ownership/deadline drift. */
    if (HktasClockBridge_RequireResidentFrameObservation(token,completed)!=1 || value.callback_phase!=(uint32_t)g_v2_boundary_callback_phase) return 0;
    memcpy(output,&value,sizeof(value)); return 1;
}
#endif
