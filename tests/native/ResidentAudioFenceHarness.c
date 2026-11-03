/* Offline test: a synthetic PE image and synthetic FMOD Thread objects only.
 * Does not load UnityPlayer, start the game, or access audio devices. */
#include "../../native/HollowKnightTAS.ClockBridge/resident_audio_fence.h"
#include "ResidentAudioFenceTestImage.h"
#include <stdio.h>
#include <stdlib.h>
#include <xmmintrin.h>

#define CHECK(x) do { if (!(x)) { fprintf(stderr,"FAIL %d: %s\n",__LINE__,#x); exit(1); } } while (0)
typedef struct {
    BYTE thread[352], system[42232];
    uintptr_t vtable[1];
    HANDLE started, wake, held, release, handle;
    volatile LONG calls, events[4];
    LONG block_phase, stop_after;
} fake_worker;
static BYTE *image;
static volatile LONG harness_faults, harness_error;
static uint64_t next_token;
static void __cdecl fault_sink(LONG reason) { if (reason != 66) InterlockedExchange(&harness_error,1); InterlockedIncrement(&harness_faults); }
static void pointer_at(BYTE *at, uintptr_t value) { memcpy(at,&value,8); }
static void jump_to(BYTE *at, const void *target) { const BYTE op[]={0xff,0x25,0,0,0,0}; memcpy(at,op,6); memcpy(at+6,&target,8); }
static void hold(fake_worker *worker, LONG phase)
{
    if (worker->block_phase != phase) return;
    SetEvent(worker->held);
    if (WaitForSingleObject(worker->release,3000) != WAIT_OBJECT_0) InterlockedExchange(&harness_error,2);
}
static void __cdecl system_callback(BYTE *system, int kind, void *unused, void *unused2)
{
    (void)unused; (void)unused2;
    fake_worker *worker=(fake_worker *)hktas_audio_pointer(system);
    if (!HktasClockBridge_GetResidentAudioFenceInFlight()) InterlockedExchange(&harness_error,3);
    if (kind >= 8 && kind <= 11) InterlockedIncrement(&worker->events[kind-8]);
    hold(worker,kind);
}
static void __cdecl user_callback(void *data)
{
    fake_worker *worker=(fake_worker *)data;
    if (!HktasClockBridge_GetResidentAudioFenceInFlight()) InterlockedExchange(&harness_error,4);
    LONG calls=InterlockedIncrement(&worker->calls);
    if (worker->stop_after && calls >= worker->stop_after) worker->thread[280]=0;
}
static void __cdecl virtual_callback(BYTE *thread) { user_callback((void *)hktas_audio_pointer(thread+288)); }
static int __cdecl semaphore_signal(HANDLE event, DWORD unused) { (void)unused; return SetEvent(event)?0:-1; }
static int __cdecl semaphore_wait(HANDLE event) { return WaitForSingleObject(event,INFINITE)==WAIT_OBJECT_0?0:-1; }
static void __cdecl native_sleep(DWORD milliseconds)
{
    hktas_audio_worker *row=(hktas_audio_worker *)TlsGetValue(g_audio_tls);
    if (!row || row->depth!=1) { InterlockedExchange(&harness_error,5); return; }
    fake_worker *worker=(fake_worker *)hktas_audio_pointer(row->thread+288);
    hold(worker,12); Sleep(milliseconds);
}
static void __cdecl end_thread(unsigned code) { (void)code; /* Return to exercise the copied epilogue. */ }

