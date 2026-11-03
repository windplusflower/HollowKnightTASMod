#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <stdint.h>
#include <limits.h>
#include <stdio.h>
#include "startup_gate.h"
#include "save_write_guard.h"
#include "restore_window.h"

#define HKTAS_CLOCK_BRIDGE_ABI 10u
#define HKTAS_CLOCK_WAIT_ATTEMPTS 600u
#define HKTAS_CLOCK_WAIT_MILLISECONDS 50u
#define HKTAS_CLOCK_STARTUP_TARGET_RATE 50LL

typedef void *(__cdecl *mono_get_root_domain_fn)(void);
typedef void *(__cdecl *mono_thread_attach_fn)(void *domain);
typedef void *(__cdecl *mono_domain_assembly_open_fn)(
    void *domain,
    const char *name);
typedef void *(__cdecl *mono_assembly_get_image_fn)(void *assembly);
typedef void *(__cdecl *mono_class_from_name_fn)(
    void *image,
    const char *name_space,
    const char *name);
typedef void *(__cdecl *mono_class_get_method_from_name_fn)(
    void *klass,
    const char *name,
    int parameter_count);
typedef void *(__cdecl *mono_runtime_invoke_fn)(
    void *method,
    void *instance,
    void **parameters,
    void **exception);

static HMODULE g_bridge_module;
static volatile LONG g_status;
static SRWLOCK g_clock_lock = SRWLOCK_INIT;
static BOOL(WINAPI *g_query_performance_counter)(LARGE_INTEGER *value);
static DWORD g_virtual_clock_main_thread_id;
static BOOL g_virtual_clock_paused;
static BOOL g_virtual_clock_resume_pending;
static LARGE_INTEGER g_virtual_clock_pause_started;
static LONGLONG g_virtual_clock_paused_ticks;
static volatile LONG g_virtual_clock_pause_count;
static volatile LONG g_virtual_clock_resume_request_count;
static volatile LONG g_virtual_clock_resume_count;
static BOOL g_deterministic_clock_enabled;
static LARGE_INTEGER g_deterministic_clock_anchor;
static LONGLONG g_deterministic_clock_frequency;
static LONGLONG g_deterministic_clock_step_ticks;
#include "fractional_frame_clock.h"
static hktas_fractional_clock g_fractional_clock;
static BOOL g_fractional_clock_configured;
static LONG g_fractional_rate_numerator;
static LONG g_fractional_rate_denominator;
static LONG g_deterministic_clock_last_advance_sequence;
static volatile LONG g_deterministic_clock_frame_advance_count;
static BOOL g_query_performance_counter_hook_installed;
static BOOL g_startup_latch_enabled;
static DWORD g_startup_hook_thread_id;
static volatile LONG g_startup_virtual_qpc_call_count;
static volatile LONG g_startup_handoff_adopt_count;
static volatile LONG g_startup_fault_code;
static BOOL g_guard_required;
static BOOL g_v2_gate_enabled;
static volatile LONG *g_boot_frame_state; /* completed, waiting, thread, hooked */
static BOOL loading_startup_clock_is_frozen(void);
static BOOL loading_startup_clock_owns_advances(void);
static BOOL prepare_early_mod_wall_clock(void);
static BOOL hktas_resident_boundary_released(void);

static void advance_boot_frame_clock(void)
{
    if (loading_startup_clock_is_frozen()) return;
    AcquireSRWLockExclusive(&g_clock_lock);
    if (g_fractional_clock_configured)
        g_deterministic_clock_step_ticks = hktas_fractional_peek(&g_fractional_clock);
    /* Preserve the legacy v3 advances. In the opt-in v4 policy this native
     * hook owns the anchor; the managed call validates sequence only. */
    if ((g_v2_gate_enabled || g_startup_handoff_adopt_count == 0)
        && g_deterministic_clock_enabled
        && g_deterministic_clock_step_ticks > 0
        && g_deterministic_clock_anchor.QuadPart <= LLONG_MAX - g_deterministic_clock_step_ticks)
    {
        g_deterministic_clock_anchor.QuadPart += g_deterministic_clock_step_ticks;
        if (g_fractional_clock_configured) hktas_fractional_consume(&g_fractional_clock);
        if (loading_startup_clock_owns_advances()) InterlockedIncrement(&g_deterministic_clock_frame_advance_count);
    }
    ReleaseSRWLockExclusive(&g_clock_lock);
}

static BOOL finish_contact_impulse_policy(void);
#include "full_run_frame_gate.h"
#include "loading_startup_clock_probe.h"
#include "contact_impulse_probe.h"
#include "startup_frame_hook.h"
#include "full_run_continuation.h"
/* The production bridge has one commit authority. Standalone producer
 * fixtures include their domain header directly and exercise its local end. */
#define HKTAS_JOINT_RESIDENT_GATE 1
static LONG hktas_reject_individual_end(void);
#include "resident_audio_fence.h"
#include "resident_particle_producer.h"
#include "resident_director_producers.h"
/* Keep the exact first audio failure in the shared fault word. It survives
 * termination; reporting only 66 also obscured explicit abort (reason 37). */
static void __cdecl hktas_resident_audio_fault(LONG family)
{
    LONG reason = HktasClockBridge_GetResidentAudioFenceFault();
    hktas_v2_fault(reason > 0 && reason < 100 ? family * 100 + reason : family);
}
static void __cdecl hktas_resident_particle_fault(LONG family)
{
    /* Called with the producer lock held. Do not re-enter its status export. */
    LONG reason = g_ps_reason;
    hktas_v2_fault(reason > 0 && reason < 100 ? family * 100 + reason : family);
}
static void __cdecl hktas_resident_director_fault(LONG family)
{
    LONG reason = g_dn_fault;
    hktas_v2_fault(reason > 0 && reason < 100 ? family * 100 + reason : family);
}
#include "resident_boundary_clock.h"
#include "resident_frame_clock.h"

