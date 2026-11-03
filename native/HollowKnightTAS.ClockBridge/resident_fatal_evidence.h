#ifndef HKTAS_RESIDENT_FATAL_EVIDENCE_H
#define HKTAS_RESIDENT_FATAL_EVIDENCE_H
#include <windows.h>
#include <stdio.h>
#include <wchar.h>
#include <string.h>

/* A final name is evidence only after a complete private file has been closed
 * and atomically renamed. Never claim the shared final name before writing:
 * another fatal thread may terminate the process immediately after returning.
 * File I/O failure still returns to the existing fail-closed termination path;
 * there are no retry waits, timeout extensions, or changes to gate admission. */
static BOOL hktas_resident_fatal_complete(const WCHAR *path)
{
    HANDLE file=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_DELETE,NULL,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);
    if (file==INVALID_HANDLE_VALUE) return FALSE;
    LARGE_INTEGER length;
    char record[160],domain[64]; DWORD bytes=0; int consumed=0;
    long reason; unsigned long thread; unsigned long long wall;
    BOOL complete=GetFileSizeEx(file,&length) && length.QuadPart>0 && length.QuadPart<(LONGLONG)sizeof(record);
    if (complete) complete=ReadFile(file,record,(DWORD)length.QuadPart,&bytes,NULL) && bytes==(DWORD)length.QuadPart;
    if (!CloseHandle(file)) complete=FALSE;
    if (!complete || memchr(record,0,bytes)!=NULL || record[bytes-1]!='\n') return FALSE;
    record[bytes]=0;
    return sscanf(record,"domain=%63[A-Za-z0-9_-] reason=%ld thread=%lu wallMs=%llu%n",domain,&reason,&thread,&wall,&consumed)==4
        && thread!=0 && consumed==(int)bytes-1;
}
static void hktas_resident_record_fatal(const char *domain, LONG reason)
{
    static volatile LONG sequence;
    WCHAR path[1024],temporary[1152];
    const DWORD capacity=(DWORD)(sizeof(path)/sizeof(path[0]));
    DWORD n=GetEnvironmentVariableW(L"HKTAS_LOADING_PROBE_OUTPUT",path,capacity);
    if (n<3 || n>=capacity-64 || path[1]!=L':' || (path[2]!=L'\\' && path[2]!=L'/')) return;
    DWORD attributes=GetFileAttributesW(path);
    if (attributes==INVALID_FILE_ATTRIBUTES || !(attributes&FILE_ATTRIBUTE_DIRECTORY)) return;
    if (swprintf(path+n,capacity-n,L"/resident-native-fatal-%lu.txt",(unsigned long)GetCurrentProcessId())<0) return;
    if (hktas_resident_fatal_complete(path)) return;
    if (swprintf(temporary,sizeof(temporary)/sizeof(temporary[0]),L"%ls.tmp-%lu-%lu",path,
        (unsigned long)GetCurrentThreadId(),(unsigned long)InterlockedIncrement(&sequence))<0) return;
    char record[160];
    int size=snprintf(record,sizeof(record),"domain=%s reason=%ld thread=%lu wallMs=%llu\n",
        domain,(long)reason,(unsigned long)GetCurrentThreadId(),(unsigned long long)GetTickCount64());
    if (size<=0 || size>=(int)sizeof(record)) return;
    HANDLE file=CreateFileW(temporary,GENERIC_WRITE,0,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);
    if (file==INVALID_HANDLE_VALUE) return;
    DWORD bytes=0; LARGE_INTEGER length;
    BOOL complete=WriteFile(file,record,(DWORD)size,&bytes,NULL) && bytes==(DWORD)size
        && GetFileSizeEx(file,&length) && length.QuadPart==size;
    if (!CloseHandle(file)) complete=FALSE;
    if (complete && MoveFileExW(temporary,path,0)) return;
    /* Only this invocation owns this CREATE_NEW temporary. Never remove or
     * replace a published final, another thread's temporary, or a stale file. */
    (void)DeleteFileW(temporary);
    if (complete) (void)hktas_resident_fatal_complete(path);
}
#endif
