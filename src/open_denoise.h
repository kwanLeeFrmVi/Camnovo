// SPDX-License-Identifier: MIT
#pragma once
#include <memory>
// Spatial, edge-preserving bilateral denoising. Direct3D 11 compute;
// no vendor runtime, temporal history or look-ahead frames.
class OpenGpuDenoiser {
public:
    OpenGpuDenoiser();
    ~OpenGpuDenoiser();
    HRESULT Process(IMFMediaType* type,IMFSample** sample,DWORD level,bool chromaOnly=false);
private:
    struct State;
    std::unique_ptr<State> state;
};