static BOOL loading_startup_clock_is_frozen(void)
{
    return g_loading_startup_clock_enabled && g_loading_startup_clock_frozen_frame;
}

static BOOL loading_startup_clock_owns_advances(void)
{
    return g_loading_startup_clock_enabled;
}

static BOOL configure_save_guard_intent(void)
{
    wchar_t mode[4];
    DWORD length = GetEnvironmentVariableW(L"HKTAS_FULL_RUN_SAVE_GUARD", mode, 4);
    if (length == 0u) return TRUE;
    if (length != 1u || mode[0] != L'1') return FALSE;
    g_guard_required = TRUE;
    return TRUE;
}

static BOOL install_save_guard_from_environment(void)
{
    wchar_t root[HKTAS_GUARD_PATH_CHARS];
    wchar_t token[65];
    DWORD root_length = GetEnvironmentVariableW(L"HKTAS_SAVE_GUARD_ORIGINAL_ROOT",
        root, HKTAS_GUARD_PATH_CHARS);
    DWORD token_length = GetEnvironmentVariableW(L"HKTAS_SAVE_GUARD_TOKEN", token, 65);
    if (root_length == 0u || root_length >= HKTAS_GUARD_PATH_CHARS
        || token_length != 64u) return FALSE;
    return install_save_write_guard(root, token);
}

static BOOL WINAPI virtual_query_performance_counter(
    LARGE_INTEGER *value)
{
    LARGE_INTEGER real_value;
    BOOL result;
    DWORD current_thread;

    if (value == NULL || g_query_performance_counter == NULL)
    {
        return FALSE;
    }

    /* The first Unity main-thread clock read is before the managed runtime
     * control channel exists. Never arm this handshake for cold restores. */
    if (!g_boot_frame_hook_enabled && GetCurrentThreadId() == g_virtual_clock_main_thread_id)
        enter_boot_gate();
    result = g_query_performance_counter(&real_value);
    if (!result)
    {
        return FALSE;
    }

    current_thread = GetCurrentThreadId();
    if (g_virtual_clock_main_thread_id != 0u
        && current_thread == g_virtual_clock_main_thread_id)
    {
        LONGLONG source;
        AcquireSRWLockShared(&g_clock_lock);
        if (g_deterministic_clock_enabled)
        {
            /*
             * The deterministic anchor already is the complete virtual
             * main-thread timestamp. Pause accounting must not be subtracted
             * a second time after a TAS resume.
             */
            value->QuadPart = g_deterministic_clock_anchor.QuadPart;
            if (g_startup_latch_enabled)
            {
                InterlockedIncrement(&g_startup_virtual_qpc_call_count);
            }
        }
        else
        {
            source = g_virtual_clock_paused
                ? g_virtual_clock_pause_started.QuadPart
                : real_value.QuadPart;
            value->QuadPart = source - g_virtual_clock_paused_ticks;
        }
        ReleaseSRWLockShared(&g_clock_lock);
        return TRUE;
    }

    AcquireSRWLockShared(&g_clock_lock);
    value->QuadPart = real_value.QuadPart;
    ReleaseSRWLockShared(&g_clock_lock);
    return TRUE;
}

static BOOL install_query_performance_counter_hook(void)
{
    if (g_query_performance_counter_hook_installed)
    {
        return TRUE;
    }

    HMODULE unity_player = GetModuleHandleW(L"UnityPlayer.dll");
    BYTE *base = (BYTE *)unity_player;
    IMAGE_DOS_HEADER *dos_header;
    IMAGE_NT_HEADERS64 *nt_headers;
    IMAGE_DATA_DIRECTORY import_directory;
    IMAGE_IMPORT_DESCRIPTOR *descriptor;

    if (unity_player == NULL)
    {
        return FALSE;
    }

    dos_header = (IMAGE_DOS_HEADER *)base;
    if (dos_header->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return FALSE;
    }
    nt_headers = (IMAGE_NT_HEADERS64 *)(base + dos_header->e_lfanew);
    if (nt_headers->Signature != IMAGE_NT_SIGNATURE
        || nt_headers->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC)
    {
        return FALSE;
    }

    import_directory = nt_headers->OptionalHeader.DataDirectory[
        IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (import_directory.VirtualAddress == 0u
        || import_directory.Size < sizeof(IMAGE_IMPORT_DESCRIPTOR))
    {
        return FALSE;
    }

    descriptor = (IMAGE_IMPORT_DESCRIPTOR *)(
        base + import_directory.VirtualAddress);
    for (; descriptor->Name != 0u; ++descriptor)
    {
        IMAGE_THUNK_DATA64 *names;
        IMAGE_THUNK_DATA64 *addresses;

        if (descriptor->OriginalFirstThunk == 0u
            || descriptor->FirstThunk == 0u)
        {
            continue;
        }
        names = (IMAGE_THUNK_DATA64 *)(
            base + descriptor->OriginalFirstThunk);
        addresses = (IMAGE_THUNK_DATA64 *)(base + descriptor->FirstThunk);
        for (; names->u1.AddressOfData != 0u; ++names, ++addresses)
        {
            IMAGE_IMPORT_BY_NAME *import_name;
            DWORD old_protection;
            DWORD ignored_protection;
            void *original;

            if (IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal))
            {
                continue;
            }
            import_name = (IMAGE_IMPORT_BY_NAME *)(
                base + names->u1.AddressOfData);
            if (lstrcmpA(
                    (const char *)import_name->Name,
                    "QueryPerformanceCounter") != 0)
            {
                continue;
            }

            original = (void *)(uintptr_t)addresses->u1.Function;
            if (original == NULL)
            {
                return FALSE;
            }
            if (!VirtualProtect(
                    &addresses->u1.Function,
                    sizeof(addresses->u1.Function),
                    PAGE_READWRITE,
                    &old_protection))
            {
                return FALSE;
            }
            g_query_performance_counter =
                (BOOL(WINAPI *)(LARGE_INTEGER *))original;
            InterlockedExchangePointer(
                (PVOID volatile *)&addresses->u1.Function,
                (PVOID)virtual_query_performance_counter);
            if (!VirtualProtect(
                    &addresses->u1.Function,
                    sizeof(addresses->u1.Function),
                    old_protection,
                    &ignored_protection))
            {
                return FALSE;
            }
            g_query_performance_counter_hook_installed = TRUE;
            return TRUE;
        }
    }

    return FALSE;
}

