#include "stdafx.h"
#include "multipinmfthelpers.h"
#include "multipinmft.h"
#include <thread>
#include <atomic>
#include <cstdio>
#include <stdexcept>
static void Check(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
static ComPtr<IMFMediaType> Type(GUID subtype){ComPtr<IMFMediaType> type;Check(SUCCEEDED(MFCreateMediaType(&type)),"media type");type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video);type->SetGUID(MF_MT_SUBTYPE,subtype);return type;}
static void Push(CPinQueue& queue,LONGLONG time){ComPtr<IMFSample> sample;Check(SUCCEEDED(MFCreateSample(&sample)),"sample");sample->SetSampleTime(time);queue.InsertInternal(sample.Get());}
static void Fifo(GUID subtype,GUID category){CPinQueue queue(0);auto type=Type(subtype);queue.SetRealtimePolicy(type.Get(),category);for(int i=0;i<100;i++)Push(queue,i);for(int i=0;i<100;i++){ComPtr<IMFSample> sample;Check(SUCCEEDED(queue.Remove(&sample)),"FIFO missing frame");LONGLONG time=-1;sample->GetSampleTime(&time);Check(time==i,"FIFO reordered frame");}Check(queue.Empty(),"FIFO did not drain");}
int main(){
    CoInitializeEx(nullptr,COINIT_MULTITHREADED);MFStartup(MF_VERSION);int result=0;
    try{
        {CPinQueue queue(0);auto type=Type(MFVideoFormat_NV12);queue.SetRealtimePolicy(type.Get());for(int i=0;i<1000;i++)Push(queue,i);ComPtr<IMFSample> sample;Check(SUCCEEDED(queue.Remove(&sample)),"latest missing");LONGLONG time=-1;sample->GetSampleTime(&time);Check(time==999,"old frame retained");UINT32 discontinuity=0;sample->GetUINT32(MFSampleExtension_Discontinuity,&discontinuity);Check(discontinuity!=0,"dropped-frame flag missing");Check(queue.Empty(),"latest queue contains stale frames");Check(queue.Remove(&sample)==MF_E_TRANSFORM_NEED_MORE_INPUT,"empty queue result");}
        Fifo(MFVideoFormat_H264,GUID_NULL);Fifo(MFVideoFormat_NV12,PINNAME_VIDEO_STILL);
        {CPinQueue queue(0);auto type=Type(MFVideoFormat_NV12);queue.SetRealtimePolicy(type.Get());std::atomic<bool> done=false;std::thread producer([&]{CoInitializeEx(nullptr,COINIT_MULTITHREADED);for(int i=0;i<10000;i++)Push(queue,i);done=true;CoUninitialize();});LONGLONG previous=-1;while(!done||!queue.Empty()){ComPtr<IMFSample> sample;if(SUCCEEDED(queue.Remove(&sample))){LONGLONG time;sample->GetSampleTime(&time);Check(time>previous,"concurrent order");previous=time;}else std::this_thread::yield();}producer.join();Check(previous==9999,"newest concurrent sample lost");}
        std::puts("PASS: decoded video keeps newest frame and marks drops; compressed video and photos retain FIFO; concurrent producer/consumer reaches newest frame.");
    }catch(const std::exception& error){std::fprintf(stderr,"FAIL: %s\n",error.what());result=1;}
    MFShutdown();CoUninitialize();return result;
}
