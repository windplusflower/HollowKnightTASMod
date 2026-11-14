#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include "../../native/HollowKnightTAS.ClockBridge/resident_depth_image.h"
#define CHECK(x) do { if (!(x)) { fprintf(stderr,"FAIL %d: %s\n",__LINE__,#x); exit(1); } } while (0)

static hktas_depth_job *capture(ID3D11Texture2D *texture)
{
    hktas_depth_job *job = HktasClockBridge_BeginDepthImage(texture,NULL,0);
    CHECK(job != NULL); hktas_depth_render_event(1,job);
    BYTE *bytes; uint32_t count; int32_t result;
    CHECK(HktasClockBridge_ReadDepthImage(job,0,&bytes,&count,&result)==1 && bytes && count>=24 && result==S_OK);
    return job;
}
int main(void)
{
    ID3D11Device *device = NULL; ID3D11DeviceContext *context = NULL; D3D_FEATURE_LEVEL feature;
    CHECK(SUCCEEDED(D3D11CreateDevice(NULL,D3D_DRIVER_TYPE_WARP,NULL,0,NULL,0,D3D11_SDK_VERSION,&device,&feature,&context)));
    CHECK(feature>=D3D_FEATURE_LEVEL_10_1);
    const DXGI_FORMAT formats[] = {DXGI_FORMAT_R16_TYPELESS,DXGI_FORMAT_R24G8_TYPELESS,DXGI_FORMAT_R32_TYPELESS,DXGI_FORMAT_R32G8X24_TYPELESS};
    for (uint32_t f=0;f<sizeof(formats)/sizeof(formats[0]);f++) {
        D3D11_TEXTURE2D_DESC desc = {0}; desc.Width=7; desc.Height=3; desc.MipLevels=1; desc.ArraySize=1;
        desc.Format=formats[f]; desc.SampleDesc.Count=1; desc.Usage=D3D11_USAGE_DEFAULT;
        desc.BindFlags=D3D11_BIND_DEPTH_STENCIL|D3D11_BIND_SHADER_RESOURCE;
        BYTE pattern[7*3*8]; uint32_t stride=hktas_depth_stride(formats[f]);
        for (uint32_t i=0;i<sizeof(pattern);i++) pattern[i]=(BYTE)(i*17+f);
        D3D11_SUBRESOURCE_DATA initial = {pattern,7*stride,7*3*stride};
        ID3D11Texture2D *source=NULL,*target=NULL;
        CHECK(SUCCEEDED(ID3D11Device_CreateTexture2D(device,&desc,&initial,&source)));
        CHECK(SUCCEEDED(ID3D11Device_CreateTexture2D(device,&desc,NULL,&target)));
        hktas_depth_job *read = capture(source);
        CHECK(read->length==24+7*3*stride && !memcmp(read->bytes+24,pattern,7*3*stride));
        hktas_depth_job *write = HktasClockBridge_BeginDepthImage(target,read->bytes,read->length);
        CHECK(write); hktas_depth_render_event(1,write);
        BYTE *bytes; uint32_t count; int32_t result;
        CHECK(HktasClockBridge_ReadDepthImage(write,0,&bytes,&count,&result)==1);
        HktasClockBridge_ReleaseDepthImage(write);
        hktas_depth_job *after = capture(target);
        CHECK(after->length==read->length && !memcmp(after->bytes,read->bytes,read->length));
        HktasClockBridge_ReleaseDepthImage(after);
        write=HktasClockBridge_BeginDepthImage(target,read->bytes,read->length); CHECK(write);
        write->bytes[4]^=1; hktas_depth_render_event(1,write);
        CHECK(HktasClockBridge_ReadDepthImage(write,0,&bytes,&count,&result)==-1 && result==E_INVALIDARG);
        HktasClockBridge_ReleaseDepthImage(write);
        HktasClockBridge_ReleaseDepthImage(read);
        /* Simulate a timeout: render event still owns the resource and job. */
        write=HktasClockBridge_BeginDepthImage(target,NULL,0); CHECK(write);
        CHECK(HktasClockBridge_ReadDepthImage(write,0,&bytes,&count,&result)==0);
        HktasClockBridge_ReleaseDepthImage(write); hktas_depth_render_event(1,write);
        ID3D11Texture2D_Release(source); ID3D11Texture2D_Release(target);
    }
    CHECK(HktasClockBridge_BeginDepthImage(NULL,NULL,0)==NULL);
    ID3D11DeviceContext_Release(context); ID3D11Device_Release(device);
    puts("PASS D3D11 WARP depth/stencil: four native formats, padded rows, exact source-target bytes, malformed descriptor rejection and timeout ownership");
    return 0;
}
