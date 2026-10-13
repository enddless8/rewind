# Rewind manual GUI testing builds

For GUI changes requiring manual testing, publish the latest implementation as a
Windows x64 self-contained Release build with `scripts/publish-manual-test.ps1`.
The consistent ignored output is `artifacts/rewind-manual-test/Rewind.exe`.
Include the .NET Desktop Runtime, resources/notices and bundled
`tools/ffmpeg.exe` / `tools/ffprobe.exe`; do not require installed .NET.
Use the cached `.tools/ffmpeg-full` media tools (or an explicit `-MediaDirectory`)
with libdav1d, AV1 NVDEC and NVENC. The essentials build's libaom probing adds
measured segmented-AV1 export latency. Retain the tested encoder/trim options.

Keep development/harness outputs separate. Stage before replacing an older manual
build, check for processes using it, and never overwrite files of a running Rewind
instance. Preserve previous outputs. Report the exact executable path and checks.
Do not commit generated binaries or alter Git history. Local `dotnet publish`
does not authorize GitHub publication.
