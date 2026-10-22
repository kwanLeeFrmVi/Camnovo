param([string]$DotNetPath='')
$ErrorActionPreference='Stop'
if (!$DotNetPath) {
    $portable=Join-Path $PSScriptRoot '..\..\work\dotnet-sdk\dotnet.exe'
    if(Test-Path -LiteralPath $portable){$DotNetPath=(Resolve-Path -LiteralPath $portable).Path}
    else {$DotNetPath=(Get-Command dotnet -ErrorAction Stop).Source}
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$destination=Join-Path $PSScriptRoot 'build\studio'
& $DotNetPath build "$PSScriptRoot\studio\LenovoCameraStudio.csproj" -c Release -o $destination --nologo
if($LASTEXITCODE -ne 0){throw 'Fluent camera app build failed. A .NET 10 SDK is required.'}
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Management.dll /reference:System.Web.Extensions.dll /reference:System.ServiceProcess.dll "/out:$destination\CameraSetup.exe" "$PSScriptRoot\src\SetupHost.cs" "$PSScriptRoot\src\EngineSetup.cs"
if($LASTEXITCODE -ne 0){throw 'Native setup helper build failed.'}
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /reference:System.Management.dll /reference:System.Web.Extensions.dll /reference:System.ServiceProcess.dll "/out:$PSScriptRoot\build\AttachmentPlanTests.exe" "$PSScriptRoot\src\AttachmentPlanTests.cs" "$PSScriptRoot\src\EngineSetup.cs"
if($LASTEXITCODE -ne 0){throw 'Camera attachment-plan tests did not compile.'}
& "$PSScriptRoot\build\AttachmentPlanTests.exe"
if($LASTEXITCODE -ne 0){throw 'Camera attachment-plan regression failed.'}
Copy-Item "$PSScriptRoot\build\LenovoPassThrough.dll","$PSScriptRoot\build\CaptureProbe.exe","$PSScriptRoot\LICENSE-MS-PL.txt","$PSScriptRoot\LICENSE-WIL.txt" $destination -Force
Copy-Item "$PSScriptRoot\LICENSE-DENOISER-MIT.txt" $destination -Force
New-Item -ItemType Directory -Path "$destination\DenoiserSource" -Force | Out-Null
Copy-Item "$PSScriptRoot\src\open_denoise.*","$PSScriptRoot\src\realtime_denoise.*","$PSScriptRoot\src\external_denoise.*" "$destination\DenoiserSource" -Force
Copy-Item "$PSScriptRoot\src\shadow_cleanup.h" "$destination\DenoiserSource" -Force
Copy-Item "$PSScriptRoot\models\*.md" "$destination\Models" -Force
Copy-Item "$PSScriptRoot\model-performance.md","$PSScriptRoot\model-provenance.json" $destination -Force
New-Item -ItemType Directory -Path "$destination\docs" -Force | Out-Null
Copy-Item "$PSScriptRoot\docs\realtek-webcam-driver.md" "$destination\docs" -Force
Write-Host "Fluent Windows app built: $destination\LenovoCameraControls.exe"
