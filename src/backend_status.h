#pragma once
#include <windows.h>
void ReportBackendStatus(HRESULT nvidiaResult,ULONGLONG count,double gpuMs,DWORD level,DWORD backend,ULONGLONG filtered,ULONGLONG raw);
