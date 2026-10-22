#include "stdafx.h"
#include "realtime_denoise.h"
#include <thread>
#include <chrono>
#include <cstdio>
static void Check(bool condition,const char* message){if(!condition)throw std::runtime_error(message);}
static void Hr(HRESULT result){if(FAILED(result))throw result;}
static ComPtr<IMFSample> Frame(){ComPtr<IMFSample> sample;Hr(MFCreateSample(&sample));sample->SetSampleTime(MFGetSystemTime());sample->SetSampleDuration(333333);return sample;}
int main(){CoInitializeEx(nullptr,COINIT_MULTITHREADED);MFStartup(MF_VERSION);int exitCode=0;try{
    ComPtr<IMFMediaType> type;Hr(MFCreateMediaType(&type));type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video);type->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_NV12);MFSetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,1920,1080);
    bool first=true;RealtimeDenoiser denoise([&](IMFMediaType*,IMFSample** input,DWORD,DWORD){if(first){first=false;Sleep(220);}Sleep(45);ComPtr<IMFSample> output;Hr(MFCreateSample(&output));Hr((*input)->CopyAllItems(output.Get()));LONGLONG time=0;Hr((*input)->GetSampleTime(&time));output->SetSampleTime(time);output->SetUINT32(MFSampleExtension_CleanPoint,1);(*input)->Release();*input=output.Detach();return S_OK;});
    int filtered=0,rawAfterWarm=0,skipped=0;LONGLONG lastTime=0;double maxCall=0;auto start=std::chrono::steady_clock::now();
    for(int i=0;i<110;i++){std::this_thread::sleep_until(start+std::chrono::milliseconds(i*33));auto frame=Frame();auto entered=std::chrono::steady_clock::now();HRESULT result=denoise.Process(type.Get(),frame.GetAddressOf(),4,0);maxCall=(std::max)(maxCall,std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-entered).count());if(result==MF_E_TRANSFORM_NEED_MORE_INPUT){Check(!frame,"skipped output returned a frame");skipped++;continue;}Hr(result);LONGLONG time;Hr(frame->GetSampleTime(&time));Check(time>lastTime,"output repeated or reversed timestamps");lastTime=time;UINT32 tag=0;bool marked=SUCCEEDED(frame->GetUINT32(MFSampleExtension_CleanPoint,&tag))&&tag==1;if(result==S_OK){Check(marked,"reported filtered but returned raw sample");filtered++;}else if(i>=35)rawAfterWarm++;}
    Check(filtered>=50,"late completed frames are being discarded");Check(rawAfterWarm==0,"raw frames silently replaced filtered frames after warm-up");Check(maxCall<40,"capture blocked on GPU work");
    auto off=Frame();auto original=off.Get();auto entered=std::chrono::steady_clock::now();Check(denoise.Process(type.Get(),off.GetAddressOf(),0,0)==S_FALSE&&off.Get()==original,"Off did not immediately return current input");Check(std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-entered).count()<40,"Off blocked");
    std::printf("PASS: 45 ms processing with 33 ms capture delivers %d tagged filtered frames; %d skipped ticks; zero raw frames after warm-up; max handoff %.2f ms; timestamps monotonic; Off immediate.\n",filtered,skipped,maxCall);
    const auto phaseStart=std::chrono::steady_clock::now();
    RealtimeDenoiser recovering([&](IMFMediaType*,IMFSample** input,DWORD,DWORD){
        const auto elapsed=std::chrono::steady_clock::now()-phaseStart;
        Sleep(elapsed<std::chrono::seconds(4)?250:10);
        // Simulate a centred temporal model returning the genuine frame from
        // two capture intervals earlier, rather than the latest input frame.
        LONGLONG referenceTime=0;(*input)->GetSampleTime(&referenceTime);
        ComPtr<IMFSample> result;Hr(MFCreateSample(&result));Hr((*input)->CopyAllItems(result.Get()));
        result->SetSampleTime(referenceTime-666666);result->SetUINT32(MFSampleExtension_CleanPoint,1);
        (*input)->Release();*input=result.Detach();return S_OK;
    });
    int fallback=0,recovered=0,rawAfterRecovery=0;LONGLONG last=0;double maximumHandoff=0;
    for(int i=0;i<235;i++){
        std::this_thread::sleep_until(phaseStart+std::chrono::milliseconds(i*33));auto frame=Frame();auto before=std::chrono::steady_clock::now();
        HRESULT result=recovering.Process(type.Get(),frame.GetAddressOf(),3,0);maximumHandoff=(std::max)(maximumHandoff,std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-before).count());
        if(result==MF_E_TRANSFORM_NEED_MORE_INPUT){Check(!frame,"recovery skipped output returned a sample");continue;}
        Hr(result);LONGLONG stamp=0;Hr(frame->GetSampleTime(&stamp));Check(stamp>last,"recovery replayed or retimestamped old output");last=stamp;
        if(result==S_OK){Check(i>110,"late result escaped the latency budget");recovered++;}
        else{if(i<125)fallback++;if(i>170)rawAfterRecovery++;}
    }
    Check(fallback>5,"regression did not exercise prolonged raw fallback");Check(recovered>45,"filtered delivery did not recover after prolonged lateness");Check(rawAfterRecovery==0,"raw delivery starved recovered filtered results");Check(maximumHandoff<40,"recovery blocks capture");
    auto recoveryOff=Frame();auto raw=recoveryOff.Get();Check(recovering.Process(type.Get(),recoveryOff.GetAddressOf(),0,0)==S_FALSE&&recoveryOff.Get()==raw,"Off after recovery was not immediate");
    std::printf("PASS: prolonged late processing then 10 ms processing with two-frame temporal lag recovers %d filtered frames without changing mode; %d initial original frames; zero original after recovery; monotonic original timestamps, capture nonblocking, Off immediate.\n",recovered,fallback);
}catch(const std::exception& e){std::printf("FAIL: %s\n",e.what());exitCode=1;}catch(HRESULT hr){std::printf("FAIL: 0x%08lx\n",hr);exitCode=1;}MFShutdown();CoUninitialize();return exitCode;}

