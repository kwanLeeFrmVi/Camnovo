#include <windows.h>
#include <dshow.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wrl/client.h>
#include <vector>
#include <string>
#include <thread>
#include <mutex>
#include <atomic>
#include <cwctype>
#include <cstring>
#include <deque>
#include <functional>
#include <future>
#include <condition_variable>
#include <map>
#include "camera_source_selection.h"
using Microsoft::WRL::ComPtr;
namespace {
// Source, controls and reader belong to one MTA, so controls target the source
// actually streaming the preview. UI callers submit serialized work to that MTA.
ComPtr<IMFMediaSource> hardware;
ComPtr<IAMVideoProcAmp> imageControls;
ComPtr<IAMCameraControl> cameraControls;
ComPtr<IMFSourceReader> reader;
std::wstring hardwareInstance;
std::wstring hardwareLink;
struct Preference {LONG value,mode;};
std::map<std::pair<int,int>,Preference> preferences;
std::mutex lifecycleMutex,commandsMutex,framesMutex;
std::condition_variable commandsReady;
std::deque<std::function<void()>> commands;
std::thread engineThread;
bool quitting=false;
std::vector<BYTE> latest;
int frameWidth=0,frameHeight=0;
unsigned long long sequence=0;
LONGLONG lastSampleTime=0,lastArrivalTime=0,firstArrivalTime=0;
UINT64 lastExposureTime=0;
std::deque<LONGLONG> recentArrivals;
double lastConversionMs=0;
std::atomic<bool> stopping{true};
std::atomic<HRESULT> previewStatus{S_FALSE};
void Check(HRESULT hr){if(FAILED(hr))throw hr;}
HRESULT OpenHardware() {
    if(hardware)return S_OK;
    ComPtr<IMFAttributes> attrs;HRESULT hr=MFCreateAttributes(&attrs,1);if(FAILED(hr))return hr;
    hr=attrs->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);if(FAILED(hr))return hr;
    IMFActivate** devices=nullptr;UINT32 count=0;hr=MFEnumDeviceSources(attrs.Get(),&devices,&count);if(FAILED(hr))return hr;
    hardwareInstance.clear();hardwareLink.clear();int preferredRank=0;
    ComPtr<IMFActivate> selected;
    for(UINT32 i=0;i<count;++i){wchar_t* link=nullptr;UINT32 length=0;if(SUCCEEDED(devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,&link,&length))){int rank=CamnovoSourceRank(link);if(rank>preferredRank){preferredRank=rank;selected=devices[i];hardwareLink=link;std::wstring path=link;size_t first=path.find(L'#',4),second=first==std::wstring::npos?first:path.find(L'#',first+1),end=second==std::wstring::npos?second:path.find(L'#',second+1);if(end!=std::wstring::npos){hardwareInstance=path.substr(4,end-4);for(auto& c:hardwareInstance)if(c==L'#')c=L'\\';}}CoTaskMemFree(link);}devices[i]->Release();}CoTaskMemFree(devices);
    if(!selected)return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    hr=selected->ActivateObject(IID_PPV_ARGS(&hardware));if(FAILED(hr))return hr;
    hardware.As(&imageControls);hardware.As(&cameraControls);
    for(const auto& entry:preferences){if(entry.first.first==0&&imageControls)imageControls->Set(entry.first.second,entry.second.value,entry.second.mode);else if(entry.first.first==1&&cameraControls)cameraControls->Set(entry.first.second,entry.second.value,entry.second.mode);}
    return S_OK;
}
struct ReaderCallback final : IMFSourceReaderCallback {
    std::atomic<ULONG> references{1};std::mutex gate;IMFSourceReader* sourceReader=nullptr;
    STDMETHODIMP QueryInterface(REFIID iid,void** result) override {if(!result)return E_POINTER;*result=nullptr;if(iid==IID_IUnknown||iid==__uuidof(IMFSourceReaderCallback)){*result=static_cast<IMFSourceReaderCallback*>(this);AddRef();return S_OK;}return E_NOINTERFACE;}
    STDMETHODIMP_(ULONG) AddRef() override{return ++references;}
    STDMETHODIMP_(ULONG) Release() override{ULONG count=--references;if(!count)delete this;return count;}
    STDMETHODIMP OnEvent(DWORD,IMFMediaEvent*) override{return S_OK;}
    STDMETHODIMP OnFlush(DWORD) override{return S_OK;}
    STDMETHODIMP OnReadSample(HRESULT status,DWORD,DWORD flags,LONGLONG,IMFSample* sample) override {
        std::lock_guard<std::mutex> guard(gate);if(stopping||!sourceReader)return S_OK;
        try {
            Check(status);if(flags&(MF_SOURCE_READERF_ERROR|MF_SOURCE_READERF_ENDOFSTREAM))throw E_FAIL;
            if(sample){
                const LONGLONG entered=MFGetSystemTime();LONGLONG sampled=0;sample->GetSampleTime(&sampled);
                UINT64 exposureTime=0;ComPtr<IMFAttributes> metadata;
                if(SUCCEEDED(sample->GetUnknown(MFSampleExtension_CaptureMetadata,IID_PPV_ARGS(&metadata))))metadata->GetUINT64(MF_CAPTURE_METADATA_EXPOSURE_TIME,&exposureTime);
                ComPtr<IMFMediaType> current;Check(sourceReader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM,&current));
                UINT32 w=0,h=0;Check(MFGetAttributeSize(current.Get(),MF_MT_FRAME_SIZE,&w,&h));if(!w||!h||w>3840||h>2160)throw MF_E_INVALIDMEDIATYPE;
                ComPtr<IMFMediaBuffer> buffer;Check(sample->ConvertToContiguousBuffer(&buffer));ComPtr<IMF2DBuffer> twoD;buffer.As(&twoD);
                BYTE* pixels=nullptr;LONG stride=LONG(MFGetAttributeUINT32(current.Get(),MF_MT_DEFAULT_STRIDE,w*4));DWORD length=0;
                if(twoD)Check(twoD->Lock2D(&pixels,&stride));else{Check(buffer->Lock(&pixels,nullptr,&length));if(length<size_t(abs(stride))*h){buffer->Unlock();throw MF_E_BUFFERTOOSMALL;}if(stride<0)pixels+=size_t(-stride)*(h-1);}
                try{if(abs(stride)<LONG(w*4))throw MF_E_BUFFERTOOSMALL;std::lock_guard<std::mutex> lock(framesMutex);latest.resize(size_t(w)*h*4);for(UINT32 y=0;y<h;++y)memcpy(latest.data()+size_t(y)*w*4,pixels+ptrdiff_t(y)*stride,size_t(w)*4);frameWidth=int(w);frameHeight=int(h);++sequence;lastSampleTime=sampled;lastExposureTime=exposureTime;lastArrivalTime=MFGetSystemTime();if(!firstArrivalTime)firstArrivalTime=lastArrivalTime;lastConversionMs=(lastArrivalTime-entered)/10000.0;previewStatus=S_OK;}catch(...){if(twoD)twoD->Unlock2D();else buffer->Unlock();throw;}
                if(twoD)twoD->Unlock2D();else buffer->Unlock();
                {std::lock_guard<std::mutex> lock(framesMutex);recentArrivals.push_back(lastArrivalTime);while(recentArrivals.size()>2&&recentArrivals.front()<lastArrivalTime-10000000)recentArrivals.pop_front();}
            }
            if(!stopping)Check(sourceReader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM,0,nullptr,nullptr,nullptr,nullptr));
        }catch(HRESULT hr){previewStatus=hr;}catch(...){previewStatus=E_FAIL;}return S_OK;
    }
};
ComPtr<ReaderCallback> callback;
void StopPreview() {
    stopping=true;
    if(callback){std::lock_guard<std::mutex> guard(callback->gate);callback->sourceReader=nullptr;}
    if(reader){reader->Flush(MF_SOURCE_READER_ALL_STREAMS);reader.Reset();}
    callback.Reset();
    cameraControls.Reset();imageControls.Reset();if(hardware)hardware->Shutdown();hardware.Reset();
}
void CloseHardware(){StopPreview();cameraControls.Reset();imageControls.Reset();if(hardware)hardware->Shutdown();hardware.Reset();}
HRESULT Dispatch(std::function<HRESULT()> operation) {
    std::lock_guard<std::mutex> lifecycle(lifecycleMutex);
    try {
        if(!engineThread.joinable()){
            quitting=false;
            engineThread=std::thread([]{
                HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);HRESULT mf=FAILED(com)?com:MFStartup(MF_VERSION);
                for(;;){std::function<void()> command;{std::unique_lock<std::mutex> lock(commandsMutex);commandsReady.wait(lock,[]{return quitting||!commands.empty();});if(commands.empty()&&quitting)break;command=std::move(commands.front());commands.pop_front();}command();}
                CloseHardware();if(SUCCEEDED(mf))MFShutdown();if(SUCCEEDED(com))CoUninitialize();
            });
        }
        auto task=std::make_shared<std::packaged_task<HRESULT()>>([operation]{try{return operation();}catch(HRESULT hr){return hr;}catch(...){return E_FAIL;}});auto result=task->get_future();
        {std::lock_guard<std::mutex> lock(commandsMutex);commands.emplace_back([task]{(*task)();});}commandsReady.notify_one();return result.get();
    }catch(...){return E_FAIL;}
}
HRESULT Query(int group,int property,LONG* lo,LONG* hi,LONG* step,LONG* def,LONG* caps,LONG* value,LONG* mode) {
    HRESULT hr=OpenHardware();if(FAILED(hr))return hr;
    if(group==0&&imageControls){hr=imageControls->GetRange(property,lo,hi,step,def,caps);if(SUCCEEDED(hr))hr=imageControls->Get(property,value,mode);return hr;}
    if(group==1&&cameraControls){hr=cameraControls->GetRange(property,lo,hi,step,def,caps);if(SUCCEEDED(hr))hr=cameraControls->Get(property,value,mode);return hr;}return E_NOTIMPL;
}
}
#define EXPORT extern "C" __declspec(dllexport)
EXPORT HRESULT __cdecl CameraInstance(wchar_t* target,int capacity){if(!target||capacity<1)return E_INVALIDARG;return Dispatch([=]{HRESULT hr=OpenHardware();if(hardwareInstance.empty())return FAILED(hr)?hr:E_FAIL;if(hardwareInstance.size()+1>size_t(capacity))return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);wcscpy_s(target,capacity,hardwareInstance.c_str());return S_OK;});}
EXPORT HRESULT __cdecl CameraSymbolicLink(wchar_t* target,int capacity){if(!target||capacity<1)return E_INVALIDARG;return Dispatch([=]{HRESULT hr=OpenHardware();if(FAILED(hr))return hr;if(hardwareLink.size()+1>size_t(capacity))return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);wcscpy_s(target,capacity,hardwareLink.c_str());return S_OK;});}
EXPORT HRESULT __cdecl CameraQuery(int group,int property,LONG* lo,LONG* hi,LONG* step,LONG* def,LONG* caps,LONG* value,LONG* mode){if(!lo||!hi||!step||!def||!caps||!value||!mode)return E_POINTER;return Dispatch([=]{return Query(group,property,lo,hi,step,def,caps,value,mode);});}
EXPORT HRESULT __cdecl CameraSet(int group,int property,LONG value,LONG mode){return Dispatch([=]{LONG lo,hi,step,def,caps,old,oldMode;HRESULT hr=Query(group,property,&lo,&hi,&step,&def,&caps,&old,&oldMode);if(FAILED(hr))return hr;if(((caps==0)?mode!=0:((mode!=1&&mode!=2)||!(caps&mode)))||value<lo||value>hi||(step>0&&(value-lo)%step))return E_INVALIDARG;hr=group==0?imageControls->Set(property,value,mode):cameraControls->Set(property,value,mode);if(SUCCEEDED(hr))preferences[{group,property}]={value,mode};return hr;});}
EXPORT HRESULT __cdecl PreviewStartSized(int wantedWidth,int wantedHeight){return Dispatch([=]{
    if(reader)return S_FALSE;Check(OpenHardware());{std::lock_guard<std::mutex> lock(framesMutex);latest.clear();recentArrivals.clear();sequence=0;lastExposureTime=0;frameWidth=frameHeight=0;firstArrivalTime=lastArrivalTime=lastSampleTime=0;}previewStatus=S_FALSE;stopping=false;
    try{
        callback.Attach(new ReaderCallback());ComPtr<IMFAttributes> attrs;Check(MFCreateAttributes(&attrs,4));Check(attrs->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING,TRUE));Check(attrs->SetUINT32(MF_LOW_LATENCY,TRUE));Check(attrs->SetUnknown(MF_SOURCE_READER_ASYNC_CALLBACK,callback.Get()));
        Check(MFCreateSourceReaderFromMediaSource(hardware.Get(),attrs.Get(),&reader));Check(reader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS,FALSE));Check(reader->SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM,TRUE));
        if(wantedWidth>0&&wantedHeight>0){
            for(DWORD index=0;;++index){ComPtr<IMFMediaType> mode;HRESULT next=reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM,index,&mode);if(FAILED(next))break;UINT32 w=0,h=0;MFGetAttributeSize(mode.Get(),MF_MT_FRAME_SIZE,&w,&h);if(w==UINT32(wantedWidth)&&h==UINT32(wantedHeight)){Check(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM,nullptr,mode.Get()));break;}}
        }
        ComPtr<IMFMediaType> rgb;Check(MFCreateMediaType(&rgb));Check(rgb->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video));Check(rgb->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_RGB32));Check(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM,nullptr,rgb.Get()));callback->sourceReader=reader.Get();Check(reader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM,0,nullptr,nullptr,nullptr,nullptr));return S_OK;
    }catch(...){StopPreview();throw;}
});}
EXPORT HRESULT __cdecl PreviewStart(){return PreviewStartSized(0,0);}
EXPORT void __cdecl PreviewStop(){Dispatch([]{StopPreview();return S_OK;});}
EXPORT HRESULT __cdecl PreviewInfo(int* width,int* height,unsigned long long* serial){if(!width||!height||!serial)return E_POINTER;std::lock_guard<std::mutex> lock(framesMutex);*width=frameWidth;*height=frameHeight;*serial=sequence;return previewStatus;}
EXPORT HRESULT __cdecl PreviewExposure(UINT64* exposureTime){if(!exposureTime)return E_POINTER;std::lock_guard<std::mutex> lock(framesMutex);*exposureTime=0;if(stopping||previewStatus!=S_OK||MFGetSystemTime()-lastArrivalTime>25000000||!lastExposureTime)return S_FALSE;*exposureTime=lastExposureTime;return S_OK;}
EXPORT HRESULT __cdecl PreviewCopy(BYTE* target,int width,int height,int stride,unsigned long long* serial){if(!target||!serial)return E_POINTER;std::lock_guard<std::mutex> lock(framesMutex);if(latest.empty()||width!=frameWidth||height!=frameHeight||stride<width*4)return E_INVALIDARG;if(*serial==sequence)return S_FALSE;for(int y=0;y<height;++y)memcpy(target+size_t(y)*stride,latest.data()+size_t(y)*width*4,size_t(width)*4);*serial=sequence;return S_OK;}
EXPORT HRESULT __cdecl PreviewStats(double* fps,double* ageMs,double* copyMs){if(!fps||!ageMs||!copyMs)return E_POINTER;std::lock_guard<std::mutex> lock(framesMutex);const auto now=MFGetSystemTime();while(!recentArrivals.empty()&&recentArrivals.front()<now-10000000)recentArrivals.pop_front();*fps=(recentArrivals.size()>1&&recentArrivals.back()>recentArrivals.front())?(recentArrivals.size()-1)*10000000.0/(recentArrivals.back()-recentArrivals.front()):0;*ageMs=lastSampleTime>0?(now-lastSampleTime)/10000.0:-1;*copyMs=lastConversionMs;return previewStatus;}
EXPORT void __cdecl CameraClose(){
    std::lock_guard<std::mutex> lifecycle(lifecycleMutex);if(!engineThread.joinable())return;
    {std::lock_guard<std::mutex> lock(commandsMutex);quitting=true;}commandsReady.notify_one();engineThread.join();
}

