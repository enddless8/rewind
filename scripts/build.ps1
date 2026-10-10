param([switch]$Test, [string]$OutputDirectory = 'dist/Rewind')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot
$dotnetPath = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Run scripts/bootstrap.ps1 first.' }
& $dotnetPath publish src/Replay/Replay.csproj -c Release -r win-x64 --self-contained true -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$mediaDirectory = Join-Path $repoRoot (Join-Path $OutputDirectory 'tools')
New-Item -ItemType Directory -Force $mediaDirectory | Out-Null
$ffmpegDirectory = (Get-ChildItem .tools/ffmpeg -Recurse -Filter ffmpeg.exe | Select-Object -First 1).Directory.FullName
if (-not $ffmpegDirectory) { throw 'FFmpeg missing. Run scripts/bootstrap.ps1.' }
Copy-Item -LiteralPath (Join-Path $ffmpegDirectory 'ffmpeg.exe'), (Join-Path $ffmpegDirectory 'ffprobe.exe') -Destination $mediaDirectory -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY.md') -Destination $OutputDirectory -Force
$ffmpegRoot = Split-Path $ffmpegDirectory -Parent
Get-ChildItem -LiteralPath $ffmpegRoot -Filter '*LICENSE*' | Copy-Item -Destination $mediaDirectory -Force
if ($Test) {
    $testDirectory = Join-Path $repoRoot 'artifacts/self-test'
    $process = Start-Process -FilePath (Join-Path $repoRoot (Join-Path $OutputDirectory 'Rewind.exe')) -ArgumentList @('--self-test', ('"' + $testDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath (Join-Path $testDirectory 'results.json')
    if ($process.ExitCode -ne 0) { throw 'Self-test failed.' }
}
