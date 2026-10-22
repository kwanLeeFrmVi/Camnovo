#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wrl/client.h>
#include <cstdio>
#include <string>
#include <cwctype>
#include "camera_source_selection.h"
using Microsoft::WRL::ComPtr;
static void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
static void Capture(bool request1440p, bool listOnly) {
    ComPtr<IMFAttributes> attrs;
    Check(MFCreateAttributes(&attrs, 1));
    Check(attrs->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID));
    IMFActivate** devices = nullptr; UINT32 count = 0;
    Check(MFEnumDeviceSources(attrs.Get(), &devices, &count));
    ComPtr<IMFActivate> selected;
    int preferredRank=0;
    for (UINT32 i = 0; i < count; ++i) {
        wchar_t* link = nullptr; UINT32 len = 0;
        if (SUCCEEDED(devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, &link, &len))) {
            int rank=CamnovoSourceRank(link);
            if (rank>preferredRank) { selected = devices[i];preferredRank=rank; }
            CoTaskMemFree(link);
        }
        devices[i]->Release();
    }
    CoTaskMemFree(devices);
    if (!selected) throw HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    std::puts(preferredRank==2?"Selected RGB path: Frame Server camera interface.":"Selected RGB path: legacy capture interface (processing attachment must be checked separately).");
    ComPtr<IMFMediaSource> source;
    std::puts("Opening RGB media source...");
    Check(selected->ActivateObject(IID_PPV_ARGS(&source)));
    try {
        ComPtr<IMFSourceReader> reader;
        std::puts("Creating source reader...");
        Check(MFCreateSourceReaderFromMediaSource(source.Get(), nullptr, &reader));
        std::puts("Selecting RGB video stream...");
        Check(reader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, FALSE));
        Check(reader->SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, TRUE));
        bool selected1440p = false;
        for (DWORD index = 0; ; ++index) {
            ComPtr<IMFMediaType> mode;
            const HRESULT next = reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, index, &mode);
            if (next == MF_E_NO_MORE_TYPES) break;
            Check(next);
            UINT32 w = 0, h = 0, n = 0, d = 1; GUID format = GUID_NULL;
            if (FAILED(MFGetAttributeSize(mode.Get(), MF_MT_FRAME_SIZE, &w, &h))) continue;
            MFGetAttributeRatio(mode.Get(), MF_MT_FRAME_RATE, &n, &d);
            mode->GetGUID(MF_MT_SUBTYPE, &format);
            std::printf("Advertised mode %lu: %ux%u, %u/%u fps, subtype 0x%08lX\n", index, w, h, n, d, format.Data1);
            if (request1440p && !selected1440p && w == 2560 && h == 1440) {
                Check(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, mode.Get()));
                selected1440p = true;
            }
        }
        if (request1440p && !selected1440p) throw MF_E_INVALIDMEDIATYPE;
        if (listOnly) { reader.Reset(); const HRESULT stopped=source->Shutdown(); if (stopped!=MF_E_SHUTDOWN) Check(stopped); return; }
        unsigned frames = 0, reads = 0;
        while (frames < 120 && ++reads <= 600) {
            DWORD stream = 0, flags = 0; LONGLONG timestamp = 0;
            ComPtr<IMFSample> sample;
            Check(reader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &stream, &flags, &timestamp, &sample));
            if (flags & (MF_SOURCE_READERF_ERROR | MF_SOURCE_READERF_ENDOFSTREAM)) throw E_FAIL;
            if (sample) {
                ++frames;
                if (frames == 1 || (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED)) {
                    ComPtr<IMFMediaType> current; Check(reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, &current));
                    UINT32 w = 0, h = 0; Check(MFGetAttributeSize(current.Get(), MF_MT_FRAME_SIZE, &w, &h));
                    std::printf("Received dimensions: %ux%u\n", w, h);
                    if (request1440p && (w != 2560 || h != 1440)) throw MF_E_INVALIDMEDIATYPE;
                }
                if (frames == 1 || frames % 30 == 0) { std::printf("Frame %u, timestamp %lld\n", frames, timestamp); std::fflush(stdout); }
            }
        }
        if (frames < 120) throw E_FAIL;
        std::puts("PASS: received 120 RGB webcam frames. Verify extension Output events separately.");
    } catch (...) { source->Shutdown(); throw; }
    const HRESULT stopped=source->Shutdown(); if (stopped!=MF_E_SHUTDOWN) Check(stopped);
}
int wmain(int argc, wchar_t** argv) {
    setvbuf(stdout,nullptr,_IONBF,0);
    bool request1440p = false, listOnly = false;
    for (int i = 1; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--1440p") request1440p = true;
        else if (std::wstring(argv[i]) == L"--list") listOnly = true;
        else return 2;
    }
    const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(com)) return 1;
    const HRESULT mf = MFStartup(MF_VERSION);
    int result = 1;
    if (SUCCEEDED(mf)) {
        try { Capture(request1440p, listOnly); result = 0; }
        catch (HRESULT hr) { std::printf("FAIL: HRESULT 0x%08lX\n", static_cast<unsigned long>(hr)); }
        MFShutdown();
    }
    CoUninitialize();
    return result;
}
