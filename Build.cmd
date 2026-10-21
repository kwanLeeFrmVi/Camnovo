@echo off
setlocal
if "%~1"=="" exit /b 2
call "%~1" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b 1
pushd "%~dp0"
if not exist build mkdir build
pushd build
cl /nologo /LD /MT /EHsc /std:c++17 /W3 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A00000C /DMFT_UNIQUE_METHOD_NAMES /DMF_DEVICEMFT_ALLOW_MFT0_LOAD /I..\vendor\wil\include /I..\src ..\src\basepin.cpp ..\src\custompin.cpp ..\src\dllmain.cpp ..\src\mftpeventgenerator.cpp ..\src\multipinmft.cpp ..\src\multipinmfthelpers.cpp ..\src\multipinmftutils.cpp ..\src\prototype_trace.cpp ..\src\backend_status.cpp ..\src\orientation.cpp ..\src\upscale1440p.cpp ..\src\nvidia_denoise.cpp ..\src\open_denoise.cpp ..\src\realtime_denoise.cpp ..\src\external_denoise.cpp /Fe:LenovoPassThrough.dll /link /DEF:..\src\Source.def onecore.lib d2d1.lib mfcore.lib mfplat.lib mfuuid.lib uuid.lib ole32.lib advapi32.lib
if errorlevel 1 exit /b 1
cl /nologo /MT /EHsc /std:c++17 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 ..\src\SmokeTest.cpp /Fe:SmokeTest.exe /link mfplat.lib mfuuid.lib ole32.lib
if errorlevel 1 exit /b 1
cl /nologo /MT /EHsc /std:c++17 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 ..\src\CaptureProbe.cpp /Fe:CaptureProbe.exe /link mf.lib mfplat.lib mfreadwrite.lib mfuuid.lib ole32.lib
if errorlevel 1 exit /b 1
cl /nologo /MT /EHsc /std:c++17 ..\src\FlipTests.cpp /Fe:FlipTests.exe
if errorlevel 1 exit /b 1
FlipTests.exe
if errorlevel 1 exit /b 1
cl /nologo /MT /EHsc /std:c++17 /DUNICODE /D_UNICODE /I..\vendor\wil\include /I..\src ..\src\OrientationTests.cpp ..\src\orientation.cpp /Fe:OrientationTests.exe /link mfplat.lib mfuuid.lib ole32.lib advapi32.lib
if errorlevel 1 exit /b 1
OrientationTests.exe
if errorlevel 1 exit /b 1
SmokeTest.exe "%CD%\LenovoPassThrough.dll"
if errorlevel 1 exit /b 1
cl /nologo /MT /EHsc /std:c++17 /W3 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A00000C /DMFT_UNIQUE_METHOD_NAMES /DMF_DEVICEMFT_ALLOW_MFT0_LOAD /I..\vendor\wil\include /I..\src ..\src\QueueTests.cpp ..\src\basepin.cpp ..\src\custompin.cpp ..\src\dllmain.cpp ..\src\mftpeventgenerator.cpp ..\src\multipinmft.cpp ..\src\multipinmfthelpers.cpp ..\src\multipinmftutils.cpp ..\src\prototype_trace.cpp ..\src\backend_status.cpp ..\src\orientation.cpp ..\src\upscale1440p.cpp ..\src\nvidia_denoise.cpp ..\src\open_denoise.cpp ..\src\realtime_denoise.cpp ..\src\external_denoise.cpp /Fe:QueueTests.exe /link onecore.lib d2d1.lib mfcore.lib mfplat.lib mfuuid.lib uuid.lib ole32.lib advapi32.lib
if errorlevel 1 exit /b 1
QueueTests.exe
if errorlevel 1 exit /b 1
cl /nologo /MT /EHsc /std:c++17 /O2 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /I..\vendor\wil\include /I..\src ..\src\DenoiseTests.cpp ..\src\open_denoise.cpp ..\src\realtime_denoise.cpp ..\src\external_denoise.cpp ..\src\nvidia_denoise.cpp /Fe:DenoiseTests.exe /link onecore.lib mfplat.lib mfuuid.lib ole32.lib advapi32.lib
if errorlevel 1 exit /b 1
DenoiseTests.exe
if errorlevel 1 exit /b 1
cl /nologo /MT /EHsc /std:c++17 /O2 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /I..\vendor\wil\include /I..\src ..\src\DeliveryTests.cpp ..\src\open_denoise.cpp ..\src\realtime_denoise.cpp ..\src\external_denoise.cpp ..\src\nvidia_denoise.cpp /Fe:DeliveryTests.exe /link onecore.lib mfplat.lib mfuuid.lib ole32.lib advapi32.lib
if errorlevel 1 exit /b 1
DeliveryTests.exe
if errorlevel 1 exit /b 1
popd
popd
exit /b 0





