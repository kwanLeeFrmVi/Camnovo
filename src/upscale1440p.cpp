#include "stdafx.h"
#include "multipinmft.h"
#include "upscale1440p.h"
bool Prototype1440pEnabled() {
    DWORD enabled = 0, length = sizeof(enabled);
    return RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\LenovoWebcamPrototype", L"Enable1440p",
        RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &enabled, &length) == ERROR_SUCCESS && enabled == 1;
}
STDMETHODIMP C1440pOutPin::AddMediaType(DWORD* pos, IMFMediaType* nativeType) {
    if (!nativeType) return E_POINTER;
    // Decode compressed camera formats before pixel processing.
    HRESULT hr = CTranslateOutPin::AddMediaType(pos, nativeType);
    if (FAILED(hr)) return hr;
    GUID format = GUID_NULL; UINT32 width = 0, height = 0;
    if (FAILED(nativeType->GetGUID(MF_MT_SUBTYPE, &format)) ||
        FAILED(MFGetAttributeSize(nativeType, MF_MT_FRAME_SIZE, &width, &height))) return S_OK;
    const bool compressed = format == MFVideoFormat_MJPG || format == MFVideoFormat_H264;
    if (width != 1920 || height != 1080 ||
        (format != MFVideoFormat_NV12 && format != MFVideoFormat_YUY2 &&
         format != MFVideoFormat_RGB32 && format != MFVideoFormat_ARGB32 && !compressed)) return S_OK;
    ComPtr<IMFMediaType> output;
    hr = MFCreateMediaType(&output); if (FAILED(hr)) return hr;
    hr = nativeType->CopyAllItems(output.Get()); if (FAILED(hr)) return hr;
    const GUID outputFormat=compressed||format==MFVideoFormat_YUY2?MFVideoFormat_NV12:format;
    hr=output->SetGUID(MF_MT_SUBTYPE,outputFormat);if(FAILED(hr))return hr;
    hr = MFSetAttributeSize(output.Get(), MF_MT_FRAME_SIZE, 2560, 1440); if (FAILED(hr)) return hr;
    for (const auto& attribute : {MF_MT_DEFAULT_STRIDE, MF_MT_MINIMUM_DISPLAY_APERTURE,
        MF_MT_GEOMETRIC_APERTURE, MF_MT_PAN_SCAN_APERTURE, MF_MT_AVG_BITRATE, MF_MT_VIDEO_ROTATION,
        MF_MT_MPEG2_PROFILE, MF_MT_MPEG2_LEVEL})
        output->DeleteItem(attribute);
    UINT32 size = 0;
    hr = MFCalculateImageSize(outputFormat, 2560, 1440, &size); if (FAILED(hr)) return hr;
    hr = output->SetUINT32(MF_MT_SAMPLE_SIZE, size); if (FAILED(hr)) return hr;
    hr = output->SetUINT32(MF_MT_COMPRESSED, FALSE); if (FAILED(hr)) return hr;
    hr = output->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE); if (FAILED(hr)) return hr;
    hr = output->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE); if (FAILED(hr)) return hr;
    hr = ExceptionBoundary([&]() {
        m_TranslatedMediaTypes.insert(std::make_pair(output, ComPtr<IMFMediaType>(nativeType)));
    });
    if (FAILED(hr)) return hr;
    return CBasePin::AddMediaType(nullptr, output.Get());
}

