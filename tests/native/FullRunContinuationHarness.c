/* Exercise the actual exported bridge functions. No game/DllMain is run. */
#include "../../native/HollowKnightTAS.ClockBridge/clock_bridge.c"
#include "ResidentAudioFenceTestImage.h"
#include "ResidentParticleFenceTestImage.h"
#include <stdio.h>
#define CHECK(value) do { if (!(value)) { fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #value); return 1; } } while (0)
static BYTE engine[0x430];
static LONGLONG origin;
static double scripting_origin;
static hktas_v2_state gate;
static BYTE *audio_image;
static BYTE *particle_image;
static const char *particle_image_file;
static DECLSPEC_ALIGN(16) BYTE director_manager[1120], director_commands[64];
static void reset_director_fixture(void)
{
    /* initialize() models a fresh process. No engine or fixture worker can
     * retain one of the old relays after its owning test has joined it. */
    AUDIO_IMAGE_CHECK(!g_dn_active && !g_dn_waiting && !g_dn_job_tickets && !g_dn_job_rows && !g_dn_job_count);
    if(g_dn_fls!=FLS_OUT_OF_INDEXES) AUDIO_IMAGE_CHECK(FlsFree(g_dn_fls));
    if(g_dn_relay) AUDIO_IMAGE_CHECK(VirtualFree(g_dn_relay,0,MEM_RELEASE));
    if(g_dn_witness.data) AUDIO_IMAGE_CHECK(HeapFree(GetProcessHeap(),0,g_dn_witness.data));
    memset(&g_dn_witness,0,sizeof(g_dn_witness));
    g_dn_fls=FLS_OUT_OF_INDEXES;g_dn_base=g_dn_relay=NULL;
    g_dn_fault_sink=NULL;g_dn_main=0;g_dn_attempt=g_dn_fault=0;
    g_dn_installed=g_dn_closed=g_dn_held=g_dn_restore_main_window=FALSE;
    g_dn_epoch=g_dn_started=g_dn_begun=g_dn_ended=g_dn_peak=0;
    g_dn_job_published=g_dn_job_retired=g_dn_job_started=g_dn_job_finished=0;
}
static void initialize_director_fixture(void)
{
    /* Same complete pinned private PE as the particle registration slots.
     * The executable, loader, frame loop and original DllMain never run. */
    AUDIO_IMAGE_CHECK(hktas_dn_install_in_exclusive_image(particle_image,GetCurrentThreadId(),hktas_resident_director_fault));
    AUDIO_IMAGE_CHECK(install_resident_clip_curve_ledger(particle_image,GetCurrentThreadId(),hktas_resident_clip_curve_fault));
    memset(director_manager,0,sizeof(director_manager));memset(director_commands,0,sizeof(director_commands));
    BYTE *ptr=director_manager;memcpy(particle_image+HKTAS_DN_MANAGER,&ptr,8);
    for(unsigned i=0;i<2;i++) {
        ptr=director_manager+808+i*16;memcpy(ptr,&ptr,8);memcpy(ptr+8,&ptr,8);
    }
    ptr=director_commands;memcpy(director_manager+1080,&ptr,8);
    AUDIO_IMAGE_CHECK(hktas_dn_verify(TRUE));
}
static void initialize_particle_fixture(void)
{
    /* A new test process image; no engine entry point is executed and no
     * particle jobs exist in these clock-only tests. Production never resets. */
    AUDIO_IMAGE_CHECK(!g_ps_count && !g_ps_writers);
    AUDIO_IMAGE_CHECK(!g_cl_active);
    if (g_cl_tls!=TLS_OUT_OF_INDEXES) AUDIO_IMAGE_CHECK(FlsFree(g_cl_tls));
    if (g_cl_relay) AUDIO_IMAGE_CHECK(VirtualFree(g_cl_relay,0,MEM_RELEASE));
    g_cl_tls=TLS_OUT_OF_INDEXES;g_cl_relay=NULL;g_cl_base=NULL;g_cl_sink=NULL;
    g_cl_main=0;g_cl_fault=g_cl_unknown=g_cl_attempted_install=0;
    g_cl_installed=g_cl_closed=g_cl_held=FALSE;g_cl_used=0;
    g_cl_previous_epoch=0;g_cl_generation=g_cl_epoch=g_cl_started=g_cl_attempted=g_cl_completed=0;
    memset(g_cl_pairs,0,sizeof(g_cl_pairs));
    if (g_ps_tls != TLS_OUT_OF_INDEXES) { AUDIO_IMAGE_CHECK(!TlsGetValue(g_ps_tls)); AUDIO_IMAGE_CHECK(TlsFree(g_ps_tls)); }
    if (particle_image) AUDIO_IMAGE_CHECK(VirtualFree(particle_image,0,MEM_RELEASE));
    g_ps_tls=TLS_OUT_OF_INDEXES; g_ps_attempted=g_ps_reason=0; g_ps_closed=g_ps_installed=FALSE;
    g_ps_epoch=g_ps_started=g_ps_completed=0;
    particle_image=hktas_ps_test_image_from_file(particle_image_file);
    AUDIO_IMAGE_CHECK(particle_image && hktas_audio_kind_at(particle_image,(uintptr_t)(particle_image+0x10d2560))==6);
    AUDIO_IMAGE_CHECK(hktas_audio_kind_at(particle_image,(uintptr_t)(particle_image+0x10d3960))==0);
    AUDIO_IMAGE_CHECK(install_resident_particle_producer(particle_image,GetCurrentThreadId(),hktas_resident_particle_fault));
}
static void initialize_audio_fixture(void)
{
    /* initialize() simulates a new process. All synthetic workers have already
     * ended; production has no unload, reinstall or fault-reset path. */
    AUDIO_IMAGE_CHECK(!(hktas_audio_atomic(&g_audio_state)&HKTAS_AUDIO_COUNT_MASK));
    AUDIO_IMAGE_CHECK(!InterlockedCompareExchange(&g_audio_pending_startups,0,0));
    if(g_audio_tls!=TLS_OUT_OF_INDEXES) AUDIO_IMAGE_CHECK(TlsGetValue(g_audio_tls)==NULL);
    for(DWORD i=0;i<HKTAS_AUDIO_MAX_WORKERS;++i) AUDIO_IMAGE_CHECK(g_audio_workers[i].used==0);
    if(g_audio_unwind_registered) AUDIO_IMAGE_CHECK(RtlDeleteFunctionTable(g_audio_runtime_functions));
    if(g_audio_relay) AUDIO_IMAGE_CHECK(VirtualFree(g_audio_relay,0,MEM_RELEASE));
    if(audio_image) AUDIO_IMAGE_CHECK(VirtualFree(audio_image,0,MEM_RELEASE));
    if(g_audio_open_event) AUDIO_IMAGE_CHECK(CloseHandle(g_audio_open_event));
    if(g_audio_drained_event) AUDIO_IMAGE_CHECK(CloseHandle(g_audio_drained_event));
    if(g_audio_tls!=TLS_OUT_OF_INDEXES) AUDIO_IMAGE_CHECK(TlsFree(g_audio_tls));
    g_audio_tls=TLS_OUT_OF_INDEXES; g_audio_main_thread=0;
    g_audio_open_event=g_audio_drained_event=NULL; g_audio_fault_sink=NULL;
    g_audio_base=g_audio_relay=NULL;
    g_audio_installed=g_audio_install_attempted=g_audio_unwind_registered=FALSE;
    g_audio_runtime_function_count=g_audio_thread_copy_size=0;
    InterlockedExchange64(&g_audio_state,0); InterlockedExchange64(&g_audio_epoch,0); InterlockedExchange64(&g_audio_started,0);
    InterlockedExchange(&g_audio_fault_reason,0); InterlockedExchange(&g_audio_unknown_callback,0);
    InterlockedExchange(&g_audio_record_version,0); InterlockedExchange(&g_audio_pending_startups,0);
    memset(g_audio_workers,0,sizeof(g_audio_workers)); memset(g_audio_thunks,0,sizeof(g_audio_thunks));
    memset(g_audio_runtime_functions,0,sizeof(g_audio_runtime_functions));
    memset(g_audio_patched_lea,0,sizeof(g_audio_patched_lea)); memset(g_audio_relay_expected,0,sizeof(g_audio_relay_expected));
    DWORD bytes=0,old;
    audio_image=hktas_audio_make_test_image(&bytes);
    AUDIO_IMAGE_CHECK(VirtualProtect(audio_image,bytes,PAGE_EXECUTE_READ,&old));
    AUDIO_IMAGE_CHECK(install_resident_audio_fence(audio_image,GetCurrentThreadId(),hktas_v2_fault));
    AUDIO_IMAGE_CHECK(HktasClockBridge_GetResidentAudioFenceCapability()==1);
}
static void initialize(void)
{
    LONG prior_writers=InterlockedCompareExchange(&g_resident_frame_writers,0,0);
    AUDIO_IMAGE_CHECK(prior_writers==0 || (prior_writers==-1 && g_resident_boundary_failed));
    InterlockedExchange(&g_resident_frame_writers,0);
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
    g_resident_boundary_serial = g_resident_boundary_token = 0;
    g_resident_boundary_failed = g_resident_boundary_imported = FALSE;
    g_resident_boundary_read_only = FALSE;
    g_boot_frame_prepared = g_boot_player_loop_active = FALSE;
    g_v2_boundary_callback_phase = 0;
    memset(&g_fractional_clock, 0, sizeof(g_fractional_clock));
    memset(engine, 0, sizeof(engine));
    SetEnvironmentVariableW(L"HKTAS_BOOT_GATE_TOKEN", L"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    SetEnvironmentVariableW(L"HKTAS_SCENE_CHECKPOINT_EXPERIMENT", L"1");
    reset_director_fixture();initialize_audio_fixture();
    initialize_particle_fixture();
    initialize_director_fixture();
}
static void rate(int kind, int frame)
{
    int numerator = 50, denominator = 1;
    if (kind == 1 || (kind == 2 && frame < 300)) { numerator = 99999; denominator = 1000; }
    else if (kind == 2 && frame < 600) { numerator = 2997; denominator = 50; }
    else if (kind == 2 && frame < 900) { numerator = 961; denominator = 8; }
    HktasClockBridge_SetFullRunFrameRateRatio(numerator, denominator);
}

static volatile LONG frame_qpc_calls;
static HANDLE qpc_entered,qpc_release;
static BOOL WINAPI frame_qpc(LARGE_INTEGER *value)
{
    value->QuadPart=987654321; InterlockedIncrement(&frame_qpc_calls);
    if(qpc_entered) { SetEvent(qpc_entered); return WaitForSingleObject(qpc_release,3000)==WAIT_OBJECT_0; }
    return TRUE;
}
static DWORD WINAPI synthetic_foreign_pause(void *ignored) { (void)ignored; return (DWORD)HktasClockBridge_BeginMainThreadPause(); }
static void __cdecl frame_completed_stub(uint64_t completed) { (void)completed; }

static void frame_fixture(LONG phase,int pause_state)
{
    initialize();
    g_virtual_clock_paused=FALSE; g_virtual_clock_resume_pending=FALSE;
    rate(1,0);
    g_resident_boundary_read_only=FALSE;
    g_status=2; g_query_performance_counter=frame_qpc; g_query_performance_counter_hook_installed=TRUE;
    g_startup_latch_enabled=TRUE; g_startup_hook_thread_id=GetCurrentThreadId();
    g_boot_frame_hook_enabled=TRUE; g_boot_loop_active=TRUE;
    g_boot_frame_prepared=(phase==3); g_boot_player_loop_active=(phase==3);
    g_loading_startup_clock_enabled=TRUE; g_loading_startup_clock_wall_begin=76543; g_loading_startup_clock_frozen_loops=87;
    g_virtual_clock_pause_started.QuadPart=4332211; g_virtual_clock_paused_ticks=887766;
    g_virtual_clock_pause_count=14; g_virtual_clock_resume_request_count=13; g_virtual_clock_resume_count=12;
    g_startup_virtual_qpc_call_count=99; g_startup_handoff_adopt_count=1; g_startup_fault_code=0;
    g_deterministic_clock_frame_advance_count=61; g_deterministic_clock_last_advance_sequence=60;
    g_deterministic_clock_anchor.QuadPart=origin+1321445; scripting_origin=1.75;
    g_fractional_clock.phase=UINT64_C(0xf123456781234567); g_deterministic_clock_step_ticks=hktas_fractional_peek(&g_fractional_clock);
    for(size_t i=0;i<sizeof(engine);++i) engine[i]=(BYTE)(i*29u+3u);
    g_v2_boundary_callback_phase=phase; gate.completed_frames=69; gate.expected_frame=69;
    gate.command_sequence=gate.ack_sequence=80; gate.mode=gate.requested_mode=HKTAS_V2_MODE_STEP;
    gate.runtime_input_ready=gate.guard_armed=1;
    memcpy(g_v2_armed_hash,gate.descriptor_sha256,32); g_v2_hash_latched=TRUE;
    g_v2_completed_callback=frame_completed_stub; g_v2_step_movie_frame_complete=0;
    g_virtual_clock_paused=pause_state!=0; g_virtual_clock_resume_pending=pause_state==2;
    InterlockedExchange(&frame_qpc_calls,0);
}

static hktas_frame_clock hooked_before,hooked_after;
static int hook_samples,original_updates,original_loops;
static int capture_hook_callback(uint64_t completed,LONG phase,hktas_frame_clock *out)
{
    CHECK(g_v2_boundary_callback_phase==phase && g_boot_loop_active);
    CHECK(g_boot_frame_prepared==(phase==3) && g_boot_player_loop_active==(phase==3));
    uint64_t token=HktasClockBridge_BeginResidentFrameObservation(completed,phase); CHECK(token!=0);
    CHECK(HktasClockBridge_ExportResidentFrameClock(token,(BYTE *)out,sizeof(*out))==1);
    CHECK(HktasClockBridge_EndResidentBoundary(token,1)==1); ++hook_samples; return 0;
}
static void __cdecl capture_hook_before(uint64_t completed) { if(capture_hook_callback(completed,1,&hooked_before)) exit(1); }
static void __cdecl capture_hook_after(uint64_t completed) { if(capture_hook_callback(completed,3,&hooked_after)) exit(1); }
static void __cdecl synthetic_unity_time_update(void) { ++original_updates; ++engine[0x58]; }
static void __cdecl synthetic_unity_player_loop(void) { ++original_loops; ++engine[0xd8]; }
static int execute_real_frame_hook_path(void)
{
    frame_fixture(1,0); g_boot_loop_active=FALSE; g_v2_boundary_callback_phase=0;
    g_v2_before_callback=capture_hook_before; g_v2_completed_callback=capture_hook_after;
    g_boot_original_time_update=synthetic_unity_time_update; g_boot_original_player_loop=synthetic_unity_player_loop;
    HANDLE old_ready=g_boot_ready; g_boot_ready=CreateEventW(NULL,TRUE,FALSE,NULL); CHECK(g_boot_ready);
    SetEnvironmentVariableW(L"HKTAS_LOADING_MOD_WALL_CLOCK",NULL);
    boot_time_update(); CHECK(original_updates==1 && original_loops==0 && hook_samples==1 && g_boot_frame_prepared);
    boot_player_loop(); CHECK(original_updates==1 && original_loops==1 && hook_samples==2);
    CHECK(!g_boot_loop_active && !g_boot_frame_prepared && !g_boot_player_loop_active && g_v2_boundary_callback_phase==0);
    CHECK(hooked_before.callback_phase==1 && hooked_after.callback_phase==3 && hooked_after.completed==hooked_before.completed+1);
    CHECK(hooked_after.anchor==hooked_before.anchor+hooked_before.step && hooked_after.fractional_phase==hooked_before.fractional_phase+hooked_before.fraction);
    CHECK(hooked_after.advance_count==hooked_before.advance_count+1 && gate.completed_frames==70 && gate.callback_sequence==1);
    BYTE expected[sizeof(hooked_before.engine)]; memcpy(expected,hooked_before.engine,sizeof(expected)); ++expected[0x58-0x30]; ++expected[0xd8-0x30];
    CHECK(!memcmp(hooked_after.engine,expected,sizeof(expected)) && !frame_qpc_calls && hktas_resident_boundary_released());
    CloseHandle(g_boot_ready); g_boot_ready=old_ready; g_v2_before_callback=NULL; g_v2_completed_callback=NULL;
    g_boot_original_time_update=NULL; g_boot_original_player_loop=NULL;
    puts("PASS actual boot_time_update -> native before -> synthetic Unity update/PlayerLoop -> native after path advances exactly one original frame");
    return 0;
}

static int frame_clock_observations(void)
{
    CHECK(HktasClockBridge_GetResidentFrameClockBytes()==1192u && HktasClockBridge_GetContinuationBytes()==1128u);
    CHECK(execute_real_frame_hook_path()==0);
    frame_fixture(1,0);
    qpc_entered=CreateEventW(NULL,TRUE,FALSE,NULL); qpc_release=CreateEventW(NULL,TRUE,FALSE,NULL); CHECK(qpc_entered && qpc_release);
    HANDLE writer=CreateThread(NULL,0,synthetic_foreign_pause,NULL,0,NULL); CHECK(writer);
    CHECK(WaitForSingleObject(qpc_entered,2000)==WAIT_OBJECT_0 && InterlockedCompareExchange(&g_resident_frame_writers,0,0)==1);
    CHECK(HktasClockBridge_BeginResidentFrameObservation(69,1)==0 && g_resident_boundary_token==0 && g_resident_boundary_serial==0);
    CHECK(hktas_audio_atomic(&g_audio_state)==0 && hktas_audio_atomic(&g_audio_epoch)==0);
    SetEvent(qpc_release); CHECK(WaitForSingleObject(writer,2000)==WAIT_OBJECT_0); DWORD writer_result;
    CHECK(GetExitCodeThread(writer,&writer_result) && (LONG)writer_result<0 && g_resident_frame_writers==0 && !g_virtual_clock_paused);
    CloseHandle(writer); CloseHandle(qpc_entered); CloseHandle(qpc_release); qpc_entered=qpc_release=NULL;
    uint64_t after_writer=HktasClockBridge_BeginResidentFrameObservation(69,1); CHECK(after_writer!=0 && g_resident_frame_writers==-1);
    CHECK(HktasClockBridge_EndResidentBoundary(after_writer,1)==1 && g_resident_frame_writers==0);
    puts("PASS real in-flight public clock writer prevents frame admission before any token/audio closure; release admits the next frame");
    for(LONG phase=1;phase<=3;phase+=2) for(int pause_state=0;pause_state<3;++pause_state) {
        frame_fixture(phase,pause_state);
        hktas_frame_clock packet,second;
        hktas_clock_continuation restore;
        memset(&packet,0xab,sizeof(packet)); memset(&second,0xab,sizeof(second)); memset(&restore,0,sizeof(restore));
        CHECK(HktasClockBridge_BeginResidentFrameObservation(69,phase==1?3:1)==0);
        CHECK(HktasClockBridge_BeginResidentFrameObservation(68,phase)==0 && HktasClockBridge_BeginResidentFrameObservation(69,2)==0);
        if(phase==1) {
            uint64_t boundary=HktasClockBridge_BeginResidentBoundary(69); CHECK(boundary!=0);
            CHECK(HktasClockBridge_RequireResidentFrameObservation(boundary,69)==0);
            CHECK(HktasClockBridge_ExportResidentFrameClock(boundary,(BYTE *)&packet,sizeof(packet))==0 && !memcmp(&packet,&second,sizeof(packet)));
            CHECK(HktasClockBridge_ExportResidentContinuation(boundary,(BYTE *)&restore,sizeof(restore))==(pause_state==0));
            CHECK(HktasClockBridge_EndResidentBoundary(boundary,1)==1);
        } else {
            CHECK(HktasClockBridge_BeginResidentBoundary(69)==0); /* original HTC1 excludes active/prepared PlayerLoop */
        }
        uint64_t token=HktasClockBridge_BeginResidentFrameObservation(69,phase); CHECK(token!=0);
        CHECK(HktasClockBridge_RequireResidentDirectorFence(token)==1 && g_dn_started==g_resident_boundary_started);
        CHECK(HktasClockBridge_RequireResidentFrameObservation(token,69)==1 && HktasClockBridge_RequireResidentAudioFence(token)==0);
        CHECK(HktasClockBridge_RequireResidentWritableBoundary(token,69)==0);
        hktas_v2_state gate_before; BYTE time_before[sizeof(engine)]; hktas_fractional_clock fractional_before=g_fractional_clock;
        memcpy(&gate_before,&gate,sizeof(gate)); memcpy(time_before,engine,sizeof(engine));
        CHECK(HktasClockBridge_ExportResidentFrameClock(token,NULL,sizeof(packet))==0);
        CHECK(HktasClockBridge_ExportResidentFrameClock(token,(BYTE *)&packet,sizeof(packet)-1)==0 && !memcmp(&packet,&second,sizeof(packet)));
        CHECK(HktasClockBridge_ExportResidentFrameClock(token+1,(BYTE *)&packet,sizeof(packet))==0 && !memcmp(&packet,&second,sizeof(packet)));
        CHECK(HktasClockBridge_ExportResidentContinuation(token,(BYTE *)&restore,sizeof(restore))==0);
        CHECK(HktasClockBridge_ImportResidentContinuation(token,(BYTE *)&restore,sizeof(restore),69)==0 && !g_resident_boundary_imported);
        CHECK(HktasClockBridge_ExportResidentFrameClock(token,(BYTE *)&packet,sizeof(packet))==1);
        CHECK(HktasClockBridge_ExportResidentFrameClock(token,(BYTE *)&second,sizeof(second))==1 && !memcmp(&packet,&second,sizeof(packet)));
        CHECK(packet.magic==0x31434652u && packet.version==1 && packet.bytes==sizeof(packet) && packet.callback_phase==(uint32_t)phase && packet.reserved==0);
        CHECK(packet.completed==69 && packet.frequency==10000000 && packet.anchor==origin+1321445 && packet.counter_origin==origin);
        CHECK(!memcmp(&packet.scripting_origin_bits,&scripting_origin,8) && packet.step==g_deterministic_clock_step_ticks);
        CHECK(packet.whole==fractional_before.whole && packet.fraction==fractional_before.fraction && packet.fractional_phase==fractional_before.phase);
        CHECK(packet.advance_sequence==60 && packet.advance_count==61 && packet.numerator==99999 && packet.denominator==1000);
        CHECK(!memcmp(packet.engine,engine+0x30,sizeof(packet.engine))); /* includes real ABI padding omitted from HTC1 */
        uint32_t flags=(UINT32_C(1)<<18)-1u;
        flags&=~(HKTAS_FRAME_PAUSED|HKTAS_FRAME_RESUME_PENDING|HKTAS_FRAME_LOADING_FROZEN|HKTAS_FRAME_PREPARED|HKTAS_FRAME_PLAYER_LOOP_ACTIVE);
        if(phase==3) flags|=HKTAS_FRAME_PREPARED|HKTAS_FRAME_PLAYER_LOOP_ACTIVE;
        if(pause_state) flags|=HKTAS_FRAME_PAUSED;
        if(pause_state==2) flags|=HKTAS_FRAME_RESUME_PENDING;
        CHECK(packet.flags==flags && packet.gate_mode==HKTAS_V2_MODE_STEP && packet.step_movie_complete==0);
        CHECK(packet.pause_started==4332211 && packet.paused_ticks==887766 && packet.pause_count==14 && packet.resume_request_count==13 && packet.resume_count==12);
        CHECK(packet.startup_qpc_count==99 && packet.startup_adopt_count==1 && packet.startup_fault==0 && packet.startup_wall_begin==76543 && packet.startup_frozen_loops==87);
        CHECK(HktasClockBridge_SetFullRunFrameRateRatio(60,1)<=0 && HktasClockBridge_SetFullRunFrameRate(60)<=0);
        CHECK(HktasClockBridge_AdvanceDeterministicFrameClock(61)<0 && HktasClockBridge_ShiftDeterministicMainThreadClock(1)<0);
        CHECK(HktasClockBridge_BeginMainThreadPause()<0 && HktasClockBridge_EndMainThreadPause()<0 && HktasClockBridge_CommitMainThreadResume()<0);
        CHECK(HktasClockBridge_EnableDeterministicMainThreadClock(packet.step,packet.frequency)<0);
        CHECK(HktasClockBridge_ReportMovieFrameCompleted()==0 && HktasClockBridge_RequestFramePause()==0 && HktasClockBridge_FinishFullRun()==0);
        CHECK(HktasClockBridge_ExportResidentFrameClock(token,(BYTE *)&second,sizeof(second))==1 && !memcmp(&packet,&second,sizeof(packet)));
        CHECK(!memcmp(&gate_before,&gate,sizeof(gate)) && !memcmp(time_before,engine,sizeof(engine)) && !memcmp(&fractional_before,&g_fractional_clock,sizeof(g_fractional_clock)));
        CHECK(frame_qpc_calls==0 && g_startup_virtual_qpc_call_count==99 && !g_continuation_imported);
        CHECK(HktasClockBridge_EndResidentBoundary(token,1)==1 && hktas_resident_boundary_released());
        CHECK(HktasClockBridge_ExportResidentFrameClock(token,(BYTE *)&second,sizeof(second))==0);
    }
    puts("PASS RFC1 1192B: real before/after flags x running/paused/resume-pending; complete TimeManager body and clock fields bit-exact; no QPC call or source mutation");
    frame_fixture(3,0); g_boot_frame_prepared=FALSE;
    CHECK(HktasClockBridge_BeginResidentFrameObservation(69,3)==0);
    g_boot_frame_prepared=TRUE; g_boot_player_loop_active=FALSE;
    CHECK(HktasClockBridge_BeginResidentFrameObservation(69,3)==0);
    frame_fixture(1,0); g_boot_loop_active=FALSE;
    CHECK(HktasClockBridge_BeginResidentFrameObservation(69,1)==0);
    g_boot_loop_active=TRUE; gate.guard_armed=0;
    CHECK(HktasClockBridge_BeginResidentFrameObservation(69,1)==0 && gate.guard_armed==0);
    gate.guard_armed=1; g_v2_hash_latched=FALSE;
    CHECK(HktasClockBridge_BeginResidentFrameObservation(69,1)==0 && !g_v2_hash_latched);
    CHECK(g_resident_frame_writers==0 && g_resident_boundary_token==0 && hktas_audio_atomic(&g_audio_epoch)==0);
    frame_fixture(1,0); g_continuation_counter_origin=NULL;
    CHECK(HktasClockBridge_BeginResidentFrameObservation(69,1)==0 && !g_continuation_counter_origin && g_continuation_scripting_origin==&scripting_origin);
    g_continuation_counter_origin=&origin; g_continuation_scripting_origin=NULL;
    CHECK(HktasClockBridge_BeginResidentFrameObservation(69,1)==0 && g_continuation_counter_origin==&origin && !g_continuation_scripting_origin);
    CHECK(g_resident_frame_writers==0 && g_resident_boundary_serial==0 && hktas_audio_atomic(&g_audio_epoch)==0);
    frame_fixture(1,0);
    hktas_clock_continuation legacy; CHECK(HktasClockBridge_ExportContinuation((BYTE *)&legacy,sizeof(legacy))==1);
    legacy.anchor-=1; uint64_t token=HktasClockBridge_BeginResidentFrameObservation(69,1); CHECK(token!=0);
    gate.mode=HKTAS_V2_MODE_PAUSED; /* stale command/mode cannot reach old experiment import through a live frame lease */
    CHECK(HktasClockBridge_ImportContinuation((BYTE *)&legacy,sizeof(legacy),69)==0 && !g_continuation_imported);
    CHECK(g_deterministic_clock_anchor.QuadPart==origin+1321445 && gate.completed_frames==69);
    CHECK(HktasClockBridge_EndResidentBoundary(token,0)==0 && g_resident_boundary_failed);
    frame_fixture(3,2); token=HktasClockBridge_BeginResidentFrameObservation(69,3); CHECK(token!=0);
    hktas_frame_clock sentinel,unchanged; memset(&sentinel,0xb5,sizeof(sentinel)); unchanged=sentinel;
    g_resident_boundary_started=GetTickCount64()-9000;
    CHECK(HktasClockBridge_ExportResidentFrameClock(token,(BYTE *)&sentinel,sizeof(sentinel))==1);
    sentinel=unchanged;
    g_resident_boundary_started=GetTickCount64()-HKTAS_RESIDENT_SOURCE_WALL;
    CHECK(HktasClockBridge_ExportResidentFrameClock(token,(BYTE *)&sentinel,sizeof(sentinel))==0 && !memcmp(&sentinel,&unchanged,sizeof(sentinel)));
    CHECK(HktasClockBridge_EndResidentBoundary(token,1)==0 && g_resident_boundary_failed && HktasClockBridge_GetResidentAudioFenceCapability()==1);
    CHECK(InterlockedCompareExchange(&g_resident_frame_writers,0,0)==-1 && HktasClockBridge_SetFullRunFrameRateRatio(60,1)==0);
    puts("PASS B/readonly kind separation, actual phase3 nesting, unlatch refusal, stale/expired tokens, legacy import and public clock writes rejected");
    return 0;
}
static BOOL all_joint_domains_faulted(void)
{
    return gate.mode==HKTAS_V2_MODE_FAULT && g_resident_boundary_failed
        && hktas_audio_atomic(&g_audio_state)==0
        && !g_audio_fault_reason && g_ps_reason && g_ps_closed && g_dn_fault && g_dn_closed;
}
static DWORD WINAPI director_reset_time(void *unused)
{
    (void)unused;
    for(unsigned i=0;i<DN_EDGE_COUNT;++i) if(g_dn_edges[i].id==DN_manager_ResetFrameTiming) {
        BYTE *site=particle_image+g_dn_edges[i].rva;
        int32_t disp;memcpy(&disp,site+1,4);
        void (__cdecl *reset)(void*)=(void(__cdecl *)(void*))(void*)(site+5+disp);
        reset(director_manager);return 0;
    }
    return 1;
}
static int director_joint_scenarios(void)
{
    initialize();rate(0,0);g_v2_boundary_callback_phase=2;
    CHECK(HktasClockBridge_GetResidentBoundaryCapability()==4);
    /* Unlike the standalone graph command/ABA scenarios, this original
     * ResetFrameTiming crosses the ACTUAL clock/audio/particle/Director
     * release while an enrolled call is blocked. Only these two verified
     * machine-code bodies need execute permission on the private PE. */
    DWORD old;
    CHECK(VirtualProtect(particle_image+0x899d80,35,PAGE_EXECUTE_READ,&old));
    CHECK(VirtualProtect(particle_image+0x89c140,34,PAGE_EXECUTE_READ,&old));
    director_manager[16]=0x45;director_manager[88]=0x67;
    uint64_t token=HktasClockBridge_BeginResidentBoundary(0);CHECK(token && HktasClockBridge_VerifyResidentDirectorQueues(token));
    hktas_dn_status s;
    CHECK(HktasClockBridge_CopyResidentDirectorFenceStatus((BYTE*)&s,sizeof(s))==sizeof(s));
    CHECK(s.installed && s.epoch==token && s.started==g_resident_boundary_started && s.held && s.closed && !s.fault
        && s.version==4 && s.required_gaps==7 && !s.complete && s.edges==153 && s.functions==51
        && s.groups_published==s.groups_retired && s.callbacks_started==s.callbacks_returned && !s.outstanding_groups && !s.outstanding_rows
        && s.lineage_issued==s.lineage_retired && s.lineage_entered==s.lineage_returned && s.lineage_nested<=s.lineage_issued
        && !s.outstanding_lineages && !s.outstanding_lineage_rows);
    HANDLE worker=CreateThread(NULL,0,director_reset_time,NULL,0,NULL);CHECK(worker);
    uint64_t start=GetTickCount64();do {
        CHECK(HktasClockBridge_CopyResidentDirectorFenceStatus((BYTE*)&s,sizeof(s))==sizeof(s));
        CHECK(GetTickCount64()-start<3000);if(!s.waiting) Sleep(1);
    } while(!s.waiting);
    CHECK(!director_manager[988] && director_manager[16]==0x45 && director_manager[88]==0x67 && !s.active);
    LONGLONG anchor=g_deterministic_clock_anchor.QuadPart,sequence=gate.command_sequence;
    CHECK(HktasClockBridge_EndResidentBoundary(token,1)==1);
    DWORD exit_code;CHECK(WaitForSingleObject(worker,3000)==WAIT_OBJECT_0 && GetExitCodeThread(worker,&exit_code) && !exit_code);
    CloseHandle(worker);
    CHECK(director_manager[988]==1 && !director_manager[16] && !director_manager[88] && gate.completed_frames==0
        && g_deterministic_clock_anchor.QuadPart==anchor && gate.command_sequence==sequence && gate.ack_sequence==sequence);
    CHECK(!hktas_audio_atomic(&g_audio_state) && !g_ps_closed && !g_dn_closed && g_dn_begun==g_dn_ended && g_dn_begun);
    /* Changing an atomic tag preserves command/node addresses. The final
     * comparison must fail BEFORE any of the three producer gates opens. */
    token=HktasClockBridge_BeginResidentBoundary(0);CHECK(token);
    ++director_commands[8];
    CHECK(!HktasClockBridge_EndResidentBoundary(token,1) && all_joint_domains_faulted());
    CHECK(gate.completed_frames==0 && g_deterministic_clock_anchor.QuadPart==anchor && !HktasClockBridge_BeginResidentBoundary(0));
    /* Abort the two already-drained domains if Director cannot open; preserve
     * its actual first failure after another domain has also been retired. */
    initialize();rate(0,0);g_v2_boundary_callback_phase=2;director_manager[987]=1;
    CHECK(!HktasClockBridge_BeginResidentBoundary(0) && all_joint_domains_faulted());
    CHECK(gate.fault_code==6811 && !gate.completed_frames && !g_dn_held);
    /* Hardware wake events do not participate in the gameplay commit. */
    initialize();rate(0,0);g_v2_boundary_callback_phase=2;
    token=HktasClockBridge_BeginResidentBoundary(0);CHECK(token);
    HANDLE audio_wake=g_audio_open_event;g_audio_open_event=NULL;
    LONG committed=HktasClockBridge_EndResidentBoundary(token,1);
    g_audio_open_event=audio_wake;
    CHECK(committed && hktas_resident_boundary_released() && !gate.completed_frames);
    for(unsigned domain=0;domain<3;domain++) for(LONG complete=0;complete<=1;complete++) {
        initialize();rate(0,0);g_v2_boundary_callback_phase=2;
        token=HktasClockBridge_BeginResidentBoundary(0);CHECK(token);
        LONG result=domain==0 ? HktasClockBridge_EndResidentAudioFence(token,complete)
            : domain==1 ? HktasClockBridge_EndResidentParticleFence(token,complete) : HktasClockBridge_EndResidentDirectorFence(token,complete);
        CHECK(!result && all_joint_domains_faulted() && !HktasClockBridge_BeginResidentBoundary(0));
    }
    puts("PASS unified clock/particle/Director commit: real blocked writer, atomic ABA drift, abort on failed Director begin, six public release/abort misuse cases; no clock or Movie advance");
    return 0;
}
int main(int argc, char **argv)
{
    if (argc != 2) { fprintf(stderr,"usage: FullRunContinuationHarness <path-to-pinned-UnityPlayer.dll>\n"); return 2; }
    particle_image_file = argv[1];
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
    initialize(); rate(1,0); SetEnvironmentVariableW(L"HKTAS_SCENE_CHECKPOINT_EXPERIMENT",NULL);
    CHECK(HktasClockBridge_BeginResidentBoundary(0) == 0); /* No native callback owns this call. */
    gate.mode = HKTAS_V2_MODE_RUN; g_v2_boundary_callback_phase = 1;
    uint64_t first = HktasClockBridge_BeginResidentBoundary(0);
    CHECK(first != 0 && HktasClockBridge_RequireResidentBoundary(first,0) == 1);
    CHECK(HktasClockBridge_RequireResidentAudioFence(first)==0 && hktas_audio_atomic(&g_audio_epoch)==0);
    CHECK(HktasClockBridge_RequireResidentParticleFence(first)==1 && g_ps_epoch==first && g_ps_started==g_resident_boundary_started && !g_ps_count);
    CHECK(hktas_audio_atomic(&g_audio_started)==0 && hktas_audio_atomic(&g_audio_state)==0);
    CHECK(!hktas_resident_boundary_released());
    CHECK(HktasClockBridge_BeginResidentBoundary(0) == 0);
    CHECK(HktasClockBridge_ExportResidentContinuation(first,(BYTE *)&snapshot,sizeof(snapshot)) == 1);
    CHECK(HktasClockBridge_ImportResidentContinuation(first,(BYTE *)&snapshot,sizeof(snapshot),0) == 0);
    CHECK(HktasClockBridge_EndResidentBoundary(first,1) == 1 && hktas_resident_boundary_released());
    CHECK(HktasClockBridge_RequireResidentBoundary(first,0) == 0);
    CHECK(HktasClockBridge_RequireResidentAudioFence(first)==0 && hktas_audio_atomic(&g_audio_state)==0);
    gate.mode = HKTAS_V2_MODE_PAUSED; g_v2_boundary_callback_phase = 2;
    for (int iteration = 0; iteration < 3; ++iteration) {
        gate.completed_frames = 7 + iteration; gate.command_sequence = gate.ack_sequence = 100 + iteration;
        uint64_t token = HktasClockBridge_BeginResidentBoundary((uint64_t)gate.completed_frames);
        CHECK(token > first);
        CHECK(HktasClockBridge_RequireResidentBoundary(first,gate.completed_frames)==0 && HktasClockBridge_RequireResidentAudioFence(first)==0);
        CHECK(HktasClockBridge_RequireResidentAudioFence(token)==0 && hktas_audio_atomic(&g_audio_started)==0);
        first = token;
        CHECK(HktasClockBridge_ImportResidentContinuation(token,(BYTE *)&snapshot,sizeof(snapshot),7+iteration) == 1);
        CHECK(gate.completed_frames == (LONGLONG)snapshot.completed && gate.mode == HKTAS_V2_MODE_PAUSED);
        CHECK(gate.command_sequence == 100+iteration && gate.ack_sequence == 100+iteration);
        CHECK(HktasClockBridge_ImportResidentContinuation(token,(BYTE *)&snapshot,sizeof(snapshot),snapshot.completed) == 0);
        hktas_clock_continuation readback;
        CHECK(HktasClockBridge_ExportResidentContinuation(token,(BYTE *)&readback,sizeof(readback)) == 1);
        CHECK(memcmp(&snapshot,&readback,sizeof(snapshot)) == 0);
        CHECK(HktasClockBridge_EndResidentBoundary(token,1) == 1);
        CHECK(hktas_audio_atomic(&g_audio_state)==0 && HktasClockBridge_GetResidentAudioFenceCapability()==1);
    }
    CHECK(!g_continuation_imported); /* Production epochs did not reset/consume the legacy gate. */
    uint64_t expired = HktasClockBridge_BeginResidentBoundary(snapshot.completed);
    CHECK(expired != 0); g_resident_boundary_started -= (HKTAS_RESIDENT_RESTORE_WALL + 1);
    CHECK(HktasClockBridge_RequireResidentBoundary(expired,snapshot.completed) == 0);
    CHECK(HktasClockBridge_EndResidentBoundary(expired,1) == 0 && gate.mode == HKTAS_V2_MODE_FAULT);
    CHECK(hktas_audio_atomic(&g_audio_state)==0);
    CHECK(HktasClockBridge_BeginResidentBoundary(snapshot.completed) == 0);
    initialize(); rate(0,0); g_v2_boundary_callback_phase = 2;
    uint64_t changed = HktasClockBridge_BeginResidentBoundary(0); CHECK(changed != 0);
    gate.command_sequence++;
    CHECK(HktasClockBridge_RequireResidentBoundary(changed,0) == 0);
    CHECK(HktasClockBridge_EndResidentBoundary(changed,1) == 0 && !hktas_resident_boundary_released());
    CHECK(hktas_audio_atomic(&g_audio_state)==0);
    initialize(); rate(0,0); g_v2_boundary_callback_phase = 2;
    uint64_t invalid = HktasClockBridge_BeginResidentBoundary(0); CHECK(invalid != 0);
    snapshot.version = 2; anchor = g_deterministic_clock_anchor.QuadPart;
    CHECK(HktasClockBridge_ImportResidentContinuation(invalid,(BYTE *)&snapshot,sizeof(snapshot),0) == 0);
    CHECK(g_deterministic_clock_anchor.QuadPart == anchor && gate.completed_frames == 0 && g_resident_boundary_failed);
    CHECK(HktasClockBridge_EndResidentBoundary(invalid,0)==0);
    CHECK(hktas_audio_atomic(&g_audio_state)==0);
    puts("PASS resident epochs: shared ordinary boundary, repeated independent restores, exact readback, native callback ownership, stale token, command drift, wall-time expiry and permanent failure");
    initialize(); rate(0,0); g_v2_boundary_callback_phase=2;
    uint64_t audio_expired=HktasClockBridge_BeginResidentBoundary(0); CHECK(audio_expired!=0);
    CHECK(hktas_audio_atomic(&g_audio_started)==0);
    InterlockedExchange64(&g_audio_started,(LONG64)(GetTickCount64()-9001));
    CHECK(HktasClockBridge_RequireResidentBoundary(audio_expired,0)==1);
    CHECK(HktasClockBridge_EndResidentBoundary(audio_expired,1)==1 && hktas_resident_boundary_released());
    initialize(); rate(0,0); g_v2_boundary_callback_phase=2;
    uint64_t wrong_end=HktasClockBridge_BeginResidentBoundary(0); CHECK(wrong_end!=0);
    CHECK(HktasClockBridge_RequireResidentBoundary(wrong_end+1,0)==0 && HktasClockBridge_RequireResidentAudioFence(wrong_end+1)==0);
    CHECK(HktasClockBridge_RequireResidentBoundary(wrong_end,0)==1 && HktasClockBridge_RequireResidentAudioFence(wrong_end)==0);
    CHECK(HktasClockBridge_EndResidentBoundary(wrong_end+1,1)==0 && gate.mode==HKTAS_V2_MODE_FAULT);
    CHECK(g_resident_boundary_failed && hktas_audio_atomic(&g_audio_state)==0);
    initialize(); rate(0,0); g_v2_boundary_callback_phase=2;
    uint64_t aborted=HktasClockBridge_BeginResidentBoundary(0); CHECK(aborted!=0);
    CHECK(HktasClockBridge_EndResidentBoundary(aborted,0)==0 && gate.mode==HKTAS_V2_MODE_FAULT);
    CHECK(g_resident_boundary_failed && hktas_audio_atomic(&g_audio_state)==0);
    initialize(); rate(0,0); g_v2_boundary_callback_phase=2;
    uint64_t early_audio_end=HktasClockBridge_BeginResidentBoundary(0); CHECK(early_audio_end!=0);
    CHECK(HktasClockBridge_EndResidentAudioFence(early_audio_end,1)==0 && all_joint_domains_faulted());
    CHECK(HktasClockBridge_RequireResidentBoundary(early_audio_end,0)==0);
    CHECK(HktasClockBridge_EndResidentBoundary(early_audio_end,1)==0 && gate.mode==HKTAS_V2_MODE_FAULT);
    CHECK(g_resident_boundary_failed && hktas_audio_atomic(&g_audio_state)==0);
    CHECK(HktasClockBridge_GetResidentAudioFenceCoverageGaps()&HKTAS_AUDIO_REQUIRED_GAP_NON_THREAD_WORKERS);
    puts("PASS hardware audio is independent of gameplay admission and expiry; wrong token and abort still fault");
    CHECK(frame_clock_observations()==0 && director_joint_scenarios()==0);
    return 0;
}
