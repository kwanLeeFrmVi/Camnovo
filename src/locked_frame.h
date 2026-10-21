#pragma once
struct Locked2D {
    ComPtr<IMF2DBuffer2> buffer;
    ComPtr<IMFMediaBuffer> linear;
    BYTE* scan = nullptr; LONG stride = 0;
    BYTE* start = nullptr; DWORD length = 0;
    ~Locked2D() { Unlock(); }
    void Unlock() {
        if (scan) { if (linear) linear->Unlock(); else buffer->Unlock2D(); }
        scan = nullptr;
    }
    HRESULT Lock(IMFMediaBuffer* media, MF2DBuffer_LockFlags flags, IMFMediaType* type = nullptr) {
        HRESULT hr = media->QueryInterface(IID_PPV_ARGS(&buffer));
        if (SUCCEEDED(hr)) {
            hr = buffer->Lock2DSize(flags, &scan, &stride, &start, &length);
            if (SUCCEEDED(hr)) return hr;
        }
        if (!type || flags != MF2DBuffer_LockFlags_Read) return hr;
        GUID format; UINT32 width = 0, height = 0, rawStride = 0;
        hr = type->GetGUID(MF_MT_SUBTYPE, &format); if (FAILED(hr)) return hr;
        hr = MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height); if (FAILED(hr)) return hr;
        if (SUCCEEDED(type->GetUINT32(MF_MT_DEFAULT_STRIDE, &rawStride))) stride = static_cast<LONG>(rawStride);
        else { hr = MFGetStrideForBitmapInfoHeader(format.Data1, width, &stride); if (FAILED(hr)) return hr; }
        DWORD capacity = 0;
        hr = media->Lock(&start, &capacity, &length); if (FAILED(hr)) return hr;
        linear = media;
        scan = start;
        if (stride < 0) {
            const uint64_t offset = static_cast<uint64_t>(-static_cast<int64_t>(stride)) * (height - 1);
            if (offset >= length) { Unlock(); return MF_E_BUFFERTOOSMALL; }
            scan += offset;
        }
        return S_OK;
    }
    bool Covers(size_t rowBytes, size_t rows) const {
        if (!scan || !start || !rows) return false;
        const int64_t pitch = stride;
        if (rowBytes > static_cast<uint64_t>(pitch < 0 ? -pitch : pitch)) return false;
        const int64_t first = reinterpret_cast<int64_t>(scan);
        const int64_t last = first + (static_cast<int64_t>(rows) - 1) * pitch;
        const int64_t low = first < last ? first : last, high = first > last ? first : last;
        const int64_t begin = reinterpret_cast<int64_t>(start);
        return low >= begin && high >= begin && static_cast<uint64_t>(high-begin) + rowBytes <= length;
    }
};