static BOOL configure_startup_latch(void)
{
    wchar_t enabled[2];
    DWORD enabled_length;
    DWORD current_thread_id = GetCurrentThreadId();
    LARGE_INTEGER frequency;
    LARGE_INTEGER now;

    enabled_length = GetEnvironmentVariableW(
        L"HKTAS_CLOCK_STARTUP_LATCH",
        enabled,
        (DWORD)(sizeof(enabled) / sizeof(enabled[0])));
    if (enabled_length == 0u)
    {
        return TRUE;
    }
    if (enabled_length != 1u || enabled[0] != L'1')
    {
        InterlockedExchange(&g_startup_fault_code, -1);
        return FALSE;
    }

    if (!install_query_performance_counter_hook())
    {
        InterlockedExchange(&g_startup_fault_code, -2);
        return FALSE;
    }
    if (!QueryPerformanceFrequency(&frequency)
        || g_query_performance_counter == NULL
        || !g_query_performance_counter(&now)
        || frequency.QuadPart <= 0
        || frequency.QuadPart % HKTAS_CLOCK_STARTUP_TARGET_RATE != 0)
    {
        InterlockedExchange(&g_startup_fault_code, -3);
        return FALSE;
    }
    AcquireSRWLockExclusive(&g_clock_lock);
    g_virtual_clock_main_thread_id = current_thread_id;
    g_deterministic_clock_anchor = now;
    g_deterministic_clock_frequency = frequency.QuadPart;
    g_deterministic_clock_step_ticks =
        frequency.QuadPart / HKTAS_CLOCK_STARTUP_TARGET_RATE;
    g_deterministic_clock_last_advance_sequence = 0;
    g_deterministic_clock_enabled = TRUE;
    g_startup_latch_enabled = TRUE;
    g_startup_hook_thread_id = current_thread_id;
    ReleaseSRWLockExclusive(&g_clock_lock);
    return TRUE;
}

static HANDLE create_startup_payload_ready_event(void)
{
    static const wchar_t prefix[] =
        L"HollowKnightTAS.T24.ClockPayloadReady.";
    wchar_t run_id[97];
    wchar_t event_name[160];
    DWORD run_id_length;
    size_t prefix_length =
        (sizeof(prefix) / sizeof(prefix[0])) - 1u;

    run_id_length = GetEnvironmentVariableW(
        L"HKTAS_CLOCK_RUN_ID",
        run_id,
        (DWORD)(sizeof(run_id) / sizeof(run_id[0])));
    if (run_id_length == 0u
        || run_id_length >= sizeof(run_id) / sizeof(run_id[0])
        || prefix_length + (size_t)run_id_length + 1u
            > sizeof(event_name) / sizeof(event_name[0]))
    {
        return NULL;
    }
    for (DWORD index = 0u; index < run_id_length; ++index)
    {
        wchar_t character = run_id[index];
        if (!((character >= L'a' && character <= L'z')
            || (character >= L'A' && character <= L'Z')
            || (character >= L'0' && character <= L'9')
            || character == L'-'
            || character == L'_'))
        {
            return NULL;
        }
    }

    CopyMemory(
        event_name,
        prefix,
        prefix_length * sizeof(wchar_t));
    CopyMemory(
        event_name + prefix_length,
        run_id,
        ((size_t)run_id_length + 1u) * sizeof(wchar_t));
    return CreateEventW(NULL, TRUE, FALSE, event_name);
}

static FARPROC require_export(HMODULE module, const char *name)
{
    if (module == NULL)
    {
        return NULL;
    }

    return GetProcAddress(module, name);
}

static BOOL load_export(
    void *destination,
    size_t destination_size,
    HMODULE module,
    const char *name)
{
    FARPROC procedure = require_export(module, name);
    if (procedure == NULL || destination_size != sizeof(procedure))
    {
        return FALSE;
    }

    CopyMemory(destination, &procedure, sizeof(procedure));
    return TRUE;
}

static BOOL payload_path_utf8(char *output, int output_capacity)
{
    wchar_t path[MAX_PATH];
    DWORD length = GetModuleFileNameW(
        g_bridge_module,
        path,
        (DWORD)(sizeof(path) / sizeof(path[0])));
    if (length == 0 || length >= (DWORD)(sizeof(path) / sizeof(path[0])))
    {
        return FALSE;
    }

    wchar_t *separator = path + length;
    while (separator > path
           && separator[-1] != L'\\'
           && separator[-1] != L'/')
    {
        --separator;
    }

    const wchar_t payload_name[] = L"HollowKnightTAS.ClockPayload.dll";
    size_t prefix_length = (size_t)(separator - path);
    size_t payload_length =
        (sizeof(payload_name) / sizeof(payload_name[0])) - 1u;
    if (prefix_length + payload_length + 1u
        > sizeof(path) / sizeof(path[0]))
    {
        return FALSE;
    }

    CopyMemory(
        path + prefix_length,
        payload_name,
        (payload_length + 1u) * sizeof(wchar_t));
    int converted = WideCharToMultiByte(
        CP_UTF8,
        WC_ERR_INVALID_CHARS,
        path,
        -1,
        output,
        output_capacity,
        NULL,
        NULL);
    return converted > 0;
}

