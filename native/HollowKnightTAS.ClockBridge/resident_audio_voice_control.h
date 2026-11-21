#ifndef HKTAS_RESIDENT_AUDIO_VOICE_CONTROL_H
#define HKTAS_RESIDENT_AUDIO_VOICE_CONTROL_H
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>

/* Read-only control API receipt. No opaque handle casts, setters, retained
 * ChannelI pointers, DSP/filter histories or claims of mixer quiescence. */
enum { HKTAS_VC_FIELDS=15, HKTAS_VC_BYTES=640 };
enum { HKTAS_VC_UNAVAILABLE=0,HKTAS_VC_VALID=1,HKTAS_VC_NA=2,HKTAS_VC_UNSUPPORTED=3,HKTAS_VC_ERROR=4 };
enum { HKTAS_VC_MODE,HKTAS_VC_VOLUME,HKTAS_VC_FREQUENCY,HKTAS_VC_PAUSED,HKTAS_VC_REVERB,
    HKTAS_VC_PAN,HKTAS_VC_PRIORITY,HKTAS_VC_MUTE,HKTAS_VC_POSITION_VELOCITY,HKTAS_VC_DISTANCE,
    HKTAS_VC_CONE,HKTAS_VC_SPREAD_PAN,HKTAS_VC_DELAY,HKTAS_VC_LOOP,HKTAS_VC_GROUP };
