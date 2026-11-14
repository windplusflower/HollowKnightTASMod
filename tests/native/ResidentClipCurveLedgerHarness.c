#include "../../native/HollowKnightTAS.ClockBridge/resident_clip_curve_ledger.h"
#include <stdio.h>
#include <stdlib.h>
#define CHECK(x) do { if (!(x)) {fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#x);exit(1);} } while(0)
typedef struct {uintptr_t vt;int32_t id;int32_t padding;} native_clip;
static LONG calls,faults;
static native_clip clips[2];
static HANDLE entered,release_original,worker_done;
static BOOL hold_original;
static void *expected[5];
static void __cdecl sink(LONG family) {CHECK(family==69);InterlockedIncrement(&faults);}
static void __cdecl original_one(void *self)
{
    CHECK(self==&clips[0] || self==&clips[1] || !self);
    InterlockedIncrement(&calls);
    if (hold_original) {SetEvent(entered);CHECK(WaitForSingleObject(release_original,2000)==WAIT_OBJECT_0);}
}
static void __cdecl original_eight(void *a,const void *b,const void *c,int32_t script,const void *d,const void *e,unsigned char combine,unsigned char update)
{
    CHECK(a==expected[0] && b==expected[1] && c==expected[2] && d==expected[3] && e==expected[4]);
    CHECK(script==57 && combine==1 && update==1);
    InterlockedIncrement(&calls);
}
static BYTE *image(const char *path)
{
    FILE *file=fopen(path,"rb");CHECK(file);CHECK(!fseek(file,0,SEEK_END));long length=ftell(file);CHECK(length>4096);
    CHECK(!fseek(file,0,SEEK_SET));BYTE *raw=malloc((size_t)length);CHECK(raw);CHECK(fread(raw,1,(size_t)length,file)==(size_t)length);fclose(file);
    IMAGE_DOS_HEADER *dos=(IMAGE_DOS_HEADER *)raw;CHECK(dos->e_magic==IMAGE_DOS_SIGNATURE);
    IMAGE_NT_HEADERS64 *nt=(IMAGE_NT_HEADERS64 *)(raw+dos->e_lfanew);CHECK(nt->OptionalHeader.SizeOfImage==0x1bc7000u);
    BYTE *out=VirtualAlloc(NULL,nt->OptionalHeader.SizeOfImage,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);CHECK(out);
    memcpy(out,raw,nt->OptionalHeader.SizeOfHeaders);IMAGE_SECTION_HEADER *sections=IMAGE_FIRST_SECTION(nt);
    for(WORD i=0;i<nt->FileHeader.NumberOfSections;i++) {IMAGE_SECTION_HEADER s=sections[i];CHECK((uint64_t)s.PointerToRawData+s.SizeOfRawData<=(uint64_t)length);memcpy(out+s.VirtualAddress,raw+s.PointerToRawData,s.SizeOfRawData);}
    for(WORD i=0;i<nt->FileHeader.NumberOfSections;i++) {
        if (sections[i].Characteristics&IMAGE_SCN_MEM_EXECUTE) {
            DWORD old;
            CHECK(VirtualProtect(out+sections[i].VirtualAddress,sections[i].Misc.VirtualSize,PAGE_EXECUTE_READ,&old));
        }
    }
    free(raw);return out;
}
static hktas_cl_receipt status(void)
{
    hktas_cl_receipt s={0};CHECK(HktasClockBridge_CopyResidentClipCurveLedgerStatus((BYTE *)&s,64)==64);CHECK(s.magic==0x314c4343u && s.version==1 && s.bytes==64);return s;
}
static LONG query(unsigned index,uint64_t token)
{
    hktas_cl_receipt receipt={0};return HktasClockBridge_QueryResidentClipCurveEligibility(token,(uintptr_t)&clips[index],clips[index].id,(BYTE *)&receipt,64);
}
static BOOL end(uint64_t token)
{
    AcquireSRWLockExclusive(&g_cl_lock);BOOL ok=hktas_cl_held_locked(token);
    if(ok) {g_cl_closed=FALSE;g_cl_held=FALSE;WakeAllConditionVariable(&g_cl_changed);}
    ReleaseSRWLockExclusive(&g_cl_lock);return ok;
}
static DWORD WINAPI worker(void *unused)
{
    (void)unused;((hktas_cl_one_fn)(void *)(g_cl_base+g_cl_entries[2].rva))(&clips[0]);SetEvent(worker_done);return 0;
}
static DWORD WINAPI drain_release(void *unused)
{
    (void)unused;ULONGLONG start=GetTickCount64();while(!status().closed){CHECK(GetTickCount64()-start<2000);Sleep(1);}SetEvent(release_original);return 0;
}
static void corrupt(BYTE *at,unsigned count)
{
    DWORD old,ignored;CHECK(VirtualProtect(at,count,PAGE_EXECUTE_READWRITE,&old));
    for(unsigned i=0;i<count;i++) at[i]^=1;
    CHECK(VirtualProtect(at,count,old,&ignored));
}
int main(int argc,char **argv)
{
    (void)hktas_de_install; (void)hktas_de_verify;
    CHECK(argc==3);BYTE *base=image(argv[1]);const char *test=argv[2];
    if(!strcmp(test,"bad-code")) corrupt(base+g_cl_entries[0].rva,1);
    if(!strcmp(test,"bad-slot")) corrupt(base+g_cl_entries[1].rva,8);
    BOOL installed=install_resident_clip_curve_ledger(base,GetCurrentThreadId(),sink);
    if(!strcmp(test,"bad-code") || !strcmp(test,"bad-slot")){CHECK(!installed);return 0;}
    CHECK(installed && !install_resident_clip_curve_ledger(base,GetCurrentThreadId(),sink));
    CHECK(status().installed_mask==7 && status().abi_valid);
    g_cl_original[0]=(void *)original_eight;g_cl_original[1]=(void *)original_one;g_cl_original[2]=(void *)original_one;
    for(unsigned i=0;i<2;i++){clips[i].vt=(uintptr_t)(base+0x171d808u);clips[i].id=(int)i+17;}
    entered=CreateEventW(NULL,TRUE,FALSE,NULL);release_original=CreateEventW(NULL,TRUE,FALSE,NULL);worker_done=CreateEventW(NULL,TRUE,FALSE,NULL);CHECK(entered&&release_original&&worker_done);
    g_resident_wall_limit=1000;
    if(!strcmp(test,"normal")) {
        expected[0]=&clips[0];for(unsigned i=1;i<5;i++)expected[i]=(void *)(uintptr_t)(100+i);
        hktas_cl_set_curve(expected[0],expected[1],expected[2],57,expected[3],expected[4],1,1);hktas_cl_ensure(&clips[0]);hktas_cl_clear(&clips[0]);
        CHECK(calls==3 && status().active==0);
        CHECK(hktas_cl_begin(1,GetTickCount64(),TRUE));CHECK(query(0,1)==2 && query(1,1)==1 && !query(1,2));
        CHECK(hktas_cl_require(1));CHECK(end(1));CHECK(!query(1,1));
        hktas_cl_clear(&clips[1]);CHECK(hktas_cl_begin(2,GetTickCount64(),TRUE));CHECK(query(1,2)==2);CHECK(end(2));
    } else if(!strcmp(test,"drain")) {
        hold_original=TRUE;HANDLE w=CreateThread(NULL,0,worker,NULL,0,NULL);CHECK(w);CHECK(WaitForSingleObject(entered,2000)==WAIT_OBJECT_0);
        HANDLE release=CreateThread(NULL,0,drain_release,NULL,0,NULL);CHECK(release);CHECK(hktas_cl_begin(1,GetTickCount64(),TRUE));CHECK(!status().active);CHECK(end(1));CHECK(WaitForSingleObject(w,2000)==WAIT_OBJECT_0);CHECK(WaitForSingleObject(release,2000)==WAIT_OBJECT_0);
    } else if(!strcmp(test,"blocked") || !strcmp(test,"timeout")) {
        if(!strcmp(test,"timeout"))g_resident_wall_limit=30;
        CHECK(hktas_cl_begin(1,GetTickCount64(),TRUE));HANDLE w=CreateThread(NULL,0,worker,NULL,0,NULL);CHECK(w);Sleep(!strcmp(test,"timeout")?100:20);
        CHECK(calls==0 && WaitForSingleObject(worker_done,0)==WAIT_TIMEOUT && status().closed);
        if(!strcmp(test,"timeout")){CHECK(status().fault && !end(1) && !query(1,1));return 0;}
        CHECK(query(1,1)==1 && hktas_cl_require(1));CHECK(end(1));CHECK(WaitForSingleObject(w,2000)==WAIT_OBJECT_0);CHECK(calls==1);
    } else if(!strcmp(test,"unknown")) {
        hktas_cl_clear(NULL);CHECK(calls==1 && status().fault && hktas_cl_begin(1,GetTickCount64(),TRUE) && !query(1,1));
    } else if(!strcmp(test,"overflow")) {
        AcquireSRWLockExclusive(&g_cl_lock);for(unsigned i=0;i<=HKTAS_CL_CAPACITY;i++){hktas_cl_pair pair={(uintptr_t)(4096u+i*16u),(int32_t)i+1};hktas_cl_dirty_locked(pair,TRUE);}ReleaseSRWLockExclusive(&g_cl_lock);
        CHECK(status().fault && g_cl_used==HKTAS_CL_CAPACITY && hktas_cl_begin(1,GetTickCount64(),TRUE));
    } else if(!strcmp(test,"tamper")) {
        CHECK(hktas_cl_begin(1,GetTickCount64(),TRUE));corrupt(base+g_cl_entries[0].rva,8);CHECK(!query(1,1) && !hktas_cl_require(1) && !end(1));
    } else if(!strcmp(test,"main-reentry")) {
        CHECK(hktas_cl_begin(1,GetTickCount64(),TRUE));hktas_cl_clear(&clips[0]);CHECK(FALSE);
    } else CHECK(FALSE);
    printf("PASS %s\n",test);return 0;
}
