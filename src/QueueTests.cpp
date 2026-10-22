#include "stdafx.h"
#include "multipinmfthelpers.h"
#include "multipinmft.h"
#include "upscale1440p.h"
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
        {
            auto packed=Type(MFVideoFormat_YUY2);MFSetAttributeSize(packed.Get(),MF_MT_FRAME_SIZE,1920,1080);
            MFSetAttributeRatio(packed.Get(),MF_MT_FRAME_RATE,30,1);packed->SetUINT32(MF_MT_DEFAULT_STRIDE,3840);
            packed->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE,MFNominalRange_16_235);
            CTranslateOutPin pin;Check(SUCCEEDED(pin.AddMediaType(nullptr,packed.Get())),"packed camera type");
            ComPtr<IMFMediaType> normalized,parent;Check(SUCCEEDED(pin.GetOutputAvailableType(0,&normalized)),"NV12 advertised");
            GUID subtype=GUID_NULL;normalized->GetGUID(MF_MT_SUBTYPE,&subtype);Check(subtype==MFVideoFormat_NV12,"YUY2 not normalized");
            Check(MFGetAttributeUINT32(normalized.Get(),MF_MT_SAMPLE_SIZE,0)==1920*1080*3/2,"NV12 sample size");
            Check(MFGetAttributeUINT32(normalized.Get(),MF_MT_VIDEO_NOMINAL_RANGE,0)==MFNominalRange_16_235,"colour range changed");
            UINT32 stride=0;Check(FAILED(normalized->GetUINT32(MF_MT_DEFAULT_STRIDE,&stride)),"packed stride leaked into NV12");
            Check(pin.IsMediaTypeSupported(normalized.Get(),&parent),"parent mapping");parent->GetGUID(MF_MT_SUBTYPE,&subtype);Check(subtype==MFVideoFormat_YUY2,"upstream must stay YUY2");
            DMFT_conversion_type operation=DeviceMftTransformTypeIllegal;
            Check(SUCCEEDED(CompareMediaTypesForConverter(packed.Get(),normalized.Get(),&operation))&&operation==DeviceMftTransformTypeXVP,"packed conversion must use video processor, not a decoder");
            auto compressed=Type(MFVideoFormat_MJPG);Check(SUCCEEDED(CompareMediaTypesForConverter(compressed.Get(),normalized.Get(),&operation))&&operation==DeviceMftTransformTypeDecoder,"compressed input still needs decoder");
            Check(SUCCEEDED(pin.AddMediaType(nullptr,normalized.Get())),"native NV12 type");parent.Reset();
            Check(pin.IsMediaTypeSupported(normalized.Get(),&parent),"native NV12 mapping");parent->GetGUID(MF_MT_SUBTYPE,&subtype);
            Check(subtype==MFVideoFormat_NV12,"native NV12 must bypass format conversion when available");
            C1440pOutPin qhd(0,nullptr,nullptr);Check(SUCCEEDED(qhd.AddMediaType(nullptr,packed.Get())),"1440p packed type");
            ComPtr<IMFMediaType> upscaled;Check(SUCCEEDED(qhd.GetOutputAvailableType(1,&upscaled)),"1440p type");
            UINT width=0,height=0;MFGetAttributeSize(upscaled.Get(),MF_MT_FRAME_SIZE,&width,&height);upscaled->GetGUID(MF_MT_SUBTYPE,&subtype);
            Check(width==2560&&height==1440&&subtype==MFVideoFormat_NV12,"1440p must remain denoiser-compatible");
            std::puts("PASS: Realtek YUY2 maps to NV12 at native and 1440p sizes; sample size, range and upstream mapping preserved.");
        }
        {CPinQueue queue(0);auto type=Type(MFVideoFormat_NV12);queue.SetRealtimePolicy(type.Get());for(int i=0;i<1000;i++)Push(queue,i);ComPtr<IMFSample> sample;Check(SUCCEEDED(queue.Remove(&sample)),"latest missing");LONGLONG time=-1;sample->GetSampleTime(&time);Check(time==999,"old frame retained");UINT32 discontinuity=0;sample->GetUINT32(MFSampleExtension_Discontinuity,&discontinuity);Check(discontinuity!=0,"dropped-frame flag missing");Check(queue.Empty(),"latest queue contains stale frames");Check(queue.Remove(&sample)==MF_E_TRANSFORM_NEED_MORE_INPUT,"empty queue result");}
        Fifo(MFVideoFormat_H264,GUID_NULL);Fifo(MFVideoFormat_NV12,PINNAME_VIDEO_STILL);
        {CPinQueue queue(0);auto type=Type(MFVideoFormat_NV12);queue.SetRealtimePolicy(type.Get());std::atomic<bool> done=false;std::thread producer([&]{CoInitializeEx(nullptr,COINIT_MULTITHREADED);for(int i=0;i<10000;i++)Push(queue,i);done=true;CoUninitialize();});LONGLONG previous=-1;while(!done||!queue.Empty()){ComPtr<IMFSample> sample;if(SUCCEEDED(queue.Remove(&sample))){LONGLONG time;sample->GetSampleTime(&time);Check(time>previous,"concurrent order");previous=time;}else std::this_thread::yield();}producer.join();Check(previous==9999,"newest concurrent sample lost");}
        std::puts("PASS: decoded video keeps newest frame and marks drops; compressed video and photos retain FIFO; concurrent producer/consumer reaches newest frame.");
    }catch(const std::exception& error){std::fprintf(stderr,"FAIL: %s\n",error.what());result=1;}
    MFShutdown();CoUninitialize();return result;
}