static BOOL prepare_early_mod_wall_clock(void)
{
    static LONG attempted;
    static BOOL installed;
    wchar_t enabled[4];
    DWORD length = GetEnvironmentVariableW(L"HKTAS_LOADING_MOD_WALL_CLOCK", enabled, 4);
    if (length == 0) return TRUE;
    if (length != 1 || enabled[0] != L'1' || !g_v2_gate_enabled
        || !g_loading_startup_clock_enabled
        || GetCurrentThreadId() != g_startup_hook_thread_id) return FALSE;
    if (InterlockedExchange(&attempted, 1) != 0) return installed;

    /* The regular worker waits for Menu_Title. This small V5-only entry must
     * precede ModLoader's assembly scan and constructors. The engine thread
     * is already attached to Mono; do not attach a competing worker thread. */
    HMODULE mono = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
    mono_get_root_domain_fn get_root_domain = NULL;
    mono_domain_assembly_open_fn domain_assembly_open = NULL;
    mono_assembly_get_image_fn assembly_get_image = NULL;
    mono_class_from_name_fn class_from_name = NULL;
    mono_class_get_method_from_name_fn class_get_method = NULL;
    mono_runtime_invoke_fn runtime_invoke = NULL;
    if (!load_export(&get_root_domain, sizeof(get_root_domain), mono, "mono_get_root_domain")
        || !load_export(&domain_assembly_open, sizeof(domain_assembly_open), mono, "mono_domain_assembly_open")
        || !load_export(&assembly_get_image, sizeof(assembly_get_image), mono, "mono_assembly_get_image")
        || !load_export(&class_from_name, sizeof(class_from_name), mono, "mono_class_from_name")
        || !load_export(&class_get_method, sizeof(class_get_method), mono, "mono_class_get_method_from_name")
        || !load_export(&runtime_invoke, sizeof(runtime_invoke), mono, "mono_runtime_invoke")) return FALSE;
    void *domain = get_root_domain();
    char payload_path[MAX_PATH * 4];
    if (domain == NULL || !payload_path_utf8(payload_path, (int)sizeof(payload_path))) return FALSE;
    void *assembly = domain_assembly_open(domain, payload_path);
    void *image = assembly == NULL ? NULL : assembly_get_image(assembly);
    void *klass = image == NULL ? NULL : class_from_name(image,
        "HollowKnightTAS.ClockPayload", "EarlyModWallClockBootstrap");
    void *method = klass == NULL ? NULL : class_get_method(klass, "Install", 0);
    if (method == NULL) return FALSE;
    void *exception = NULL;
    runtime_invoke(method, NULL, NULL, &exception);
    installed = exception == NULL;
    if (installed) loading_startup_clock_diagnostic("mod-wall-clock-installed");
    return installed;
}

static DWORD WINAPI clock_worker(LPVOID ignored)
{
    (void)ignored;
    InterlockedExchange(&g_status, 1);

    if (g_guard_required)
    {
        if (!install_save_guard_from_environment())
        {
            InterlockedExchange(&g_guard_install_status, -1);
            InterlockedExchange(&g_status, -14);
            return 14u;
        }
        InterlockedExchange(&g_guard_install_status, 1);
    }

    /* Time spent deliberately paused before initialization must not consume
     * the subsequent payload initialization timeout. */
    wait_boot_gate_release();

    if (g_startup_latch_enabled)
    {
        HANDLE payload_ready = create_startup_payload_ready_event();
        if (payload_ready == NULL)
        {
            InterlockedExchange(&g_status, -10);
            return 10u;
        }
        DWORD ready_wait = WaitForSingleObject(payload_ready, 120000u);
        CloseHandle(payload_ready);
        if (ready_wait != WAIT_OBJECT_0)
        {
            InterlockedExchange(&g_status, -11);
            return 11u;
        }
    }

    HMODULE mono = NULL;
    mono_get_root_domain_fn get_root_domain = NULL;
    mono_thread_attach_fn thread_attach = NULL;
    mono_domain_assembly_open_fn domain_assembly_open = NULL;
    mono_assembly_get_image_fn assembly_get_image = NULL;
    mono_class_from_name_fn class_from_name = NULL;
    mono_class_get_method_from_name_fn class_get_method = NULL;
    mono_runtime_invoke_fn runtime_invoke = NULL;
    void *domain = NULL;

    for (uint32_t attempt = 0;
         attempt < HKTAS_CLOCK_WAIT_ATTEMPTS;
         ++attempt)
    {
        mono = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
        if (mono != NULL)
        {
            if (load_export(
                    &get_root_domain,
                    sizeof(get_root_domain),
                    mono,
                    "mono_get_root_domain")
                && load_export(
                    &thread_attach,
                    sizeof(thread_attach),
                    mono,
                    "mono_thread_attach")
                && load_export(
                    &domain_assembly_open,
                    sizeof(domain_assembly_open),
                    mono,
                    "mono_domain_assembly_open")
                && load_export(
                    &assembly_get_image,
                    sizeof(assembly_get_image),
                    mono,
                    "mono_assembly_get_image")
                && load_export(
                    &class_from_name,
                    sizeof(class_from_name),
                    mono,
                    "mono_class_from_name")
                && load_export(
                    &class_get_method,
                    sizeof(class_get_method),
                    mono,
                    "mono_class_get_method_from_name")
                && load_export(
                    &runtime_invoke,
                    sizeof(runtime_invoke),
                    mono,
                    "mono_runtime_invoke"))
            {
                domain = get_root_domain();
                if (domain != NULL)
                {
                    break;
                }
            }
        }

        Sleep(HKTAS_CLOCK_WAIT_MILLISECONDS);
    }

    if (domain == NULL)
    {
        InterlockedExchange(&g_status, -1);
        return 1u;
    }

    if (thread_attach(domain) == NULL)
    {
        InterlockedExchange(&g_status, -2);
        return 2u;
    }

    if (!g_query_performance_counter_hook_installed
        && !install_query_performance_counter_hook())
    {
        InterlockedExchange(&g_status, -8);
        return 8u;
    }

    char payload_path[MAX_PATH * 4];
    if (!payload_path_utf8(payload_path, (int)sizeof(payload_path)))
    {
        InterlockedExchange(&g_status, -3);
        return 3u;
    }

    void *assembly = domain_assembly_open(domain, payload_path);
    if (assembly == NULL)
    {
        InterlockedExchange(&g_status, -4);
        return 4u;
    }

    void *image = assembly_get_image(assembly);
    void *klass = image == NULL
        ? NULL
        : class_from_name(
            image,
            "HollowKnightTAS.ClockPayload",
            "ClockController");
    void *method = klass == NULL
        ? NULL
        : class_get_method(klass, "Bootstrap", 0);
    if (method == NULL)
    {
        InterlockedExchange(&g_status, -5);
        return 5u;
    }

    void *exception = NULL;
    runtime_invoke(method, NULL, NULL, &exception);
    if (exception != NULL)
    {
        InterlockedExchange(&g_status, -6);
        return 6u;
    }

    InterlockedExchange(&g_status, 2);
    return 0u;
}

