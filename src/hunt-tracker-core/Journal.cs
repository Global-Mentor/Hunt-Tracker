using System.Text;
using System.Text.Json;

namespace HuntTracker;

public sealed class Journal : IDisposable
{
    private readonly StreamWriter writer;
    public string Path { get; }
    public Journal(string root, string session)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(root, "logs"));
        Path = System.IO.Path.Combine(root, "logs", $"events-{DateTime.Now:yyyyMMdd-HHmmss}-{session[..8]}.jsonl");
        writer = new(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }
    public void Write(string type, string message, object? data = null) => writer.WriteLine(JsonSerializer.Serialize(new { at = DateTimeOffset.Now.ToString("o"), type, message = AtomicFile.Protect(message), data }, Statistics.Json));
    public void Dispose() => writer.Dispose();
    public static string Export(string source)
    {
        var destination = System.IO.Path.ChangeExtension(source, ".txt");
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var snapshot = temp + ".snapshot";
        try
        {
            var terminated = true;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = File.Create(snapshot))
            {
                var remaining = input.Length;
                var buffer = new byte[65536];
                while (remaining > 0)
                {
                    var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0)
                        throw new IOException("Журнал изменился во время экспорта.");
                    copy.Write(buffer, 0, read);
                    remaining -= read;
                    terminated = buffer[read - 1] == 10;
                }
            }
            using (var reader = new StreamReader(snapshot))
            using (var output = new StreamWriter(temp, false, new UTF8Encoding(true)))
                while (reader.ReadLine() is { } line)
                {
                    if (reader.EndOfStream && !terminated)
                        break;
                    using var entry = JsonDocument.Parse(line);
                    output.WriteLine($"[{entry.RootElement.GetProperty("at").GetString()}] {entry.RootElement.GetProperty("message").GetString()}");
                }
            if (File.Exists(destination))
                File.Replace(temp, destination, null);
            else
                File.Move(temp, destination);
            return destination;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); if (File.Exists(snapshot)) File.Delete(snapshot); }
    }
}
