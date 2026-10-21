#include <windows.h>
#include <ks.h>
#include <ksproxy.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wrl/client.h>
#include <cstdio>
using Microsoft::WRL::ComPtr;
static void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    HMODULE module = nullptr;
    bool mf = false, com = false;
    int result = 1;
    try {
        Check(CoInitializeEx(nullptr, COINIT_MULTITHREADED)); com = true;
        Check(MFStartup(MF_VERSION)); mf = true;
        module = LoadLibraryExW(argv[1], nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!module) throw HRESULT_FROM_WIN32(GetLastError());
        auto get = reinterpret_cast<HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**)>(GetProcAddress(module, "DllGetClassObject"));
        if (!get) throw E_NOINTERFACE;
        CLSID clsid; Check(CLSIDFromString(L"{73DF2569-1A6D-4976-BA38-F089C0162864}", &clsid));
        {
            ComPtr<IClassFactory> factory;
            Check(get(clsid, IID_PPV_ARGS(&factory)));
            ComPtr<IUnknown> identity;
            Check(factory.As(&identity));
            GUID unknown = {0x6bd98132,0x4758,0x4af0,{0x88,0x34,0x22,0x25,0x7c,0xfa,0x39,0x05}};
            void* unsupported = reinterpret_cast<void*>(1);
            if (factory->QueryInterface(unknown, &unsupported) != E_NOINTERFACE || unsupported) throw E_UNEXPECTED;
            ComPtr<IMFDeviceTransform> transform;
            Check(factory->CreateInstance(nullptr, IID_PPV_ARGS(&transform)));
            ComPtr<IMFShutdown> shutdown; Check(transform.As(&shutdown));
            ComPtr<IMFRealTimeClientEx> realtime; Check(transform.As(&realtime));
            ComPtr<IMFMediaEventGenerator> events; Check(transform.As(&events));
            ComPtr<IKsControl> controls; Check(transform.As(&controls));
            ComPtr<IMFTransform> legacy; Check(transform.As(&legacy));
        }
        auto canUnload = reinterpret_cast<HRESULT(STDAPICALLTYPE*)()>(GetProcAddress(module, "DllCanUnloadNow"));
        if (!canUnload || canUnload() != S_OK) throw E_UNEXPECTED;
        std::puts("PASS: COM class factory, required interfaces, unsupported interface and object lifetime.");
        result = 0;
    } catch (HRESULT hr) { std::printf("FAIL: HRESULT 0x%08lX\n", static_cast<unsigned long>(hr)); }
    if (module) FreeLibrary(module);
    if (mf) MFShutdown();
    if (com) CoUninitialize();
    return result;
}