__declspec(dllexport) uint32_t __cdecl HktasClockBridge_GetAbi(void)
{
    return HKTAS_CLOCK_BRIDGE_ABI;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_GetStatus(void)
{
    return InterlockedCompareExchange(&g_status, 0, 0);
}

static LONG hktas_writer_BeginMainThreadPause(void);
__declspec(dllexport) LONG __cdecl HktasClockBridge_BeginMainThreadPause(void)
{
    if (!hktas_enter_clock_writer()) return -1;
    LONG result = hktas_writer_BeginMainThreadPause(); hktas_leave_clock_writer(); return result;
}
static LONG hktas_writer_BeginMainThreadPause(void)
{
    LARGE_INTEGER now;
    DWORD current_thread = GetCurrentThreadId();

    if (g_status != 2 || g_query_performance_counter == NULL)
    {
        return -1;
    }
    if (!g_query_performance_counter(&now))
    {
        return -2;
    }

    AcquireSRWLockExclusive(&g_clock_lock);
    if (g_virtual_clock_main_thread_id == 0u)
    {
        g_virtual_clock_main_thread_id = current_thread;
    }
    if (g_virtual_clock_main_thread_id != current_thread)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -3;
    }
    if (g_virtual_clock_paused)
    {
        if (g_virtual_clock_resume_pending)
        {
            ReleaseSRWLockExclusive(&g_clock_lock);
            return -4;
        }
        ReleaseSRWLockExclusive(&g_clock_lock);
        return 0;
    }

    g_virtual_clock_pause_started = now;
    g_virtual_clock_paused = TRUE;
    g_virtual_clock_resume_pending = FALSE;
    InterlockedIncrement(&g_virtual_clock_pause_count);
    ReleaseSRWLockExclusive(&g_clock_lock);
    return 1;
}

static LONG hktas_writer_EnableDeterministicMainThreadClock(
    LONGLONG step_ticks,
    LONGLONG expected_frequency);
__declspec(dllexport) LONG __cdecl HktasClockBridge_EnableDeterministicMainThreadClock(
    LONGLONG step_ticks,
    LONGLONG expected_frequency)
{
    if (!hktas_enter_clock_writer()) return -1;
    LONG result = hktas_writer_EnableDeterministicMainThreadClock(step_ticks, expected_frequency); hktas_leave_clock_writer(); return result;
}
static LONG hktas_writer_EnableDeterministicMainThreadClock(
    LONGLONG step_ticks,
    LONGLONG expected_frequency)
{
    LARGE_INTEGER frequency;
    LARGE_INTEGER now;
    DWORD current_thread = GetCurrentThreadId();

    if (g_status != 2 || g_query_performance_counter == NULL)
    {
        return -1;
    }
    if (!QueryPerformanceFrequency(&frequency)
        || !g_query_performance_counter(&now)
        || frequency.QuadPart <= 0)
    {
        return -2;
    }
    if (step_ticks <= 0
        || expected_frequency != frequency.QuadPart
        || step_ticks > frequency.QuadPart)
    {
        return -3;
    }

    AcquireSRWLockExclusive(&g_clock_lock);
    if (g_virtual_clock_main_thread_id == 0u)
    {
        g_virtual_clock_main_thread_id = current_thread;
    }
    if (g_virtual_clock_main_thread_id != current_thread)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -4;
    }
    if (g_deterministic_clock_enabled)
    {
        if (g_startup_latch_enabled
            && g_virtual_clock_main_thread_id == current_thread
            && g_deterministic_clock_frequency == expected_frequency
            && g_deterministic_clock_step_ticks == step_ticks
            && !g_virtual_clock_paused
            && !g_virtual_clock_resume_pending)
        {
            InterlockedIncrement(&g_startup_handoff_adopt_count);
            ReleaseSRWLockExclusive(&g_clock_lock);
            return 2;
        }
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -5;
    }
    if (g_virtual_clock_paused
        || g_virtual_clock_resume_pending)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -5;
    }

    g_deterministic_clock_anchor.QuadPart =
        now.QuadPart - g_virtual_clock_paused_ticks;
    g_deterministic_clock_frequency = frequency.QuadPart;
    g_deterministic_clock_step_ticks = step_ticks;
    g_deterministic_clock_last_advance_sequence = 0;
    InterlockedExchange(&g_deterministic_clock_frame_advance_count, 0);
    g_deterministic_clock_enabled = TRUE;
    ReleaseSRWLockExclusive(&g_clock_lock);
    return 1;
}

