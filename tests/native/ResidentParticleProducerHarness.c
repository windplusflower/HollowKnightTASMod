/* Real pinned UnityPlayer.dll is mapped as inert file data. No game or Mono
 * module is loaded or run. Test the actual registration slots and exported
 * gate using deterministic substitutes for the five original engine callees.
 * Run each named scenario in a fresh process (faults are permanent). */
#include "../../native/HollowKnightTAS.ClockBridge/resident_particle_producer.h"
#include "ResidentParticleFenceTestImage.h"
#include <stdio.h>
#include <stdlib.h>
#define CHECK(x) do { if (!(x)) { fprintf(stderr,"FAIL %d: %s\n",__LINE__,#x); exit(1); } } while (0)
typedef struct { BYTE padding[112]; hktas_ps_handle fence; BYTE data[24]; } fake_owner;
typedef char owner_offset_check[(offsetof(fake_owner,data)==128)?1:-1];
static fake_owner owners[2];
static hktas_ps_handle scheduled;
static LONG sink_fault, bad_readback, schedules, publishes;
static BYTE *fixture;
static HANDLE entered, ready, released;
static void __cdecl sink(LONG family) { CHECK(family==67); InterlockedIncrement(&sink_fault); }
static void __cdecl original_get(void *owner, hktas_ps_handle *out) { *out=((fake_owner *)owner)->fence; }
static void *__cdecl original_data(void *owner) { return ((fake_owner *)owner)->data; }
static void __cdecl original_schedule(void *p, void *d, hktas_ps_handle *out)
{
    CHECK(p && d && out); out->group=(uintptr_t)p; out->version=(uint32_t)InterlockedIncrement(&schedules); out->padding=0;
}
static void __cdecl original_defer(void *p, int batch, void *d, void *safety, hktas_ps_handle *out)
{ CHECK(batch==3 && !safety); original_schedule(p,d,out); }
static void __cdecl original_set(void *owner, const hktas_ps_handle *input)
{ if (!bad_readback) ((fake_owner *)owner)->fence=*input; InterlockedIncrement(&publishes); }
static void *registration(int i) { void *fn; memcpy(&fn,fixture+g_ps_bindings[i].slot,8); return fn; }
static void start_job(fake_owner *owner, BOOL parallel)
{
    hktas_ps_handle old={0}; ((hktas_ps_get_handle_fn)registration(0))(owner,&old);
    void *data=((hktas_ps_get_data_fn)registration(1))(owner);
    if (parallel) ((hktas_ps_defer_fn)registration(3))(owner,3,data,NULL,&scheduled);
    else ((hktas_ps_schedule_fn)registration(2))(owner,data,&scheduled);
}
static void publish(fake_owner *owner) { ((hktas_ps_set_fn)registration(4))(owner,&scheduled); }
static hktas_ps_status status(void)
{
    hktas_ps_status s={0}; CHECK(HktasClockBridge_CopyResidentParticleFenceStatus((BYTE *)&s,sizeof(s))==64);
    CHECK(s.magic==0x31465052 && s.bytes==64 && s.version==1 && s.installed==1 && s.abi_valid==1); return s;
}
static DWORD WINAPI worker(LPVOID mode)
{
    SetEvent(entered); start_job(&owners[1],TRUE); SetEvent(ready);
    if ((intptr_t)mode==1) { CHECK(WaitForSingleObject(released,2000)==WAIT_OBJECT_0); publish(&owners[1]); }
    else if ((intptr_t)mode==0) publish(&owners[1]);
    return 0;
}
static DWORD WINAPI release_when_closed(LPVOID unused)
{
    (void)unused; ULONGLONG start=GetTickCount64();
    while (!status().closed) { CHECK(GetTickCount64()-start<2000); Sleep(1); }
    SetEvent(released); return 0;
}
static void done(HANDLE thread)
{ CHECK(WaitForSingleObject(thread,3000)==WAIT_OBJECT_0); DWORD rc; CHECK(GetExitCodeThread(thread,&rc)&&rc==0); CloseHandle(thread); }
int main(int argc,char **argv)
{
    CHECK(argc==3); const char *scenario=argv[2]; fixture=hktas_ps_test_image_from_file(argv[1]); DWORD main_thread=GetCurrentThreadId();
    if (!strcmp(scenario,"tamper")) {
        fixture[g_ps_bindings[2].code+61]^=1; CHECK(!install_resident_particle_producer(fixture,main_thread,sink));
        for (int i=0;i<5;++i) CHECK(registration(i)==fixture+g_ps_bindings[i].code);
        puts("PASS tamper"); return 0;
    }
    CHECK(install_resident_particle_producer(fixture,main_thread,sink));
    for (int i=0;i<2;++i) { void *type=fixture+0x15e0148u; memcpy(&owners[i],&type,8); }
    CHECK(!install_resident_particle_producer(fixture,main_thread,sink)); /* single installation for process lifetime */
    for (int i=0;i<5;++i) CHECK(registration(i)==g_ps_replacements[i]);
    g_ps_original[0]=(void *)original_get; g_ps_original[1]=(void *)original_data; g_ps_original[2]=(void *)original_schedule;
    g_ps_original[3]=(void *)original_defer; g_ps_original[4]=(void *)original_set;
    entered=CreateEventW(NULL,TRUE,FALSE,NULL); ready=CreateEventW(NULL,TRUE,FALSE,NULL); released=CreateEventW(NULL,TRUE,FALSE,NULL);
    CHECK(entered && ready && released);
    if (!strcmp(scenario,"normal")) {
        start_job(&owners[0],FALSE); CHECK(status().writers==1); publish(&owners[0]); CHECK(status().writers==0 && status().completed==1);
        CHECK(HktasClockBridge_BeginResidentParticleFence(1,GetTickCount64())); CHECK(HktasClockBridge_RequireResidentParticleFence(1));
        HANDLE thread=CreateThread(NULL,0,worker,0,0,NULL); CHECK(thread && WaitForSingleObject(entered,1000)==WAIT_OBJECT_0);
        CHECK(WaitForSingleObject(ready,30)==WAIT_TIMEOUT && !status().writers);
        /* An unrelated generic deferred job is still the original engine call. */
        hktas_ps_handle other; ((hktas_ps_defer_fn)registration(3))(&other,3,&other,NULL,&other);
        CHECK(HktasClockBridge_EndResidentParticleFence(1,1)); done(thread);
        CHECK(status().completed==2 && !status().writers && !status().closed && !sink_fault && schedules==3 && publishes==2);
        CHECK(HktasClockBridge_BeginResidentParticleFence(2,GetTickCount64())); CHECK(HktasClockBridge_EndResidentParticleFence(2,1));
    } else if (!strcmp(scenario,"drain")) {
        HANDLE thread=CreateThread(NULL,0,worker,(LPVOID)(intptr_t)1,0,NULL); CHECK(thread && WaitForSingleObject(ready,1000)==WAIT_OBJECT_0);
        CHECK(status().writers==1);
        HANDLE wake=CreateThread(NULL,0,release_when_closed,NULL,0,NULL); CHECK(wake);
        CHECK(HktasClockBridge_BeginResidentParticleFence(1,GetTickCount64())); CHECK(status().completed==1 && !status().writers);
        CHECK(HktasClockBridge_EndResidentParticleFence(1,1)); done(wake); done(thread);
    } else {
        if (!strcmp(scenario,"orphan-main")) { start_job(&owners[0],FALSE); CHECK(!HktasClockBridge_BeginResidentParticleFence(1,GetTickCount64())); }
        else if (!strcmp(scenario,"orphan-worker")) {
            HANDLE thread=CreateThread(NULL,0,worker,(LPVOID)(intptr_t)2,0,NULL); CHECK(thread); done(thread);
            CHECK(!HktasClockBridge_BeginResidentParticleFence(1,GetTickCount64()-8980u)); CHECK(status().writers==1);
        } else if (!strcmp(scenario,"wrong-owner")) { start_job(&owners[0],TRUE); publish(&owners[1]); }
        else if (!strcmp(scenario,"wrong-handle")) { start_job(&owners[0],FALSE); ++scheduled.version; publish(&owners[0]); }
        else if (!strcmp(scenario,"wrong-readback")) { start_job(&owners[0],FALSE); bad_readback=1; publish(&owners[0]); }
        else if (!strcmp(scenario,"escaped")) {
            start_job(&owners[0],TRUE); publish(&owners[0]); ((hktas_ps_defer_fn)registration(3))(&owners[0],3,owners[0].data,NULL,&scheduled);
        } else if (!strcmp(scenario,"unpaired-set")) { publish(&owners[0]); }
        else if (!strcmp(scenario,"drift")) {
            CHECK(HktasClockBridge_BeginResidentParticleFence(1,GetTickCount64()));
            fixture[g_ps_bindings[3].code+31]^=1;
            CHECK(!HktasClockBridge_RequireResidentParticleFence(1));
            fixture[g_ps_bindings[3].code+31]^=1; CHECK(!HktasClockBridge_RequireResidentParticleFence(1));
        } else if (!strcmp(scenario,"abort")) {
            CHECK(HktasClockBridge_BeginResidentParticleFence(1,GetTickCount64())); CHECK(!HktasClockBridge_EndResidentParticleFence(1,0));
        } else if (!strcmp(scenario,"stale")) {
            CHECK(HktasClockBridge_BeginResidentParticleFence(2,GetTickCount64())); CHECK(HktasClockBridge_EndResidentParticleFence(2,1));
            CHECK(!HktasClockBridge_BeginResidentParticleFence(2,GetTickCount64()));
        } else CHECK(0);
        CHECK(status().fault && sink_fault==1); CHECK(!HktasClockBridge_RequireResidentParticleFence(1));
        CHECK(!HktasClockBridge_BeginResidentParticleFence(3,GetTickCount64()));
    }
    printf("PASS %s\n",scenario); return 0;
}
