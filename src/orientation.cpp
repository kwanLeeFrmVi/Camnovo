#include "stdafx.h"
#include "orientation.h"
#include "flip_pixels.h"
#include "locked_frame.h"
#include <mutex>
#include <cmath>
#pragma optimize("gt", on)
namespace {
std::mutex settingsMutex;
ULONGLONG lastRead = 0;
DWORD cachedFlags = 0;
int cachedGain = 0;
struct Adjustments {DWORD flags;int gain;};
Adjustments Settings() {
    std::lock_guard<std::mutex> lock(settingsMutex);
    const auto now = GetTickCount64();
    if (lastRead == 0 || now - lastRead >= 75) {
        DWORD flags = 0, bytes = sizeof(flags);
        if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\LenovoWebcamPrototype", L"FlipFlags",
            RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &flags, &bytes) != ERROR_SUCCESS) flags = 0;
        cachedFlags = flags & 3;
        DWORD rawGain=0;bytes=sizeof(rawGain);
        if(RegGetValueW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\LenovoWebcamPrototype",L"SoftwareGainThirds",RRF_RT_REG_DWORD|RRF_SUBKEY_WOW6464KEY,nullptr,&rawGain,&bytes)!=ERROR_SUCCESS)rawGain=0;
        cachedGain=static_cast<int>(rawGain);if(cachedGain < -6 || cachedGain > 12)cachedGain=0;
        lastRead = now;
    }
    return {cachedFlags,cachedGain};
}

}
HRESULT ApplyPrototypeOrientation(IMFMediaType* type, IMFSample** sample) {
    const auto settings=Settings();return ApplyPrototypeImageAdjustments(type,sample,settings.flags,settings.gain);
}
HRESULT ApplyPrototypeOrientationWithFlags(IMFMediaType* type, IMFSample** sample, DWORD flags) {
    return ApplyPrototypeImageAdjustments(type,sample,flags,0);
}
HRESULT ApplyPrototypeImageAdjustments(IMFMediaType* type,IMFSample** sample,DWORD flags,int gainThirds) {
    flags &= 3;
    if(gainThirds < -6 || gainThirds > 12)return E_INVALIDARG;
    if (!flags && !gainThirds) return S_FALSE;
    if (!type || !sample || !*sample) return E_POINTER;
    GUID format; UINT32 width = 0, height = 0;
    HRESULT hr = type->GetGUID(MF_MT_SUBTYPE, &format);
    if (FAILED(hr)) return hr;
    hr = MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height);
    if (FAILED(hr)) return hr;
    const bool nv12 = format == MFVideoFormat_NV12;
    const bool yuy2 = format == MFVideoFormat_YUY2;
    const bool uyvy = format == MFVideoFormat_UYVY;
    const bool rgb32 = format == MFVideoFormat_RGB32 || format == MFVideoFormat_ARGB32;
    const bool rgb24 = format == MFVideoFormat_RGB24;
    if (!nv12 && !yuy2 && !uyvy && !rgb32 && !rgb24) return MF_E_INVALIDMEDIATYPE;
    if (!width || !height || width > 32768 || height > 32768 || ((nv12 || yuy2 || uyvy) && width % 2) || (nv12 && height % 2)) return E_INVALIDARG;
    DWORD buffers = 0;
    hr = (*sample)->GetBufferCount(&buffers);
    if (FAILED(hr)) return hr;
    if (buffers != 1) return MF_E_INVALIDMEDIATYPE;
    ComPtr<IMFMediaBuffer> input, output;
    hr = (*sample)->GetBufferByIndex(0, &input); if (FAILED(hr)) return hr;
    Locked2D source, target;
    hr = source.Lock(input.Get(), MF2DBuffer_LockFlags_Read, type); if (FAILED(hr)) return hr;
    if (nv12 && source.stride <= 0) return MF_E_INVALIDMEDIATYPE;
    hr = MFCreate2DMediaBuffer(width, height, format.Data1, FALSE, &output); if (FAILED(hr)) return hr;
    hr = target.Lock(output.Get(), MF2DBuffer_LockFlags_Write); if (FAILED(hr)) return hr;
    const bool horizontal = (flags & 1) != 0, vertical = (flags & 2) != 0;
    const size_t units = (yuy2 || uyvy) ? width / 2 : width;
    const size_t bytes = rgb32 || yuy2 || uyvy ? 4 : rgb24 ? 3 : 1;
    const size_t rows = nv12 ? height + height / 2 : height;
    if (!source.Covers(units * bytes, rows) || !target.Covers(units * bytes, rows)) return MF_E_BUFFERTOOSMALL;
    ZeroMemory(target.start, target.length);
    FlipRows(source.scan, source.stride, target.scan, target.stride, units, height, bytes, horizontal, vertical, yuy2 || uyvy, uyvy);
    if (nv12) {
        FlipRows(source.scan + static_cast<ptrdiff_t>(height) * source.stride, source.stride,
            target.scan + static_cast<ptrdiff_t>(height) * target.stride, target.stride,
            width / 2, height / 2, 2, horizontal, vertical);
    }
    if(gainThirds){
        // Post-capture digital signal gain, explicitly not sensor ISO. Work in
        // the existing encoded color space and preserve its nominal range.
        // A lookup pass shares the flip buffer: no added frame queue or GPU load.
        const double gain=std::exp2(gainThirds/3.0);
        BYTE luma[256]{},chroma[256]{},rgb[256]{};
        bool full=MFGetAttributeUINT32(type,MF_MT_VIDEO_NOMINAL_RANGE,MFNominalRange_16_235)==MFNominalRange_0_255;
        int black=full?0:16,white=full?255:235,chromaLow=full?0:16,chromaHigh=full?255:240;
        for(int value=0;value<256;value++){
            luma[value]=static_cast<BYTE>(std::clamp<int>(static_cast<int>(std::lround(black+(value-black)*gain)),black,white));
            chroma[value]=static_cast<BYTE>(std::clamp<int>(static_cast<int>(std::lround(128+(value-128)*gain)),chromaLow,chromaHigh));
            rgb[value]=static_cast<BYTE>(std::clamp<int>(static_cast<int>(std::lround(value*gain)),0,255));
        }
        for(UINT32 y=0;y<height;y++){
            BYTE* row=target.scan+static_cast<ptrdiff_t>(y)*target.stride;
            if(nv12){for(UINT32 x=0;x<width;x++)row[x]=luma[row[x]];}
            else if(yuy2||uyvy){for(UINT32 x=0;x<width*2;x++)row[x]=((x&1)==(uyvy?1u:0u))?luma[row[x]]:chroma[row[x]];}
            else{for(UINT32 x=0;x<width;x++)for(unsigned channel=0;channel<3;channel++)row[x*bytes+channel]=rgb[row[x*bytes+channel]];}
        }
        if(nv12)for(UINT32 y=0;y<height/2;y++){BYTE* row=target.scan+static_cast<ptrdiff_t>(height+y)*target.stride;for(UINT32 x=0;x<width;x++)row[x]=chroma[row[x]];}
    }
    // Unlock before returning a sample containing the new buffer.
    source.Unlock();
    target.Unlock();
    ComPtr<IMFSample> replacement;
    hr = MFCreateSample(&replacement); if (FAILED(hr)) return hr;
    hr = (*sample)->CopyAllItems(replacement.Get()); if (FAILED(hr)) return hr;
    LONGLONG value = 0;
    if (SUCCEEDED((*sample)->GetSampleTime(&value))) replacement->SetSampleTime(value);
    if (SUCCEEDED((*sample)->GetSampleDuration(&value))) replacement->SetSampleDuration(value);
    DWORD sampleFlags = 0;
    if (SUCCEEDED((*sample)->GetSampleFlags(&sampleFlags))) replacement->SetSampleFlags(sampleFlags);
    DWORD length = 0; hr = output->GetMaxLength(&length); if (FAILED(hr)) return hr;
    hr = output->SetCurrentLength(length); if (FAILED(hr)) return hr;
    hr = replacement->AddBuffer(output.Get()); if (FAILED(hr)) return hr;
    (*sample)->Release(); *sample = replacement.Detach();
    return S_OK;
}

