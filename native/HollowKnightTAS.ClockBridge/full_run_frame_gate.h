#ifndef HKTAS_FULL_RUN_FRAME_GATE_H
#define HKTAS_FULL_RUN_FRAME_GATE_H

#include <stddef.h>
#include <string.h>

#define HKTAS_V2_MAGIC 0x32544648u /* HFT2 */
#define HKTAS_V2_VERSION 2u
#define HKTAS_V2_MODE_PAUSED 0
#define HKTAS_V2_MODE_STEP 1
#define HKTAS_V2_MODE_RUN 2
#define HKTAS_V2_MODE_FAULT 3
#define HKTAS_V2_MODE_FINISHED 4

typedef struct hktas_v2_state
{
    uint32_t magic;
    uint32_t version;
    char token[32];
    volatile LONGLONG completed_frames;
    volatile LONGLONG command_sequence;
    volatile LONGLONG ack_sequence;
    volatile LONGLONG expected_frame;
    volatile LONG requested_mode;
    volatile LONG mode;
    volatile LONG bootstrap_armed;
    volatile LONG runtime_input_ready;
    volatile LONG fault_code;
    volatile LONG guard_armed;
    BYTE descriptor_sha256[32];
    volatile LONGLONG callback_sequence;
} hktas_v2_state;

typedef char hktas_v2_state_size_check[(sizeof(hktas_v2_state) == 136u) ? 1 : -1];
typedef char hktas_v2_descriptor_offset_check[(offsetof(hktas_v2_state, descriptor_sha256) == 96u) ? 1 : -1];

static HANDLE g_v2_state_mapping;
static HANDLE g_v2_command_event;
static HANDLE g_v2_observation_event;
static volatile LONG g_v2_observation_requested;
static void (__cdecl *g_v2_observation_callback)(uint64_t completed);
static hktas_v2_state *g_v2_state;
static BYTE g_v2_armed_hash[32];
static BOOL g_v2_hash_latched;
static void (__cdecl *g_v2_before_callback)(uint64_t completed);
static void (__cdecl *g_v2_completed_callback)(uint64_t completed);
static volatile LONG g_v2_step_movie_frame_complete;

static uint64_t hktas_get_completed_player_loops(void)
{
    return g_v2_state == NULL ? 0u : (uint64_t)InterlockedCompareExchange64(
        &g_v2_state->completed_frames, 0, 0);
}

static void hktas_v2_fault(LONG code)
{
    if (g_v2_state == NULL) return;
    InterlockedCompareExchange(&g_v2_state->fault_code, code, 0);
    InterlockedExchange(&g_v2_state->mode, HKTAS_V2_MODE_FAULT);
    if (g_boot_ready != NULL) ResetEvent(g_boot_ready);
}

static LONG hktas_v2_request_observation(void)
{
    if (!g_v2_gate_enabled || g_v2_state == NULL || g_v2_observation_event == NULL
        || g_v2_observation_callback == NULL
        || InterlockedCompareExchange(&g_v2_state->mode, 0, 0) == HKTAS_V2_MODE_FAULT)
        return 0;
    InterlockedExchange(&g_v2_observation_requested, 1);
    return SetEvent(g_v2_observation_event) ? 1 : 0;
}

static BOOL hktas_v2_identity_valid(void)
{
    if (g_v2_state == NULL || g_v2_state->magic != HKTAS_V2_MAGIC
        || g_v2_state->version != HKTAS_V2_VERSION) return FALSE;
    wchar_t token[40];
    if (GetEnvironmentVariableW(L"HKTAS_BOOT_GATE_TOKEN", token, 40) != 32u) return FALSE;
    for (int i = 0; i < 32; ++i)
        if (g_v2_state->token[i] != (char)token[i]) return FALSE;
    return TRUE;
}

