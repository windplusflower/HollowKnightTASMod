#ifndef HKTAS_RESIDENT_DEPTH_IMAGE_H
#define HKTAS_RESIDENT_DEPTH_IMAGE_H
#define COBJMACROS
#include <d3d11.h>
#include <stdlib.h>
#include <string.h>

/* Invoked through Unity's render-thread plugin event. No immediate-context
 * calls run on the managed capture thread. Only this referenced attachment is
 * copied; device state, resource directories and gameplay frames are untouched. */
#define HKTAS_DEPTH_MAX_BYTES (32u * 1024u * 1024u)
typedef struct {
    uint32_t magic, width, height, mips, slices, format;
} hktas_depth_header;
typedef struct {
    volatile LONG references;
    HANDLE done;
    ID3D11Texture2D *texture;
    BYTE *bytes;
    uint32_t length;
    HRESULT result;
    BOOL writing;
} hktas_depth_job;
static const GUID hktas_texture2d_iid = {0x6f15aaf2,0xd208,0x4e89,{0x9a,0xb4,0x48,0x95,0x35,0xd3,0x4f,0x9c}};

static void hktas_depth_release(hktas_depth_job *job)
{
    if (!job || InterlockedDecrement(&job->references)) return;
    if (job->texture) ID3D11Texture2D_Release(job->texture);
    if (job->done) CloseHandle(job->done);
    free(job->bytes); free(job);
}
static uint32_t hktas_depth_stride(DXGI_FORMAT format)
{
    switch (format) {
    case DXGI_FORMAT_R16_TYPELESS: case DXGI_FORMAT_D16_UNORM: return 2;
    case DXGI_FORMAT_R24G8_TYPELESS: case DXGI_FORMAT_D24_UNORM_S8_UINT:
    case DXGI_FORMAT_R32_TYPELESS: case DXGI_FORMAT_D32_FLOAT: return 4;
    case DXGI_FORMAT_R32G8X24_TYPELESS: case DXGI_FORMAT_D32_FLOAT_S8X24_UINT: return 8;
    default: return 0;
    }
}
static uint32_t hktas_depth_extent(uint32_t value, uint32_t mip)
{ value >>= mip; return value ? value : 1; }
static HRESULT hktas_depth_transfer(hktas_depth_job *job)
{
    D3D11_TEXTURE2D_DESC desc;
    ID3D11Texture2D_GetDesc(job->texture,&desc);
    uint32_t stride = hktas_depth_stride(desc.Format);
    if (!stride || !desc.Width || !desc.Height || desc.Width > 32768 || desc.Height > 32768
        || !desc.MipLevels || desc.MipLevels > 16 || !desc.ArraySize || desc.ArraySize > 2048
        || desc.SampleDesc.Count != 1) return E_INVALIDARG;
    uint64_t length = sizeof(hktas_depth_header);
    for (uint32_t mip=0;mip<desc.MipLevels;mip++)
        length += (uint64_t)hktas_depth_extent(desc.Width,mip)*hktas_depth_extent(desc.Height,mip)*stride*desc.ArraySize;
    if (length > HKTAS_DEPTH_MAX_BYTES) return E_OUTOFMEMORY;
    hktas_depth_header header = {0x31504452u,desc.Width,desc.Height,desc.MipLevels,desc.ArraySize,(uint32_t)desc.Format};
    if (job->writing) {
        if (job->length != length || memcmp(job->bytes,&header,sizeof(header))) return E_INVALIDARG;
    } else {
        job->bytes = (BYTE *)malloc((size_t)length);
        if (!job->bytes) return E_OUTOFMEMORY;
        job->length = (uint32_t)length; memcpy(job->bytes,&header,sizeof(header));
    }
    ID3D11Device *device = NULL; ID3D11DeviceContext *context = NULL; ID3D11Texture2D *staging = NULL;
    ID3D11Texture2D_GetDevice(job->texture,&device);
    if (!device) return E_FAIL;
    HRESULT result = E_FAIL;
    if (ID3D11Device_GetFeatureLevel(device) < D3D_FEATURE_LEVEL_10_1) goto cleanup;
    ID3D11Device_GetImmediateContext(device,&context);
    if (!context) goto cleanup;
    desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.MiscFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ | D3D11_CPU_ACCESS_WRITE;
    result = ID3D11Device_CreateTexture2D(device,&desc,NULL,&staging);
    if (FAILED(result)) goto cleanup;
    if (!job->writing) ID3D11DeviceContext_CopyResource(context,(ID3D11Resource *)staging,(ID3D11Resource *)job->texture);
    uint32_t at = sizeof(header);
    for (uint32_t slice=0;slice<desc.ArraySize;slice++) {
        for (uint32_t mip=0;mip<desc.MipLevels;mip++) {
            uint32_t sub = slice*desc.MipLevels+mip, row = hktas_depth_extent(desc.Width,mip)*stride;
            uint32_t height = hktas_depth_extent(desc.Height,mip);
            D3D11_MAPPED_SUBRESOURCE mapped;
            result = ID3D11DeviceContext_Map(context,(ID3D11Resource *)staging,sub,job->writing ? D3D11_MAP_WRITE : D3D11_MAP_READ,0,&mapped);
            if (FAILED(result)) goto cleanup;
            if (!mapped.pData || mapped.RowPitch < row) {
                ID3D11DeviceContext_Unmap(context,(ID3D11Resource *)staging,sub); result = E_FAIL; goto cleanup;
            }
            for (uint32_t y=0;y<height;y++) {
                BYTE *gpu = (BYTE *)mapped.pData+(size_t)y*mapped.RowPitch;
                if (job->writing) memcpy(gpu,job->bytes+at,row); else memcpy(job->bytes+at,gpu,row);
                at += row;
            }
            ID3D11DeviceContext_Unmap(context,(ID3D11Resource *)staging,sub);
        }
    }
    if (job->writing) ID3D11DeviceContext_CopyResource(context,(ID3D11Resource *)job->texture,(ID3D11Resource *)staging);
    result = ID3D11Device_GetDeviceRemovedReason(device);
cleanup:
    if (staging) ID3D11Texture2D_Release(staging);
    if (context) ID3D11DeviceContext_Release(context);
    ID3D11Device_Release(device); return result;
}
static void __stdcall hktas_depth_render_event(int event, void *data)
{
    hktas_depth_job *job = (hktas_depth_job *)data;
    if (!job) return;
    job->result = event == 1 ? hktas_depth_transfer(job) : E_INVALIDARG;
    SetEvent(job->done); hktas_depth_release(job);
}
__declspec(dllexport) void *__cdecl HktasClockBridge_GetDepthImageRenderEvent(void)
{ return (void *)hktas_depth_render_event; }
__declspec(dllexport) void *__cdecl HktasClockBridge_BeginDepthImage(void *resource, const BYTE *bytes, uint32_t length)
{
    if (!resource || length > HKTAS_DEPTH_MAX_BYTES || (length && !bytes)) return NULL;
    hktas_depth_job *job = (hktas_depth_job *)calloc(1,sizeof(*job));
    if (!job) return NULL;
    job->references = 1;
    if (FAILED(IUnknown_QueryInterface((IUnknown *)resource,&hktas_texture2d_iid,(void **)&job->texture))) goto fail;
    job->done = CreateEventW(NULL,TRUE,FALSE,NULL);
    if (!job->done) goto fail;
    job->writing = length != 0; job->result = E_PENDING;
    if (length) {
        job->bytes = (BYTE *)malloc(length);
        if (!job->bytes) goto fail;
        memcpy(job->bytes,bytes,length); job->length = length;
    }
    /* The submitted event owns a reference even if managed waiting times out. */
    InterlockedIncrement(&job->references); return job;
fail:
    hktas_depth_release(job); return NULL;
}
__declspec(dllexport) int __cdecl HktasClockBridge_ReadDepthImage(void *handle, uint32_t wait_ms, BYTE **bytes, uint32_t *length, int32_t *result)
{
    hktas_depth_job *job = (hktas_depth_job *)handle;
    if (!job || !bytes || !length || !result || wait_ms > 5000) return 0;
    *bytes = NULL; *length = 0; *result = E_PENDING;
    if (WaitForSingleObject(job->done,wait_ms) != WAIT_OBJECT_0) return 0;
    *result = job->result;
    if (FAILED(job->result)) return -1;
    *bytes = job->bytes; *length = job->length; return 1;
}
__declspec(dllexport) void __cdecl HktasClockBridge_ReleaseDepthImage(void *handle)
{ hktas_depth_release((hktas_depth_job *)handle); }
#endif
