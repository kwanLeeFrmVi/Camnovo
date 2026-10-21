// SPDX-License-Identifier: MIT
#pragma once
#include <vector>
class ExternalDenoiser {
public:
    ~ExternalDenoiser();
    HRESULT Process(IMFMediaType*,IMFSample**,DWORD,DWORD,const std::vector<Microsoft::WRL::ComPtr<IMFSample>>&);
private:
    HANDLE pipe=INVALID_HANDLE_VALUE;
    std::wstring endpoint;
    DWORD activeBackend=0,activeLevel=0;
    HRESULT Transfer(bool,void*,DWORD);
    void Close();
};
