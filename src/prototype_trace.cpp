#include "stdafx.h"
#include <TraceLoggingProvider.h>
// Metadata and counters only. No image buffers, faces or audio are logged.
TRACELOGGING_DEFINE_PROVIDER(g_PrototypeProvider, "LenovoWebcamPrototype",
    (0xb4687c70, 0x598d, 0x47fd, 0x92, 0x37, 0x4f, 0xeb, 0x63, 0xe4, 0x9a, 0x51));
static INIT_ONCE g_traceOnce = INIT_ONCE_STATIC_INIT;
static bool g_registered = false;
static BOOL CALLBACK RegisterTrace(PINIT_ONCE, PVOID, PVOID*) {
    g_registered = TraceLoggingRegister(g_PrototypeProvider) == ERROR_SUCCESS;
    return TRUE;
}
void PrototypeShutdownTrace() {
    if (g_registered) TraceLoggingUnregister(g_PrototypeProvider);
}
static void EnsureTrace() {
    InitOnceExecuteOnce(&g_traceOnce, RegisterTrace, nullptr, nullptr);
}
void PrototypeOperation(const char* operation, HRESULT result, DWORD stream) {
    EnsureTrace();
    TraceLoggingWrite(g_PrototypeProvider, "Operation", TraceLoggingString(operation,"Operation"),
        TraceLoggingHResult(result,"Result"),TraceLoggingUInt32(stream,"Stream"));
}
void PrototypeInitialize(HRESULT result, DWORD inputs, DWORD outputs) {
    EnsureTrace();
    TraceLoggingWrite(g_PrototypeProvider, "Initialize",
        TraceLoggingHResult(result, "Result"), TraceLoggingUInt32(inputs, "Inputs"),
        TraceLoggingUInt32(outputs, "Outputs"), TraceLoggingUInt32(GetCurrentProcessId(), "PID"));
}
void PrototypeOutput(DWORD stream, LONG64 count, HRESULT orientationResult, HRESULT nvidiaResult) {
    EnsureTrace();
    TraceLoggingWrite(g_PrototypeProvider, "Output",
        TraceLoggingUInt32(stream, "Stream"), TraceLoggingInt64(count, "Count"),
        TraceLoggingUInt32(GetCurrentProcessId(), "PID"), TraceLoggingHResult(orientationResult, "OrientationResult"), TraceLoggingHResult(nvidiaResult,"NvidiaResult"));
}
