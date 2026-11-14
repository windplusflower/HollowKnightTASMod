#define HKTAS_VC_TEST 1
#include "../../native/HollowKnightTAS.ClockBridge/resident_audio_voice_control.h"
#include <stdio.h>
#include <stdlib.h>
#define CHECK(x) do {if (!(x)) {fprintf(stderr,"FAIL %d %s\n",__LINE__,#x);exit(1);}} while(0)
static BYTE channel[672],other[672],real[120];
static uintptr_t group_opaque=0x12345678u,group_real=0x87654321u;
static int validations,volume_reads,fail_volume,loop_code,group_code,drift,recycle,validate_error,connection,reads;
static uint32_t mode;
static int system_reads,clock_reads,system_error,clock_error,owner_change,system_change,late_clock_error,delay_drift;
static uint64_t clocks[2];
static int __cdecl validate_system(uintptr_t opaque,uintptr_t *output)
{
    CHECK(opaque==0x55667788u);
    ++system_reads;
    if (system_error) return system_error;
    *output=(uintptr_t)(system_change && system_reads>1 ? 0x9999u : 0x8888u);
    return 0;
}
static int __cdecl get_clock(uintptr_t opaque,uint32_t *hi,uint32_t *lo)
{
    CHECK(opaque==0x55667788u && clock_reads<2);
    if (clock_error || (late_clock_error && clock_reads)) return clock_error ? clock_error : 0x36;
    if (delay_drift && clock_reads) {uint32_t changed=1;memcpy(channel+352,&changed,4);}
    uint64_t clock=clocks[clock_reads++];
    *hi=(uint32_t)(clock>>32);*lo=(uint32_t)clock;
    if (owner_change) {uintptr_t changed=0x9999u;memcpy(channel+128,&changed,8);}
    return 0;
}
static void put32(BYTE *body,unsigned offset,uint32_t value) {memcpy(body+offset,&value,4);}
static void putptr(BYTE *body,unsigned offset,uintptr_t value) {memcpy(body+offset,&value,8);}
static int __cdecl validate(uintptr_t opaque,uintptr_t *output)
{
    CHECK(opaque==0x11223344u);++validations;
    if (validate_error && validations>1) return validate_error;
    *output=(uintptr_t)(recycle && validations>1 ? other : channel);return 0;
}
static int __cdecl validate_group(uintptr_t opaque,uintptr_t *output)
{
    CHECK(opaque==group_opaque);
    if (group_code) return group_code;
    *output=group_real;return 0;
}
static int __cdecl getmode(uintptr_t pointer,void *output) {CHECK(pointer==(uintptr_t)channel);memcpy(output,&mode,4);return 0;}
static int __cdecl volume(uintptr_t pointer,void *output)
{
    CHECK(pointer==(uintptr_t)channel);uint32_t value=drift ? (uint32_t)++volume_reads : 0x3f000000u;
    memcpy(output,&value,4);return fail_volume;
}
static int __cdecl frequency(uintptr_t pointer,void *output) {CHECK(pointer==(uintptr_t)channel);uint32_t value=0x472c4400u;memcpy(output,&value,4);return 0;}
static int __cdecl paused(uintptr_t pointer,void *output) {CHECK(pointer==(uintptr_t)channel);*(unsigned char *)output=1;return 0;}
static int __cdecl reverb(uintptr_t pointer,void *output)
{
    CHECK(pointer==(uintptr_t)channel);hktas_vc_reverb value={-1000,-2000,7,0xdeadbeefu,connection?0xabcdef0123456789ull:0};memcpy(output,&value,sizeof(value));return 0;
}
static int loop(uintptr_t pointer,int32_t *output) {CHECK(pointer==(uintptr_t)real);*output=-1;return loop_code;}
static BOOL read_control(uintptr_t address,void *output,SIZE_T bytes)
{
    ++reads;CHECK(bytes<=32);return hktas_vc_read(address,output,bytes);
}
static hktas_vc_receipt capture(void)
{
    hktas_vc_api api={validate,validate_group,getmode,volume,frequency,paused,reverb,read_control,loop};
    hktas_vc_receipt value;
    CHECK(hktas_vc_collect(&api,0x11223344u,&group_opaque,1,&value,sizeof(value))==640);
    return value;
}
static void reset(void)
{
    memset(channel,0,sizeof(channel));memset(real,0,sizeof(real));
    validations=volume_reads=fail_volume=loop_code=group_code=drift=recycle=validate_error=connection=reads=0;mode=8;
    put32(channel,136,1);putptr(channel,144,(uintptr_t)real);putptr(channel,312,group_real);
    put32(channel,284,128);put32(channel,412,0x3f000000u);put32(real,96,2);put32(real,100,10);
    system_reads=clock_reads=system_error=clock_error=owner_change=system_change=late_clock_error=delay_drift=0;
    clocks[0]=100;clocks[1]=110;putptr(channel,128,0x8888u);
}
static hktas_vc_receipt_v2 capture_v2(void)
{
    hktas_vc_api api={validate,validate_group,getmode,volume,frequency,paused,reverb,read_control,loop};
    hktas_vc_receipt_v2 value;
    CHECK(hktas_vc_collect_v2(&api,validate_system,get_clock,0x11223344u,0x55667788u,
        &group_opaque,1,&value,sizeof(value))==664);
    return value;
}
int main(void)
{
    hktas_vc_receipt value;
    reset();value=capture();CHECK(value.valid==1 && value.magic==0x31435641u && value.field_count==15);
    CHECK(value.field[HKTAS_VC_GROUP].status==HKTAS_VC_VALID && value.field[HKTAS_VC_GROUP].value[0]==0);
    CHECK(value.field[HKTAS_VC_POSITION_VELOCITY].status==HKTAS_VC_NA);
    CHECK(value.field[HKTAS_VC_LOOP].value[0]==2 && value.field[HKTAS_VC_LOOP].value[1]==10 && value.field[HKTAS_VC_LOOP].value[2]==UINT32_MAX);
    CHECK(value.field[HKTAS_VC_REVERB].value[0]==(uint32_t)-1000 && value.field[HKTAS_VC_REVERB].value[2]==7 && value.field[HKTAS_VC_REVERB].value[4]==0);
    reset();mode=0x10;put32(channel,532,0x3f800000u);value=capture();CHECK(value.valid && value.field[HKTAS_VC_POSITION_VELOCITY].status==HKTAS_VC_VALID && value.field[HKTAS_VC_POSITION_VELOCITY].value[0]==0x3f800000u);
    reset();fail_volume=0x24;value=capture();CHECK(value.valid && value.field[HKTAS_VC_VOLUME].status==HKTAS_VC_ERROR && value.field[HKTAS_VC_VOLUME].error==0x24 && value.field[HKTAS_VC_VOLUME].value[0]==0);
    reset();drift=1;value=capture();CHECK(!value.valid && value.result==-4);
    reset();recycle=1;value=capture();CHECK(!value.valid && value.result==-4);
    reset();validate_error=0xb;value=capture();CHECK(!value.valid && value.result==0xb);
    reset();put32(channel,136,0);value=capture();CHECK(!value.valid && value.result==-3);
    reset();put32(channel,136,2);value=capture();CHECK(!value.valid && value.result==-3);
    reset();loop_code=0x36;value=capture();CHECK(value.valid && value.field[HKTAS_VC_LOOP].error==0x36 && value.field[HKTAS_VC_LOOP].status==HKTAS_VC_ERROR);
    reset();loop_code=-3;value=capture();CHECK(value.valid && value.field[HKTAS_VC_LOOP].status==HKTAS_VC_UNSUPPORTED);
    reset();connection=1;value=capture();CHECK(value.valid && value.field[HKTAS_VC_REVERB].status==HKTAS_VC_UNSUPPORTED && value.field[HKTAS_VC_REVERB].value[3]==1);
    reset();group_code=0x24;value=capture();CHECK(value.valid && value.field[HKTAS_VC_GROUP].error==0x24 && value.field[HKTAS_VC_GROUP].status==HKTAS_VC_ERROR);
    reset();putptr(channel,312,0x7777u);value=capture();CHECK(value.valid && value.field[HKTAS_VC_GROUP].status==HKTAS_VC_UNSUPPORTED);
    reset();mode=0;value=capture();CHECK(value.valid && value.field[HKTAS_VC_POSITION_VELOCITY].status==HKTAS_VC_UNSUPPORTED);
    hktas_vc_receipt_v2 v2;
    reset();put32(channel,360,90);v2=capture_v2();
    CHECK(v2.valid && v2.version==2 && v2.bytes==664 && v2.delay_class==1 && v2.clock_witness_flags==0xf);
    CHECK(v2.clock_first==100 && v2.clock_second==110 && v2.field[HKTAS_VC_DELAY].value[1]==90);
    reset();put32(channel,360,120);v2=capture_v2();CHECK(v2.valid && v2.delay_class==2 && v2.field[HKTAS_VC_DELAY].status==HKTAS_VC_UNSUPPORTED);
    reset();put32(channel,360,105);v2=capture_v2();CHECK(!v2.valid && v2.result==-7 && !v2.clock_witness_flags);
    reset();clocks[1]=99;v2=capture_v2();CHECK(!v2.valid && v2.result==-6);
    reset();owner_change=1;v2=capture_v2();CHECK(!v2.valid && v2.result==-5);
    reset();system_change=1;v2=capture_v2();CHECK(!v2.valid && v2.result==-5);
    reset();putptr(channel,128,0x9999u);v2=capture_v2();CHECK(!v2.valid && v2.result==-5 && !clock_reads);
    reset();system_error=0x24;v2=capture_v2();CHECK(!v2.valid && v2.result==0x24);
    reset();clock_error=0x36;v2=capture_v2();CHECK(!v2.valid && v2.result==0x36);
    reset();put32(channel,352,1);v2=capture_v2();CHECK(v2.valid && v2.delay_class==2);
    reset();put32(channel,368,1);v2=capture_v2();CHECK(v2.valid && v2.delay_class==2);
    reset();put32(channel,376,1);v2=capture_v2();CHECK(v2.valid && v2.delay_class==2);
    reset();put32(channel,276,0x200);v2=capture_v2();CHECK(v2.valid && v2.delay_class==2);
    reset();clocks[0]=clocks[1]=0x100000064ull;put32(channel,360,100);put32(channel,364,1);
    v2=capture_v2();CHECK(v2.valid && v2.delay_class==1 && v2.clock_first==0x100000064ull);
    reset();late_clock_error=1;v2=capture_v2();CHECK(!v2.valid && v2.result==0x36 && !v2.clock_witness_flags);
    reset();delay_drift=1;put32(channel,368,1);v2=capture_v2();CHECK(!v2.valid && v2.result==-4);
    reset();recycle=1;v2=capture_v2();CHECK(!v2.valid && v2.result==-3);
    reset();validate_error=0xb;v2=capture_v2();CHECK(!v2.valid && v2.result==0xb);
    reset();
    hktas_vc_api api={validate,validate_group,getmode,volume,frequency,paused,reverb,read_control,loop};
    memset(&v2,0xa5,sizeof(v2));
    CHECK(!hktas_vc_collect_v2(&api,validate_system,get_clock,0x11223344u,0x55667788u,
        &group_opaque,1,&v2,640) && v2.magic==0xa5a5a5a5u);
    CHECK(!hktas_vc_collect_v2(&api,validate_system,get_clock,0x11223344u,0,
        &group_opaque,1,&v2,sizeof(v2)) && v2.magic==0xa5a5a5a5u);
    puts("PASS AudioVoiceControl focused 14 v1 + 20 v2 scenarios; no UnityPlayer loaded");return 0;
}