static hktas_audio_status status_now(void)
{
    hktas_audio_status result;
    CHECK(HktasClockBridge_CopyResidentAudioFenceStatus((BYTE *)&result,sizeof(result))==1);
    CHECK(result.magic==0x31464152u && result.version==1 && result.bytes==96 && result.worker_bytes==64);
    CHECK(result.maximum_workers==256 && result.reserved==0 && result.caller_thread==GetCurrentThreadId());
    return result;
}
static DWORD WINAPI foreign_status(void *value) { *(hktas_audio_status *)value=status_now(); return 0; }
static void status_buffers(void)
{
    BYTE buffer[97],original[97]; memset(buffer,0xc3,sizeof(buffer)); memcpy(original,buffer,sizeof(buffer));
    CHECK(HktasClockBridge_CopyResidentAudioFenceStatus(NULL,96)==0);
    CHECK(HktasClockBridge_CopyResidentAudioFenceStatus(buffer,95)==0 && !memcmp(buffer,original,sizeof(buffer)));
    CHECK(HktasClockBridge_CopyResidentAudioFenceStatus(buffer,97)==0 && !memcmp(buffer,original,sizeof(buffer)));
    InterlockedExchange(&g_audio_workers[HKTAS_AUDIO_MAX_WORKERS-1].used,1); /* synthetic publication in progress */
    CHECK(HktasClockBridge_CopyResidentAudioFenceStatus(buffer,96)==0 && !memcmp(buffer,original,sizeof(buffer)));
    InterlockedExchange(&g_audio_workers[HKTAS_AUDIO_MAX_WORKERS-1].used,0);
    hktas_audio_status own=status_now(),other;
    CHECK(own.state==0 && own.epoch==0 && own.started==0 && own.worker_count==0 && own.pending_startups==0 && own.fault==0);
    CHECK(own.capability==1 && own.abi_valid==1 && own.main_thread==own.caller_thread);
    HANDLE thread=CreateThread(NULL,0,foreign_status,&other,0,NULL); CHECK(thread);
    CHECK(WaitForSingleObject(thread,2000)==WAIT_OBJECT_0); CloseHandle(thread);
    CHECK(other.main_thread==own.main_thread && other.caller_thread!=own.caller_thread && other.fault==0);
    CHECK(!harness_faults && hktas_audio_atomic(&g_audio_state)==0 && hktas_audio_atomic(&g_audio_epoch)==0);
    puts("PASS 96B read-only receipt: exact size, untouched rejected buffers, mixed publication and caller identity");
}

static void make_image(void)
{
    DWORD bytes=0; image=hktas_audio_make_test_image(&bytes);
    jump_to(image+0x10cf540u,semaphore_signal); jump_to(image+0x10cf570u,semaphore_wait);
    jump_to(image+0x10cf730u,native_sleep); jump_to(image+0x1596460u,end_thread);
    jump_to(image+0x1143ee0u,user_callback);
    DWORD old; CHECK(VirtualProtect(image,bytes,PAGE_EXECUTE_READ,&old));
    CHECK(install_resident_audio_fence(image,GetCurrentThreadId(),fault_sink));
    CHECK(HktasClockBridge_GetResidentAudioFenceCapability()==1);
    CHECK(!install_resident_audio_fence(image,GetCurrentThreadId(),fault_sink));
    puts("PASS exact ABI and early-install evidence; late audio/FMOD state and byte/PDB drift reject");
}

