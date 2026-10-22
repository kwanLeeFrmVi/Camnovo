// Local comparison harness: runs the same native filters on supplied NV12.
#include "stdafx.h"
#include "nvidia_denoise.h"
#include "open_denoise.h"
#include "locked_frame.h"
struct QualityContext {ComPtr<IMFMediaType> type;UINT w,h;NvidiaDenoiser rtx;OpenGpuDenoiser bilateral;};
#define API extern "C" __declspec(dllexport)
API HRESULT QualityCreate(UINT w,UINT h,QualityContext** output){
    if(!output||!w||!h||(w%2)||(h%2))return E_INVALIDARG;
    *output=nullptr;CoInitializeEx(nullptr,COINIT_MULTITHREADED);RETURN_IF_FAILED(MFStartup(MF_VERSION));
    auto p=std::make_unique<QualityContext>();p->w=w;p->h=h;
    RETURN_IF_FAILED(MFCreateMediaType(&p->type));RETURN_IF_FAILED(p->type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video));
    RETURN_IF_FAILED(p->type->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_NV12));RETURN_IF_FAILED(MFSetAttributeSize(p->type.Get(),MF_MT_FRAME_SIZE,w,h));
    RETURN_IF_FAILED(p->type->SetUINT32(MF_MT_DEFAULT_STRIDE,w));RETURN_IF_FAILED(p->type->SetUINT32(MF_MT_YUV_MATRIX,MFVideoTransferMatrix_BT709));
    RETURN_IF_FAILED(p->type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE,MFNominalRange_16_235));*output=p.release();return S_OK;
}
API HRESULT QualityRun(QualityContext* p,DWORD backend,DWORD level,const BYTE* input,BYTE* output){
    if(!p||!input||!output||backend>1||level<1||level>4)return E_INVALIDARG;
    ComPtr<IMFMediaBuffer> buffer;ComPtr<IMFSample> sample;DWORD size=p->w*p->h*3/2;
    RETURN_IF_FAILED(MFCreateMemoryBuffer(size,&buffer));BYTE* data=nullptr;RETURN_IF_FAILED(buffer->Lock(&data,nullptr,nullptr));
    memcpy(data,input,size);buffer->Unlock();RETURN_IF_FAILED(buffer->SetCurrentLength(size));RETURN_IF_FAILED(MFCreateSample(&sample));
    RETURN_IF_FAILED(sample->AddBuffer(buffer.Get()));RETURN_IF_FAILED(sample->SetSampleTime(MFGetSystemTime()));
    if(backend==0){RETURN_IF_FAILED(p->rtx.Process(p->type.Get(),sample.GetAddressOf(),level));RETURN_IF_FAILED(p->bilateral.Process(p->type.Get(),sample.GetAddressOf(),level,true));}
    else RETURN_IF_FAILED(p->bilateral.Process(p->type.Get(),sample.GetAddressOf(),level));
    buffer.Reset();RETURN_IF_FAILED(sample->ConvertToContiguousBuffer(&buffer));Locked2D lock;RETURN_IF_FAILED(lock.Lock(buffer.Get(),MF2DBuffer_LockFlags_Read,p->type.Get()));
    if(lock.stride<=0||!lock.Covers(p->w,p->h+p->h/2))return MF_E_BUFFERTOOSMALL;
    for(UINT y=0;y<p->h+p->h/2;++y)memcpy(output+size_t(y)*p->w,lock.scan+size_t(y)*lock.stride,p->w);
    return S_OK;
}
API void QualityDestroy(QualityContext* p){delete p;}
