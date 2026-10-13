param([string]$MediaDirectory = '.tools/ffmpeg-full')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifactRoot = Join-Path $repoRoot 'artifacts'
$outputPath = Join-Path $artifactRoot 'rewind-manual-test'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$stagingPath = Join-Path $artifactRoot "rewind-manual-staging-$stamp"
$previousPath = Join-Path $artifactRoot "rewind-manual-test-previous-$stamp"
function Assert-ArtifactPath([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Target outside workspace artifacts: $resolved" }
    return $resolved
}
function Assert-NotRunning {
    $prefix = $outputPath.Replace('\', '/').TrimEnd('/') + '/'
    $blockers = @(Get-CimInstance Win32_Process | Where-Object {
        ($_.ExecutablePath -and $_.ExecutablePath.Replace('\', '/').StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) -or
        ($_.CommandLine -and $_.Name -in @('dotnet.exe','Rewind.exe') -and $_.CommandLine.Replace('\', '/').IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0)
    })
    if ($blockers.Count) { throw "Manual build is running (PID $($blockers.ProcessId -join ', ')). Close it before replacing this directory." }
}
$outputPath = Assert-ArtifactPath $outputPath
$stagingPath = Assert-ArtifactPath $stagingPath
$previousPath = Assert-ArtifactPath $previousPath
Assert-NotRunning
$mediaSource = [IO.Path]::GetFullPath((Join-Path $repoRoot $MediaDirectory))
foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $mediaSource $name))) {
        throw "Missing $name in $mediaSource. Supply the cached libdav1d-enabled FFmpeg build and matching-version ffprobe, or pass -MediaDirectory."
    }
}
$decoders = & (Join-Path $mediaSource 'ffmpeg.exe') -hide_banner -decoders 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $decoders -notmatch '\blibdav1d\b' -or $decoders -notmatch '\bav1_cuvid\b') {
    throw 'Manual-test FFmpeg must include libdav1d and av1_cuvid; essentials/libaom adds measured per-segment probing latency.'
}
$encoders = & (Join-Path $mediaSource 'ffmpeg.exe') -hide_banner -encoders 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $encoders -notmatch '\bav1_nvenc\b') { throw 'Manual-test FFmpeg is missing AV1 NVENC.' }
New-Item -ItemType Directory -Path $stagingPath | Out-Null
$dotnetPath = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& $dotnetPath publish (Join-Path $repoRoot 'src/Replay/Replay.csproj') -c Release -r win-x64 --self-contained true --nologo `
    -p:NuGetAudit=false "-p:BaseOutputPath=$artifactRoot/rewind-manual-build/" -o $stagingPath
if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed; staging retained at $stagingPath" }
$tools = Join-Path $stagingPath 'tools'
New-Item -ItemType Directory -Path $tools | Out-Null
foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
    $source = Get-Item -LiteralPath (Join-Path $mediaSource $name)
    Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $tools $name)
    Get-ChildItem -LiteralPath $source.Directory.Parent.FullName -File -Filter '*LICENSE*' | Copy-Item -Destination $tools -Force
}
@('ffmpeg.exe','ffprobe.exe') | ForEach-Object {
    $binary = Join-Path $tools $_
    [PSCustomObject]@{ Name = $_; Source = (Join-Path $mediaSource $_); SHA256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash }
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $tools 'build-provenance.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $stagingPath
& (Join-Path $tools 'ffmpeg.exe') -L *> (Join-Path $tools 'LICENSE-FFmpeg-build.txt')
if ($LASTEXITCODE -ne 0) { throw 'Bundled FFmpeg license query failed.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $tools 'GPL-3.0.txt')
$runtime = Get-Content -LiteralPath (Join-Path $stagingPath 'Rewind.runtimeconfig.json') -Raw | ConvertFrom-Json
$coreVersion = ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$desktopVersion = ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
foreach ($notice in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot ".tools/nuget/microsoft.netcore.app.runtime.win-x64/$coreVersion/$notice") -Destination (Join-Path $stagingPath "DOTNET-$notice")
}
Copy-Item -LiteralPath (Join-Path $repoRoot ".tools/nuget/microsoft.windowsdesktop.app.runtime.win-x64/$desktopVersion/LICENSE") -Destination (Join-Path $stagingPath 'DOTNET-WINDOWSDESKTOP-LICENSE.txt')
foreach ($required in @('Rewind.exe','Rewind.dll','Rewind.runtimeconfig.json','Rewind.deps.json',
    'hostfxr.dll','hostpolicy.dll','coreclr.dll','System.Private.CoreLib.dll','PresentationFramework.dll',
    'PresentationCore.dll','WindowsBase.dll','LICENSE','NOTICE','THIRD-PARTY.md',
    'licenses/fonts/LICENSE-Rewindtica.txt','licenses/fonts/NOTICE-Rewindtica.txt','licenses/fonts/LICENSE-Inter.txt',
    'tools/ffmpeg.exe','tools/ffprobe.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stagingPath $required))) { throw "Publish is missing $required; staging retained" }
}
$runtime = Get-Content -LiteralPath (Join-Path $stagingPath 'Rewind.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.frameworks) { throw 'Publish requires an installed runtime.' }
$smokePath = Join-Path $artifactRoot "rewind-manual-build/smoke-$stamp.json"
$smoke = Start-Process -FilePath (Join-Path $stagingPath 'Rewind.exe') -ArgumentList @('--manual-build-check', ('"' + $smokePath + '"')) -WindowStyle Hidden -Wait -PassThru
if ($smoke.ExitCode -ne 0) { throw "Manual build smoke failed; inspect $smokePath. Staging retained." }
Assert-NotRunning
# Resolved absolute workspace paths only. Preserve the previous complete build.
if (Test-Path -LiteralPath $outputPath) { Move-Item -LiteralPath $outputPath -Destination $previousPath }
Move-Item -LiteralPath $stagingPath -Destination $outputPath
Write-Output "Manual-test executable: $(Join-Path $outputPath 'Rewind.exe')"
if (Test-Path -LiteralPath $previousPath) { Write-Output "Previous build preserved: $previousPath" }