static BOOL hktas_v2_bootstrap_valid(void)
{
    if (!hktas_v2_identity_valid() || !save_write_guard_is_armed()
        || g_guard_install_status != 1) return FALSE;
    InterlockedExchange(&g_v2_state->guard_armed, 1);
    if (InterlockedCompareExchange(&g_v2_state->bootstrap_armed, 0, 0) != 1)
        return FALSE;
    BYTE empty[32] = {0};
    if (memcmp(g_v2_state->descriptor_sha256, empty, 32) == 0) return FALSE;
    if (!g_v2_hash_latched)
    {
        memcpy(g_v2_armed_hash, g_v2_state->descriptor_sha256, 32);
        g_v2_hash_latched = TRUE;
    }
    return memcmp(g_v2_armed_hash, g_v2_state->descriptor_sha256, 32) == 0;
}

static void hktas_v2_apply_command(void)
{
    if (g_v2_state == NULL) return;
    if (InterlockedCompareExchange(&g_v2_state->mode, 0, 0)
        == HKTAS_V2_MODE_FINISHED) return;
    LONGLONG command = InterlockedCompareExchange64(&g_v2_state->command_sequence, 0, 0);
    LONGLONG ack = InterlockedCompareExchange64(&g_v2_state->ack_sequence, 0, 0);
    if (command == ack) return;
    if (command < ack || command != ack + 1)
    {
        hktas_v2_fault(4);
        return;
    }
    LONG requested = InterlockedCompareExchange(&g_v2_state->requested_mode, 0, 0);
    LONGLONG expected = InterlockedCompareExchange64(&g_v2_state->expected_frame, 0, 0);
    LONGLONG completed = InterlockedCompareExchange64(&g_v2_state->completed_frames, 0, 0);
    if (!hktas_v2_bootstrap_valid())
    {
        hktas_v2_fault(2);
        return;
    }
    if ((requested != HKTAS_V2_MODE_PAUSED && requested != HKTAS_V2_MODE_STEP
            && requested != HKTAS_V2_MODE_RUN)
        || expected < 0 || expected > completed
        || (requested != HKTAS_V2_MODE_PAUSED && expected != completed))
    {
        hktas_v2_fault(3);
        return;
    }
    if (requested == HKTAS_V2_MODE_STEP)
        InterlockedExchange(&g_v2_step_movie_frame_complete, 0);
    InterlockedExchange(&g_v2_state->mode, requested);
    InterlockedExchange64(&g_v2_state->ack_sequence, command);
}

