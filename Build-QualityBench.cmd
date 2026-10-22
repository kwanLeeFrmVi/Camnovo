@echo off
setlocal
if "%~1"=="" exit /b 2
call "%~1" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b 1
pushd "%~dp0build"
cl /nologo /LD /MT /EHsc /std:c++17 /O2 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /I..\vendor\wil\include /I..\src ..\src\QualityBridge.cpp ..\src\nvidia_denoise.cpp ..\src\open_denoise.cpp /Fe:QualityBridge.dll /link mfplat.lib mfuuid.lib ole32.lib advapi32.lib
set "result=%errorlevel%"
popd
exit /b %result%
