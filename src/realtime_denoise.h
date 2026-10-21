// SPDX-License-Identifier: MIT
#pragma once
#include <memory>
#include <functional>
struct DenoiseStatus {HRESULT result=S_FALSE;DWORD level=0,backend=0;double milliseconds=0;ULONGLONG completed=0,dropped=0,delivered=0,raw=0;};
class RealtimeDenoiser {
public:
    using Processor=std::function<HRESULT(IMFMediaType*,IMFSample**,DWORD,DWORD)>;
    explicit RealtimeDenoiser(Processor processor={});
    ~RealtimeDenoiser();
    HRESULT Process(IMFMediaType* type,IMFSample** sample);
    HRESULT Process(IMFMediaType* type,IMFSample** sample,DWORD level,DWORD backend);
    DenoiseStatus Status();
private:
    struct State;
    std::unique_ptr<State> state;
    ULONGLONG checked=0;
    DWORD level=0,backend=0;
};