static LONG hktas_writer_ShiftDeterministicMainThreadClock(LONGLONG delta_ticks);
__declspec(dllexport) LONG __cdecl HktasClockBridge_ShiftDeterministicMainThreadClock(LONGLONG delta_ticks)
{
    if (!hktas_enter_clock_writer()) return -1;
    LONG result = hktas_writer_ShiftDeterministicMainThreadClock(delta_ticks); hktas_leave_clock_writer(); return result;
}
static LONG hktas_writer_ShiftDeterministicMainThreadClock(LONGLONG delta_ticks)
{
    DWORD current_thread = GetCurrentThreadId();

    if (g_status != 2 || g_query_performance_counter == NULL)
    {
        return -1;
    }

    AcquireSRWLockExclusive(&g_clock_lock);
    if (g_virtual_clock_main_thread_id != current_thread)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -2;
    }
    if (!g_deterministic_clock_enabled
        || g_deterministic_clock_frequency <= 0
        || g_deterministic_clock_step_ticks <= 0)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -3;
    }
    if (g_virtual_clock_paused || g_virtual_clock_resume_pending)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -4;
    }
    if ((delta_ticks > 0
            && g_deterministic_clock_anchor.QuadPart
                > LLONG_MAX - delta_ticks)
        || (delta_ticks < 0
            && g_deterministic_clock_anchor.QuadPart
                < LLONG_MIN - delta_ticks))
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -5;
    }

    g_deterministic_clock_anchor.QuadPart += delta_ticks;
    ReleaseSRWLockExclusive(&g_clock_lock);
    return 1;
}

static LONG hktas_writer_EndMainThreadPause(void);
__declspec(dllexport) LONG __cdecl HktasClockBridge_EndMainThreadPause(void)
{
    if (!hktas_enter_clock_writer()) return -1;
    LONG result = hktas_writer_EndMainThreadPause(); hktas_leave_clock_writer(); return result;
}
static LONG hktas_writer_EndMainThreadPause(void)
{
    DWORD current_thread = GetCurrentThreadId();

    if (g_status != 2 || g_query_performance_counter == NULL)
    {
        return -1;
    }
    AcquireSRWLockExclusive(&g_clock_lock);
    if (g_virtual_clock_main_thread_id != current_thread)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -3;
    }
    if (!g_virtual_clock_paused)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return 0;
    }
    if (g_virtual_clock_resume_pending)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return 0;
    }

    g_virtual_clock_resume_pending = TRUE;
    InterlockedIncrement(&g_virtual_clock_resume_request_count);
    ReleaseSRWLockExclusive(&g_clock_lock);
    return 1;
}

static LONG hktas_writer_CommitMainThreadResume(void);
__declspec(dllexport) LONG __cdecl HktasClockBridge_CommitMainThreadResume(void)
{
    if (!hktas_enter_clock_writer()) return -1;
    LONG result = hktas_writer_CommitMainThreadResume(); hktas_leave_clock_writer(); return result;
}
static LONG hktas_writer_CommitMainThreadResume(void)
{
    LARGE_INTEGER now;
    DWORD current_thread = GetCurrentThreadId();

    if (g_status != 2 || g_query_performance_counter == NULL)
    {
        return -1;
    }
    if (!g_query_performance_counter(&now))
    {
        return -2;
    }

    AcquireSRWLockExclusive(&g_clock_lock);
    if (g_virtual_clock_main_thread_id != current_thread)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -3;
    }
    if (!g_virtual_clock_paused)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -4;
    }
    if (!g_virtual_clock_resume_pending)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -5;
    }

    /*
     * The managed payload invokes this immediately before Unity's original
     * TimeUpdate.WaitForLastPresentationAndUpdateTime subsystem. Everything
     * between the completed-frame pause boundary and this frame-transition
     * callback is excluded; Unity's own presentation wait and time sample run
     * unmodified after the commit.
     */
    g_virtual_clock_paused_ticks +=
        now.QuadPart - g_virtual_clock_pause_started.QuadPart;
    g_virtual_clock_paused = FALSE;
    g_virtual_clock_resume_pending = FALSE;
    InterlockedIncrement(&g_virtual_clock_resume_count);
    ReleaseSRWLockExclusive(&g_clock_lock);
    return 1;
}

