#pragma once
// On unsupported formats or buffer layouts, keep the original sample unchanged.
HRESULT ApplyPrototypeOrientation(IMFMediaType* type, IMFSample** sample);
HRESULT ApplyPrototypeOrientationWithFlags(IMFMediaType* type, IMFSample** sample, DWORD flags);
HRESULT ApplyPrototypeImageAdjustments(IMFMediaType* type,IMFSample** sample,DWORD flags,int gainThirds,DWORD shadowStrength=0);
