/* Isolated filesystem fixture only. No UnityPlayer load or process termination. */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <string.h>
static volatile LONG inject_short_write,inject_rename_failure,block_first_write,first_write_claimed;
static HANDLE write_blocked,write_release,worker_returned;
static BOOL WINAPI evidence_write(HANDLE file,LPCVOID bytes,DWORD length,LPDWORD written,LPOVERLAPPED overlapped)
{ if(InterlockedCompareExchange(&block_first_write,0,0) && InterlockedCompareExchange(&first_write_claimed,1,0)==0) { SetEvent(write_blocked); if(WaitForSingleObject(write_release,5000)!=WAIT_OBJECT_0)return FALSE; } return WriteFile(file,bytes,InterlockedCompareExchange(&inject_short_write,0,0) && length ? length-1 : length,written,overlapped); }
static BOOL WINAPI evidence_move(LPCWSTR from,LPCWSTR to,DWORD flags)
{ if(InterlockedCompareExchange(&inject_rename_failure,0,0)){SetLastError(ERROR_ACCESS_DENIED);return FALSE;}return MoveFileExW(from,to,flags); }
#define WriteFile evidence_write
#define MoveFileExW evidence_move
#include "../../native/HollowKnightTAS.ClockBridge/resident_fatal_evidence.h"
#undef WriteFile
#undef MoveFileExW
#define CHECK(x) do { if(!(x)){fprintf(stderr,"FAIL line=%d %s error=%lu\n",__LINE__,#x,(unsigned long)GetLastError());exit(1);} }while(0)
static WCHAR directory[1024],final_path[1152];
static HANDLE start_event;static volatile LONG returned_without_complete;
static DWORD WINAPI worker(void *unused)
{ (void)unused;CHECK(WaitForSingleObject(start_event,5000)==WAIT_OBJECT_0);hktas_resident_record_fatal("concurrent",91);if(!hktas_resident_fatal_complete(final_path))InterlockedIncrement(&returned_without_complete);if(worker_returned)SetEvent(worker_returned);return 0; }
static int temporary_count(void)
{ WCHAR pattern[1200];WIN32_FIND_DATAW data;swprintf(pattern,1200,L"%ls.tmp-*",final_path);HANDLE search=FindFirstFileW(pattern,&data);if(search==INVALID_HANDLE_VALUE){CHECK(GetLastError()==ERROR_FILE_NOT_FOUND);return 0;}int count=1;while(FindNextFileW(search,&data))count++;FindClose(search);return count; }
static void remove_final(void){if(!DeleteFileW(final_path))CHECK(GetLastError()==ERROR_FILE_NOT_FOUND);}
static DWORD read_final(char *bytes,DWORD count)
{ HANDLE file=CreateFileW(final_path,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);CHECK(file!=INVALID_HANDLE_VALUE);DWORD got;CHECK(ReadFile(file,bytes,count,&got,NULL));CHECK(CloseHandle(file));return got; }
static void seed_final(const char *bytes,DWORD size)
{ HANDLE file=CreateFileW(final_path,GENERIC_WRITE,0,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);CHECK(file!=INVALID_HANDLE_VALUE);DWORD wrote;CHECK(WriteFile(file,bytes,size,&wrote,NULL)&&wrote==size);CHECK(CloseHandle(file)); }
int main(int argc,char **argv)
{
 CHECK(argc==2);WCHAR root[900];CHECK(MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,argv[1],-1,root,900)>0);
 CHECK(swprintf(directory,1024,L"%ls/fatal-fixture-%lu",root,(unsigned long)GetCurrentProcessId())>0);CHECK(CreateDirectoryW(directory,NULL));
 CHECK(swprintf(final_path,1152,L"%ls/resident-native-fatal-%lu.txt",directory,(unsigned long)GetCurrentProcessId())>0);
 CHECK(SetEnvironmentVariableW(L"HKTAS_LOADING_PROBE_OUTPUT",NULL));hktas_resident_record_fatal("absent",1);CHECK(GetFileAttributesW(final_path)==INVALID_FILE_ATTRIBUTES);
 CHECK(SetEnvironmentVariableW(L"HKTAS_LOADING_PROBE_OUTPUT",L"relative"));hktas_resident_record_fatal("relative",1);CHECK(GetFileAttributesW(final_path)==INVALID_FILE_ATTRIBUTES);
 CHECK(SetEnvironmentVariableW(L"HKTAS_LOADING_PROBE_OUTPUT",directory));
 start_event=CreateEventW(NULL,TRUE,FALSE,NULL);CHECK(start_event!=NULL);HANDLE workers[32];for(int i=0;i<32;i++){workers[i]=CreateThread(NULL,0,worker,NULL,0,NULL);CHECK(workers[i]!=NULL);}CHECK(SetEvent(start_event));CHECK(WaitForMultipleObjects(32,workers,TRUE,10000)==WAIT_OBJECT_0);for(int i=0;i<32;i++)CHECK(CloseHandle(workers[i]));CHECK(CloseHandle(start_event));CHECK(returned_without_complete==0);CHECK(hktas_resident_fatal_complete(final_path));CHECK(temporary_count()==0);
 char before[160],after[160];DWORD size=read_final(before,160);hktas_resident_record_fatal("must-not-overwrite",99);CHECK(read_final(after,160)==size && memcmp(before,after,size)==0);CHECK(temporary_count()==0);remove_final();
 /* Deterministically stall the first private writer. A different fatal thread
  * must publish and return complete evidence without waiting for that writer. */
 start_event=CreateEventW(NULL,TRUE,TRUE,NULL);write_blocked=CreateEventW(NULL,TRUE,FALSE,NULL);write_release=CreateEventW(NULL,TRUE,FALSE,NULL);worker_returned=CreateEventW(NULL,TRUE,FALSE,NULL);CHECK(start_event&&write_blocked&&write_release&&worker_returned);block_first_write=1;
 workers[0]=CreateThread(NULL,0,worker,NULL,0,NULL);CHECK(workers[0]);CHECK(WaitForSingleObject(write_blocked,5000)==WAIT_OBJECT_0);workers[1]=CreateThread(NULL,0,worker,NULL,0,NULL);CHECK(workers[1]);CHECK(WaitForSingleObject(worker_returned,5000)==WAIT_OBJECT_0);CHECK(hktas_resident_fatal_complete(final_path));CHECK(SetEvent(write_release));CHECK(WaitForMultipleObjects(2,workers,TRUE,5000)==WAIT_OBJECT_0);CHECK(returned_without_complete==0);CHECK(temporary_count()==0);for(int i=0;i<2;i++)CHECK(CloseHandle(workers[i]));CHECK(CloseHandle(start_event));CHECK(CloseHandle(write_blocked));CHECK(CloseHandle(write_release));CHECK(CloseHandle(worker_returned));worker_returned=NULL;block_first_write=0;remove_final();
 inject_short_write=1;hktas_resident_record_fatal("short-write",2);inject_short_write=0;CHECK(GetFileAttributesW(final_path)==INVALID_FILE_ATTRIBUTES);CHECK(temporary_count()==0);
 inject_rename_failure=1;hktas_resident_record_fatal("rename-failure",3);inject_rename_failure=0;CHECK(GetFileAttributesW(final_path)==INVALID_FILE_ATTRIBUTES);CHECK(temporary_count()==0);
 seed_final("",0);hktas_resident_record_fatal("existing-incomplete",4);CHECK(!hktas_resident_fatal_complete(final_path));CHECK(read_final(after,160)==0);CHECK(temporary_count()==0);remove_final();
 const char *existing="domain=existing reason=5 thread=11 wallMs=123\n";seed_final(existing,(DWORD)strlen(existing));CHECK(hktas_resident_fatal_complete(final_path));hktas_resident_record_fatal("later",6);CHECK(hktas_resident_fatal_complete(final_path));remove_final();
 CHECK(SetEnvironmentVariableW(L"HKTAS_LOADING_PROBE_OUTPUT",NULL));CHECK(RemoveDirectoryW(directory));
 puts("PASS fatal evidence: 32 concurrent returns and stalled-first-writer race see complete publication; existing final preserved; no output directory; short-write/rename failure/private cleanup; incomplete final never overwritten");return 0;
}
