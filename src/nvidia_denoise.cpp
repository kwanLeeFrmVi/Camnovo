#include "stdafx.h"
#include "nvidia_denoise.h"
#include "locked_frame.h"
#include <string>
#include <stdexcept>
#include <cstddef>
namespace {
// NVIDIA's documented x64 NvCVImage C ABI; descriptors are owned by the runtime.
struct Image {
    unsigned width,height; int pitch; int format,type;
    unsigned char pixelBytes,componentBytes,numComponents,planar,gpuMem,colorSpace,reserved[2];
    void* pixels; void* deletePtr; void(*deleteProc)(void*); unsigned long long bufferBytes;
};
static_assert(sizeof(Image)==64 && offsetof(Image,pixels)==32,"NvCVImage ABI");
struct VendorError { int code; };
void Check(int code) { if(code)throw VendorError{code}; }
template<class T>T Function(HMODULE module,const char* name) {
    auto f=reinterpret_cast<T>(GetProcAddress(module,name));if(!f)throw E_NOINTERFACE;return f;
}
struct Api {
    int(*createImage)(unsigned,unsigned,int,int,unsigned,unsigned,unsigned,Image**);
    void(*destroyImage)(Image*);
    int(*transfer)(const Image*,Image*,float,void*,Image*);
    int(*createStream)(void**);int(*destroyStream)(void*);int(*syncStream)(void*);
    int(*createEffect)(const char*,void**);int(*destroyEffect)(void*);
    int(*setImage)(void*,const char*,Image*);int(*setStream)(void*,const char*,void*);
    int(*setU32)(void*,const char*,unsigned);int(*setS32)(void*,const char*,int);
    int(*load)(void*);int(*run)(void*,int);
    Api() {
        wchar_t system[MAX_PATH],modulePath[MAX_PATH];HMODULE self=nullptr;
        GetSystemDirectoryW(system,MAX_PATH);
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&Check),&self);
        if(!GetModuleFileNameW(self,modulePath,MAX_PATH))throw E_FAIL;
        std::wstring root(modulePath);root.resize(root.find_last_of(L"\\"));root+=L"\\Nvidia";
        auto loadModule=[](const std::wstring& path,DWORD flags) {
            auto m=LoadLibraryExW(path.c_str(),nullptr,flags);if(!m)throw HRESULT_FROM_WIN32(GetLastError());return m;
        };
        // Preload Windows' own libraries before searching the private runtime directory.
        for(auto name:{L"nvcuda.dll",L"dxgi.dll",L"d3d11.dll",L"d3d12.dll",L"version.dll",L"winmm.dll"})loadModule(std::wstring(system)+L"\\"+name,LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!AddDllDirectory(root.c_str()))throw HRESULT_FROM_WIN32(GetLastError());
        SetEnvironmentVariableW(L"NV_VIDEO_EFFECTS_PATH",root.c_str());
        constexpr DWORD flags=LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS;
        loadModule(root+L"\\nvngxruntime.dll",flags);
        auto cv=loadModule(root+L"\\NVCVImage.dll",flags);
        auto vfx=loadModule(root+L"\\NVVideoEffects.dll",flags);
        loadModule(root+L"\\nvVFXVideoSuperRes.dll",flags);
        auto getVersion=Function<int(*)(unsigned*)>(vfx,"NvVFX_GetVersion");unsigned version=0;Check(getVersion(&version));
        if(version!=0x01020000)throw E_NOTIMPL;
        createImage=Function<decltype(createImage)>(cv,"NvCVImage_Create");destroyImage=Function<decltype(destroyImage)>(cv,"NvCVImage_Destroy");
        transfer=Function<decltype(transfer)>(cv,"NvCVImage_Transfer");
        createStream=Function<decltype(createStream)>(vfx,"NvVFX_CudaStreamCreate");destroyStream=Function<decltype(destroyStream)>(vfx,"NvVFX_CudaStreamDestroy");syncStream=Function<decltype(syncStream)>(vfx,"NvVFX_CudaStreamSynchronize");
        createEffect=Function<decltype(createEffect)>(vfx,"NvVFX_CreateEffect");destroyEffect=Function<decltype(destroyEffect)>(vfx,"NvVFX_DestroyEffect");
        setImage=Function<decltype(setImage)>(vfx,"NvVFX_SetImage");setStream=Function<decltype(setStream)>(vfx,"NvVFX_SetCudaStream");
        setU32=Function<decltype(setU32)>(vfx,"NvVFX_SetU32");setS32=Function<decltype(setS32)>(vfx,"NvVFX_SetS32");
        load=Function<decltype(load)>(vfx,"NvVFX_Load");run=Function<decltype(run)>(vfx,"NvVFX_Run");
    }
};
Api& Runtime(){static Api api;return api;}
void Hr(HRESULT result){if(FAILED(result))throw result;}
}
struct NvidiaDenoiser::State {
    Api& api;unsigned width,height,quality;void* stream=nullptr;void* effect=nullptr;
    Image *cpuIn=nullptr,*gpuIn=nullptr,*gpuOut=nullptr,*cpuOut=nullptr,*temporary=nullptr;
    State(Api& a,unsigned w,unsigned h,unsigned q):api(a),width(w),height(h),quality(q) {
        try {
            Check(api.setS32(nullptr,"GPU",0));Check(api.createStream(&stream));
            // NvCVImage 1.2 adds ARGB/ABGR at 8/9; YUV420 is 10.
            Check(api.createImage(w,h,10,1,7,0,32,&cpuIn));Check(api.createImage(w,h,10,1,7,0,32,&cpuOut));
            Check(api.createImage(w,h,6,1,0,1,32,&gpuIn));Check(api.createImage(w,h,6,1,0,1,32,&gpuOut));
            Check(api.createImage(w,h,10,1,7,1,32,&temporary));
            if(cpuIn->width!=w||cpuIn->height!=h||cpuIn->pixelBytes!=1||cpuIn->numComponents!=3||cpuIn->planar!=7||cpuIn->pitch<int(w)||!cpuIn->pixels||cpuIn->bufferBytes<uint64_t(cpuIn->pitch)*(h+h/2))throw E_UNEXPECTED;
            Check(api.createEffect("VideoSuperRes",&effect));Check(api.setImage(effect,"SrcImage0",gpuIn));Check(api.setImage(effect,"DstImage0",gpuOut));
            Check(api.setStream(effect,"CudaStream",stream));Check(api.setU32(effect,"QualityLevel",7+q));Check(api.load(effect));
        } catch(...) {Release();throw;}
    }
    void Release() {
        if(effect)api.destroyEffect(effect);effect=nullptr;
        for(auto image:{temporary,cpuOut,gpuOut,gpuIn,cpuIn})if(image)api.destroyImage(image);
        temporary=cpuOut=gpuOut=gpuIn=cpuIn=nullptr;
        if(stream)api.destroyStream(stream);stream=nullptr;
    }
    ~State(){Release();}
};
NvidiaDenoiser::NvidiaDenoiser()=default;
NvidiaDenoiser::~NvidiaDenoiser()=default;
HRESULT NvidiaDenoiser::Process(IMFMediaType* type,IMFSample** sample,DWORD requested) {
    const auto now=GetTickCount64();
    if(requested!=MAXDWORD||!checked||now-checked>=100) {
        DWORD q=requested,bytes=sizeof(q);
        if(requested==MAXDWORD&&(RegGetValueW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\LenovoWebcamPrototype",L"DenoiseLevel",RRF_RT_REG_DWORD|RRF_SUBKEY_WOW6464KEY,nullptr,&q,&bytes)!=ERROR_SUCCESS))q=0;
        if(q>4)q=0;
        if(q!=quality){state.reset();quality=q;failed=S_OK;}
        checked=now;
    }
    if(!quality)return S_FALSE;
    if(FAILED(failed))return failed;
    try {
        if(!type||!sample||!*sample)throw E_POINTER;
        GUID subtype;UINT32 w=0,h=0;Hr(type->GetGUID(MF_MT_SUBTYPE,&subtype));Hr(MFGetAttributeSize(type,MF_MT_FRAME_SIZE,&w,&h));
        if(subtype!=MFVideoFormat_NV12||!w||!h||(w%2)||(h%2)||w>3840||h>2160)throw MF_E_INVALIDMEDIATYPE;
        if(!state||state->width!=w||state->height!=h)state=std::make_unique<State>(Runtime(),w,h,quality);
        auto& s=*state;Check(s.api.setS32(nullptr,"GPU",0));
        ComPtr<IMFMediaBuffer> input,output;Hr((*sample)->ConvertToContiguousBuffer(&input));
        Locked2D src,dst;Hr(src.Lock(input.Get(),MF2DBuffer_LockFlags_Read,type));
        if(src.stride<=0||!src.Covers(w,h+h/2))throw MF_E_BUFFERTOOSMALL;
        unsigned matrix=MFGetAttributeUINT32(type,MF_MT_YUV_MATRIX,MFVideoTransferMatrix_BT709);
        unsigned range=MFGetAttributeUINT32(type,MF_MT_VIDEO_NOMINAL_RANGE,MFNominalRange_16_235);
        s.cpuIn->colorSpace=s.cpuOut->colorSpace=static_cast<unsigned char>((matrix==MFVideoTransferMatrix_BT709?1:0)|(range==MFNominalRange_0_255?4:0));
        s.temporary->colorSpace=s.cpuIn->colorSpace;
        for(unsigned y=0;y<h+h/2;++y)memcpy(static_cast<BYTE*>(s.cpuIn->pixels)+size_t(y)*s.cpuIn->pitch,src.scan+size_t(y)*src.stride,w);
        src.Unlock();
        Check(s.api.transfer(s.cpuIn,s.gpuIn,1.0f,s.stream,s.temporary));Check(s.api.run(s.effect,0));
        Check(s.api.transfer(s.gpuOut,s.cpuOut,1.0f,s.stream,s.temporary));Check(s.api.syncStream(s.stream));
        Hr(MFCreate2DMediaBuffer(w,h,MFVideoFormat_NV12.Data1,FALSE,&output));Hr(dst.Lock(output.Get(),MF2DBuffer_LockFlags_Write));
        if(!dst.Covers(w,h+h/2))throw MF_E_BUFFERTOOSMALL;
        ZeroMemory(dst.start,dst.length);
        for(unsigned y=0;y<h+h/2;++y)memcpy(dst.scan+size_t(y)*dst.stride,static_cast<BYTE*>(s.cpuOut->pixels)+size_t(y)*s.cpuOut->pitch,w);
        dst.Unlock();DWORD length=0;Hr(output->GetMaxLength(&length));Hr(output->SetCurrentLength(length));
        ComPtr<IMFSample> replacement;Hr(MFCreateSample(&replacement));Hr((*sample)->CopyAllItems(replacement.Get()));
        LONGLONG value=0;DWORD flags=0;
        if(SUCCEEDED((*sample)->GetSampleTime(&value)))Hr(replacement->SetSampleTime(value));
        if(SUCCEEDED((*sample)->GetSampleDuration(&value)))Hr(replacement->SetSampleDuration(value));
        if(SUCCEEDED((*sample)->GetSampleFlags(&flags)))Hr(replacement->SetSampleFlags(flags));
        Hr(replacement->AddBuffer(output.Get()));(*sample)->Release();*sample=replacement.Detach();return S_OK;
    }catch(HRESULT error){failed=error;}
     catch(const VendorError& error){failed=MAKE_HRESULT(SEVERITY_ERROR,FACILITY_ITF,0x600+(-error.code&0xFF));}
     catch(...){failed=E_FAIL;}
    state.reset();return failed;
}
