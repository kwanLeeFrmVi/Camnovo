// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Lenovo Camera Studio contributors
// This original implementation is distributed under LICENSE-DENOISER-MIT.txt.
#include "stdafx.h"
#include "open_denoise.h"
#include "locked_frame.h"
#include <d3dcompiler.h>
#pragma comment(lib,"d3d11.lib")
#pragma comment(lib,"d3dcompiler.lib")
namespace {
void Hr(HRESULT hr){if(FAILED(hr))throw hr;}
// Byte-address buffers keep NV12 on the GPU without RGB conversion or
// optional R8 typed-UAV support. Each thread writes four packed output bytes.
const char* shader=R"(
ByteAddressBuffer source : register(t0);
RWByteAddressBuffer destination : register(u0);
cbuffer Parameters : register(b0) { uint width; uint height; uint bytes; float sigma; uint chromaOnly; uint radius; float guideSigma; uint padding; };
float Read(uint index) { return (source.Load(index & ~3u) >> ((index & 3u)*8u)) & 255u; }
uint Filter(uint index) {
    if(index>=bytes)return 0;
    bool chroma=index>=width*height;
    if(chromaOnly!=0&&!chroma)return uint(Read(index));
    uint base=chroma?width*height:0;
    uint pos=index-base;
    int x=pos%width,y=pos/width;
    int step=chroma?2:1, parity=chroma?(x&1):0;
    int rows=chroma?height/2:height;
    float center=Read(index),sum=0,weights=0;
    float rangeSigma=chroma?sigma*1.4:sigma;
    int r=chroma?int(radius):2;
    float guide=chroma?Read((y*2)*width+(x&~1)):0;
    [loop]for(int dy=-r;dy<=r;dy++) [loop]for(int dx=-r;dx<=r;dx++) {
        int sx=clamp(x+dx*step,parity,int(width)-step+parity);
        int sy=clamp(y+dy,0,rows-1);
        float value=Read(base+sy*width+sx),difference=value-center;
        float weight=exp2(-0.32*(dx*dx+dy*dy)-0.72134752*difference*difference/(rangeSigma*rangeSigma));
        if(chromaOnly!=0){float guideDifference=Read((sy*2)*width+(sx&~1))-guide;weight*=exp2(-0.72134752*guideDifference*guideDifference/(guideSigma*guideSigma));}
        sum+=weight*value;weights+=weight;
    }
    return uint(clamp(round(sum/weights),0.0,255.0));
}
[numthreads(256,1,1)]void main(uint3 tid:SV_DispatchThreadID) {
    uint index=tid.x*4;
    if(index>=bytes)return;
    uint value=Filter(index)|(Filter(index+1)<<8)|(Filter(index+2)<<16)|(Filter(index+3)<<24);
    destination.Store(index,value);
})";
}
struct OpenGpuDenoiser::State {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11ComputeShader> compute;
    ComPtr<ID3D11Buffer> input,output,readback,constants;
    ComPtr<ID3D11ShaderResourceView> view;
    ComPtr<ID3D11UnorderedAccessView> target;
    UINT width,height,total,allocated;
    std::vector<BYTE> packed;
    State(UINT w,UINT h):width(w),height(h),total(w*(h+h/2)),allocated((total+3)&~3u),packed(allocated) {
        D3D_FEATURE_LEVEL actual,requested=D3D_FEATURE_LEVEL_11_0;
        Hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,&requested,1,D3D11_SDK_VERSION,&device,&actual,&context));
        ComPtr<ID3DBlob> code,errors;
        Hr(D3DCompile(shader,strlen(shader),"OpenBilateral.hlsl",nullptr,nullptr,"main","cs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors));
        Hr(device->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&compute));
        D3D11_BUFFER_DESC desc{};desc.ByteWidth=allocated;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;desc.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
        Hr(device->CreateBuffer(&desc,nullptr,&input));desc.BindFlags=D3D11_BIND_UNORDERED_ACCESS;Hr(device->CreateBuffer(&desc,nullptr,&output));
        desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;desc.MiscFlags=0;Hr(device->CreateBuffer(&desc,nullptr,&readback));
        desc={};desc.ByteWidth=32;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_CONSTANT_BUFFER;Hr(device->CreateBuffer(&desc,nullptr,&constants));
        D3D11_SHADER_RESOURCE_VIEW_DESC srv{};srv.Format=DXGI_FORMAT_R32_TYPELESS;srv.ViewDimension=D3D11_SRV_DIMENSION_BUFFEREX;srv.BufferEx.NumElements=allocated/4;srv.BufferEx.Flags=D3D11_BUFFEREX_SRV_FLAG_RAW;
        Hr(device->CreateShaderResourceView(input.Get(),&srv,&view));
        D3D11_UNORDERED_ACCESS_VIEW_DESC uav{};uav.Format=DXGI_FORMAT_R32_TYPELESS;uav.ViewDimension=D3D11_UAV_DIMENSION_BUFFER;uav.Buffer.NumElements=allocated/4;uav.Buffer.Flags=D3D11_BUFFER_UAV_FLAG_RAW;
        Hr(device->CreateUnorderedAccessView(output.Get(),&uav,&target));
    }
};
OpenGpuDenoiser::OpenGpuDenoiser()=default;
OpenGpuDenoiser::~OpenGpuDenoiser()=default;
HRESULT OpenGpuDenoiser::Process(IMFMediaType* type,IMFSample** sample,DWORD level,bool chromaOnly) {
    if(!level)return S_FALSE;
    try {
        if(!type||!sample||!*sample)throw E_POINTER;
        GUID subtype;UINT w=0,h=0;Hr(type->GetGUID(MF_MT_SUBTYPE,&subtype));Hr(MFGetAttributeSize(type,MF_MT_FRAME_SIZE,&w,&h));
        if(subtype!=MFVideoFormat_NV12||!w||!h||(w%2)||(h%2)||w>3840||h>2160||level>4)throw MF_E_INVALIDMEDIATYPE;
        if(!state||state->width!=w||state->height!=h)state=std::make_unique<State>(w,h);
        auto& s=*state;ComPtr<IMFMediaBuffer> input,output;Hr((*sample)->ConvertToContiguousBuffer(&input));Locked2D src,dst;Hr(src.Lock(input.Get(),MF2DBuffer_LockFlags_Read,type));
        if(src.stride<=0||!src.Covers(w,h+h/2))throw MF_E_BUFFERTOOSMALL;
        for(UINT y=0;y<h+h/2;y++)memcpy(s.packed.data()+size_t(y)*w,src.scan+size_t(y)*src.stride,w);src.Unlock();
        struct Parameters{UINT width,height,bytes;float sigma;UINT chromaOnly,radius;float guideSigma;UINT padding;} parameters{w,h,s.total,level==1?12.0f:level==2?20.0f:level==3?32.0f:48.0f,chromaOnly?1u:0u,level>=3?3u:2u,24.0f,0};
        s.context->UpdateSubresource(s.input.Get(),0,nullptr,s.packed.data(),0,0);s.context->UpdateSubresource(s.constants.Get(),0,nullptr,&parameters,0,0);
        auto view=s.view.Get();auto target=s.target.Get();auto constants=s.constants.Get();
        s.context->CSSetShader(s.compute.Get(),nullptr,0);s.context->CSSetShaderResources(0,1,&view);s.context->CSSetUnorderedAccessViews(0,1,&target,nullptr);s.context->CSSetConstantBuffers(0,1,&constants);
        s.context->Dispatch((s.allocated/4+255)/256,1,1);
        view=nullptr;target=nullptr;s.context->CSSetShaderResources(0,1,&view);s.context->CSSetUnorderedAccessViews(0,1,&target,nullptr);
        s.context->CopyResource(s.readback.Get(),s.output.Get());D3D11_MAPPED_SUBRESOURCE mapped{};Hr(s.context->Map(s.readback.Get(),0,D3D11_MAP_READ,0,&mapped));
        memcpy(s.packed.data(),mapped.pData,s.total);s.context->Unmap(s.readback.Get(),0);
        Hr(MFCreate2DMediaBuffer(w,h,MFVideoFormat_NV12.Data1,FALSE,&output));Hr(dst.Lock(output.Get(),MF2DBuffer_LockFlags_Write));if(!dst.Covers(w,h+h/2))throw MF_E_BUFFERTOOSMALL;
        ZeroMemory(dst.start,dst.length);for(UINT y=0;y<h+h/2;y++)memcpy(dst.scan+size_t(y)*dst.stride,s.packed.data()+size_t(y)*w,w);dst.Unlock();DWORD bytes=0;Hr(output->GetMaxLength(&bytes));Hr(output->SetCurrentLength(bytes));
        ComPtr<IMFSample> replacement;Hr(MFCreateSample(&replacement));Hr((*sample)->CopyAllItems(replacement.Get()));LONGLONG value=0;DWORD flags=0;
        if(SUCCEEDED((*sample)->GetSampleTime(&value)))Hr(replacement->SetSampleTime(value));if(SUCCEEDED((*sample)->GetSampleDuration(&value)))Hr(replacement->SetSampleDuration(value));if(SUCCEEDED((*sample)->GetSampleFlags(&flags)))Hr(replacement->SetSampleFlags(flags));
        Hr(replacement->AddBuffer(output.Get()));(*sample)->Release();*sample=replacement.Detach();return S_OK;
    }catch(HRESULT hr){state.reset();return hr;}catch(...){state.reset();return E_FAIL;}
}
