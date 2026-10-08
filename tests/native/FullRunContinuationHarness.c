/* Exercise the actual exported bridge functions. No game/DllMain is run. */
#include "../../native/HollowKnightTAS.ClockBridge/clock_bridge.c"
#include <stdio.h>
#define CHECK(value) do { if (!(value)) { fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #value); return 1; } } while (0)
static BYTE engine[0x430];
static LONGLONG origin;
static double scripting_origin;
static hktas_v2_state gate;
static void initialize(void)
{
    memset(&gate, 0, sizeof(gate));
    gate.magic = HKTAS_V2_MAGIC; gate.version = HKTAS_V2_VERSION;
    memset(gate.token, 'a', sizeof(gate.token)); gate.bootstrap_armed = 1; gate.descriptor_sha256[0] = 1;
    g_v2_state = &gate; g_v2_hash_latched = FALSE; g_v2_gate_enabled = TRUE;
    g_guard_armed = g_guard_install_status = 1;
    g_virtual_clock_main_thread_id = GetCurrentThreadId();
    g_loading_startup_time_manager = engine;
    g_loading_startup_clock_initialized = g_loading_startup_clock_released = TRUE;
    g_loading_startup_clock_frozen_frame = FALSE; g_deterministic_clock_enabled = TRUE;
    g_deterministic_clock_frequency = 10000000;
    g_deterministic_clock_anchor.QuadPart = origin = 100000000000LL;
    scripting_origin = 0;
    g_continuation_counter_origin = &origin; g_continuation_scripting_origin = &scripting_origin;
    g_continuation_imported = FALSE; g_fractional_clock_configured = FALSE;
    memset(&g_fractional_clock, 0, sizeof(g_fractional_clock));
    memset(engine, 0, sizeof(engine));
    SetEnvironmentVariableW(L"HKTAS_BOOT_GATE_TOKEN", L"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    SetEnvironmentVariableW(L"HKTAS_SCENE_CHECKPOINT_EXPERIMENT", L"1");
}
static void rate(int kind, int frame)
{
    int numerator = 50, denominator = 1;
    if (kind == 1 || (kind == 2 && frame < 300)) { numerator = 99999; denominator = 1000; }
    else if (kind == 2 && frame < 600) { numerator = 2997; denominator = 50; }
    else if (kind == 2 && frame < 900) { numerator = 961; denominator = 8; }
    HktasClockBridge_SetFullRunFrameRateRatio(numerator, denominator);
}
int main(void)
{
    const int captures[] = {121,300,600,900};
    for (int kind = 0; kind < 3; ++kind) for (int point = 0; point < 4; ++point) {
        initialize();
        for (int i = 0; i < captures[point]; ++i) { rate(kind, i); advance_boot_frame_clock(); ++gate.completed_frames; }
        /* Engine scalar state must transfer; the Object base/padding must not. */
        for (unsigned i = 0; i < sizeof(engine); ++i) engine[i] = (BYTE)i;
        hktas_clock_continuation snapshot;
        CHECK(HktasClockBridge_ExportContinuation((BYTE *)&snapshot, sizeof(snapshot)) == 1);
        LONGLONG anchors[1000], steps[1000]; uint64_t phases[1000];
        for (int i = 0; i < 1000; ++i) {
            rate(kind, captures[point] + i); advance_boot_frame_clock();
            anchors[i] = g_deterministic_clock_anchor.QuadPart;
            phases[i] = g_fractional_clock.phase; steps[i] = g_deterministic_clock_step_ticks;
        }
        /* A replacement has its own command acknowledgement and ownership. */
        memset(engine, 0x55, sizeof(engine)); gate.completed_frames = 7;
        gate.command_sequence = gate.ack_sequence = 12;
        g_deterministic_clock_anchor.QuadPart = origin = 900000000000LL;
        CHECK(HktasClockBridge_ImportContinuation((BYTE *)&snapshot, sizeof(snapshot), 6) == 0);
        CHECK(origin == 900000000000LL && gate.completed_frames == 7);
        CHECK(HktasClockBridge_ImportContinuation((BYTE *)&snapshot, sizeof(snapshot), 7) == 1);
        CHECK(gate.completed_frames == captures[point] && gate.mode == HKTAS_V2_MODE_PAUSED);
        CHECK(gate.command_sequence == 12 && gate.ack_sequence == 12 && engine[0] == 0x55);
        CHECK(engine[0x31] == 0x31 && engine[0x5c] == 0x55 && engine[0x42f] == 0x2f);
        CHECK(HktasClockBridge_ImportContinuation((BYTE *)&snapshot, sizeof(snapshot), captures[point]) == 0);
        for (int i = 0; i < 1000; ++i) {
            rate(kind, captures[point] + i); advance_boot_frame_clock();
            CHECK(g_deterministic_clock_anchor.QuadPart == anchors[i]);
            CHECK(g_deterministic_clock_step_ticks == steps[i] && g_fractional_clock.phase == phases[i]);
        }
    }
    puts("PASS 12 continuation points / 12000 frames: 50, 99.999, mixed 99.999->59.94->120.125->50; bit-exact ticks, steps, phase");
    initialize(); rate(1,0);
    hktas_clock_continuation snapshot;
    CHECK(HktasClockBridge_ExportContinuation((BYTE *)&snapshot, sizeof(snapshot)) == 1);
    LONGLONG anchor = g_deterministic_clock_anchor.QuadPart;
    snapshot.version = 2;
    CHECK(HktasClockBridge_ImportContinuation((BYTE *)&snapshot, sizeof(snapshot),0) == 0);
    snapshot.version = 1; snapshot.engine[0x2c] = 1;
    CHECK(HktasClockBridge_ImportContinuation((BYTE *)&snapshot, sizeof(snapshot),0) == 0);
    snapshot.engine[0x2c] = 0; snapshot.fraction ^= 1;
    CHECK(HktasClockBridge_ImportContinuation((BYTE *)&snapshot, sizeof(snapshot),0) == 0);
    CHECK(g_deterministic_clock_anchor.QuadPart == anchor && !g_continuation_imported);
    snapshot.fraction ^= 1; gate.mode = HKTAS_V2_MODE_RUN;
    CHECK(HktasClockBridge_ImportContinuation((BYTE *)&snapshot, sizeof(snapshot),0) == 0);
    gate.mode = HKTAS_V2_MODE_PAUSED; SetEnvironmentVariableW(L"HKTAS_SCENE_CHECKPOINT_EXPERIMENT",NULL);
    CHECK(HktasClockBridge_ExportContinuation((BYTE *)&snapshot, sizeof(snapshot)) == 0);
    puts("PASS wrong boundary/schema/phase/padding, running writes, repeated import and disabled experiment reject without mutation");
    return 0;
}