typedef struct { int32_t status,error; uint32_t value[8]; } hktas_vc_field;
typedef struct {
    uint32_t magic,version,bytes,valid;
    int32_t result;
    uint32_t mode,real_count,field_count,reserved[2];
    hktas_vc_field field[HKTAS_VC_FIELDS];
} hktas_vc_receipt;
typedef char hktas_vc_receipt_size[(sizeof(hktas_vc_receipt)==640 && offsetof(hktas_vc_receipt,field)==40)?1:-1];
typedef struct {
    uint32_t magic,version,bytes,valid;
    int32_t result;
    uint32_t mode,real_count,field_count,delay_class,clock_witness_flags;
    uint64_t clock_first,clock_second;
    uint32_t reserved[2];
    hktas_vc_field field[HKTAS_VC_FIELDS];
} hktas_vc_receipt_v2;
typedef char hktas_vc_receipt_v2_size[(sizeof(hktas_vc_receipt_v2)==664 && offsetof(hktas_vc_receipt_v2,field)==64)?1:-1];
typedef int (__cdecl *hktas_vc_clock_fn)(uintptr_t,uint32_t *,uint32_t *);
typedef struct { int32_t direct,room; uint32_t flags,padding; uintptr_t connection; } hktas_vc_reverb;
typedef int (__cdecl *hktas_vc_validate_fn)(uintptr_t,uintptr_t *);
typedef int (__cdecl *hktas_vc_get_fn)(uintptr_t,void *);
typedef BOOL (*hktas_vc_read_fn)(uintptr_t,void *,SIZE_T);
typedef int (*hktas_vc_loop_fn)(uintptr_t,int32_t *);
typedef struct {
    hktas_vc_validate_fn validate,validate_group;
    hktas_vc_get_fn mode,volume,frequency,paused,reverb;
    hktas_vc_read_fn read;
    hktas_vc_loop_fn loop;
} hktas_vc_api;
static BOOL hktas_vc_read(uintptr_t address,void *output,SIZE_T bytes)
{
    SIZE_T actual=0;
    return address && ReadProcessMemory(GetCurrentProcess(),(const void *)address,output,bytes,&actual) && actual==bytes;
}
static void hktas_vc_status(hktas_vc_field *field,int code)
{
    field->error=code;
    field->status=code ? HKTAS_VC_ERROR : HKTAS_VC_VALID;
    if (code) memset(field->value,0,sizeof(field->value));
}
static BOOL hktas_vc_values(const hktas_vc_api *api,uintptr_t channel,uint32_t offset,hktas_vc_field *field,uint32_t count)
{
    if (count>8 || !api->read(channel+offset,field->value,count*4u)) return FALSE;
    field->status=HKTAS_VC_VALID;
    return TRUE;
}
static BOOL hktas_vc_once(const hktas_vc_api *api,uintptr_t channel,uintptr_t real,
    const uintptr_t *groups,uint32_t group_count,hktas_vc_receipt *out,uintptr_t *group_witness)
{
    hktas_vc_status(&out->field[HKTAS_VC_MODE],api->mode(channel,&out->mode));
    out->field[HKTAS_VC_MODE].value[0]=out->mode;
    if (out->field[HKTAS_VC_MODE].status!=HKTAS_VC_VALID) return TRUE;
    hktas_vc_status(&out->field[HKTAS_VC_VOLUME],api->volume(channel,out->field[HKTAS_VC_VOLUME].value));
    hktas_vc_status(&out->field[HKTAS_VC_FREQUENCY],api->frequency(channel,out->field[HKTAS_VC_FREQUENCY].value));
    unsigned char paused=0;
    hktas_vc_status(&out->field[HKTAS_VC_PAUSED],api->paused(channel,&paused));
    if (out->field[HKTAS_VC_PAUSED].status==HKTAS_VC_VALID) out->field[HKTAS_VC_PAUSED].value[0]=paused;
    hktas_vc_reverb reverb={0};
    hktas_vc_status(&out->field[HKTAS_VC_REVERB],api->reverb(channel,&reverb));
    if (out->field[HKTAS_VC_REVERB].status==HKTAS_VC_VALID) {
        out->field[HKTAS_VC_REVERB].value[0]=(uint32_t)reverb.direct;
        out->field[HKTAS_VC_REVERB].value[1]=(uint32_t)reverb.room;
        out->field[HKTAS_VC_REVERB].value[2]=reverb.flags;
        out->field[HKTAS_VC_REVERB].value[3]=reverb.connection!=0;
        if (reverb.connection) out->field[HKTAS_VC_REVERB].status=HKTAS_VC_UNSUPPORTED;
    }
    if (!hktas_vc_values(api,channel,412,&out->field[HKTAS_VC_PAN],1)
        || !api->read(channel+280,&out->field[HKTAS_VC_PAN].value[1],4)
        || !hktas_vc_values(api,channel,284,&out->field[HKTAS_VC_PRIORITY],1)) return FALSE;
    uint32_t flags=0;
    if (!api->read(channel+276,&flags,4)) return FALSE;
    out->field[HKTAS_VC_MUTE].status=HKTAS_VC_VALID;
    out->field[HKTAS_VC_MUTE].value[0]=(flags>>1)&1u;
    for (unsigned index=HKTAS_VC_POSITION_VELOCITY;index<=HKTAS_VC_SPREAD_PAN;index++)
        out->field[index].status=(out->mode&0x18u)==0x8u ? HKTAS_VC_NA : HKTAS_VC_UNSUPPORTED;
    if ((out->mode&0x18u)==0x10u) {
        if (!hktas_vc_values(api,channel,532,&out->field[HKTAS_VC_POSITION_VELOCITY],6)
            || !hktas_vc_values(api,channel,560,&out->field[HKTAS_VC_DISTANCE],2)
            || !hktas_vc_values(api,channel,572,&out->field[HKTAS_VC_CONE],6)
            || !hktas_vc_values(api,channel,644,&out->field[HKTAS_VC_SPREAD_PAN],2)) return FALSE;
        out->field[HKTAS_VC_POSITION_VELOCITY].value[6]=(flags&0x10u)!=0;
    }
    if (!hktas_vc_values(api,channel,352,&out->field[HKTAS_VC_DELAY],1)
        || !api->read(channel+360,&out->field[HKTAS_VC_DELAY].value[1],24)
        || !hktas_vc_values(api,real,96,&out->field[HKTAS_VC_LOOP],2)) return FALSE;
    out->field[HKTAS_VC_DELAY].value[7]=(flags&0x200u)!=0;
    int32_t loops=0;
    int code=api->loop(real,&loops);
    hktas_vc_status(&out->field[HKTAS_VC_LOOP],code);
    if (!code) out->field[HKTAS_VC_LOOP].value[2]=(uint32_t)loops;
    if (code==-3) out->field[HKTAS_VC_LOOP].status=HKTAS_VC_UNSUPPORTED;
    uintptr_t actual_group=0;
    if (!api->read(channel+312,&actual_group,8)) return FALSE;
    *group_witness=actual_group;
    out->field[HKTAS_VC_GROUP].status=HKTAS_VC_UNSUPPORTED;
    out->field[HKTAS_VC_GROUP].value[0]=UINT32_MAX;
    for (uint32_t index=0;index<group_count;index++) {
        uintptr_t opaque=0,validated=0;
        if (!api->read((uintptr_t)(groups+index),&opaque,8)) return FALSE;
        code=api->validate_group(opaque,&validated);
        if (code) {
            hktas_vc_status(&out->field[HKTAS_VC_GROUP],code);
            return TRUE;
        }
        if (validated==actual_group) {
            if (out->field[HKTAS_VC_GROUP].status==HKTAS_VC_VALID) {
                out->field[HKTAS_VC_GROUP].status=HKTAS_VC_UNSUPPORTED;
                out->field[HKTAS_VC_GROUP].value[0]=UINT32_MAX;
                return TRUE;
            }
            out->field[HKTAS_VC_GROUP].status=HKTAS_VC_VALID;
            out->field[HKTAS_VC_GROUP].value[0]=index;
        }
    }
    return TRUE;
}
static int hktas_vc_collect(const hktas_vc_api *api,uintptr_t opaque,const uintptr_t *groups,uint32_t group_count,
    hktas_vc_receipt *output,uint32_t bytes)
{
    if (!output || bytes!=sizeof(*output) || group_count>262144u || (group_count && !groups)) return 0;
    hktas_vc_receipt first={0},second={0};
    first.magic=0x31435641u; first.version=1; first.bytes=sizeof(first); first.field_count=HKTAS_VC_FIELDS;
    uintptr_t channel=0,real=0,again=0,real_again=0,group_first=0,group_second=0,vtable=0,vtable_again=0;
    int count=0,count_again=0;
    first.result=api->validate(opaque,&channel);
    if (first.result || !channel) goto done;
    if (!api->read(channel+136,&count,4) || !api->read(channel+144,&real,8)) {first.result=-2;goto done;}
    first.real_count=(uint32_t)count;
    if (count!=1 || !real) {
        first.result=-3;
        for (unsigned index=0;index<HKTAS_VC_FIELDS;index++) first.field[index].status=HKTAS_VC_UNSUPPORTED;
        goto done;
    }
    if (!api->read(real,&vtable,8) || !hktas_vc_once(api,channel,real,groups,group_count,&first,&group_first)) {first.result=-2;goto done;}
    second=first;
    memset(second.field,0,sizeof(second.field));
    first.result=api->validate(opaque,&again);
    if (first.result || again!=channel || !api->read(channel+136,&count_again,4)
        || !api->read(channel+144,&real_again,8) || count_again!=count || real_again!=real) {if (!first.result) first.result=-4;goto done;}
    if (!hktas_vc_once(api,channel,real,groups,group_count,&second,&group_second)) {first.result=-2;goto done;}
    first.result=api->validate(opaque,&again);
    if (first.result || again!=channel || !api->read(channel+144,&real_again,8) || real_again!=real
        || !api->read(channel+136,&count_again,4) || count_again!=count
        || !api->read(real,&vtable_again,8) || vtable_again!=vtable || group_first!=group_second
        || first.mode!=second.mode || memcmp(first.field,second.field,sizeof(first.field))) {if (!first.result) first.result=-4;goto done;}
    first.valid=1;
done:
    memcpy(output,&first,sizeof(first));
    return sizeof(first);
}

