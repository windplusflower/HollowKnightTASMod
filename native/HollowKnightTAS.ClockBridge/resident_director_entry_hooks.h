#ifndef HKTAS_DIRECTOR_ENTRY_HOOKS_H
#define HKTAS_DIRECTOR_ENTRY_HOOKS_H
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include "resident_director_entry_layout.h"
#include "resident_input_entry_layout.h"
#ifndef HKTAS_BOUNDARY_ENTRIES
#define HKTAS_BOUNDARY_ENTRIES(X) HKTAS_DE_ENTRIES(X) HKTAS_INPUT_ENTRIES(X)
#endif

/* The linker puts the relocated instruction prefix and its x64 unwind
 * metadata in this module's ordinary .text/.pdata/.xdata. Installation
 * registers no exception handler or dynamic function table. R11 is Win64
 * volatile scratch, never an argument or return register. Use the explicit
 * mod=01 encoding of JMP [R11+0]; the Windows unwinder recognizes the
 * tempting JMP [RIP+disp32] as a tail epilogue and skips a partial frame. */
#define HKTAS_DE_THUNK(id, abi, rva, length, hash, cut, bytes, unwind) \
    extern BYTE hktas_de_resume_##id[]; \
    extern const BYTE hktas_de_original_##id[]; \
    __asm__(".section .hktasde,\"xr\"\n.p2align 4\n.globl hktas_de_original_" #id "\nhktas_de_original_" #id ":\n" \
        ".byte " bytes "\n.byte 0x4c,0x8d,0x1d,4,0,0,0,0x41,0xff,0x63,0\nhktas_de_end_" #id ":\n" \
        ".globl hktas_de_resume_" #id "\nhktas_de_resume_" #id ":\n.quad 0\n" \
        ".section .xdata,\"dr\"\n.p2align 2\nhktas_de_unwind_" #id ":\n.byte " unwind "\n.p2align 2\n" \
        ".section .pdata,\"dr\"\n.p2align 2\n.rva hktas_de_original_" #id ",hktas_de_end_" #id ",hktas_de_unwind_" #id "\n.text\n");
HKTAS_BOUNDARY_ENTRIES(HKTAS_DE_THUNK)
#undef HKTAS_DE_THUNK
#define HKTAS_DE_ID(id,abi,rva,length,hash,cut,bytes,unwind) HKTAS_DE_##id,
enum hktas_de_id { HKTAS_BOUNDARY_ENTRIES(HKTAS_DE_ID) HKTAS_DE_COUNT };
#undef HKTAS_DE_ID
typedef struct {
    DWORD rva,length;
    uint64_t hash;
    unsigned prefix;
    const BYTE *original;
    BYTE *continuation;
} hktas_de_entry;
#define HKTAS_DE_ROW(id,abi,rva,length,hash,cut,bytes,unwind) \
    {rva,length,hash,cut,hktas_de_original_##id,hktas_de_resume_##id},
static const hktas_de_entry hktas_de_entries[]={HKTAS_BOUNDARY_ENTRIES(HKTAS_DE_ROW)};
#undef HKTAS_DE_ROW

static BOOL hktas_de_unwind_matches(BYTE *image,const hktas_de_entry *entry,const BYTE *clone)
{
    const IMAGE_NT_HEADERS64 *nt=(const IMAGE_NT_HEADERS64*)(image+((const IMAGE_DOS_HEADER*)image)->e_lfanew);
    IMAGE_DATA_DIRECTORY d=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
    if(!d.VirtualAddress || d.Size%sizeof(RUNTIME_FUNCTION) || d.Size>nt->OptionalHeader.SizeOfImage
        || d.VirtualAddress>nt->OptionalHeader.SizeOfImage-d.Size) return FALSE;
    const RUNTIME_FUNCTION *rows=(const RUNTIME_FUNCTION*)(image+d.VirtualAddress);
    size_t lo=0,hi=d.Size/sizeof(*rows),length=hi;
    while(lo<hi) {size_t mid=lo+(hi-lo)/2;if(rows[mid].BeginAddress<entry->rva) lo=mid+1;else hi=mid;}
    if(clone[0]!=1 || clone[1]!=entry->prefix+11) return FALSE;
    if(lo==length || rows[lo].BeginAddress!=entry->rva) return !clone[2] && !clone[3];
    if(rows[lo].UnwindData>=nt->OptionalHeader.SizeOfImage-4) return FALSE;
    const BYTE *orig=image+rows[lo].UnwindData;
    if(orig[0]!=1 || rows[lo].UnwindData>nt->OptionalHeader.SizeOfImage-4-2u*orig[2]) return FALSE;
    unsigned copied=0,frame=0;
    for(unsigned i=0;i<orig[2];) {
        const BYTE *code=orig+4+2*i;unsigned op=code[1]&15u,info=code[1]>>4;
        static const unsigned slots[11]={1,0,1,1,2,3,0,0,2,3,1};
        unsigned count=op==1 ? (info==0?2u:info==1?3u:0u) : op<11 ? slots[op] : 0;
        if(!count || count>orig[2]-i) return FALSE;
        if(code[0]<=entry->prefix) {
            if(copied+count>clone[2] || memcmp(code,clone+4+2*copied,2*count)) return FALSE;
            if(op==3) frame=orig[3];
            copied+=count;
        }
        i+=count;
    }
    return clone[2]==copied && clone[3]==frame;
}
static BOOL hktas_de_destination(BYTE *site,BYTE *target,int32_t *result)
{
    int64_t delta=(int64_t)(uintptr_t)target-(int64_t)(uintptr_t)(site+5);
    if(delta<INT32_MIN || delta>INT32_MAX) return FALSE;
    *result=(int32_t)delta;return TRUE;
}
static BOOL hktas_de_verify_table(BYTE *base,BYTE *relays,const void *const *wrappers,BOOL installed,const hktas_de_entry *entries,unsigned count)
{
    if(!base || (installed && !relays) || !wrappers) return FALSE;
    for(unsigned i=0;i<count;++i) {
        const hktas_de_entry *e=&entries[i];
        BYTE *site=base+e->rva;
        DWORD64 module=0;PRUNTIME_FUNCTION frame=RtlLookupFunctionEntry((DWORD64)(uintptr_t)e->original,&module,NULL);
        if(!frame || module+frame->BeginAddress!=(DWORD64)(uintptr_t)e->original || frame->EndAddress-frame->BeginAddress!=e->prefix+11
            || !wrappers[i] || e->prefix<5 || e->prefix>e->length
            || !hktas_de_unwind_matches(base,e,(const BYTE*)(uintptr_t)(module+frame->UnwindData))) return FALSE;
        const BYTE *lea=e->original+e->prefix;int32_t disp;memcpy(&disp,lea+3,4);
        if(memcmp(lea,"\x4c\x8d\x1d",3) || lea+7+disp!=(const BYTE*)e->continuation || memcmp(lea+7,"\x41\xff\x63\0",4)
            || memcmp(e->original+5,site+5,e->prefix-5)) return FALSE;
        if(installed) {
            BYTE *relay=relays+i*16,*resume;int32_t delta;const void *wrapper;
            memcpy(&wrapper,relay+6,8);
            memcpy(&resume,e->continuation,8);
            if(!hktas_de_destination(site,relay,&delta) || site[0]!=0xe9 || memcmp(site+1,&delta,4)
                || memcmp(relay,"\xff\x25\0\0\0\0",6) || wrapper!=wrappers[i] || resume!=site+e->prefix) return FALSE;
        } else if(memcmp(site,e->original,e->prefix)) return FALSE;
        uint64_t hash=UINT64_C(14695981039346656037);
        for(DWORD j=0;j<e->length;++j) {
            BYTE b=installed && j<5 ? e->original[j] : site[j];
            hash=(hash^b)*UINT64_C(1099511628211);
        }
        if(hash!=e->hash) return FALSE;
    }
    return TRUE;
}
/* Only the pre-main exclusive publisher may call this. All fixed prefixes,
 * linker unwind rows, origins and relative jump ranges pass preflight
 * before the first instruction becomes reachable. A partial write is a
 * terminal install error; the caller must retain the relays and fault. */
static BOOL hktas_de_install_table(BYTE *base,BYTE *relays,const void *const *wrappers,const hktas_de_entry *entries,unsigned count)
{
    if(!hktas_de_verify_table(base,relays,wrappers,FALSE,entries,count)) return FALSE;
    for(unsigned i=0;i<count;++i) {
        const hktas_de_entry *e=&entries[i];
        BYTE *site=base+e->rva,*relay=relays+i*16;int32_t delta;
        BYTE *resume=site+e->prefix;DWORD old,ignored;
        if(!hktas_de_destination(site,relay,&delta) || !VirtualProtect(e->continuation,8,PAGE_EXECUTE_READWRITE,&old)) return FALSE;
        memcpy(e->continuation,&resume,8);
        if(!VirtualProtect(e->continuation,8,old,&ignored)) return FALSE;
        memcpy(relay,"\xff\x25\0\0\0\0",6);memcpy(relay+6,&wrappers[i],8);
    }
    DWORD old,ignored;
    if(!VirtualProtect(relays,count*16,PAGE_EXECUTE_READ,&old)
        || !FlushInstructionCache(GetCurrentProcess(),relays,count*16)) return FALSE;
    for(unsigned i=0;i<count;++i) {
        BYTE *site=base+entries[i].rva,*relay=relays+i*16;int32_t delta;
        if(!hktas_de_destination(site,relay,&delta) || !VirtualProtect(site,5,PAGE_EXECUTE_READWRITE,&old)) return FALSE;
        site[0]=0xe9;memcpy(site+1,&delta,4);
        if(!VirtualProtect(site,5,old,&ignored) || !FlushInstructionCache(GetCurrentProcess(),site,5)) return FALSE;
    }
    return hktas_de_verify_table(base,relays,wrappers,TRUE,entries,count);
}
static BOOL hktas_de_verify(BYTE *base,BYTE *relays,const void *const *wrappers,BOOL installed)
{ return hktas_de_verify_table(base,relays,wrappers,installed,hktas_de_entries,HKTAS_DE_COUNT); }
static BOOL hktas_de_install(BYTE *base,BYTE *relays,const void *const *wrappers)
{ return hktas_de_install_table(base,relays,wrappers,hktas_de_entries,HKTAS_DE_COUNT); }
#endif
