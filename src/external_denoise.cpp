// SPDX-License-Identifier: MIT
#include "stdafx.h"
#include "external_denoise.h"
#include "locked_frame.h"
#pragma pack(push,1)
struct ModelRequest {DWORD magic,version,backend,level,width,height,count,bytes,modelWidth,request;};
struct ModelResponse {DWORD magic,version,result,bytes;LONGLONG time;double milliseconds;};
#pragma pack(pop)
static_assert(sizeof(ModelRequest)==40&&sizeof(ModelResponse)==32,"Model pipe ABI");
void ExternalDenoiser::Close(){if(pipe!=INVALID_HANDLE_VALUE){CancelIoEx(pipe,nullptr);CloseHandle(pipe);pipe=INVALID_HANDLE_VALUE;}}
ExternalDenoiser::~ExternalDenoiser(){Close();}
HRESULT ExternalDenoiser::Transfer(bool write,void* data,DWORD bytes){
    auto p=static_cast<BYTE*>(data);ULONGLONG deadline=GetTickCount64()+30000;
    while(bytes){OVERLAPPED op{};op.hEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);if(!op.hEvent)return HRESULT_FROM_WIN32(GetLastError());
        DWORD done=0;BOOL ok=write?WriteFile(pipe,p,bytes,&done,&op):ReadFile(pipe,p,bytes,&done,&op);DWORD error=ok?ERROR_SUCCESS:GetLastError();
        if(!ok&&error==ERROR_IO_PENDING){DWORD wait=WAIT_TIMEOUT;bool cancelled=false;while(wait==WAIT_TIMEOUT&&GetTickCount64()<deadline){wait=WaitForSingleObject(op.hEvent,100);if(wait==WAIT_TIMEOUT){DWORD q=0,b=0,cb=4;RegGetValueW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\LenovoWebcamPrototype",L"DenoiseLevel",RRF_RT_REG_DWORD|RRF_SUBKEY_WOW6464KEY,nullptr,&q,&cb);cb=4;RegGetValueW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\LenovoWebcamPrototype",L"DenoiseBackend",RRF_RT_REG_DWORD|RRF_SUBKEY_WOW6464KEY,nullptr,&b,&cb);if(q!=activeLevel||b!=activeBackend){cancelled=true;break;}}}if(wait==WAIT_OBJECT_0)ok=GetOverlappedResult(pipe,&op,&done,FALSE);else{CancelIoEx(pipe,&op);GetOverlappedResult(pipe,&op,&done,TRUE);error=cancelled?ERROR_CANCELLED:ERROR_TIMEOUT;}if(!ok&&error!=ERROR_TIMEOUT&&error!=ERROR_CANCELLED)error=GetLastError();}
        CloseHandle(op.hEvent);if(!ok||!done)return HRESULT_FROM_WIN32(error?error:ERROR_BROKEN_PIPE);p+=done;bytes-=done;
    }return S_OK;
}
HRESULT ExternalDenoiser::Process(IMFMediaType* type,IMFSample** sample,DWORD q,DWORD b,const std::vector<ComPtr<IMFSample>>& frames){
    try{
        wchar_t id[80]{};DWORD size=sizeof(id);
        if(RegGetValueW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\LenovoWebcamPrototype",L"DenoisePipeId",RRF_RT_REG_SZ|RRF_SUBKEY_WOW6464KEY,nullptr,id,&size)!=ERROR_SUCCESS)return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        std::wstring token=id;if(token.size()!=32||token.find_first_not_of(L"0123456789abcdef")!=std::wstring::npos)return E_INVALIDARG;
        std::wstring path=L"\\\\.\\pipe\\LenovoCameraStudio-Denoise-"+token;if(endpoint!=path){Close();endpoint=path;}
        if(pipe==INVALID_HANDLE_VALUE){pipe=CreateFileW(path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr);if(pipe==INVALID_HANDLE_VALUE)return HRESULT_FROM_WIN32(GetLastError());}
        UINT w=0,h=0;RETURN_IF_FAILED(MFGetAttributeSize(type,MF_MT_FRAME_SIZE,&w,&h));DWORD n=b==3?6:b==4?3:5;
        if(!w||!h||w>2560||h>1440||w%2||h%2||frames.size()<n)return MF_E_TRANSFORM_NEED_MORE_INPUT;
        DWORD modelWidth=640,cb=sizeof(modelWidth);RegGetValueW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\LenovoWebcamPrototype",L"DenoiseModelWidth",RRF_RT_REG_DWORD|RRF_SUBKEY_WOW6464KEY,nullptr,&modelWidth,&cb);
        if(modelWidth!=128&&modelWidth!=192&&modelWidth!=320&&modelWidth!=640&&modelWidth!=960&&modelWidth!=1280&&modelWidth!=1920)modelWidth=640;
        UINT32 matrix=MFVideoTransferMatrix_BT709,range=MFNominalRange_16_235;type->GetUINT32(MF_MT_YUV_MATRIX,&matrix);type->GetUINT32(MF_MT_VIDEO_NOMINAL_RANGE,&range);DWORD colourFlags=(matrix==MFVideoTransferMatrix_BT601?1:0)|(range==MFNominalRange_0_255?2:0);
        DWORD bytes=w*(h+h/2);activeBackend=b;activeLevel=q;ModelRequest request{0x3144434c,1,b,q,w,h,n,bytes,modelWidth,colourFlags};
        auto hr=Transfer(true,&request,sizeof(request));if(FAILED(hr)){Close();return hr;}
        std::vector<BYTE> packed(bytes);const size_t start=frames.size()-n;
        for(size_t i=start;i<frames.size();++i){LONGLONG time=0;RETURN_IF_FAILED(frames[i]->GetSampleTime(&time));ComPtr<IMFMediaBuffer> buffer;RETURN_IF_FAILED(frames[i]->ConvertToContiguousBuffer(&buffer));Locked2D locked;RETURN_IF_FAILED(locked.Lock(buffer.Get(),MF2DBuffer_LockFlags_Read,type));if(locked.stride<=0||!locked.Covers(w,h+h/2))return MF_E_BUFFERTOOSMALL;for(UINT y=0;y<h+h/2;++y)memcpy(packed.data()+size_t(y)*w,locked.scan+size_t(y)*locked.stride,w);locked.Unlock();hr=Transfer(true,&time,sizeof(time));if(SUCCEEDED(hr))hr=Transfer(true,packed.data(),bytes);if(FAILED(hr)){Close();return hr;}}
        ModelResponse response{};hr=Transfer(false,&response,sizeof(response));if(FAILED(hr)){Close();return hr;}
        if(response.magic!=request.magic||response.version!=1||response.bytes>bytes){Close();return E_UNEXPECTED;}
        if(static_cast<HRESULT>(response.result)!=S_OK){if(response.bytes){Close();return E_UNEXPECTED;}return static_cast<HRESULT>(response.result);}
        if(response.bytes!=bytes){Close();return E_UNEXPECTED;}
        ComPtr<IMFSample> reference;for(size_t i=start;i<frames.size();++i){LONGLONG t=0;frames[i]->GetSampleTime(&t);if(t==response.time)reference=frames[i];}if(!reference){Close();return E_UNEXPECTED;}
        hr=Transfer(false,packed.data(),bytes);if(FAILED(hr)){Close();return hr;}
        ComPtr<IMFMediaBuffer> output;RETURN_IF_FAILED(MFCreate2DMediaBuffer(w,h,MFVideoFormat_NV12.Data1,FALSE,&output));Locked2D dst;RETURN_IF_FAILED(dst.Lock(output.Get(),MF2DBuffer_LockFlags_Write));if(!dst.Covers(w,h+h/2))return MF_E_BUFFERTOOSMALL;ZeroMemory(dst.start,dst.length);for(UINT y=0;y<h+h/2;++y)memcpy(dst.scan+size_t(y)*dst.stride,packed.data()+size_t(y)*w,w);dst.Unlock();DWORD length=0;RETURN_IF_FAILED(output->GetMaxLength(&length));RETURN_IF_FAILED(output->SetCurrentLength(length));
        ComPtr<IMFSample> replacement;RETURN_IF_FAILED(MFCreateSample(&replacement));RETURN_IF_FAILED(reference->CopyAllItems(replacement.Get()));RETURN_IF_FAILED(replacement->SetSampleTime(response.time));LONGLONG duration=0;DWORD flags=0;if(SUCCEEDED(reference->GetSampleDuration(&duration)))replacement->SetSampleDuration(duration);if(SUCCEEDED(reference->GetSampleFlags(&flags)))replacement->SetSampleFlags(flags);RETURN_IF_FAILED(replacement->AddBuffer(output.Get()));(*sample)->Release();*sample=replacement.Detach();return S_OK;
    }catch(...){Close();return E_FAIL;}
}
