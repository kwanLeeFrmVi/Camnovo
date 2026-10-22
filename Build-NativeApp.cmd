@echo off
setlocal
if "%~1"=="" exit /b 2
call "%~1" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b 1
pushd "%~dp0build"
cl /nologo /LD /MT /EHsc /std:c++17 /O2 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 ..\src\CameraNative.cpp /Fe:CameraNative.dll /link strmiids.lib ole32.lib mf.lib mfplat.lib mfreadwrite.lib mfuuid.lib
if errorlevel 1 exit /b 1
cl /nologo /MT /EHsc /std:c++17 /O2 ..\src\CameraSourceSelectionTests.cpp /Fe:CameraSourceSelectionTests.exe
if errorlevel 1 exit /b 1
CameraSourceSelectionTests.exe
if errorlevel 1 exit /b 1
cl /nologo /MT /EHsc /std:c++17 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 ..\src\CaptureProbe.cpp /Fe:CaptureProbe.exe /link mf.lib mfplat.lib mfreadwrite.lib mfuuid.lib ole32.lib
set "buildResult=%errorlevel%"
popd
exit /b %buildResult%