static void hktas_v2_wait_command(void)
{
    HANDLE handles[3] = {g_v2_command_event, g_boot_owner, g_v2_observation_event};
    for (;;)
    {
        DWORD result = MsgWaitForMultipleObjectsEx(3, handles, INFINITE,
            QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (result == WAIT_OBJECT_0 || result == WAIT_OBJECT_0 + 2 || result == WAIT_FAILED) return;
        if (result == WAIT_OBJECT_0 + 1)
        {
            hktas_v2_fault(5);
            ExitProcess(74u);
        }
        if (result != WAIT_OBJECT_0 + 3) return;
        MSG message;
        for (int i = 0; i < 128 && PeekMessageW(&message, NULL, 0, 0, PM_REMOVE); ++i)
        {
            if (message.message == WM_QUIT)
            {
                PostQuitMessage((int)message.wParam);
                hktas_v2_fault(6);
                return;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
}

static void hktas_v2_wait_fault(void)
{
    /* A terminal fault must not return to Unity with a skipped PlayerLoop:
       its outer/title-bar loop can then wait for a render that never ran.
       Keep g_boot_loop_active set while servicing only window messages.
       Do not run observations, accept playback commands or advance time. */
    for (;;)
    {
        DWORD result = MsgWaitForMultipleObjectsEx(1, &g_boot_owner, INFINITE,
            QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (result == WAIT_OBJECT_0 || result == WAIT_FAILED) ExitProcess(74u);
        if (result != WAIT_OBJECT_0 + 1) continue;
        MSG message;
        for (int i = 0; i < 128 && PeekMessageW(&message, NULL, 0, 0, PM_REMOVE); ++i)
        {
            if (message.message == WM_QUIT) ExitProcess(0u);
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
}

static BOOL hktas_v2_before_frame(void)
{
    if (g_v2_state == NULL) return FALSE;
    for (;;)
    {
        if (!hktas_v2_identity_valid() || !save_write_guard_is_armed()
            || g_guard_install_status != 1)
        {
            hktas_v2_fault(1);
            return FALSE;
        }
        if (g_v2_hash_latched && !hktas_v2_bootstrap_valid())
        {
            hktas_v2_fault(2);
            return FALSE;
        }
        hktas_v2_apply_command();
        LONG mode = InterlockedCompareExchange(&g_v2_state->mode, 0, 0);
        if ((mode == HKTAS_V2_MODE_FINISHED || mode == HKTAS_V2_MODE_FAULT)
            && !finish_contact_impulse_policy()) {
            hktas_v2_fault(64); return FALSE;
        }
        if (mode == HKTAS_V2_MODE_FAULT) return FALSE;
        /* Read-only work runs on the Unity thread at a completed-frame boundary.
           It does not consume input, acknowledge a command, or execute PlayerLoop. */
        if (InterlockedExchange(&g_v2_observation_requested, 0) != 0
            && g_v2_observation_callback != NULL)
            g_v2_observation_callback(hktas_get_completed_player_loops());
        if (mode == HKTAS_V2_MODE_RUN || mode == HKTAS_V2_MODE_STEP)
        {
            if (!hktas_v2_bootstrap_valid())
            {
                hktas_v2_fault(2);
                return FALSE;
            }
            if (g_v2_before_callback != NULL)
            {
                g_v2_before_callback(hktas_get_completed_player_loops());
                LONG after_callback = InterlockedCompareExchange(&g_v2_state->mode, 0, 0);
                if (after_callback == HKTAS_V2_MODE_FAULT) return FALSE;
                if (after_callback != mode) continue;
            }
            ResetEvent(g_boot_ready);
            return TRUE;
        }
        InterlockedExchange(&g_v2_state->guard_armed, 1);
        SetEvent(g_boot_ready);
        hktas_v2_wait_command();
    }
}

static void hktas_v2_after_frame(void)
{
    if (g_v2_state == NULL) return;
    LONGLONG completed = InterlockedIncrement64(&g_v2_state->completed_frames);
    if (g_v2_completed_callback != NULL)
    {
        g_v2_completed_callback((uint64_t)completed);
        InterlockedIncrement64(&g_v2_state->callback_sequence);
    }
    if (InterlockedCompareExchange(&g_v2_state->mode, 0, 0) == HKTAS_V2_MODE_STEP
        && InterlockedExchange(&g_v2_step_movie_frame_complete, 0) == 1)
        InterlockedExchange(&g_v2_state->mode, HKTAS_V2_MODE_PAUSED);
}

static BOOL install_full_run_frame_gate(void)
{
    wchar_t enabled[4], token[40], name[110];
    DWORD length = GetEnvironmentVariableW(L"HKTAS_FULL_RUN_V2", enabled, 4);
    if (length == 0u) return TRUE;
    if (length != 1u || enabled[0] != L'1' || !g_guard_required
        || g_boot_frame_state == NULL || g_boot_ready == NULL) return FALSE;
    if (GetEnvironmentVariableW(L"HKTAS_BOOT_GATE_TOKEN", token, 40) != 32u) return FALSE;
    wsprintfW(name, L"Local\\HKTAS.Boot.%s.V2State", token);
    g_v2_state_mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    wsprintfW(name, L"Local\\HKTAS.Boot.%s.V2Command", token);
    g_v2_command_event = OpenEventW(SYNCHRONIZE, FALSE, name);
    g_v2_observation_event = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (g_v2_state_mapping == NULL || g_v2_command_event == NULL
        || g_v2_observation_event == NULL) return FALSE;
    g_v2_state = (hktas_v2_state *)MapViewOfFile(g_v2_state_mapping,
        FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(hktas_v2_state));
    if (g_v2_state == NULL || !hktas_v2_identity_valid()
        || g_v2_state->completed_frames != 0 || g_v2_state->mode != HKTAS_V2_MODE_PAUSED)
        return FALSE;
    g_v2_gate_enabled = TRUE;
    return TRUE;
}

#endif
