#include "stdafx.h"
#include "external_denoise.h"
#include <cstdio>
static void Hr(HRESULT hr){if(FAILED(hr))throw hr;}
static void Set(HKEY key,const wchar_t* name,DWORD value){RegSetValueExW(key,name,0,REG_DWORD,reinterpret_cast<BYTE*>(&value),4);}
int main(){CoInitializeEx(nullptr,COINIT_MULTITHREADED);MFStartup(MF_VERSION);HKEY key=nullptr;DWORD oldQ=0,oldB=0,oldW=640,cb=4;int result=0;
    try{Hr(HRESULT_FROM_WIN32(RegOpenKeyExW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\LenovoWebcamPrototype",0,KEY_READ|KEY_SET_VALUE|KEY_WOW64_64KEY,&key)));RegQueryValueExW(key,L"DenoiseLevel",nullptr,nullptr,reinterpret_cast<BYTE*>(&oldQ),&cb);cb=4;RegQueryValueExW(key,L"DenoiseBackend",nullptr,nullptr,reinterpret_cast<BYTE*>(&oldB),&cb);cb=4;RegQueryValueExW(key,L"DenoiseModelWidth",nullptr,nullptr,reinterpret_cast<BYTE*>(&oldW),&cb);Set(key,L"DenoiseModelWidth",640);
        constexpr UINT w=1920,h=1080,bytes=w*h*3/2;ComPtr<IMFMediaType> type;Hr(MFCreateMediaType(&type));type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video);type->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_NV12);MFSetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,w,h);type->SetUINT32(MF_MT_YUV_MATRIX,MFVideoTransferMatrix_BT709);type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE,MFNominalRange_16_235);
        std::vector<ComPtr<IMFSample>> frames;UINT random=123;
        for(int i=0;i<6;i++){ComPtr<IMFMediaBuffer> buffer;Hr(MFCreateMemoryBuffer(bytes,&buffer));BYTE* data=nullptr;Hr(buffer->Lock(&data,nullptr,nullptr));memset(data+size_t(w)*h,128,w*h/2);for(UINT y=0;y<h;++y)for(UINT x=0;x<w;++x){random^=random<<13;random^=random>>17;random^=random<<5;data[y*w+x]=static_cast<BYTE>((x<w/2?100:160)+int(random%49)-24);}buffer->Unlock();buffer->SetCurrentLength(bytes);ComPtr<IMFSample> sample;Hr(MFCreateSample(&sample));sample->AddBuffer(buffer.Get());sample->SetSampleTime(1000000+333333*i);sample->SetSampleDuration(333333);sample->SetUINT32(MFSampleExtension_CleanPoint,1);frames.push_back(sample);}
        ExternalDenoiser denoise;for(DWORD b=2;b<=8;b++){Set(key,L"DenoiseBackend",b);Set(key,L"DenoiseLevel",3);auto sample=frames.back();ULONGLONG begin=GetTickCount64();Hr(denoise.Process(type.Get(),sample.GetAddressOf(),3,b,frames));DWORD count=b==3?6:b==4?3:5;LONGLONG expected=1000000+333333*(b==3?5:b==5?4:6-count+count/2),time=0;Hr(sample->GetSampleTime(&time));if(time!=expected)throw E_UNEXPECTED;UINT32 marker=0;Hr(sample->GetUINT32(MFSampleExtension_CleanPoint,&marker));if(marker!=1||sample.Get()==frames.back().Get())throw E_UNEXPECTED;ComPtr<IMFMediaBuffer> output;Hr(sample->ConvertToContiguousBuffer(&output));DWORD length=0;Hr(output->GetCurrentLength(&length));if(length<bytes)throw E_UNEXPECTED;std::printf("PASS: backend %lu actual 1080p NV12 -> model -> 1080p bridge, %llu ms; source-frame timestamp %lld and metadata preserved.\n",b,GetTickCount64()-begin,time);fflush(stdout);}
    }catch(HRESULT hr){std::printf("FAIL: model bridge 0x%08lx\n",hr);result=1;}catch(...){std::puts("FAIL: model bridge exception");result=1;}
    if(key){Set(key,L"DenoiseLevel",oldQ);Set(key,L"DenoiseBackend",oldB);Set(key,L"DenoiseModelWidth",oldW);RegCloseKey(key);}MFShutdown();CoUninitialize();return result;
}



