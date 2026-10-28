using System.IO;
using fNbt;
using Songyu.Services;

var root = Path.Combine(Path.GetFullPath(args.FirstOrDefault() ?? ".tmp-probe"), Guid.NewGuid().ToString("N"));
var paths = new MinecraftPaths(root);
var service = new ServerListService(paths);
void Check(bool condition, string message)
{
    if (!condition) { throw new Exception(message); }
    Console.WriteLine($"PASS: {message}");
}

Check((await service.ReadAsync("test")).Count == 0, "Missing server list is empty");
GameServer[] entries = [new("中文服务器 🌲", "localhost:25565", false), new("Private", "example.invalid", true)];
await service.SaveAsync("test", entries);
Check((await service.ReadAsync("test")).SequenceEqual(entries), "Server names, addresses and flags round-trip");
var path = service.ServersDatPath("test");
var header = await File.ReadAllBytesAsync(path);
Check(header[0] == 0x1f && header[1] == 0x8b, "Server file uses GZip");
var file = new NbtFile(path);
file.RootTag.Add(new NbtString("extra", "preserved"));
((NbtCompound)file.RootTag.Get<NbtList>("servers")![0]).Add(new NbtString("icon", "test-icon"));
file.SaveToFile(path, NbtCompression.GZip);
await service.SaveAsync("test", [entries[0] with { Name = "重命名" }]);
var updated = new NbtFile(path);
Check(updated.RootTag.Get<NbtString>("extra")?.Value == "preserved", "Unknown root fields survive save");
Check(((NbtCompound)updated.RootTag.Get<NbtList>("servers")![0]).Get<NbtString>("icon")?.Value == "test-icon", "Server icons survive edits");
Check((await service.ReadAsync("test")).Count == 1, "Removed server stays removed");
var before = await File.ReadAllBytesAsync(path);
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try { await service.SaveAsync("test", [], cancelled.Token); throw new Exception("Cancellation was ignored"); }
catch (OperationCanceledException) { }
var after = await File.ReadAllBytesAsync(path);
Check(before.SequenceEqual(after), "Cancellation preserves existing file");
await File.WriteAllBytesAsync(path, [1, 2, 3]);
try { await service.SaveAsync("test", entries); throw new Exception("Corrupt file was overwritten"); }
catch (InvalidDataException) { }
Check((await File.ReadAllBytesAsync(path)).SequenceEqual(new byte[] { 1, 2, 3 }), "Malformed file is preserved");
Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "No temporary server files remain");
var javas = await new JavaService().ScanAsync();
foreach (var java in javas) { Console.WriteLine(java); }
Check(javas.All(j => j.MajorVersion > 0 && File.Exists(j.JavaExePath)), "Detected Java runtimes exist and report versions");
if (args.Contains("--require-java")) { Check(javas.Count > 0, "At least one Java runtime detected"); }
try { await new JavaService().ScanAsync(cancelled.Token); throw new Exception("Cancellation was ignored"); }
catch (OperationCanceledException) { Console.WriteLine("PASS: Java discovery honors cancellation"); }
Console.WriteLine("All service checks passed.");
