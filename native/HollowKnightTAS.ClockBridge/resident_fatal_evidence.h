#ifndef HKTAS_RESIDENT_FATAL_EVIDENCE_H
#define HKTAS_RESIDENT_FATAL_EVIDENCE_H
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

/* A native gate can terminate before the managed exception handler runs.
 * Only the protected probe supplies an evidence directory. Failure to write
 * must never affect the existing fault or allow a callback to proceed. */
static void hktas_resident_record_fatal(const char *domain, LONG reason)
{
    WCHAR path[1024];
    const DWORD capacity = (DWORD)(sizeof(path) / sizeof(path[0]));
    DWORD n = GetEnvironmentVariableW(L"HKTAS_LOADING_PROBE_OUTPUT", path, capacity);
    if (n < 3 || n >= capacity - 64 || path[1] != L':' ||
        (path[2] != L'\\' && path[2] != L'/')) return;
    DWORD attributes = GetFileAttributesW(path);
    if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY)) return;
    if (swprintf(path+n,capacity-n,L"/resident-native-fatal-%lu.txt",(unsigned long)GetCurrentProcessId()) < 0) return;
    HANDLE file = CreateFileW(path,GENERIC_WRITE,FILE_SHARE_READ,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);
    if (file == INVALID_HANDLE_VALUE) return;
    char record[160];
    int size = snprintf(record,sizeof(record),"domain=%s reason=%ld thread=%lu wallMs=%llu\n",
        domain,(long)reason,(unsigned long)GetCurrentThreadId(),(unsigned long long)GetTickCount64());
    if (size > 0 && size < (int)sizeof(record)) {
        DWORD bytes;
        (void)WriteFile(file,record,(DWORD)size,&bytes,NULL);
    }
    CloseHandle(file);
}
#endif
