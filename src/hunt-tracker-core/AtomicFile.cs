using System.Text;

namespace HuntTracker;

public static class AtomicFile
{
    private static readonly (string Path, string Token)[] PrivateFolders =
    [
        (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
        (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%")
    ];
    public static string Read(string path, long maximumBytes = 64 * 1024 * 1024)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length > maximumBytes || length > int.MaxValue)
            throw new InvalidDataException("Файл превышает допустимый размер.");
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        using var snapshot = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(snapshot);
        return reader.ReadToEnd();
    }

    public static void Write(string path, string text)
        => Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(text);
        });

    public static void Write(string path, Action<Stream> write)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                write(stream);
            for (var attempt = 0; ; attempt++)
                try
                {
                    if (File.Exists(path))
                        File.Replace(temp, path, null);
                    else
                        File.Move(temp, path);
                    break;
                }
                catch (IOException) when (attempt < 4) { Thread.Sleep(15); }
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public static string Protect(string text)
    {
        foreach (var (path, token) in PrivateFolders)
        {
            if (path.Length == 0)
                continue;
            text = text.Replace(path, token, StringComparison.OrdinalIgnoreCase);
            text = text.Replace(path.Replace('\\', '/'), token, StringComparison.OrdinalIgnoreCase);
        }
        return text.Replace('\r', ' ').Replace('\n', ' ');
    }
}