static LONG hktas_writer_AdvanceDeterministicFrameClock(LONG sequence);
__declspec(dllexport) LONG __cdecl HktasClockBridge_AdvanceDeterministicFrameClock(LONG sequence)
{
    if (!hktas_enter_clock_writer()) return -1;
    LONG result = hktas_writer_AdvanceDeterministicFrameClock(sequence); hktas_leave_clock_writer(); return result;
}
static LONG hktas_writer_AdvanceDeterministicFrameClock(LONG sequence)
{
    DWORD current_thread = GetCurrentThreadId();

    if (g_status != 2 || g_query_performance_counter == NULL)
    {
        return -1;
    }

    AcquireSRWLockExclusive(&g_clock_lock);
    if (g_virtual_clock_main_thread_id != current_thread)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -2;
    }
    if (!g_deterministic_clock_enabled
        || g_deterministic_clock_step_ticks <= 0
        || g_deterministic_clock_frequency <= 0)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -3;
    }
    if (g_virtual_clock_paused || g_virtual_clock_resume_pending)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -4;
    }
    if (sequence <= 0
        || sequence != g_deterministic_clock_last_advance_sequence + 1)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -5;
    }
    if (g_loading_startup_clock_enabled) {
        /* New policy: acknowledge the legacy managed observer, while the
         * controlled PlayerLoop remains the single QPC/fractional owner. */
        g_deterministic_clock_last_advance_sequence = sequence;
        ReleaseSRWLockExclusive(&g_clock_lock);
        return 1;
    }
    if (g_deterministic_clock_anchor.QuadPart
        > LLONG_MAX - g_deterministic_clock_step_ticks)
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return -6;
    }

    g_deterministic_clock_anchor.QuadPart +=
        g_deterministic_clock_step_ticks;
    g_deterministic_clock_last_advance_sequence = sequence;
    InterlockedIncrement(&g_deterministic_clock_frame_advance_count);
    ReleaseSRWLockExclusive(&g_clock_lock);
    return 1;
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetVirtualClockPaused(void)
{
    LONG paused;
    AcquireSRWLockShared(&g_clock_lock);
    paused = g_virtual_clock_paused ? 1 : 0;
    ReleaseSRWLockShared(&g_clock_lock);
    return paused;
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetVirtualClockPauseCount(void)
{
    return InterlockedCompareExchange(
        &g_virtual_clock_pause_count,
        0,
        0);
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetVirtualClockResumePending(void)
{
    LONG pending;
    AcquireSRWLockShared(&g_clock_lock);
    pending = g_virtual_clock_resume_pending ? 1 : 0;
    ReleaseSRWLockShared(&g_clock_lock);
    return pending;
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetVirtualClockResumeRequestCount(void)
{
    return InterlockedCompareExchange(
        &g_virtual_clock_resume_request_count,
        0,
        0);
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetVirtualClockResumeCount(void)
{
    return InterlockedCompareExchange(
        &g_virtual_clock_resume_count,
        0,
        0);
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetDeterministicClockEnabled(void)
{
    LONG enabled;
    AcquireSRWLockShared(&g_clock_lock);
    enabled = g_deterministic_clock_enabled ? 1 : 0;
    ReleaseSRWLockShared(&g_clock_lock);
    return enabled;
}

__declspec(dllexport) LONGLONG __cdecl
HktasClockBridge_GetDeterministicClockFrequency(void)
{
    LONGLONG frequency;
    AcquireSRWLockShared(&g_clock_lock);
    frequency = g_deterministic_clock_frequency;
    ReleaseSRWLockShared(&g_clock_lock);
    return frequency;
}

__declspec(dllexport) LONGLONG __cdecl
HktasClockBridge_GetDeterministicClockStepTicks(void)
{
    LONGLONG step_ticks;
    AcquireSRWLockShared(&g_clock_lock);
    step_ticks = g_deterministic_clock_step_ticks;
    ReleaseSRWLockShared(&g_clock_lock);
    return step_ticks;
}

__declspec(dllexport) LONGLONG __cdecl
HktasClockBridge_GetDeterministicClockAnchor(void)
{
    LONGLONG anchor;
    AcquireSRWLockShared(&g_clock_lock);
    anchor = g_deterministic_clock_anchor.QuadPart;
    ReleaseSRWLockShared(&g_clock_lock);
    return anchor;
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetDeterministicClockFrameAdvanceCount(void)
{
    return InterlockedCompareExchange(
        &g_deterministic_clock_frame_advance_count,
        0,
        0);
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetStartupHookInstalled(void)
{
    return g_query_performance_counter_hook_installed ? 1 : 0;
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetStartupLatchEnabled(void)
{
    return g_startup_latch_enabled ? 1 : 0;
}

__declspec(dllexport) DWORD __cdecl
HktasClockBridge_GetStartupHookThreadId(void)
{
    return g_startup_hook_thread_id;
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetStartupVirtualQpcCallCount(void)
{
    return InterlockedCompareExchange(
        &g_startup_virtual_qpc_call_count,
        0,
        0);
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetStartupHandoffAdoptCount(void)
{
    return InterlockedCompareExchange(
        &g_startup_handoff_adopt_count,
        0,
        0);
}

__declspec(dllexport) LONG __cdecl
HktasClockBridge_GetStartupFaultCode(void)
{
    return InterlockedCompareExchange(&g_startup_fault_code, 0, 0);
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_GetSaveGuardArmed(void)
{
    return save_write_guard_is_armed() ? 1 : 0;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_GetSaveGuardInstallStatus(void)
{
    return InterlockedCompareExchange(&g_guard_install_status, 0, 0);
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_GetSaveGuardRejectedCount(void)
{
    return InterlockedCompareExchange(&g_guard_rejected_count, 0, 0);
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_ConfirmSaveGuardIdentity(
    const wchar_t *original_root, const wchar_t *session_token)
{
    return save_write_guard_matches(original_root, session_token) ? 1 : 0;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_GetFullRunCapability(void)
{
    return g_v2_gate_enabled && hktas_v2_identity_valid() ? 2 : 0;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_CopyFullRunDescriptorHash(
    BYTE *output, DWORD capacity)
{
    if (!g_v2_gate_enabled || !hktas_v2_identity_valid() || output == NULL
        || capacity != 32u || g_v2_state->bootstrap_armed != 1)
        return 0;
    memcpy(output, g_v2_state->descriptor_sha256, 32u);
    return 1;
}

__declspec(dllexport) uint64_t __cdecl HktasClockBridge_GetCompletedPlayerLoops(void)
{
    return hktas_get_completed_player_loops();
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_GetFullRunMode(void)
{
    return g_v2_state == NULL ? -1 : InterlockedCompareExchange(
        &g_v2_state->mode, 0, 0);
}

static LONG hktas_writer_RequestFramePause(void);
__declspec(dllexport) LONG __cdecl HktasClockBridge_RequestFramePause(void)
{
    if (!hktas_enter_clock_writer()) return 0;
    LONG result = hktas_writer_RequestFramePause(); hktas_leave_clock_writer(); return result;
}
static LONG hktas_writer_RequestFramePause(void)
{
    if (!g_v2_gate_enabled || g_v2_state == NULL) return 0;
    if (g_v2_state->mode == HKTAS_V2_MODE_FAULT
        || g_v2_state->mode == HKTAS_V2_MODE_FINISHED) return 0;
    InterlockedExchange(&g_v2_state->mode, HKTAS_V2_MODE_PAUSED);
    return 1;
}

static LONG hktas_writer_FinishFullRun(void);
__declspec(dllexport) LONG __cdecl HktasClockBridge_FinishFullRun(void)
{
    if (!hktas_enter_clock_writer()) return 0;
    LONG result = hktas_writer_FinishFullRun(); hktas_leave_clock_writer(); return result;
}
static LONG hktas_writer_FinishFullRun(void)
{
    if (!g_v2_gate_enabled || g_v2_state == NULL
        || g_v2_state->mode == HKTAS_V2_MODE_FAULT
        || InterlockedCompareExchange(&g_v2_state->runtime_input_ready, 0, 0) != 1)
        return 0;
    InterlockedExchange(&g_v2_state->mode, HKTAS_V2_MODE_FINISHED);
    return 1;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_SetFrameCompletedCallback(
    void (__cdecl *callback)(uint64_t completed))
{
    if (!g_v2_gate_enabled || callback == NULL || g_v2_completed_callback != NULL)
        return 0;
    g_v2_completed_callback = callback;
    InterlockedExchange(&g_v2_state->runtime_input_ready, 1);
    return 1;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_SetBeforeFrameCallback(
    void (__cdecl *callback)(uint64_t completed))
{
    if (!g_v2_gate_enabled || callback == NULL || g_v2_before_callback != NULL)
        return 0;
    g_v2_before_callback = callback;
    return 1;
}

static LONG hktas_writer_ReportMovieFrameCompleted(void);
__declspec(dllexport) LONG __cdecl HktasClockBridge_ReportMovieFrameCompleted(void)
{
    if (!hktas_enter_clock_writer()) return 0;
    LONG result = hktas_writer_ReportMovieFrameCompleted(); hktas_leave_clock_writer(); return result;
}
static LONG hktas_writer_ReportMovieFrameCompleted(void)
{
    if (!g_v2_gate_enabled || g_v2_state == NULL
        || g_v2_completed_callback == NULL) return 0;
    if (InterlockedCompareExchange(&g_v2_state->mode, 0, 0) == HKTAS_V2_MODE_STEP)
        InterlockedExchange(&g_v2_step_movie_frame_complete, 1);
    return 1;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_SetObservationCallback(
    void (__cdecl *callback)(uint64_t completed))
{
    if (!g_v2_gate_enabled || g_v2_state == NULL || callback == NULL
        || g_v2_observation_callback != NULL) return 0;
    g_v2_observation_callback = callback;
    return 1;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_RequestObservation(void)
{
    return hktas_v2_request_observation();
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_FaultFullRun(LONG code)
{
    if (!g_v2_gate_enabled || code <= 0) return 0;
    hktas_v2_fault(code);
    return 1;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_bridge_module = instance;
        DisableThreadLibraryCalls(instance);
        if (!configure_startup_latch())
        {
            InterlockedExchange(&g_status, -9);
            return FALSE;
        }
        if (!configure_boot_gate())
        {
            InterlockedExchange(&g_status, -12);
            return FALSE;
        }
        if (!configure_save_guard_intent())
        {
            InterlockedExchange(&g_status, -14);
            return FALSE;
        }
        if (!install_restore_window_hooks())
        {
            /* Imports may already point into this DLL, so unloading is unsafe.
             * Abort this newly launched process before any game loop can run. */
            InterlockedExchange(&g_status, -15);
            TerminateProcess(GetCurrentProcess(), ERROR_INVALID_FUNCTION);
            return TRUE;
        }
        if (!install_boot_frame_hook())
        {
            InterlockedExchange(&g_status, -13);
            /* The QPC IAT hook was installed above. Do not unload its target
             * DLL on a frame-profile mismatch. Suppress the legacy QPC gate
             * so the controller receives no false frame acknowledgement. */
            g_boot_frame_hook_enabled = TRUE;
            return TRUE;
        }
        if (g_v2_gate_enabled && (!g_guard_required || !g_startup_latch_enabled
            || !install_resident_audio_fence((BYTE *)GetModuleHandleW(L"UnityPlayer.dll"), g_startup_hook_thread_id, hktas_resident_audio_fault)
            || !install_resident_particle_producer((BYTE *)GetModuleHandleW(L"UnityPlayer.dll"), g_startup_hook_thread_id, hktas_resident_particle_fault)
            || !install_resident_director_producers((BYTE *)GetModuleHandleW(L"UnityPlayer.dll"), g_startup_hook_thread_id, hktas_resident_director_fault)))
        {
            InterlockedExchange(&g_status, -17);
            TerminateProcess(GetCurrentProcess(), ERROR_INVALID_FUNCTION);
            return TRUE;
        }
        if (!configure_loading_startup_clock_probe()) {
            InterlockedExchange(&g_status, -16);
            TerminateProcess(GetCurrentProcess(), ERROR_INVALID_FUNCTION);
            return TRUE;
        }
        HANDLE worker = CreateThread(
            NULL,
            0,
            clock_worker,
            NULL,
            0,
            NULL);
        if (worker == NULL)
        {
            InterlockedExchange(&g_status, -7);
            return FALSE;
        }

        CloseHandle(worker);
    }
    else if (reason == DLL_PROCESS_DETACH && reserved != NULL && g_impulse_installed)
    {
        /* Process termination has stopped other threads; never unpatch a
         * running physics callback from an arbitrary managed finalizer. */
        g_impulse_canonical = FALSE;
        HktasClockBridge_StopContactImpulseProbe();
    }

    return TRUE;
}

static LONG hktas_writer_SetFullRunFrameRateRatio(LONG numerator, LONG denominator);
__declspec(dllexport) LONG __cdecl HktasClockBridge_SetFullRunFrameRateRatio(LONG numerator, LONG denominator)
{
    if (!hktas_enter_clock_writer()) return 0;
    LONG result = hktas_writer_SetFullRunFrameRateRatio(numerator, denominator); hktas_leave_clock_writer(); return result;
}
static LONG hktas_writer_SetFullRunFrameRateRatio(LONG numerator, LONG denominator)
{
    AcquireSRWLockExclusive(&g_clock_lock);
    if (!g_deterministic_clock_enabled || !hktas_fractional_configure(
        &g_fractional_clock, g_deterministic_clock_frequency, numerator, denominator))
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return 0;
    }
    g_fractional_clock_configured = TRUE;
    g_fractional_rate_numerator = numerator;
    g_fractional_rate_denominator = denominator;
    g_deterministic_clock_step_ticks = hktas_fractional_peek(&g_fractional_clock);
    ReleaseSRWLockExclusive(&g_clock_lock);
    return 1;
}

__declspec(dllexport) LONG __cdecl HktasClockBridge_SetFullRunFrameRate(LONG fps)
{
    return HktasClockBridge_SetFullRunFrameRateRatio(fps, 1);
}
