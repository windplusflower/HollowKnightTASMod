#define main hktas_unused_fixture_main
#include "ResidentClipCurveLedgerHarness.c"
#undef main
typedef void (__cdecl *mono_barrier_fn)(void *,void **,void *);
static mono_barrier_fn barrier;
static void (__cdecl *collect)(int);
static int (__cdecl *string_length)(void *);
static void *(__cdecl *invalid_operation)(const char *);
static void (__cdecl *raise_exception)(void *);
static void *(__cdecl *invoke)(void *,void *,void **,void **);
static BOOL callback_mode;
static void *callback;
static LONG callback_exceptions,root_count;
static void __cdecl native_original(void *self,const void *path,const void *type,int32_t script,const void *attribute,const void *curve,unsigned char combine,unsigned char update)
{
    CHECK(self==&clips[0] && type && curve && g_cl_active==1 && script==57 && combine && update);
    collect(2);
    CHECK(string_length((void *)path)==100 && string_length((void *)attribute)==100);
    if (callback_mode) {
        void *exception=NULL;invoke(callback,NULL,NULL,&exception);
        CHECK(exception);++callback_exceptions;
    }
}
/* Models the pinned generated wrapper boundary: actual Mono roots, then
 * marshalling exception, then native method. The ledger sees no Mono refs. */
static void __cdecl outer_generated(void *self,void *path,void *type,void *attribute,void *curve)
{
    void * volatile roots[5]={0};void *values[5]={self,path,type,attribute,curve};
    for(unsigned i=0;i<5;i++){barrier(NULL,(void **)&roots[i],values[i]);++root_count;}
    if (!roots[2]) raise_exception(invalid_operation("marshalling fixture exception"));
    uintptr_t pointer=0;CHECK(hktas_cl_read((uintptr_t)roots[0]+16,&pointer,8));
    ((hktas_cl_eight_fn)(void *)(g_cl_base+g_cl_entries[0].rva))((void *)pointer,roots[1],roots[2],57,roots[3],roots[4],1,1);
}
#define LOAD(name,type) ((type)(void *)GetProcAddress(mono,name))
int main(int argc,char **argv)
{
    CHECK(argc==6);(void)hktas_de_install;(void)hktas_de_verify;
    BYTE *base=image(argv[1]);CHECK(install_resident_clip_curve_ledger(base,GetCurrentThreadId(),sink));
    HMODULE mono=LoadLibraryA(argv[2]);CHECK(mono);
    void (__cdecl *set_dirs)(const char *,const char *)=LOAD("mono_set_dirs",void (__cdecl *)(const char *,const char *));
    void *(__cdecl *jit_init)(const char *,const char *)=LOAD("mono_jit_init_version",void *(__cdecl *)(const char *,const char *));
    void (__cdecl *add_icall)(const char *,const void *)=LOAD("mono_add_internal_call",void (__cdecl *)(const char *,const void *));
    void *(__cdecl *assembly_open)(void *,const char *)=LOAD("mono_domain_assembly_open",void *(__cdecl *)(void *,const char *));
    void *(__cdecl *assembly_image)(void *)=LOAD("mono_assembly_get_image",void *(__cdecl *)(void *));
    void *(__cdecl *class_name)(void *,const char *,const char *)=LOAD("mono_class_from_name",void *(__cdecl *)(void *,const char *,const char *));
    void *(__cdecl *method_name)(void *,const char *,int)=LOAD("mono_class_get_method_from_name",void *(__cdecl *)(void *,const char *,int));
    invoke=LOAD("mono_runtime_invoke",void *(__cdecl *)(void *,void *,void **,void **));
    void *(__cdecl *unbox)(void *)=LOAD("mono_object_unbox",void *(__cdecl *)(void *));
    barrier=LOAD("mono_gc_wbarrier_set_field",mono_barrier_fn);
    collect=LOAD("mono_gc_collect",void (__cdecl *)(int));string_length=LOAD("mono_string_length",int (__cdecl *)(void *));
    invalid_operation=LOAD("mono_get_exception_invalid_operation",void *(__cdecl *)(const char *));raise_exception=LOAD("mono_raise_exception",void (__cdecl *)(void *));
    CHECK(set_dirs && jit_init && add_icall && assembly_open && assembly_image && class_name && method_name && invoke && unbox && barrier && collect && string_length && invalid_operation && raise_exception);
    set_dirs(argv[3],argv[3]);void *domain=jit_init("clip-ledger-host","v4.0.30319");CHECK(domain);
    add_icall("ClipLedgerMonoFixture::Mutate",(void *)outer_generated);
    void *assembly=assembly_open(domain,argv[4]);CHECK(assembly);
    void *klass=class_name(assembly_image(assembly),"","ClipLedgerMonoFixture");CHECK(klass);
    void *method=method_name(klass,"Run",2);CHECK(method);callback=method_name(klass,"ThrowCallback",0);CHECK(callback);
    clips[0].vt=(uintptr_t)(base+0x171d808u);clips[0].id=17;g_cl_original[0]=(void *)native_original;
    int mode=!strcmp(argv[5],"marshal-throw") ? 1 : !strcmp(argv[5],"callback-throw") ? 2 : 0;callback_mode=mode==2;
    intptr_t native=(intptr_t)&clips[0];void *args[]={&native,&mode};void *exception=NULL;
    void *result=invoke(method,NULL,args,&exception);CHECK(!exception && result);
    int value=*(int *)unbox(result);CHECK(value==(mode==1 ? 42 : 10));
    printf("MONO native-entry result=%d roots=%ld active=%u attempted=%llu completed=%llu callbacks=%ld\n",value,root_count,g_cl_active,(unsigned long long)g_cl_attempted,(unsigned long long)g_cl_completed,callback_exceptions);
    CHECK(root_count==5 && !g_cl_active && g_cl_attempted==(mode==1?0u:1u) && g_cl_completed==g_cl_attempted);
    CHECK(!mode || mode==1 || callback_exceptions==1);
    CHECK(hktas_cl_begin(1,GetTickCount64(),TRUE));CHECK(query(0,1)==(mode==1?1:2));CHECK(end(1));
    return 0;
}