static void relocation_and_unwind(void)
{
    DWORD cursor=0, starts[79];
    CHECK(sizeof(g_audio_instructions)/sizeof(g_audio_instructions[0])==79);
    for (DWORD i=0;i<79;++i) { starts[i]=cursor; cursor+=hktas_audio_instruction_size(&g_audio_instructions[i]); }
    CHECK(cursor==g_audio_thread_copy_size && g_audio_runtime_function_count==25);
    DWORD branches=0, injections=0;
    for (DWORD i=0;i<79;++i) {
        const hktas_audio_instruction *row=&g_audio_instructions[i]; DWORD at=starts[i];
        if (row->action) {
            int32_t relative; memcpy(&relative,g_audio_relay+at+1,4);
            CHECK(g_audio_relay[at]==0xe8 && g_audio_relay+at+5+relative==g_audio_relay+g_audio_thunks[row->action-1].start);
            ++injections; at+=5;
        }
        if (!row->kind) { CHECK(!memcmp(g_audio_relay+at,g_audio_original_thread+row->offset,row->size)); continue; }
        ++branches; BYTE *target=image+row->target;
        if (row->target>=HKTAS_AUDIO_THREAD_RVA && row->target<HKTAS_AUDIO_THREAD_RVA+HKTAS_AUDIO_THREAD_SIZE) {
            DWORD j; for(j=0;j<79;++j) if (g_audio_instructions[j].offset==row->target-HKTAS_AUDIO_THREAD_RVA) break;
            CHECK(j<79); target=g_audio_relay+starts[j];
        }
        DWORD length=row->kind==3?6:5; int32_t relative; memcpy(&relative,g_audio_relay+at+length-4,4);
        CHECK(g_audio_relay+at+length+relative==target);
        CHECK(row->kind==3?(g_audio_relay[at]==0x0f && g_audio_relay[at+1]==(0x80|row->condition)):
            g_audio_relay[at]==(row->kind==1?0xe8:0xe9));
    }
    CHECK(branches==17 && injections==6);
    for (DWORD t=0;t<6;++t) {
        hktas_audio_thunk *thunk=&g_audio_thunks[t];
        /* Every emitted instruction boundary, including partial prologue and
         * the duplicate-flags, POPFQ, LEA and RET windows. */
        DWORD offsets[64], count=0, at=thunk->start;
        offsets[count++]=at++; offsets[count++]=at; at+=7;
        for(DWORD i=0;i<7;++i) { offsets[count++]=at; at+=5; }
        offsets[count++]=at; at+=9;
        if (!t) for(DWORD i=0;i<3;++i) offsets[count++]=at++;
        else { offsets[count++]=at; at+=3; }
        offsets[count++]=at; at+=5; offsets[count++]=at; at+=10; offsets[count++]=at; at+=2;
        offsets[count++]=at; at+=9;
        for(DWORD i=0;i<7;++i) { offsets[count++]=at; at+=5; }
        offsets[count++]=at; at+=7; offsets[count++]=at++; offsets[count++]=at; at+=8; offsets[count++]=at++;
        CHECK(at==thunk->end);
        for(DWORD i=0;i<count;++i) {
            DECLSPEC_ALIGN(16) BYTE stack[2048]={0}; BYTE *entry=stack+1032;
            DWORD offset=offsets[i], allocation=offset==thunk->start?0:offset<thunk->start+8?8:616;
            if(offset==thunk->pop_flags) allocation=624;
            if(offset==thunk->ret) allocation=0;
            const DWORD64 expected=UINT64_C(0x123456789abcdef0); memcpy(entry,&expected,8);
            CONTEXT context; memset(&context,0,sizeof(context)); context.ContextFlags=CONTEXT_FULL;
            context.Rip=(DWORD64)(g_audio_relay+offset); context.Rsp=(DWORD64)(entry-allocation);
            context.Rbx=0x101; context.Rsi=0x202; context.Rdi=0x303;
            DWORD64 base=0, establisher=0; PVOID data=NULL;
            PRUNTIME_FUNCTION function=RtlLookupFunctionEntry(context.Rip,&base,NULL);
            CHECK(function && base==(DWORD64)g_audio_relay);
            RtlVirtualUnwind(UNW_FLAG_NHANDLER,base,context.Rip,function,&context,&data,&establisher,NULL);
            CHECK(context.Rip==expected && context.Rsp==(DWORD64)(entry+8));
            CHECK(context.Rbx==0x101 && context.Rsi==0x202 && context.Rdi==0x303);
        }
    }
    puts("PASS 79 relocated instructions / 17 transfers / 6 scope calls; every thunk instruction unwinds");
}

