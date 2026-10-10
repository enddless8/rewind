param([string]$OutputDirectory = 'artifacts/packet-copy-test')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot
$dotnetPath = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Run scripts/bootstrap.ps1 first.' }
$testDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$harnessDirectory = Join-Path $testDirectory 'harness'
New-Item -ItemType Directory -Force $harnessDirectory | Out-Null
$projectPath = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'src/Replay/Replay.csproj'))
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$projectPath"/></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $harnessDirectory 'FidelityChecks.csproj') -Encoding utf8
@'
using System.IO;
using System.Text.Json;
using System.Windows;
using Replay;
static class Program
{
    static byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    static bool Same(byte[]? before, byte[]? after) => before == null ? after == null : after != null && before.SequenceEqual(after);
    [STAThread] static void Main(string[] args)
    {
        var root = args.Single(); Directory.CreateDirectory(root);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () => {
            var checks = new List<string>(); bool passed = false; string? error = null;
            var configPath = Path.Combine(Settings.DataDirectory, "settings.json");
            var queuePath = Path.Combine(Settings.DataDirectory, "compression-queue.json");
            var config = Read(configPath); var queue = Read(queuePath);
            try {
                void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS: " + name); }
                await RewindPacketCopyTests.Run(Path.Combine(root, "deterministic"), Check);
                await RewindPacketCopyTests.RunMedia(Path.Combine(root, "media"), Check);
                await RewindPacketCopyTests.RunProduction(Path.Combine(root, "production"), Check);
                if (!Same(config, Read(configPath)) || !Same(queue, Read(queuePath))) throw new Exception("User settings/queue changed");
                passed = true;
            } catch (Exception ex) { error = ex.ToString(); Console.WriteLine(error); }
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { Passed = passed, Checks = checks, Error = error }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(passed ? 0 : 1);
        }); app.Run();
    }
}
'@ | Set-Content -LiteralPath (Join-Path $harnessDirectory 'Program.cs') -Encoding utf8
& $dotnetPath build (Join-Path $harnessDirectory 'FidelityChecks.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Native packet-copy test build failed.' }
# Explicit Windows/GPU test: real desktop capture, NVDEC, NVENC and WASAPI.
# No MainWindow/user queue worker is constructed. Results/media stay local.
& $dotnetPath (Join-Path $harnessDirectory 'bin/Release/net10.0-windows/FidelityChecks.dll') $testDirectory
if ($LASTEXITCODE -ne 0) { throw "Native packet-copy tests failed. See $testDirectory/results.json" }

