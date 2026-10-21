#include "stdafx.h"
#include "backend_status.h"
void ReportBackendStatus(HRESULT result,ULONGLONG count,double gpuMs,DWORD level,DWORD backend,ULONGLONG filtered,ULONGLONG raw)
{
    // Diagnostic data only. The camera service can set values in this app's
    // dedicated settings key; processing DLLs and COM registration stay protected.
    HKEY key=nullptr;
    if(RegOpenKeyExW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\LenovoWebcamPrototype",0,KEY_SET_VALUE|KEY_WOW64_64KEY,&key)!=ERROR_SUCCESS)return;
    DWORD version=3,hr=static_cast<DWORD>(result),microseconds=static_cast<DWORD>(max(0.0,min(60000000.0,gpuMs*1000.0)));
    ULONGLONG now=GetTickCount64();
    RegSetValueExW(key,L"BackendVersion",0,REG_DWORD,reinterpret_cast<BYTE*>(&version),sizeof(version));
    RegSetValueExW(key,L"BackendNvidiaResult",0,REG_DWORD,reinterpret_cast<BYTE*>(&hr),sizeof(hr));
    RegSetValueExW(key,L"BackendDenoiseLevel",0,REG_DWORD,reinterpret_cast<BYTE*>(&level),sizeof(level));
    RegSetValueExW(key,L"BackendDenoiseKind",0,REG_DWORD,reinterpret_cast<BYTE*>(&backend),sizeof(backend));
    RegSetValueExW(key,L"BackendGpuMicroseconds",0,REG_DWORD,reinterpret_cast<BYTE*>(&microseconds),sizeof(microseconds));
    RegSetValueExW(key,L"BackendFrameCount",0,REG_QWORD,reinterpret_cast<BYTE*>(&count),sizeof(count));
    RegSetValueExW(key,L"BackendFilteredFrames",0,REG_QWORD,reinterpret_cast<BYTE*>(&filtered),sizeof(filtered));
    RegSetValueExW(key,L"BackendRawFrames",0,REG_QWORD,reinterpret_cast<BYTE*>(&raw),sizeof(raw));
    RegSetValueExW(key,L"BackendTick",0,REG_QWORD,reinterpret_cast<BYTE*>(&now),sizeof(now));
    RegCloseKey(key);
}