static void put_bytes(BYTE *code,DWORD *at,const BYTE *bytes,DWORD length) { memcpy(code+*at,bytes,length); *at+=length; }
static void put_u32(BYTE *code,DWORD *at,DWORD value) { memcpy(code+*at,&value,4); *at+=4; }
static void fx_at(BYTE *code,DWORD *at,BOOL restore,BOOL stack,DWORD displacement)
{
    const BYTE op[]={0x48,0x0f,0xae,(BYTE)(stack?(restore?0x8c:0x84):(restore?0x8f:0x87))};
    put_bytes(code,at,op,4); if(stack) code[(*at)++]=0x24; put_u32(code,at,displacement);
}
static void store_registers(BYTE *code,DWORD *at,DWORD offset)
{
    const BYTE registers[]={0,1,2,8,9,10,11};
    for(DWORD i=0;i<7;++i) {
        BYTE reg=registers[i]; code[(*at)++]=(BYTE)(0x48|((reg&8)?4:0)); code[(*at)++]=0x89;
        code[(*at)++]=(BYTE)(0x87|((reg&7)<<3)); put_u32(code,at,offset+i*8);
    }
    const BYTE flags[]={0x9c,0x8f,0x87}; put_bytes(code,at,flags,3); put_u32(code,at,offset+56);
    fx_at(code,at,FALSE,FALSE,offset+128);
}
static void register_preservation(void)
{
    BYTE *code=(BYTE *)VirtualAlloc(NULL,4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
    BYTE *output=(BYTE *)VirtualAlloc(NULL,8192,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE); CHECK(code && output);
    const DWORD seed=6*640, expected=seed+512, helper=1024, wrapper=2048;
    for(DWORD i=0;i<256;++i) output[seed+i]=(BYTE)(i*37u+19u);
    DWORD mxcsr=0x3f80u; memcpy(output+seed+256,&mxcsr,4);
    /* Deliberately violate normal helper ABI for the test: clobber all 16 XMM,
     * x87, MXCSR, volatile GPRs and arithmetic flags. The production-emitted
     * thunk must restore everything despite this stronger disturbance. */
    DWORD at=helper;
    for(DWORD i=0;i<16;++i) {
        code[at++]=0x66; if(i>=8) code[at++]=0x45;
        code[at++]=0x0f; code[at++]=0xef; code[at++]=(BYTE)(0xc0+9*(i&7));
    }
    const BYTE init[]={0xdb,0xe3}; put_bytes(code,&at,init,2);
    const BYTE load_mxcsr[]={0x0f,0xae,0x15}; put_bytes(code,&at,load_mxcsr,3); DWORD mxcsr_disp=at; at+=4;
    const BYTE clear[]={0x48,0x31,0xc0,0x48,0x31,0xc9,0x48,0x31,0xd2,
        0x4d,0x31,0xc0,0x4d,0x31,0xc9,0x4d,0x31,0xd2,0x4d,0x31,0xdb,0xc3};
    put_bytes(code,&at,clear,sizeof(clear)); DWORD helper_mxcsr=0x1f80u;
    memcpy(code+at,&helper_mxcsr,4); CHECK(hktas_audio_rel32(code+mxcsr_disp,code+mxcsr_disp+4,code+at));
    for(DWORD i=0;i<6;++i) {
        hktas_audio_thunk layout={0}; CHECK(hktas_audio_emit_thunk(code+i*160,i+1,&layout)<160);
        CHECK(code[i*160+60]==0x48 && code[i*160+61]==0xb8);
        uintptr_t target=(uintptr_t)(code+helper); memcpy(code+i*160+62,&target,8);
    }
    at=wrapper;
    const BYTE prologue[]={0x53,0x57,0x48,0x81,0xec,0x88,0x02,0,0,0x48,0x89,0xcb,0x48,0x89,0xd7};
    put_bytes(code,&at,prologue,sizeof(prologue)); fx_at(code,&at,FALSE,TRUE,32);
    const BYTE known_x87[]={0xdb,0xe3,0xd9,0xe8,0xd9,0xeb}; put_bytes(code,&at,known_x87,sizeof(known_x87));
    const BYTE load_seed_mxcsr[]={0x0f,0xae,0x97}; put_bytes(code,&at,load_seed_mxcsr,3); put_u32(code,&at,seed+256);
    for(DWORD i=0;i<16;++i) {
        code[at++]=0xf3; if(i>=8) code[at++]=0x44;
        code[at++]=0x0f; code[at++]=0x6f; code[at++]=(BYTE)(0x87+((i&7)<<3)); put_u32(code,&at,seed+16*i);
    }
    const BYTE registers[]={0,1,2,8,9,10,11}; uint64_t values[7];
    for(DWORD i=0;i<7;++i) {
        values[i]=UINT64_C(0x1020304050607080)+UINT64_C(0x0101010101010101)*i;
        code[at++]=(BYTE)(0x48|(registers[i]>=8?1:0)); code[at++]=(BYTE)(0xb8+(registers[i]&7));
        memcpy(code+at,&values[i],8); at+=8;
    }
    const BYTE set_flags[]={0x68,0x03,0x02,0,0,0x9d}; put_bytes(code,&at,set_flags,sizeof(set_flags));
    const BYTE save_flags[]={0x9c,0x8f,0x87}; put_bytes(code,&at,save_flags,3); put_u32(code,&at,seed+264);
    fx_at(code,&at,FALSE,FALSE,expected);
    for(DWORD i=0;i<6;++i) {
        const BYTE call_indirect[]={0xff,0x15,2,0,0,0,0xeb,8}; put_bytes(code,&at,call_indirect,sizeof(call_indirect));
        uintptr_t target=(uintptr_t)(code+i*160); memcpy(code+at,&target,8); at+=8;
        store_registers(code,&at,i*640);
    }
    fx_at(code,&at,TRUE,TRUE,32);
    const BYTE epilogue[]={0x48,0x81,0xc4,0x88,0x02,0,0,0x5f,0x5b,0xc3}; put_bytes(code,&at,epilogue,sizeof(epilogue));
    CHECK(at<4096); DWORD old; CHECK(VirtualProtect(code,4096,PAGE_EXECUTE_READ,&old));
    CHECK(FlushInstructionCache(GetCurrentProcess(),code,4096));
    ((void (__cdecl *)(void *,void *))(code+wrapper))(NULL,output);
    for(DWORD i=0;i<6;++i) {
        CHECK(!memcmp(output+i*640,values,sizeof(values)));
        CHECK(!memcmp(output+i*640+56,output+seed+264,8));
        CHECK(!memcmp(output+i*640+128,output+expected,5)); /* x87 control, status and tag */
        CHECK(!memcmp(output+i*640+128+24,output+expected+24,8)); /* MXCSR and mask */
        CHECK(!memcmp(output+i*640+128+32,output+expected+32,384)); /* x87 registers and all XMM */
    }
    VirtualFree(code,0,MEM_RELEASE); VirtualFree(output,0,MEM_RELEASE);
    puts("PASS all 6 emitted thunks preserve volatile GPRs, flags, all XMM, x87 and MXCSR under deliberate clobber");
}

static fake_worker *start_worker(LONG block_phase, LONG stop_after, BOOL freely, BOOL virtual_call)
{
    fake_worker *worker=(fake_worker *)calloc(1,sizeof(*worker)); CHECK(worker);
    worker->block_phase=block_phase; worker->stop_after=stop_after;
    worker->started=CreateEventW(NULL,TRUE,FALSE,NULL); worker->wake=CreateEventW(NULL,FALSE,TRUE,NULL);
    worker->held=CreateEventW(NULL,TRUE,FALSE,NULL); worker->release=CreateEventW(NULL,TRUE,FALSE,NULL);
    CHECK(worker->started && worker->wake && worker->held && worker->release);
    pointer_at(worker->system,(uintptr_t)worker); pointer_at(worker->system+38800,(uintptr_t)system_callback);
    pointer_at(worker->thread+328,(uintptr_t)worker->system); pointer_at(worker->thread+288,(uintptr_t)worker);
    pointer_at(worker->thread+312,(uintptr_t)worker->started); pointer_at(worker->thread+304,freely?0:(uintptr_t)worker->wake);
    pointer_at(worker->thread+320,virtual_call?0:(uintptr_t)(image+0x1143ee0u));
    worker->vtable[0]=(uintptr_t)virtual_callback; pointer_at(worker->thread,(uintptr_t)worker->vtable);
    worker->thread[336]=1;
    worker->handle=CreateThread(NULL,0,(LPTHREAD_START_ROUTINE)g_audio_relay,worker->thread,0,NULL); CHECK(worker->handle);
    return worker;
}
static void finish_worker(fake_worker *worker)
{
    worker->thread[280]=0; SetEvent(worker->wake); SetEvent(worker->release);
    CHECK(WaitForSingleObject(worker->handle,3000)==WAIT_OBJECT_0);
    CloseHandle(worker->handle); CloseHandle(worker->started); CloseHandle(worker->wake);
    CloseHandle(worker->held); CloseHandle(worker->release); free(worker);
    CHECK(!harness_error);
}
static DWORD WINAPI release_later(void *event) { Sleep(40); SetEvent((HANDLE)event); return 0; }
static void drain_regions(void)
{
    const LONG phases[]={8,11,12,9};
    for(DWORD i=0;i<4;++i) {
        fake_worker *worker=start_worker(phases[i],1,FALSE,FALSE);
        CHECK(WaitForSingleObject(worker->held,2000)==WAIT_OBJECT_0);
        CHECK(HktasClockBridge_GetResidentAudioFenceInFlight()>0);
        hktas_audio_status active=status_now(); CHECK(active.state==1 && active.worker_count==1 && active.fault==0 && active.capability==1);
        HANDLE releaser=CreateThread(NULL,0,release_later,worker->release,0,NULL); CHECK(releaser);
        uint64_t started=GetTickCount64()-100, token=++next_token;
        CHECK(HktasClockBridge_BeginResidentAudioFence(token,started)==1);
        CHECK(hktas_audio_atomic(&g_audio_started)==started && HktasClockBridge_RequireResidentAudioFence(token)==1);
        CHECK(HktasClockBridge_GetResidentAudioFenceInFlight()==0);
        hktas_audio_status closed=status_now(); CHECK(closed.state==HKTAS_AUDIO_CLOSED && closed.epoch==token && closed.started==started);
        CHECK(closed.observed>=started && closed.observed-started<9000 && closed.capability==1 && closed.abi_valid==1);
        hktas_audio_worker_row rows[HKTAS_AUDIO_MAX_WORKERS];
        LONG count=HktasClockBridge_CopyResidentAudioWorkers(token,rows,HKTAS_AUDIO_MAX_WORKERS);
        CHECK(count==(phases[i]==9?0:1));
        CHECK(closed.worker_count==(uint32_t)count);
        if(count) CHECK(rows[0].thread==(uintptr_t)worker->thread && rows[0].kind==1 && rows[0].depth==0);
        LONG calls=worker->calls, shutdowns=worker->events[1]; Sleep(20);
        CHECK(worker->calls==calls && worker->events[1]==shutdowns);
        CHECK(HktasClockBridge_EndResidentAudioFence(token,1)==1);
        CHECK(HktasClockBridge_RequireResidentAudioFence(token)==0);
        CHECK(WaitForSingleObject(releaser,2000)==WAIT_OBJECT_0); CloseHandle(releaser);
        CHECK(WaitForSingleObject(worker->handle,2000)==WAIT_OBJECT_0);
        CHECK(worker->calls==1 && worker->events[0]==1 && worker->events[1]==1 && worker->events[2]==1 && worker->events[3]==1);
        finish_worker(worker);
    }
    puts("PASS actual cloned startup, iteration callback tail, sleep and shutdown drain before closure");
}
static void pending_and_stress(void)
{
    uint64_t token=++next_token; CHECK(HktasClockBridge_BeginResidentAudioFence(token,GetTickCount64())==1);
    fake_worker *pending=start_worker(0,1,FALSE,FALSE); DWORD start=GetTickCount();
    while(!InterlockedCompareExchange(&g_audio_pending_startups,0,0) && GetTickCount()-start<2000) Sleep(1);
    CHECK(HktasClockBridge_GetResidentAudioFenceCoverageGaps()&HKTAS_AUDIO_GAP_PENDING_STARTUP);
    hktas_audio_status waiting=status_now();
    CHECK(waiting.epoch==token && waiting.state==HKTAS_AUDIO_CLOSED && waiting.pending_startups==1 && waiting.worker_count==0);
    CHECK((waiting.coverage&HKTAS_AUDIO_GAP_PENDING_STARTUP) && !waiting.fault);
    hktas_audio_worker_row rows[HKTAS_AUDIO_MAX_WORKERS];
    CHECK(HktasClockBridge_CopyResidentAudioWorkers(token,rows,HKTAS_AUDIO_MAX_WORKERS)==0);
    CHECK(pending->events[0]==0 && HktasClockBridge_GetResidentAudioFenceInFlight()==0);
    CHECK(HktasClockBridge_EndResidentAudioFence(token,1)==1);
    CHECK(WaitForSingleObject(pending->handle,2000)==WAIT_OBJECT_0); finish_worker(pending);
    fake_worker *workers[8];
    for(DWORD i=0;i<8;++i) workers[i]=start_worker(0,0,TRUE,FALSE);
    for(DWORD i=0;i<8;++i) CHECK(WaitForSingleObject(workers[i]->started,2000)==WAIT_OBJECT_0);
    for(DWORD pass=0;pass<30;++pass) {
        token=++next_token; CHECK(HktasClockBridge_BeginResidentAudioFence(token,GetTickCount64())==1);
        CHECK(HktasClockBridge_CopyResidentAudioWorkers(token,rows,HKTAS_AUDIO_MAX_WORKERS)==8);
        LONG calls[8]; for(DWORD i=0;i<8;++i) calls[i]=workers[i]->calls;
        Sleep(2);
        for(DWORD i=0;i<8;++i) CHECK(calls[i]==workers[i]->calls && rows[i].depth==0);
        CHECK(HktasClockBridge_EndResidentAudioFence(token,1)==1);
    }
    for(DWORD i=0;i<8;++i) finish_worker(workers[i]);
    CHECK(HktasClockBridge_GetResidentAudioFenceInFlight()==0);
    fake_worker *unknown=start_worker(0,1,FALSE,TRUE);
    CHECK(WaitForSingleObject(unknown->handle,2000)==WAIT_OBJECT_0); finish_worker(unknown);
    CHECK(HktasClockBridge_GetResidentAudioFenceCoverageGaps()&HKTAS_AUDIO_GAP_UNKNOWN_CALLBACK);
    CHECK(HktasClockBridge_GetResidentAudioFenceCoverageGaps()&HKTAS_AUDIO_REQUIRED_GAP_NON_THREAD_WORKERS);
    puts("PASS startup blocked before publication, 8 workers / 30 epochs, virtual callback retained as coverage gap");
}

static void reset_fault_fixture(void)
{
    /* Test isolation only; production has no reset path. No worker is alive. */
    CHECK(!(hktas_audio_atomic(&g_audio_state)&HKTAS_AUDIO_COUNT_MASK));
    InterlockedExchange64(&g_audio_state,0); InterlockedExchange(&g_audio_fault_reason,0);
    InterlockedExchange(&harness_faults,0); SetEvent(g_audio_open_event); SetEvent(g_audio_drained_event);
}
static void faults(void)
{
    CHECK(!harness_error && !harness_faults);
    CHECK(HktasClockBridge_BeginResidentAudioFence(next_token,GetTickCount64())==0);
    CHECK(harness_faults==1 && HktasClockBridge_GetResidentAudioFenceCapability()==0);
    CHECK(HktasClockBridge_BeginResidentAudioFence(++next_token,GetTickCount64())==0 && harness_faults==1);
    reset_fault_fixture();
    CHECK(HktasClockBridge_BeginResidentAudioFence(++next_token,GetTickCount64()-9000)==0 && harness_faults==1);
    reset_fault_fixture();
    uint64_t token=++next_token; CHECK(HktasClockBridge_BeginResidentAudioFence(token,GetTickCount64())==1);
    InterlockedExchange64(&g_audio_started,(LONG64)(GetTickCount64()-9000));
    hktas_audio_status timed_out=status_now();
    CHECK(timed_out.state==HKTAS_AUDIO_CLOSED && timed_out.observed-timed_out.started>=9000 && !timed_out.fault && !harness_faults);
    CHECK(HktasClockBridge_RequireResidentAudioFence(token)==0 && harness_faults==1);
    CHECK(HktasClockBridge_EndResidentAudioFence(token,1)==0 && (hktas_audio_atomic(&g_audio_state)&HKTAS_AUDIO_FAILED));
    reset_fault_fixture();
    token=++next_token; CHECK(HktasClockBridge_BeginResidentAudioFence(token,GetTickCount64())==1);
    CHECK(HktasClockBridge_EndResidentAudioFence(token,0)==0 && harness_faults==1);
    reset_fault_fixture();
    token=++next_token; CHECK(HktasClockBridge_BeginResidentAudioFence(token,GetTickCount64())==1);
    DWORD old,ignored; CHECK(VirtualProtect(g_audio_relay,HKTAS_AUDIO_PAGE_SIZE,PAGE_READWRITE,&old));
    g_audio_relay[100]^=1; CHECK(VirtualProtect(g_audio_relay,HKTAS_AUDIO_PAGE_SIZE,old,&ignored));
    hktas_audio_status drift=status_now();
    CHECK(drift.epoch==token && drift.capability==1 && drift.abi_valid==0 && !drift.fault && !harness_faults);
    CHECK(HktasClockBridge_RequireResidentAudioFence(token)==0 && harness_faults==1);
    hktas_audio_status faulted=status_now();
    CHECK(faulted.state==(HKTAS_AUDIO_CLOSED|HKTAS_AUDIO_FAILED) && faulted.fault==36 && faulted.capability==0 && faulted.abi_valid==0 && harness_faults==1);
    puts("PASS stale token, shared wall-clock deadline, abort and relay drift permanently fault and stay closed");
}

int main(void)
{
    make_image(); status_buffers(); relocation_and_unwind(); register_preservation(); drain_regions(); pending_and_stress(); faults();
    puts("PASS ResidentAudioFence offline harness; complete audio state and real-game qualification remain absent");
    return 0;
}
