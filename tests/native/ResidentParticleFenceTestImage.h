#ifndef HKTAS_RESIDENT_PARTICLE_FENCE_TEST_IMAGE_H
#define HKTAS_RESIDENT_PARTICLE_FENCE_TEST_IMAGE_H
/* Include after resident_particle_producer.h (or clock_bridge.c).
 * Input is a trusted path to the pinned UnityPlayer.dll, opened as ordinary
 * read-only data. The returned PAGE_READWRITE buffer never executes PE entry,
 * imports, TLS callbacks or engine code. Caller owns VirtualFree on teardown.
 * Registration slots are relocated from the on-disk preferred image base. */
#include <stdio.h>
#include <stdlib.h>
#define HKTAS_PS_FIXTURE_CHECK(x) do { if (!(x)) { fprintf(stderr,"particle image fixture failed, line %d: %s\n",__LINE__,#x); exit(1); } } while (0)
static BYTE *hktas_ps_test_image_from_file(const char *filename)
{
    HANDLE file=CreateFileA(filename,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,NULL,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);
    HKTAS_PS_FIXTURE_CHECK(file!=INVALID_HANDLE_VALUE); LARGE_INTEGER size; HKTAS_PS_FIXTURE_CHECK(GetFileSizeEx(file,&size) && size.QuadPart>=4096 && size.QuadPart<INT32_MAX);
    HANDLE mapping=CreateFileMappingW(file,NULL,PAGE_READONLY,0,0,NULL); HKTAS_PS_FIXTURE_CHECK(mapping);
    BYTE *raw=(BYTE *)MapViewOfFile(mapping,FILE_MAP_READ,0,0,0); HKTAS_PS_FIXTURE_CHECK(raw);
    IMAGE_DOS_HEADER *dos=(IMAGE_DOS_HEADER *)raw;
    HKTAS_PS_FIXTURE_CHECK(dos->e_magic==IMAGE_DOS_SIGNATURE && dos->e_lfanew>=64 && dos->e_lfanew<=4096);
    IMAGE_NT_HEADERS64 *nt=(IMAGE_NT_HEADERS64 *)(raw+dos->e_lfanew);
    HKTAS_PS_FIXTURE_CHECK(nt->Signature==IMAGE_NT_SIGNATURE && nt->OptionalHeader.SizeOfImage==0x1bc7000u
        && nt->OptionalHeader.SizeOfHeaders<=4096 && nt->FileHeader.NumberOfSections<=64);
    BYTE *image=(BYTE *)VirtualAlloc(NULL,nt->OptionalHeader.SizeOfImage,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE); HKTAS_PS_FIXTURE_CHECK(image);
    memcpy(image,raw,nt->OptionalHeader.SizeOfHeaders);
    IMAGE_SECTION_HEADER *sections=IMAGE_FIRST_SECTION(nt);
    for (WORD i=0;i<nt->FileHeader.NumberOfSections;++i) {
        IMAGE_SECTION_HEADER s=sections[i]; HKTAS_PS_FIXTURE_CHECK((uint64_t)s.PointerToRawData+s.SizeOfRawData<=(uint64_t)size.QuadPart
            && (uint64_t)s.VirtualAddress+s.SizeOfRawData<=nt->OptionalHeader.SizeOfImage);
        memcpy(image+s.VirtualAddress,raw+s.PointerToRawData,s.SizeOfRawData);
    }
    for (size_t i=0;i<5;++i) {
        void *pointer=NULL; memcpy(&pointer,image+g_ps_bindings[i].slot,8);
        HKTAS_PS_FIXTURE_CHECK((uintptr_t)pointer==nt->OptionalHeader.ImageBase+g_ps_bindings[i].code);
        pointer=image+g_ps_bindings[i].code; memcpy(image+g_ps_bindings[i].slot,&pointer,8);
    }
    HKTAS_PS_FIXTURE_CHECK(UnmapViewOfFile(raw)); CloseHandle(mapping); CloseHandle(file); return image;
}
#undef HKTAS_PS_FIXTURE_CHECK
#endif
