using System.Text.Json;
using System.IO;
using Replay;

// Development-only executable. Nothing in Replay references this project.
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args[0] == "batch")
            {
                Console.WriteLine(JsonSerializer.Serialize(args.Skip(1).ToDictionary(Path.GetFullPath,
                    path => Av1SegmentIndex.Read(Path.GetFullPath(path), cached: false)), Json));
            }
            else if (args[0] == "index")
            {
                var index = Av1SegmentIndex.Read(Path.GetFullPath(args[1]), cached: false);
                Console.WriteLine(JsonSerializer.Serialize(index, Json));
            }
            else if (args[0] == "guards")
            {
                var checks = new List<object>();
                void Check(bool passed, string name) { checks.Add(new { passed, name }); if (!passed) throw new Exception(name); }
                var privateData = Convert.FromHexString(args[1]);
                var sequence = privateData[6..];
                byte[] Frame(byte flags) => [0x0a, (byte)sequence.Length, .. sequence, 0x32, 1, flags];
                Check(Av1SegmentIndex.InspectPacket(Frame(0x10), sequence, out var key) && key, "Shown KEY resets references");
                Check(!Av1SegmentIndex.InspectPacket(Frame(0x00), sequence, out _), "Hidden KEY rejected");
                Check(!Av1SegmentIndex.InspectPacket(Frame(0x80), sequence, out _), "show_existing_frame rejected");
                Check(!Av1SegmentIndex.InspectPacket(Frame(0x50), sequence, out _), "INTRA_ONLY rejected");
                Check(!Av1SegmentIndex.InspectPacket(Frame(0x70), sequence, out _), "SWITCH_FRAME rejected");
                Check(!Av1SegmentIndex.InspectPacket([.. Frame(0x10), 0x32, 1, 0x30], sequence, out _), "Multiple frame OBU rejected");
                Check(!Av1SegmentIndex.InspectPacket([0x36, 1, 1, 0x10], sequence, out _), "Nonzero temporal/spatial layer rejected");
                Check(!Av1SegmentIndex.InspectPacket([0x1a, 1, 0x10], sequence, out _), "Separate frame-header/tile-group syntax rejected");
                Check(!Av1SegmentIndex.InspectPacket([0x32, 1, 0x10], sequence, out _), "Key without in-band sequence rejected");
                Check(!Av1SegmentIndex.InspectPacket([0x32, 127, 0x30], sequence, out _), "Truncated temporal unit rejected");
                Console.WriteLine(JsonSerializer.Serialize(checks, Json));
            }
            else if (args[0] == "regress")
            {
                var checks = new List<object>();
                void Check(bool passed, string name) { checks.Add(new { passed, name }); if (!passed) throw new Exception(name); }
                RegressionTests.Run(Path.GetFullPath(args[1]), Check).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]), "results.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks }, Json));
                Console.WriteLine($"Регрессионные проверки: {checks.Count}");
            }
            else if (args[0] == "phase2")
                ExistingExportBenchmark.Run(args[1],args[2]).GetAwaiter().GetResult();
            else if (args[0] == "live" || args[0] == "concurrent")
            {
                var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                app.Dispatcher.InvokeAsync(async () => {
                    try { await LiveSnapshotCapture.Run(Path.GetFullPath(args[1]),args[0]=="concurrent" ? args[2] : null); }
                    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                    finally { app.Shutdown(); }
                });
                app.Run();
            }
            else throw new ArgumentException("Неизвестная команда прототипа.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
