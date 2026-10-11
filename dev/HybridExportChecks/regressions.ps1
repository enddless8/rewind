param([string]$OutputDirectory = 'artifacts/hybrid-phase3b/regressions-v1')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskRoot.StartsWith([IO.Path]::GetFullPath('artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifacts only.' }
New-Item -ItemType Directory -Force -Path $taskRoot | Out-Null
$privateDirectory = Join-Path $env:USERPROFILE 'AppData/Local/Replay'
function Read-PrivateHashes {
    $result = @{}
    if (Test-Path -LiteralPath $privateDirectory) {
        foreach ($file in Get-ChildItem -LiteralPath $privateDirectory -File -Recurse) { $result[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    }
    return $result
}
$before = Read-PrivateHashes
$summary = @()
try {
    foreach ($mode in @('regress','phase1-media','phase2-media','recording','edges','recovery','process','concurrency')) {
        $destination = Join-Path $taskRoot $mode
        $pointer = Join-Path $destination 'completed-attempt.json'
        if (Test-Path -LiteralPath $pointer) { $destination = (Get-Content -LiteralPath $pointer -Raw | ConvertFrom-Json).Folder }
        $suiteWatch = [Diagnostics.Stopwatch]::StartNew()
        if (-not (Test-Path -LiteralPath (Join-Path $destination 'results.json'))) {
            $suffix = ''
            if (Test-Path -LiteralPath $destination) {
                $attempt = 2
                while (Test-Path -LiteralPath (Join-Path $destination "attempt-$attempt")) { $attempt++ }
                $destination = Join-Path $destination "attempt-$attempt"; $suffix = "-attempt-$attempt"
            }
            & .tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll $mode $destination 2>&1 | Tee-Object -FilePath (Join-Path $taskRoot "$mode$suffix.log")
            if ($LASTEXITCODE -ne 0) { throw "Suite failed: $mode" }
            if ($suffix) { @{ Folder = $destination } | ConvertTo-Json | Set-Content -LiteralPath $pointer }
        }
        $result = Get-Content -LiteralPath (Join-Path $destination 'results.json') -Raw | ConvertFrom-Json
        $count = if ($result.PSObject.Properties['count']) { $result.count } else { @($result.checks).Count }
        $summary += [pscustomobject]@{ Suite = $mode; Passed = $result.passed; Checks = $count; ElapsedSeconds = $suiteWatch.Elapsed.TotalSeconds; Folder = $destination }
        $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRoot 'summary.json')
    }
} finally {
    $after = Read-PrivateHashes
    $unchanged = $before.Count -eq $after.Count
    foreach ($name in $before.Keys) { if ($after[$name] -ne $before[$name]) { $unchanged = $false } }
    @{ PrivateRuntimeUnchanged = $unchanged; Files = $before.Count } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRoot 'private-guard.json')
    if (-not $unchanged) { throw 'Private runtime inventory/hashes changed.' }
}
