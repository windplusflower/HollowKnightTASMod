#ifndef HKTAS_RESIDENT_AUDIO_FENCE_TEST_IMAGE_H
#define HKTAS_RESIDENT_AUDIO_FENCE_TEST_IMAGE_H
/* Synthetic PE fixture only. No UnityPlayer image is loaded or modified.
 * Include after the real resident_audio_fence.h implementation. */
#include <stdio.h>
#include <stdlib.h>

static void hktas_audio_fixture_check(BOOL condition, const char *expression, int line)
{
    if (condition) return;
    fprintf(stderr,"FAIL audio image fixture %d: %s\n",line,expression); exit(1);
}
#define AUDIO_IMAGE_CHECK(value) hktas_audio_fixture_check(!!(value),#value,__LINE__)

static BYTE *hktas_audio_make_test_image(DWORD *image_size)
{
    DWORD bytes=(HKTAS_AUDIO_IMAGE_MINIMUM+4095u)&~4095u;
    BYTE *base=(BYTE *)VirtualAlloc(NULL,bytes,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE); AUDIO_IMAGE_CHECK(base);
    IMAGE_DOS_HEADER *dos=(IMAGE_DOS_HEADER *)base; dos->e_magic=IMAGE_DOS_SIGNATURE; dos->e_lfanew=0x100;
    IMAGE_NT_HEADERS64 *nt=(IMAGE_NT_HEADERS64 *)(base+0x100);
    nt->Signature=IMAGE_NT_SIGNATURE; nt->OptionalHeader.Magic=IMAGE_NT_OPTIONAL_HDR64_MAGIC;
    nt->OptionalHeader.SizeOfImage=bytes; nt->OptionalHeader.NumberOfRvaAndSizes=IMAGE_NUMBEROF_DIRECTORY_ENTRIES;
    nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG].VirtualAddress=0x1000;
    nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG].Size=sizeof(IMAGE_DEBUG_DIRECTORY);
    IMAGE_DEBUG_DIRECTORY *debug=(IMAGE_DEBUG_DIRECTORY *)(base+0x1000);
    debug->Type=IMAGE_DEBUG_TYPE_CODEVIEW; debug->SizeOfData=24; debug->AddressOfRawData=0x1100;
    const BYTE cv[]={0x52,0x53,0x44,0x53,0x90,0xbe,0x34,0x03,0xdd,0x63,0x44,0x46,0xbe,0xe1,0xfc,0x8d,0x56,0xc8,0x40,0xc2,1,0,0,0};
    memcpy(base+0x1100,cv,sizeof(cv));
    memcpy(base+HKTAS_AUDIO_THREAD_RVA,g_audio_original_thread,sizeof(g_audio_original_thread));
    memcpy(base+HKTAS_AUDIO_LEA_RVA,g_audio_original_lea,7);
    memcpy(base+HKTAS_AUDIO_UNWIND_RVA,g_audio_original_unwind,sizeof(g_audio_original_unwind));
    AUDIO_IMAGE_CHECK(hktas_audio_image(base,TRUE));
    const DWORD pointers[]={0x1a28450u,0x19d7a48u,0x1a58a10u,0x1a58a18u,0x1a58a20u,0x1a58a28u,0x1a58a30u};
    for (DWORD i=0;i<sizeof(pointers)/sizeof(pointers[0]);++i) {
        uintptr_t value=1; memcpy(base+pointers[i],&value,8); AUDIO_IMAGE_CHECK(!hktas_audio_image(base,TRUE));
        value=0; memcpy(base+pointers[i],&value,8);
    }
    base[0x1a4e450u]=1; AUDIO_IMAGE_CHECK(!hktas_audio_image(base,TRUE)); base[0x1a4e450u]=0;
    base[HKTAS_AUDIO_THREAD_RVA+95]^=1; AUDIO_IMAGE_CHECK(!hktas_audio_image(base,TRUE)); base[HKTAS_AUDIO_THREAD_RVA+95]^=1;
    base[0x1114]=2; AUDIO_IMAGE_CHECK(!hktas_audio_image(base,TRUE)); base[0x1114]=1;
    if(image_size) *image_size=bytes;
    return base;
}
#endif
