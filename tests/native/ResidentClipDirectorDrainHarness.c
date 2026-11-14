static void clip_joint_before_wait(void);
#define HKTAS_DN_TEST_BEFORE_WAIT() clip_joint_before_wait()
#define main hktas_unused_clock_main
#include "FullRunContinuationHarness.c"
#undef main
static HANDLE joint_ready,joint_release,joint_done;
static LONG joint_calls;
static int joint_case;
static BOOL lost_wake_injected;
static BYTE joint_clip[16];
static void __cdecl joint_original(void *self)
{
    if(self!=joint_clip) ExitProcess(101);
    InterlockedIncrement(&joint_calls);SetEvent(joint_ready);
    if(WaitForSingleObject(joint_release,2000)!=WAIT_OBJECT_0) ExitProcess(102);
    if(joint_case==1) {
        hktas_dn_enter(DN_manager_ResetFrameTiming);
        hktas_dn_leave(DN_manager_ResetFrameTiming);
    }
}
static DWORD WINAPI joint_worker(void *unused)
{
    (void)unused;
    if(joint_case==0 || joint_case==4) {
        hktas_dn_enter(DN_manager_ResetFrameTiming);
        if(joint_case==4) {
            AcquireSRWLockExclusive(&g_dn_lock);
            ((hktas_dn_thread *)FlsGetValue(g_dn_fls))->counted_epoch=UINT64_MAX;
            ReleaseSRWLockExclusive(&g_dn_lock);
        }
        SetEvent(joint_ready);
        if(WaitForSingleObject(joint_release,2000)!=WAIT_OBJECT_0) ExitProcess(103);
    }
    hktas_cl_clear(joint_clip);
    if(joint_case==0 || joint_case==4) hktas_dn_leave(DN_manager_ResetFrameTiming);
    SetEvent(joint_done);return 0;
}
static DWORD WINAPI joint_release_when_closed(void *unused)
{
    (void)unused;ULONGLONG started=GetTickCount64();
    for(;;) {
        AcquireSRWLockShared(&g_dn_lock);BOOL closed=g_dn_closed;ReleaseSRWLockShared(&g_dn_lock);
        if(closed) break;
        if(GetTickCount64()-started>2000) ExitProcess(104);
        Sleep(1);
    }
    if(joint_case==4) g_resident_wall_limit=30;
    SetEvent(joint_release);return 0;
}
static void clip_joint_before_wait(void)
{
    if(joint_case!=2 || lost_wake_injected) return;
    lost_wake_injected=TRUE;SetEvent(joint_release);
    /* Complete Clip and signal DNcv while the waiter still owns DN lock,
     * before it calls SleepConditionVariableSRW: deliberately lose the wake. */
    if(WaitForSingleObject(joint_done,2000)!=WAIT_OBJECT_0) ExitProcess(105);
}
int main(int argc,char **argv)
{
    CHECK(argc==3);particle_image_file=argv[1];joint_case=atoi(argv[2]);
    initialize();rate(0,0);g_v2_boundary_callback_phase=2;
    uintptr_t vt=(uintptr_t)(particle_image+0x171d808u);int32_t id=117;
    memcpy(joint_clip,&vt,8);memcpy(joint_clip+8,&id,4);g_cl_original[2]=(void *)joint_original;
    joint_ready=CreateEventW(NULL,TRUE,FALSE,NULL);joint_release=CreateEventW(NULL,TRUE,FALSE,NULL);joint_done=CreateEventW(NULL,TRUE,FALSE,NULL);
    CHECK(joint_ready&&joint_release&&joint_done);
    if(joint_case==3) {
        uint64_t token=HktasClockBridge_BeginResidentBoundary(0);CHECK(token);
        HANDLE worker=CreateThread(NULL,0,joint_worker,NULL,0,NULL);CHECK(worker);Sleep(20);
        hktas_cl_receipt receipt;CHECK(joint_calls==0 && HktasClockBridge_QueryResidentClipCurveEligibility(token,(uintptr_t)joint_clip,id,(BYTE *)&receipt,64)==1);
        CHECK(HktasClockBridge_RequireResidentBoundary(token,0));CHECK(HktasClockBridge_EndResidentBoundary(token,1));
        SetEvent(joint_release);CHECK(WaitForSingleObject(worker,2000)==WAIT_OBJECT_0);
        CHECK(joint_calls==1);printf("PASS joint held/external writer last-race isolation\n");return 0;
    }
    HANDLE worker=CreateThread(NULL,0,joint_worker,NULL,0,NULL);CHECK(worker);
    CHECK(WaitForSingleObject(joint_ready,2000)==WAIT_OBJECT_0);
    HANDLE release=NULL;
    if(joint_case!=2) {release=CreateThread(NULL,0,joint_release_when_closed,NULL,0,NULL);CHECK(release);}
    uint64_t started=GetTickCount64();uint64_t token=HktasClockBridge_BeginResidentBoundary(0);
    if(joint_case==4) {
        CHECK(!token && !joint_calls && g_cl_closed && g_dn_closed && g_resident_boundary_failed);
        printf("PASS stale Director epoch cannot inherit Clip draining\n");return 0;
    }
    CHECK(token && !g_cl_active && !g_dn_active && g_cl_held && g_dn_held);
    CHECK(WaitForSingleObject(worker,2000)==WAIT_OBJECT_0);
    if(release) CHECK(WaitForSingleObject(release,2000)==WAIT_OBJECT_0);
    if(joint_case==2) CHECK(lost_wake_injected && GetTickCount64()-started<500);
    CHECK(HktasClockBridge_EndResidentBoundary(token,1));
    printf("PASS joint direction/lost-wake case=%d calls=%ld\n",joint_case,joint_calls);return 0;
}
