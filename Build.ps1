$ErrorActionPreference = 'Stop'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (!(Test-Path $vswhere)) { throw 'Install Visual Studio Build Tools with C++ x64/x86 tools and Windows 11 SDK, then rerun Build.ps1.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'No Visual Studio installation with C++ tools was found.' }
$devCmd = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
& "$PSScriptRoot\Build.cmd" $devCmd
if ($LASTEXITCODE -ne 0) { throw "Build or native smoke test failed (exit $LASTEXITCODE). See compiler output." }
& "$PSScriptRoot\Build-NativeApp.cmd" $devCmd
if ($LASTEXITCODE -ne 0) { throw 'Native preview/control library build failed.' }
& "$PSScriptRoot\Build-Controls.cmd"
if ($LASTEXITCODE -ne 0) { throw 'Native control app build failed.' }
Copy-Item "$PSScriptRoot\build\CameraNative.dll","$PSScriptRoot\build\LenovoPassThrough.dll","$PSScriptRoot\build\CaptureProbe.exe" $PSScriptRoot -Force
Copy-Item "$PSScriptRoot\build\studio\*" $PSScriptRoot -Recurse -Force
Copy-Item "$PSScriptRoot\LenovoCameraControls.exe" "$PSScriptRoot\Camera-Controls.exe" -Force
Get-FileHash "$PSScriptRoot\build\LenovoPassThrough.dll" | Format-List
Write-Host 'Build, COM interface, pixel and Media Foundation orientation tests passed. This build command does not perform live camera integration tests.'
