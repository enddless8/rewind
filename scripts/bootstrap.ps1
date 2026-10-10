$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot
New-Item -ItemType Directory -Force .tools | Out-Null
if (-not (Test-Path .tools/dotnet/dotnet.exe)) {
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile .tools/dotnet-install.ps1
    & .tools/dotnet-install.ps1 -Version 10.0.401 -InstallDir (Join-Path $repoRoot '.tools/dotnet') -NoPath
}
if (-not (Test-Path .tools/ffmpeg)) {
    Invoke-WebRequest https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip -OutFile .tools/ffmpeg.zip
    Expand-Archive .tools/ffmpeg.zip .tools/ffmpeg
}
& .tools/dotnet/dotnet.exe --version