/* Flags 0xf certify owner match, successful clocks, monotonic clocks and
 * matching delay classifications with stable controls. No clock is restored. */
static int hktas_vc_system_owner(const hktas_vc_api *api,hktas_vc_validate_fn validate_system,
    uintptr_t opaque,uintptr_t opaque_system,uintptr_t expected_channel,uintptr_t expected_system,
    uintptr_t *channel,uintptr_t *system)
{
    int code=api->validate(opaque,channel);
    if (code) return code;
    code=validate_system(opaque_system,system);
    if (code) return code;
    uintptr_t owner=0;
    if (!*channel || !*system || !api->read(*channel+128,&owner,8)) return -2;
    if (owner!=*system || (expected_channel && *channel!=expected_channel)
        || (expected_system && *system!=expected_system)) return -5;
    return 0;
}
static uint32_t hktas_vc_delay_class(const hktas_vc_field *delay,uint64_t clock)
{
    uint64_t start=((uint64_t)delay->value[2]<<32)|delay->value[1];
    return delay->status==HKTAS_VC_VALID && !delay->value[0] && !delay->value[3]
        && !delay->value[4] && !delay->value[5] && !delay->value[6] && !delay->value[7]
        && start<=clock ? 1u : 2u;
}
static int hktas_vc_collect_v2(const hktas_vc_api *api,hktas_vc_validate_fn validate_system,
    hktas_vc_clock_fn get_clock,uintptr_t opaque,uintptr_t opaque_system,
    const uintptr_t *groups,uint32_t group_count,hktas_vc_receipt_v2 *output,uint32_t bytes)
{
    if (!output || bytes!=sizeof(*output) || !opaque_system || !validate_system || !get_clock
        || group_count>262144u || (group_count && !groups)) return 0;
    hktas_vc_receipt_v2 value={0};
    hktas_vc_receipt rounds[2]={{0}};
    uintptr_t channel=0,system=0,again=0,system_again=0;
    uintptr_t real=0,vtable=0,real_again=0,vtable_again=0;
    uint32_t classes[2]={0};
    value.magic=0x31435641u;value.version=2;value.bytes=sizeof(value);value.field_count=HKTAS_VC_FIELDS;
    value.result=hktas_vc_system_owner(api,validate_system,opaque,opaque_system,0,0,&channel,&system);
    if (value.result) goto done_v2;
    if (!api->read(channel+144,&real,8) || !real || !api->read(real,&vtable,8)) {
        value.result=-2;goto done_v2;
    }
    for (unsigned round=0;round<2;round++) {
        uint32_t hi=0,lo=0;
        value.result=get_clock(opaque_system,&hi,&lo);
        if (value.result) goto done_v2;
        if (!api->read(channel+144,&real_again,8) || real_again!=real
            || !api->read(real,&vtable_again,8) || vtable_again!=vtable) {
            value.result=-4;goto done_v2;
        }
        uint64_t clock=((uint64_t)hi<<32)|lo;
        if (!round) value.clock_first=clock;
        else value.clock_second=clock;
        if (hktas_vc_collect(api,opaque,groups,group_count,&rounds[round],sizeof(rounds[round]))!=sizeof(rounds[round])) {
            value.result=-2;goto done_v2;
        }
        value.mode=rounds[round].mode;value.real_count=rounds[round].real_count;
        memcpy(value.field,rounds[round].field,sizeof(value.field));
        if (!rounds[round].valid) {value.result=rounds[round].result;goto done_v2;}
        classes[round]=hktas_vc_delay_class(&rounds[round].field[HKTAS_VC_DELAY],clock);
        value.result=hktas_vc_system_owner(api,validate_system,opaque,opaque_system,channel,system,&again,&system_again);
        if (value.result) goto done_v2;
        if (!api->read(channel+144,&real_again,8) || real_again!=real
            || !api->read(real,&vtable_again,8) || vtable_again!=vtable) {
            value.result=-4;goto done_v2;
        }
    }
    if (value.clock_second<value.clock_first) {value.result=-6;goto done_v2;}
    if (classes[0]!=classes[1]) {value.result=-7;goto done_v2;}
    if (rounds[0].mode!=rounds[1].mode || memcmp(rounds[0].field,rounds[1].field,sizeof(value.field))) {
        value.result=-4;goto done_v2;
    }
    value.delay_class=classes[0];value.clock_witness_flags=0xfu;
    if (value.delay_class!=1) value.field[HKTAS_VC_DELAY].status=HKTAS_VC_UNSUPPORTED;
    value.valid=1;
done_v2:
    memcpy(output,&value,sizeof(value));
    return sizeof(value);
}

