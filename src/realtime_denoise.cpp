// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Lenovo Camera Studio contributors
#include "stdafx.h"
#include "realtime_denoise.h"
#include "nvidia_denoise.h"
#include "open_denoise.h"
#include "external_denoise.h"
#include "locked_frame.h"
#include <deque>
#include <thread>
#include <mutex>
#include <condition_variable>
namespace {
LONGLONG Time(IMFSample* sample){LONGLONG time=0;if(sample)sample->GetSampleTime(&time);return time;}
DWORD Setting(const wchar_t* name,DWORD maximum){DWORD value=0,bytes=sizeof(value);if(RegGetValueW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\LenovoWebcamPrototype",name,RRF_RT_REG_DWORD|RRF_SUBKEY_WOW6464KEY,nullptr,&value,&bytes)!=ERROR_SUCCESS||value>maximum)return 0;return value;}
HRESULT HistoryCopy(IMFMediaType* type,IMFSample* input,ComPtr<IMFSample>& copy){
    UINT w=0,h=0;RETURN_IF_FAILED(MFGetAttributeSize(type,MF_MT_FRAME_SIZE,&w,&h));ComPtr<IMFMediaBuffer> source,dest;RETURN_IF_FAILED(input->ConvertToContiguousBuffer(&source));RETURN_IF_FAILED(MFCreate2DMediaBuffer(w,h,MFVideoFormat_NV12.Data1,FALSE,&dest));Locked2D a,b;RETURN_IF_FAILED(a.Lock(source.Get(),MF2DBuffer_LockFlags_Read,type));RETURN_IF_FAILED(b.Lock(dest.Get(),MF2DBuffer_LockFlags_Write));if(a.stride<=0||!a.Covers(w,h+h/2)||!b.Covers(w,h+h/2))return MF_E_BUFFERTOOSMALL;for(UINT y=0;y<h+h/2;++y)memcpy(b.scan+size_t(y)*b.stride,a.scan+size_t(y)*a.stride,w);a.Unlock();b.Unlock();DWORD bytes=0;RETURN_IF_FAILED(dest->GetMaxLength(&bytes));RETURN_IF_FAILED(dest->SetCurrentLength(bytes));RETURN_IF_FAILED(MFCreateSample(&copy));RETURN_IF_FAILED(input->CopyAllItems(copy.Get()));LONGLONG t=0;if(SUCCEEDED(input->GetSampleTime(&t)))copy->SetSampleTime(t);if(SUCCEEDED(input->GetSampleDuration(&t)))copy->SetSampleDuration(t);RETURN_IF_FAILED(copy->AddBuffer(dest.Get()));return S_OK;
}
}
struct RealtimeDenoiser::State {
    std::mutex mutex;
    std::condition_variable wake;
    std::thread worker;
    bool quit=false;
    ULONGLONG generation=0;
    DWORD level=0,backend=0;
    DenoiseStatus status;
    HRESULT workerResult=S_FALSE;
    bool initialized=false;
    LONGLONG lastOutputTime=0;
    ULONGLONG lastFilteredTick=0;
    UINT width=0,height=0;
    ComPtr<IMFMediaType> pendingType;
    ComPtr<IMFSample> pending,completed,previous;
    std::deque<ComPtr<IMFSample>> history;
    std::vector<ComPtr<IMFSample>> pendingFrames;
    Processor processor;
    explicit State(Processor p):processor(std::move(p)){}
    ~State(){ {std::lock_guard<std::mutex> lock(mutex);quit=true;}wake.notify_one();if(worker.joinable())worker.join(); }
    void Work(){
        HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
        NvidiaDenoiser nvidia;OpenGpuDenoiser open;ExternalDenoiser external;
        for(;;){
            ComPtr<IMFSample> sample;ComPtr<IMFMediaType> type;std::vector<ComPtr<IMFSample>> frames;DWORD q,b;ULONGLONG token;
            {std::unique_lock<std::mutex> lock(mutex);wake.wait(lock,[&]{return quit||pending;});if(quit)break;sample=std::move(pending);type=std::move(pendingType);frames=std::move(pendingFrames);q=level;b=backend;token=generation;}
            LARGE_INTEGER begin,end,frequency;QueryPerformanceCounter(&begin);
            HRESULT result=E_FAIL;
            try{if(processor)result=processor(type.Get(),sample.GetAddressOf(),q,b);else if(b>=2)result=external.Process(type.Get(),sample.GetAddressOf(),q,b,frames);else if(b==1)result=open.Process(type.Get(),sample.GetAddressOf(),q);else {result=nvidia.Process(type.Get(),sample.GetAddressOf(),q);if(result==S_OK)result=open.Process(type.Get(),sample.GetAddressOf(),q,true);}}catch(...){result=E_FAIL;}
            QueryPerformanceCounter(&end);QueryPerformanceFrequency(&frequency);
            {std::lock_guard<std::mutex> lock(mutex);if(token==generation){status.milliseconds=(end.QuadPart-begin.QuadPart)*1000.0/frequency.QuadPart;workerResult=result;if(status.completed==0&&result==S_OK)lastFilteredTick=GetTickCount64();initialized=true;if(result==S_OK){completed=std::move(sample);++status.completed;}}}
        }
        if(SUCCEEDED(com))CoUninitialize();
    }
};
RealtimeDenoiser::RealtimeDenoiser(Processor processor):state(std::make_unique<State>(std::move(processor))){}
RealtimeDenoiser::~RealtimeDenoiser()=default;
HRESULT RealtimeDenoiser::Process(IMFMediaType* type,IMFSample** sample){
    auto now=GetTickCount64();if(!checked||now-checked>=75){level=Setting(L"DenoiseLevel",4);backend=Setting(L"DenoiseBackend",8);checked=now;}
    return Process(type,sample,level,backend);
}
HRESULT RealtimeDenoiser::Process(IMFMediaType* type,IMFSample** sample,DWORD q,DWORD b){
    if(!type||!sample||!*sample)return E_POINTER;
    GUID subtype=GUID_NULL;type->GetGUID(MF_MT_SUBTYPE,&subtype);
    // Never add a one-frame delay to encoded video, still-image formats or
    // unsupported layouts. Their original samples remain intact.
    if(subtype!=MFVideoFormat_NV12)return q?MF_E_INVALIDMEDIATYPE:S_FALSE;
    auto& s=*state;ComPtr<IMFSample> ready,previous;
    UINT width=0,height=0;MFGetAttributeSize(type,MF_MT_FRAME_SIZE,&width,&height);
    const LONGLONG incomingTime=Time(*sample);const ULONGLONG now=GetTickCount64();
    ComPtr<IMFSample> historyCopy;if(q&&b>=2){HRESULT hr=HistoryCopy(type,*sample,historyCopy);if(FAILED(hr))return hr;}
    bool initialized=false;HRESULT workerResult=S_FALSE;
    {
        std::lock_guard<std::mutex> lock(s.mutex);
        if(s.level!=q||s.backend!=b||s.width!=width||s.height!=height){++s.generation;s.pending.Reset();s.pendingType.Reset();s.completed.Reset();s.history.clear();s.pendingFrames.clear();s.level=q;s.backend=b;s.status={};s.status.level=q;s.status.backend=b;s.initialized=false;s.workerResult=S_FALSE;s.lastFilteredTick=now;if(s.width!=width||s.height!=height)s.previous.Reset();s.width=width;s.height=height;}
        if(!q){s.previous.Reset();s.lastOutputTime=incomingTime;return S_FALSE;}
        if(!s.worker.joinable())s.worker=std::thread([&s]{s.Work();});
        previous=std::move(s.previous);
        if(b>=2){s.history.emplace_back(std::move(historyCopy));while(s.history.size()>6)s.history.pop_front();s.previous=s.history.back();}else s.previous=*sample;
        ready=std::move(s.completed);
        initialized=s.initialized;workerResult=s.workerResult;
        if(s.pending)++s.status.dropped;
        if(b<2||s.history.size()>=6){s.pending=b>=2?s.history.back():ComPtr<IMFSample>(*sample);s.pendingType=type;s.pendingFrames.assign(s.history.begin(),s.history.end());}
    }
    s.wake.notify_one();
    // Prefer the latest completed, undelivered result. GPU work may take more
    // than one capture interval: an exact "previous frame" match discarded
    // useful results and silently substituted raw frames. After warm-up, skip
    // output ticks with no fresh filtered result instead of alternating raw
    // and filtered images. Capture and the newest-only worker remain unblocked.
    (*sample)->Release();*sample=nullptr;
    std::lock_guard<std::mutex> lock(s.mutex);
    const LONGLONG readyTime=Time(ready.Get());
    const bool tooOld=ready&&b>=2&&MFGetSystemTime()-readyTime>4500000;
    if(tooOld)workerResult=HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    if(ready&&!tooOld&&readyTime>s.lastOutputTime&&readyTime<=incomingTime&&incomingTime-readyTime<=(b>=2?4500000:1500000)){s.lastOutputTime=readyTime;s.lastFilteredTick=now;s.status.result=S_OK;++s.status.delivered;*sample=ready.Detach();return S_OK;}
    // During prolonged fallback, raw output advances lastOutputTime ahead of a
    // temporal result. Once processing catches up to the latency budget, briefly
    // stop raw delivery so a subsequent filtered timestamp can overtake it.
    // Without this handoff, every valid result remains older than the latest raw
    // frame forever, even after reducing model size. Never retimestamp or replay
    // the old result; the wall-clock age limit still applies to recovery.
    if(ready&&!tooOld&&readyTime>0&&readyTime<=s.lastOutputTime&&readyTime<=incomingTime&&incomingTime-readyTime<=(b>=2?4500000:1500000)){
        s.lastFilteredTick=now;s.status.result=S_FALSE;return MF_E_TRANSFORM_NEED_MORE_INPUT;
    }
    if(initialized&&SUCCEEDED(workerResult)&&now-s.lastFilteredTick<(b>=2?3000:750)){s.status.result=S_FALSE;return MF_E_TRANSFORM_NEED_MORE_INPUT;}
    if(!previous||Time(previous.Get())<=s.lastOutputTime)return MF_E_TRANSFORM_NEED_MORE_INPUT;
    s.lastOutputTime=Time(previous.Get());++s.status.raw;
    s.status.result=FAILED(workerResult)?workerResult:initialized?HRESULT_FROM_WIN32(ERROR_TIMEOUT):S_FALSE;
    *sample=previous.Detach();return S_FALSE;
}
DenoiseStatus RealtimeDenoiser::Status(){auto& s=*state;std::lock_guard<std::mutex> lock(s.mutex);return s.status;}
