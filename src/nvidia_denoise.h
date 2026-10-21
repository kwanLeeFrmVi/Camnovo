#pragma once
#include <memory>
class NvidiaDenoiser {
public:
    NvidiaDenoiser();
    ~NvidiaDenoiser();
    HRESULT Process(IMFMediaType* type, IMFSample** sample,DWORD requested=MAXDWORD);
    DWORD Level() const { return quality; }
private:
    struct State;
    std::unique_ptr<State> state;
    ULONGLONG checked=0;
    DWORD quality=0;
    HRESULT failed=S_OK;
};
