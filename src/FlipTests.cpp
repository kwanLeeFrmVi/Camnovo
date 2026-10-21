#include "flip_pixels.h"
#include <cstdio>
#include <cstring>
static int failures = 0;
template<size_t N> void Equal(const char* name, const uint8_t (&a)[N], const uint8_t (&b)[N]) {
    if (std::memcmp(a,b,N)) { std::printf("FAIL: %s\n", name); ++failures; }
}
int main() {
    const uint8_t src[] = {1,2,3,4,5,6}; uint8_t dst[6] = {};
    const uint8_t h[] = {3,2,1,6,5,4}, v[] = {4,5,6,1,2,3}, both[] = {6,5,4,3,2,1};
    FlipRows(src,3,dst,3,3,2,1,true,false); Equal("horizontal",dst,h);
    FlipRows(src,3,dst,3,3,2,1,false,true); Equal("vertical",dst,v);
    FlipRows(src,3,dst,3,3,2,1,true,true); Equal("both",dst,both);
    FlipRows(src+3,-3,dst,3,3,2,1,false,false); Equal("negative source stride",dst,v);
    const uint8_t uv[] = {10,11,20,21,30,31,40,41}; uint8_t uvOut[8] = {};
    const uint8_t uvExpected[] = {40,41,30,31,20,21,10,11};
    FlipRows(uv,4,uvOut,4,2,2,2,true,true); Equal("NV12 chroma pairs",uvOut,uvExpected);
    const uint8_t yuy[] = {1,10,2,20,3,30,4,40}; uint8_t yuyOut[8] = {};
    const uint8_t yuyExpected[] = {4,30,3,40,2,10,1,20};
    FlipRows(yuy,8,yuyOut,8,2,1,4,true,false,true,false); Equal("YUY2",yuyOut,yuyExpected);
    const uint8_t uyvy[] = {10,1,20,2,30,3,40,4}; uint8_t uyvyOut[8] = {};
    const uint8_t uyvyExpected[] = {30,4,40,3,10,2,20,1};
    FlipRows(uyvy,8,uyvyOut,8,2,1,4,true,false,true,true); Equal("UYVY",uyvyOut,uyvyExpected);
    uint8_t roundtrip[6] = {};
    FlipRows(dst,3,roundtrip,3,3,2,1,false,true); Equal("double vertical returns original",roundtrip,src);
    const uint8_t rgb[] = {1,2,3,4,5,6}; uint8_t rgbOut[6] = {};
    const uint8_t rgbExpected[] = {4,5,6,1,2,3};
    FlipRows(rgb,6,rgbOut,6,2,1,3,true,false); Equal("RGB pixels remain intact",rgbOut,rgbExpected);
    if (!failures) std::puts("PASS: orientation pixel tests.");
    return failures ? 1 : 0;
}
