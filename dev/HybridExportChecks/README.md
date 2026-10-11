# Production Hybrid AV1 SaveClip checks (Phase 3B)

This standalone project calls `RewindHybridExport` and the actual `Recorder.Save`.
It is not included in the application build. Phase 3A remains an independent
research exporter/reference. No private settings or compression queue is loaded.
`ClipFiles.DiagnosticDirectoryForTests` is set before any work; every native
invocation guards the actual user's complete Replay runtime file inventory/hash.

Build from the repository root with the bundled SDK:

```powershell
.tools/dotnet/dotnet.exe build dev/HybridExportChecks/HybridExportChecks.csproj -c Release -p:NuGetAudit=false
python dev/HybridExportChecks/checks.py checks-final
python dev/HybridExportChecks/benchmark.py live-matrix-v1 15,30,60,120 A,B,C,D 3
.tools/dotnet/dotnet.exe dev/HybridExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll native-failures artifacts/hybrid-phase3b/native-failures
.tools/dotnet/dotnet.exe dev/HybridExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll live artifacts/hybrid-phase3b/live-history B 30 history
.tools/dotnet/dotnet.exe dev/HybridExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll live artifacts/hybrid-phase3b/live-controls C 30 controls
python dev/HybridExportChecks/paired.py live-matrix-v1
python dev/HybridExportChecks/verify_live.py live-matrix-v1
python dev/HybridExportChecks/verify_smoke.py live-history
python dev/HybridExportChecks/verify_smoke.py live-controls
./dev/HybridExportChecks/regressions.ps1
```

Native capture requires an unlocked desktop and access to FFmpeg/NVIDIA.
Re-use the completed Phase 3A fixtures at `artifacts/hybrid-phase3a/fixtures`;
the Phase 3A README describes their generation. `checks.py` writes selected PCM
and new results only inside Phase 3B artifacts. It independently verifies output
with laboratory libdav1d in addition to production full validation. NumPy is used
only by development verification, never by production.

Benchmark A schedules a real acceptance before initial history expires, B uses
the unchanged rolling-window NVDEC baseline, C opts in to verified Hybrid, and D
injects one boundary failure then runs the existing full fallback. Actual QPC is
never edited. Check each output's recorded decoder/eligibility; a rejected A or C
is a fallback result rather than a successful fast-strategy sample. Every run
uses a new isolated Recorder and the real publication/overlay service. No
MainWindow is constructed (it would load private settings/queued user jobs).
The sampler records device-wide GPU values at 500 ms and process/disk at 100 ms.
CPU is accumulated FFmpeg child CPU, separate from wall time. Boundary decode
and encode share one direct CUDA process; their separate wall times are unknown.

`export artifacts/... mode` consumes `job.json` containing a frozen snapshot and
the original full-export argument list. Modes: `hybrid`, `default`, `nvdec`,
`head-fail`, `decoder-fail`, `head-corrupt`, `mux-fail`, `mux-corrupt`, `software`,
`validation-software`, `interrupted`, `no-space`, `missing-scratch`, `cancel`, `locked`.
`plan artifacts/...` only indexes/inspects. These instance-scoped seams are never
persisted settings. Production defaults to Phase 2 copy then Phase 1 fallback;
Hybrid is disabled pending a measured advantageous selection rule.

The first implementation deliberately verifies output copied pixels against the
unchanged decoded-trim source and AAC against the same selected WAV/filter/AAC
reference. This supplements exact compressed hashes/PTS, complete all-stream
decode and bounded boundary PSNR. These guarantees have real latency costs;
benchmark results, rather than prototype timings, must govern activation.
