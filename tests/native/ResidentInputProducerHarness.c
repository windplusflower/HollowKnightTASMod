/* Reuse the private fixed-PE loader and the actual unified installer. No game
 * module is loaded. Only main-thread classification and scripting callbacks
 * are substituted; both installed input entries run their original PE body. */
#define wmain hktas_input_reused_director_fixture
#include "ResidentDirectorProducerHarness.c"
#undef wmain

static DECLSPEC_ALIGN(16) BYTE input_state[424],input_main[65536];
static BYTE input_event[20],nested_event[20];
static volatile LONG input_latch,input_nested,input_worker_started,input_worker_finished;
static unsigned before_kind,update_kind,update_count,update_bytes;
static uint64_t input_started;
static unsigned char queue_event(BYTE *event)
{ return ((unsigned char(__cdecl *)(const void*))(void*)(image+0x292a30))(event); }
static void send_events(int kind)
{ ((void(__cdecl *)(int))(void*)(image+0x293690))(kind); }
static void event_init(BYTE *event)
{ memset(event,0,20);u32(event,0x54455854u);event[4]=20; }
static unsigned char __cdecl fixture_main_thread(void)
{
    if(InterlockedCompareExchange(&input_latch,0,0)) {
        SetEvent(entered);CHECK(WaitForSingleObject(resume_call,4000)==WAIT_OBJECT_0);
    }
    if(InterlockedCompareExchange(&input_nested,0,1)==1) CHECK(queue_event(nested_event)==1);
    return 1; /* exercise the genuine main dynamic-array path on fixture workers */
}
static void __cdecl before_update(int kind,void *exception)
{
    CHECK(!exception);before_kind=(unsigned)kind;
    if(InterlockedCompareExchange(&input_nested,0,2)==2) CHECK(queue_event(nested_event)==1);
}
static void __cdecl update(int kind,BYTE *buffer,void *exception)
{
    CHECK(!exception);update_kind=(unsigned)kind;
    update_count=hktas_dn_u32(buffer+8);update_bytes=hktas_dn_u32(buffer+12);
    CHECK(hktas_dn_ptr(buffer)==(uintptr_t)input_main || !update_count);
    u32(buffer+8,0);u32(buffer+12,0); /* controlled scripting consumer */
}
static void receipt(uint64_t token,BYTE *out)
{
    memset(out,0xa5,96);
    CHECK(HktasClockBridge_CopyResidentInputFenceStatus(token,out,96)==96);
    CHECK(hktas_dn_u32(out)==0x31464952 && hktas_dn_u32(out+4)==1
        && hktas_dn_u32(out+8)==96 && hktas_dn_u32(out+12)==3);
    CHECK(hktas_dn_u64(out+16)==token && hktas_dn_u64(out+24)==input_started
        && hktas_dn_ptr(out+32)==(uintptr_t)input_state && hktas_dn_ptr(out+40)==(uintptr_t)image);
    CHECK(hktas_dn_u32(out+88)==GetCurrentThreadId() && hktas_dn_u32(out+92)==GetCurrentThreadId());
    CHECK(hktas_dn_u64(out+48)==hktas_dn_u64(out+56)
        && hktas_dn_u64(out+72)==hktas_dn_u64(out+80));
}
static void denied(uint64_t token,unsigned length)
{
    BYTE out[112],before[112];memset(out,0xa5,sizeof(out));memcpy(before,out,sizeof(out));
    CHECK(!HktasClockBridge_CopyResidentInputFenceStatus(token,out,length));
    CHECK(!memcmp(before,out,sizeof(out)));
}
static DWORD WINAPI wrong_thread(void *unused)
{ (void)unused;denied(1,96);return 0; }
static DWORD WINAPI input_worker(void *unused)
{
    (void)unused;SetEvent(entered);InterlockedIncrement(&input_worker_started);
    CHECK(queue_event(input_event)==1);InterlockedIncrement(&input_worker_finished);return 0;
}
static DWORD WINAPI send_worker(void *unused)
{
    (void)unused;SetEvent(entered);send_events(11);
    InterlockedIncrement(&input_worker_finished);return 0;
}
static DWORD WINAPI release_draining(void *unused)
{
    (void)unused;uint64_t end=GetTickCount64()+3000;
    while(!status().closed) {CHECK(GetTickCount64()<end);Sleep(1);}
    CHECK(status().active && !status().held);SetEvent(resume_call);return 0;
}
static void begin_input(uint64_t token)
{ input_started=GetTickCount64();CHECK(HktasClockBridge_BeginResidentDirectorFence(token,input_started)); }
int wmain(int argc,wchar_t **argv)
{
    CHECK(argc==3);setup(argv[1]);
    pointer(input_state+56,input_main);
    u32(input_state+64,9);uint64_t size=sizeof(input_main),tagged=size*2+1;
    memcpy(input_state+72,&size,8);memcpy(input_state+80,&tagged,8);
    event_init(input_event);event_init(nested_event);
    jump(image+0x81c590,(void*)fixture_main_thread);
    jump(image+0xa56a0,(void*)before_update);jump(image+0xa5810,(void*)update);
    CHECK(FlushInstructionCache(GetCurrentProcess(),image,HKTAS_DN_IMAGE));install();
    pointer(image+0x194e800,input_state);
    if(!wcscmp(argv[2],L"prefix-drift") || !wcscmp(argv[2],L"owner-drift")) {
        begin_input(1);BYTE out[96];receipt(1,out);
        if(!wcscmp(argv[2],L"prefix-drift")) image[0x292a31]^=1;
        else pointer(image+0x194e800,input_state+8);
        denied(1,96);puts("PASS input prefix/owner drift refuses untouched receipt");return 0;
    }
    CHECK(!wcscmp(argv[2],L"normal"));
    BYTE invalid[20]={0};invalid[4]=4;CHECK(queue_event(invalid)==0);
    input_nested=1;CHECK(queue_event(input_event)==1);
    CHECK(hktas_dn_u32(input_state+92)==2 && hktas_dn_u32(input_state+88)==40);
    CHECK(hktas_dn_u32(input_main+16)==hktas_dn_u32(nested_event+16));
    input_nested=2;send_events(7);
    CHECK(before_kind==7 && update_kind==7 && update_count==3 && update_bytes==60
        && !hktas_dn_u32(input_state+92) && !hktas_dn_u32(input_state+88));
    begin_input(1);BYTE first[96],second[96];receipt(1,first);
    CHECK(hktas_dn_u64(first+48)==4 && hktas_dn_u64(first+64)==3 && hktas_dn_u64(first+72)==1);
    denied(0,96);denied(2,96);denied(1,95);denied(1,97);check_thread(CreateThread(NULL,0,wrong_thread,NULL,0,NULL));
    receipt(1,second);CHECK(!memcmp(first,second,96));CHECK(HktasClockBridge_EndResidentDirectorFence(1,1));
    /* A worker already inside the real Queue body must finish before hold. */
    ResetEvent(entered);ResetEvent(resume_call);input_latch=1;
    HANDLE worker=CreateThread(NULL,0,input_worker,NULL,0,NULL);CHECK(worker && WaitForSingleObject(entered,3000)==WAIT_OBJECT_0);
    /* entered is published both before entry and from the dependency latch. */
    uint64_t until=GetTickCount64()+3000;while(!status().active) {CHECK(GetTickCount64()<until);Sleep(1);}
    HANDLE helper=CreateThread(NULL,0,release_draining,NULL,0,NULL);CHECK(helper);begin_input(2);
    check_thread(worker);check_thread(helper);CHECK(input_worker_finished==1);input_latch=0;
    receipt(2,first);CHECK(HktasClockBridge_EndResidentDirectorFence(2,1));
    /* A newly arriving worker stays outside the original function while held. */
    begin_input(3);receipt(3,first);ResetEvent(entered);
    worker=CreateThread(NULL,0,input_worker,NULL,0,NULL);CHECK(worker && WaitForSingleObject(entered,3000)==WAIT_OBJECT_0);
    until=GetTickCount64()+3000;while(!status().waiting) {CHECK(GetTickCount64()<until);Sleep(1);}
    CHECK(input_worker_finished==1);receipt(3,second);CHECK(!memcmp(first,second,96));
    CHECK(HktasClockBridge_EndResidentDirectorFence(3,1));check_thread(worker);CHECK(input_worker_finished==2);
    begin_input(4);receipt(4,second);CHECK(hktas_dn_u64(second+48)==hktas_dn_u64(first+48)+1);
    memcpy(first,second,96);ResetEvent(entered);
    worker=CreateThread(NULL,0,send_worker,NULL,0,NULL);CHECK(worker && WaitForSingleObject(entered,3000)==WAIT_OBJECT_0);
    until=GetTickCount64()+3000;while(!status().waiting) {CHECK(GetTickCount64()<until);Sleep(1);}
    CHECK(input_worker_finished==2);receipt(4,second);CHECK(!memcmp(first,second,96));
    CHECK(HktasClockBridge_EndResidentDirectorFence(4,1));check_thread(worker);
    CHECK(input_worker_finished==3 && before_kind==11 && update_kind==11);
    begin_input(5);receipt(5,second);CHECK(hktas_dn_u64(second+72)==hktas_dn_u64(first+72)+1);
    CHECK(HktasClockBridge_EndResidentDirectorFence(5,1));
    puts("PASS actual typed Queue/Send entries, nested publication, pre-close drain, blocked/reopened worker, stable 96B receipt and rejected callers");return 0;
}
