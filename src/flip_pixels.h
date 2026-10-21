#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>
// The source and destination must be separate buffers. Strides may be negative.
inline void FlipRows(const uint8_t* src, ptrdiff_t srcStride, uint8_t* dst,
    ptrdiff_t dstStride, size_t widthUnits, size_t height, size_t unitBytes,
    bool horizontal, bool vertical, bool packedYuv = false, bool uyvy = false) {
    for (size_t y = 0; y < height; ++y) {
        const auto* in = src + static_cast<ptrdiff_t>(vertical ? height - 1 - y : y) * srcStride;
        auto* out = dst + static_cast<ptrdiff_t>(y) * dstStride;
        if(!horizontal){std::memcpy(out,in,widthUnits*unitBytes);continue;}
        for (size_t x = 0; x < widthUnits; ++x) {
            const auto* pixel = in + (horizontal ? widthUnits - 1 - x : x) * unitBytes;
            auto* target = out + x * unitBytes;
            for (size_t b = 0; b < unitBytes; ++b) target[b] = pixel[b];
            // YUY2/UYVY share chroma for two pixels. Reverse luma within each pair.
            if (horizontal && packedYuv) {
                const size_t a = uyvy ? 1 : 0, b = uyvy ? 3 : 2;
                target[a] = pixel[b]; target[b] = pixel[a];
            }
        }
    }
}