#ifndef HKTAS_VC_TEST
static BOOL hktas_vc_prefix(BYTE *base,uint32_t rva,const unsigned char *expected,SIZE_T count)
{
    unsigned char actual[32];
    return count<=sizeof(actual) && hktas_vc_read((uintptr_t)(base+rva),actual,count) && !memcmp(actual,expected,count);
}
static BYTE *hktas_vc_image(void)
{
    static BOOL s_vc_pe_warned;
    BYTE *base=(BYTE *)GetModuleHandleW(L"UnityPlayer.dll");
    IMAGE_DOS_HEADER dos;
    IMAGE_NT_HEADERS64 nt;
    if (!base || !hktas_vc_read((uintptr_t)base,&dos,sizeof(dos)) || dos.e_magic!=IMAGE_DOS_SIGNATURE
        || dos.e_lfanew<=0 || dos.e_lfanew>4096
        || !hktas_vc_read((uintptr_t)(base+dos.e_lfanew),&nt,sizeof(nt)) || nt.Signature!=IMAGE_NT_SIGNATURE
        || nt.FileHeader.Machine!=IMAGE_FILE_MACHINE_AMD64 || nt.OptionalHeader.Magic!=IMAGE_NT_OPTIONAL_HDR64_MAGIC
        || nt.OptionalHeader.SizeOfImage<0x1150000u) return NULL;
    if ((nt.FileHeader.TimeDateStamp!=0x6008c66eu || nt.OptionalHeader.SizeOfImage!=0x1bc7000u) && !s_vc_pe_warned) {
        s_vc_pe_warned=TRUE;
        OutputDebugStringA("[HKTAS][WARN] resident_audio_voice_control: UnityPlayer PE header mismatch; verifying function byte prefixes.\n");
    }
    return base;
}
static int hktas_vc_loop(uintptr_t real,int32_t *output)
{
    BYTE *base=hktas_vc_image();
    uintptr_t vtable=0,entry=0;
    BOOL known=FALSE;
    if (!base || !hktas_vc_read(real,&vtable,8) || !hktas_vc_read(vtable+0xe8,&entry,8)) return -2;
    if (entry==(uintptr_t)(base+0x1126f70u) && hktas_vc_prefix(base,0x1126f70u,(const unsigned char[]){0x48,0x85,0xd2,0x75,0x06,0xb8,0x25,0x00,0x00,0x00,0xc3,0x8b,0x41,0x5c,0x89,0x02,0x33,0xc0,0xc3},19u)) known=TRUE;
    if (entry==(uintptr_t)(base+0x113e650u) && hktas_vc_prefix(base,0x113e650u,(const unsigned char[]){0x48,0x83,0xec,0x28,0x48,0x8b,0xc1,0x48,0x85,0xd2,0x75,0x08,0x8d,0x42,0x25,0x48,0x83,0xc4,0x28,0xc3,0x48,0x8b,0x89,0x88,0x04,0x00,0x00,0x48,0x85,0xc9,0x74,0x2f},32u)) known=TRUE;
    if (!known) return -3;
    return ((hktas_vc_get_fn)entry)(real,output);
}
static BOOL hktas_vc_bind(hktas_vc_api *api)
{
    BYTE *base=hktas_vc_image();
    if (!base) return FALSE;
    if (!hktas_vc_prefix(base,0x10bf020u,(const unsigned char[]){0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x6c,0x24,0x18,0x48,0x89,0x74,0x24,0x20,0x57,0x48,0x83,0xec,0x20,0x8b,0xd9,0x0f,0xb7,0xf1,0xc1,0xeb,0x10,0x48,0x8b,0xfa,0x81},32u)) return FALSE;
    if (!hktas_vc_prefix(base,0x10c2790u,(const unsigned char[]){0x48,0x85,0xc9,0x75,0x04,0x8d,0x41,0x24,0xc3,0x48,0x85,0xd2,0x75,0x06,0xb8,0x25,0x00,0x00,0x00,0xc3,0x48,0x89,0x0a,0x33,0xc0,0xc3},26u)) return FALSE;
    if (!hktas_vc_prefix(base,0x10bae10u,(const unsigned char[]){0x48,0x85,0xd2,0x75,0x06,0xb8,0x25,0x00,0x00,0x00,0xc3,0x48,0x8b,0x81,0x90,0x00,0x00,0x00,0x48,0x85,0xc0,0x75,0x06,0xb8,0x24,0x00,0x00,0x00,0xc3,0x8b,0x40,0x48},32u)) return FALSE;
    if (!hktas_vc_prefix(base,0x10bb1e0u,(const unsigned char[]){0x48,0x85,0xd2,0x75,0x04,0x8d,0x42,0x25,0xc3,0x48,0x83,0xb9,0x90,0x00,0x00,0x00,0x00,0x75,0x06,0xb8,0x24,0x00,0x00,0x00,0xc3,0x8b,0x81,0x94,0x01,0x00,0x00,0x89},32u)) return FALSE;
    if (!hktas_vc_prefix(base,0x10bae40u,(const unsigned char[]){0x48,0x83,0xec,0x28,0x48,0x85,0xd2,0x75,0x08,0x8d,0x42,0x25,0x48,0x83,0xc4,0x28,0xc3,0x48,0x83,0xb9,0x90,0x00,0x00,0x00,0x00,0x75,0x0a,0xb8,0x24,0x00,0x00,0x00},32u)) return FALSE;
    if (!hktas_vc_prefix(base,0x10bad60u,(const unsigned char[]){0x48,0x85,0xd2,0x75,0x04,0x8d,0x42,0x25,0xc3,0x48,0x83,0xb9,0x90,0x00,0x00,0x00,0x00,0x75,0x06,0xb8,0x24,0x00,0x00,0x00,0xc3,0x8b,0x81,0x98,0x01,0x00,0x00,0x89},32u)) return FALSE;
    if (!hktas_vc_prefix(base,0x10bafa0u,(const unsigned char[]){0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x6c,0x24,0x18,0x48,0x89,0x7c,0x24,0x20,0x41,0x56,0x48,0x83,0xec,0x20,0x33,0xdb,0x48,0x8d,0xb9,0x90,0x00,0x00,0x00,0x4c,0x8b},32u)) return FALSE;
    api->validate=(hktas_vc_validate_fn)(base+0x10bf020u);
    api->validate_group=(hktas_vc_validate_fn)(base+0x10c2790u);
    api->mode=(hktas_vc_get_fn)(base+0x10bae10u);
    api->volume=(hktas_vc_get_fn)(base+0x10bb1e0u);
    api->frequency=(hktas_vc_get_fn)(base+0x10bad60u);
    api->paused=(hktas_vc_get_fn)(base+0x10bae40u);
    api->reverb=(hktas_vc_get_fn)(base+0x10bafa0u);
    api->read=hktas_vc_read;
    api->loop=hktas_vc_loop;
    return TRUE;
}
/* The bridge integration owns the exported wrapper. A coherent receipt is not
 * qualification: callers must reject every required field not VALID/NA. */
