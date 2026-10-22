#include "stdafx.h"
#include "orientation.h"
#include <cstdio>
#include <cstring>
#include <chrono>
#include <vector>
static void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
static void Require(bool value) { if (!value) throw E_UNEXPECTED; }
static ComPtr<IMFSample> Sample(const BYTE* bytes, DWORD length) {
    ComPtr<IMFSample> sample; ComPtr<IMFMediaBuffer> buffer;
    Check(MFCreateSample(&sample)); Check(MFCreateMemoryBuffer(length, &buffer));
    BYTE* data; Check(buffer->Lock(&data, nullptr, nullptr)); memcpy(data, bytes, length); Check(buffer->Unlock());
    Check(buffer->SetCurrentLength(length)); Check(sample->AddBuffer(buffer.Get()));
    Check(sample->SetSampleTime(12345)); Check(sample->SetSampleDuration(333333)); Check(sample->SetSampleFlags(7));
    Check(sample->SetUINT32(MFSampleExtension_Discontinuity, TRUE));
    return sample;
}
static ComPtr<IMFMediaType> Type(GUID subtype, UINT32 width, UINT32 height, LONG stride) {
    ComPtr<IMFMediaType> type; Check(MFCreateMediaType(&type));
    Check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video)); Check(type->SetGUID(MF_MT_SUBTYPE, subtype));
    Check(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, width, height));
    Check(type->SetUINT32(MF_MT_DEFAULT_STRIDE, static_cast<UINT32>(stride)));
    return type;
}
static void Pixels(IMFSample* sample, const BYTE* expected, DWORD length) {
    ComPtr<IMFMediaBuffer> buffer; Check(sample->GetBufferByIndex(0, &buffer));
    ComPtr<IMF2DBuffer> image;
    if (SUCCEEDED(buffer.As(&image))) {
        BYTE actual[128] = {}; Check(image->ContiguousCopyTo(actual, length));
        Require(memcmp(actual, expected, length) == 0);
    } else {
        BYTE* data; DWORD current = 0; Check(buffer->Lock(&data, nullptr, &current));
        bool equal = current == length && memcmp(data, expected, length) == 0;
        Check(buffer->Unlock()); Require(equal);
    }
}
int main() {
    Check(CoInitializeEx(nullptr, COINIT_MULTITHREADED)); Check(MFStartup(MF_VERSION));
    int result = 1;
    try {
        {
            const BYTE input[] = {1,2,3,4,10,20}, output[] = {4,3,2,1,10,20};
            auto type = Type(MFVideoFormat_NV12,2,2,2); auto sample = Sample(input,sizeof input);
            ComPtr<IMFSample> original = sample;
            Check(ApplyPrototypeOrientationWithFlags(type.Get(),sample.GetAddressOf(),3));
            Require(sample.Get() != original.Get()); Pixels(sample.Get(),output,sizeof output); Pixels(original.Get(),input,sizeof input);
            LONGLONG time = 0, duration = 0; DWORD flags = 0; UINT32 discontinuity = 0;
            Check(sample->GetSampleTime(&time)); Check(sample->GetSampleDuration(&duration)); Check(sample->GetSampleFlags(&flags));
            Check(sample->GetUINT32(MFSampleExtension_Discontinuity,&discontinuity));
            Require(time == 12345 && duration == 333333 && flags == 7 && discontinuity == TRUE);
        }
        {
            const BYTE input[] = {1,10,2,20,3,30,4,40}, output[] = {4,30,3,40,2,10,1,20};
            auto type = Type(MFVideoFormat_YUY2,4,1,8); auto sample = Sample(input,sizeof input);
            Check(ApplyPrototypeOrientationWithFlags(type.Get(),sample.GetAddressOf(),1)); Pixels(sample.Get(),output,sizeof output);
        }
        {
            const BYTE input[] = {9,8,7,0,6,5,4,0,3,2,1,0,12,11,10,0};
            const BYTE output[] = {3,2,1,0,12,11,10,0,9,8,7,0,6,5,4,0};
            auto type = Type(MFVideoFormat_RGB32,2,2,-8); auto sample = Sample(input,sizeof input);
            Check(ApplyPrototypeOrientationWithFlags(type.Get(),sample.GetAddressOf(),1));
            // Bottom-up input: logical first row is bytes 8..15, mirrored in each row.
            const BYTE expected[] = {12,11,10,0,3,2,1,0,6,5,4,0,9,8,7,0};
            Pixels(sample.Get(),expected,sizeof expected);
        }
        {
            const BYTE input[] = {1,2,3,4}; auto type=Type(MFVideoFormat_MJPG,2,2,2); auto sample=Sample(input,sizeof input);
            auto* original = sample.Get();
            Require(FAILED(ApplyPrototypeOrientationWithFlags(type.Get(),sample.GetAddressOf(),1)) && sample.Get()==original);
            Require(ApplyPrototypeOrientationWithFlags(type.Get(),sample.GetAddressOf(),0)==S_FALSE && sample.Get()==original);
        }
        {
            const BYTE shortInput[] = {1}; auto type=Type(MFVideoFormat_NV12,2,2,2); auto sample=Sample(shortInput,sizeof shortInput);
            auto* original=sample.Get();
            Require(FAILED(ApplyPrototypeOrientationWithFlags(type.Get(),sample.GetAddressOf(),1)) && sample.Get()==original);
        }
        std::puts("PASS: Media Foundation frame flips, linear buffers, negative stride, unchanged source, metadata, unsupported format and short-buffer handling.");
        {
            const BYTE input[]={16,32,64,128,128,144},boosted[]={16,48,112,235,128,160},dimmed[]={16,24,40,72,128,136},mirrored[]={235,112,48,16,128,160};
            auto type=Type(MFVideoFormat_NV12,2,2,2);auto sample=Sample(input,sizeof input);auto original=sample;
            Require(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0)==S_FALSE&&sample.Get()==original.Get());
            Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,3));Pixels(sample.Get(),boosted,sizeof boosted);Pixels(original.Get(),input,sizeof input);
            LONGLONG time=0;sample->GetSampleTime(&time);Require(time==12345);
            sample=Sample(input,sizeof input);Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,-3));Pixels(sample.Get(),dimmed,sizeof dimmed);
            sample=Sample(input,sizeof input);Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),3,3));Pixels(sample.Get(),mirrored,sizeof mirrored);
            sample=Sample(input,sizeof input);auto same=sample.Get();Require(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,13)==E_INVALIDARG&&sample.Get()==same);
        }
        {
            const BYTE input[]={16,32,200,201,64,128,202,203,128,144,204,205},output[]={16,48,112,235,128,160};
            auto type=Type(MFVideoFormat_NV12,2,2,4);auto sample=Sample(input,sizeof input);Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,3));Pixels(sample.Get(),output,sizeof output);
        }
        {
            const BYTE input[]={0,16,64,255,128,144},output[]={0,32,128,255,128,160};auto type=Type(MFVideoFormat_NV12,2,2,2);type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE,MFNominalRange_0_255);auto sample=Sample(input,sizeof input);Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,3));Pixels(sample.Get(),output,sizeof output);
        }
        {
            const BYTE input[]={20,40,200,77},output[]={40,80,255,77};auto type=Type(MFVideoFormat_ARGB32,1,1,4);auto sample=Sample(input,sizeof input);Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,3));Pixels(sample.Get(),output,sizeof output);
        }
        {
            const BYTE input[]={128,32,144,64},output[]={128,48,160,112};auto type=Type(MFVideoFormat_UYVY,2,1,4);auto sample=Sample(input,sizeof input);Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,3));Pixels(sample.Get(),output,sizeof output);
        }
        std::puts("PASS: software gain 2x/0.5x, clipping, limited/full-range NV12, padded stride, chroma, RGB alpha, UYVY, combined flips, neutral bypass, source and timestamp preservation.");
        {
            // Deep-red shadow next to a bright coloured surface: do not
            // mistake legitimate bright colour for noise or blur the luma edge.
            const BYTE input[]={20,20,180,180,20,20,180,180,90,200,90,200};
            const BYTE clean[]={20,20,180,180,20,20,180,180,128,128,90,200};
            auto type=Type(MFVideoFormat_NV12,4,2,4);auto sample=Sample(input,sizeof input);auto source=sample;
            Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0,100));
            Pixels(sample.Get(),clean,sizeof clean);Pixels(source.Get(),input,sizeof input);
            LONGLONG time=0,duration=0;DWORD flags=0;UINT32 discontinuity=0;
            Check(sample->GetSampleTime(&time));Check(sample->GetSampleDuration(&duration));Check(sample->GetSampleFlags(&flags));Check(sample->GetUINT32(MFSampleExtension_Discontinuity,&discontinuity));
            Require(time==12345&&duration==333333&&flags==7&&discontinuity==TRUE);
            sample=Sample(input,sizeof input);auto same=sample.Get();
            Require(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0,0)==S_FALSE&&sample.Get()==same);
            Require(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0,101)==E_INVALIDARG&&sample.Get()==same);
            const BYTE flipped[]={180,180,20,20,180,180,20,20,90,200,128,128};
            Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),1,0,100));Pixels(sample.Get(),flipped,sizeof flipped);
            const BYTE edge[]={20,180,20,180,20,20,20,20,90,200,90,200};
            sample=Sample(edge,sizeof edge);Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0,100));Pixels(sample.Get(),edge,sizeof edge);
            type->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_MJPG);sample=Sample(input,sizeof input);same=sample.Get();
            Require(FAILED(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0,100))&&sample.Get()==same);
        }
        {
            const BYTE padded[]={4,4,180,180,99,99,99,99,4,4,180,180,99,99,99,99,90,200,90,200,99,99,99,99};
            const BYTE clean[]={4,4,180,180,4,4,180,180,128,128,90,200};
            auto type=Type(MFVideoFormat_NV12,4,2,8);type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE,MFNominalRange_0_255);
            auto sample=Sample(padded,sizeof padded);Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0,100));Pixels(sample.Get(),clean,sizeof clean);
            const BYTE shortInput[]={20};sample=Sample(shortInput,sizeof shortInput);auto same=sample.Get();
            Require(FAILED(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0,100))&&sample.Get()==same);
        }
        {
            const BYTE uyvy[]={90,20,200,20,90,180,200,180},uyvyClean[]={128,20,128,20,90,180,200,180};
            auto type=Type(MFVideoFormat_UYVY,4,1,8);auto sample=Sample(uyvy,sizeof uyvy);Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0,100));Pixels(sample.Get(),uyvyClean,sizeof uyvyClean);
            const BYTE yuy2[]={20,90,20,200,180,90,180,200},yuy2Clean[]={20,128,20,128,180,90,180,200};
            type=Type(MFVideoFormat_YUY2,4,1,8);sample=Sample(yuy2,sizeof yuy2);Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0,100));Pixels(sample.Get(),yuy2Clean,sizeof yuy2Clean);
            const BYTE rgba[]={4,4,4,77},rgbaClean[]={4,4,4,77};
            type=Type(MFVideoFormat_ARGB32,1,1,4);sample=Sample(rgba,sizeof rgba);Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0,100));Pixels(sample.Get(),rgbaClean,sizeof rgbaClean);
        }
        std::puts("PASS: shadow cleanup neutralizes deep-shadow red chroma, preserves luma/bright colour/edge footprints, supports flips and limited/full range/padded NV12/YUY2/UYVY/alpha; source and metadata intact; Off bypasses and malformed inputs remain unchanged.");
        for(UINT width:{1920u,2560u}){
            UINT height=width==1920?1080:1440;std::vector<BYTE> pixels(width*(height+height/2),20);std::fill(pixels.begin()+width*height,pixels.end(),200);
            auto type=Type(MFVideoFormat_NV12,width,height,width);double total=0;
            for(int i=0;i<22;++i){auto sample=Sample(pixels.data(),DWORD(pixels.size()));auto begin=std::chrono::steady_clock::now();Check(ApplyPrototypeImageAdjustments(type.Get(),sample.GetAddressOf(),0,0,70));if(i>=2)total+=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-begin).count();}
            std::printf("PASS: shadow cleanup including output allocation/copy %ux%u averages %.3f ms; no temporal queue.\n",width,height,total/20);
        }
        result=0;
    } catch (HRESULT hr) { std::printf("FAIL: HRESULT 0x%08lX\n",static_cast<unsigned long>(hr)); }
    MFShutdown(); CoUninitialize(); return result;
}
