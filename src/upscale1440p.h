#pragma once
#include "basepin.h"
bool Prototype1440pEnabled();
// Reuses the sample's media-type mapping and video-processor tee.
// 1440p is a separate processed output mode; upstream remains native 1080p.
class C1440pOutPin : public CTranslateOutPin {
public:
    C1440pOutPin(ULONG id, CMultipinMft* parent, IKsControl* controls)
        : CTranslateOutPin(id, parent, controls) {
        DeleteItem(MF_SD_VIDEO_SPHERICAL);
    }
    STDMETHODIMP AddMediaType(DWORD* pos, IMFMediaType* nativeType) override;
};