static int hktas_vc_query(uintptr_t opaque,const uintptr_t *groups,uint32_t group_count,BYTE *output,uint32_t bytes)
{
    hktas_vc_api api;
    if (!hktas_vc_bind(&api)) return 0;
    return hktas_vc_collect(&api,opaque,groups,group_count,(hktas_vc_receipt *)output,bytes);
}
static int hktas_vc_query_v2(uintptr_t opaque,uintptr_t opaque_system,const uintptr_t *groups,
    uint32_t group_count,BYTE *output,uint32_t bytes)
{
    hktas_vc_api api;
    BYTE *base=hktas_vc_image();
    if (!base || !hktas_vc_bind(&api)) return 0;
    if (!hktas_vc_prefix(base,0x10b9780u,(const unsigned char[]){0x4c,0x8b,0xca,0x4c,0x8b,0xc1,0x48,0x85,0xc9,0x74,0x32,0x48,0x85,0xd2,0x75,0x06,0xb8,0x25,0x00,0x00,0x00,0xc3,0x48,0x8b,0x05,0x43,0x56,0x8d,0x00,0x48,0x8d,0x51},32u)
        || !hktas_vc_prefix(base,0x10a3b20u,(const unsigned char[]){0x48,0x89,0x5c,0x24,0x08,0x57,0x48,0x83,0xec,0x20,0x48,0x8b,0xfa,0x49,0x8b,0xd8,0x48,0x8d,0x54,0x24,0x48,0xe8,0x46,0x5c,0x01,0x00,0x85,0xc0,0x75,0x10,0x48,0x8b},32u)
        || !hktas_vc_prefix(base,0x10b3750u,(const unsigned char[]){0x48,0x85,0xd2,0x74,0x08,0x8b,0x81,0x7c,0x0c,0x00,0x00,0x89,0x02,0x4d,0x85,0xc0,0x74,0x09,0x8b,0x81,0x78,0x0c,0x00,0x00,0x41,0x89,0x00,0x33,0xc0,0xc3,0xcc,0xcc},32u)) return 0;
    return hktas_vc_collect_v2(&api,(hktas_vc_validate_fn)(base+0x10b9780u),
        (hktas_vc_clock_fn)(base+0x10a3b20u),opaque,opaque_system,groups,group_count,
        (hktas_vc_receipt_v2 *)output,bytes);
}
#endif
#endif
