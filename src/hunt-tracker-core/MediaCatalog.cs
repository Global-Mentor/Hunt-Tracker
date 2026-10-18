namespace HuntTracker;

public sealed record EventMedia(string[] Images, string[] Sounds);
public static class MediaCatalog
{
    public static Dictionary<string, EventMedia> Empty() => new() { ["kill"] = new([], []), ["death"] = new([], []), ["trade"] = new([], []) };
    public static Dictionary<string, EventMedia> Read(string root)
    {
        var result = Empty();
        foreach (var kind in result.Keys.ToArray())
        {
            string[] Files(string category, string[] extensions)
            {
                var relative = $"source/{category}/{kind}";
                var folder = Path.Combine(root, relative);
                Directory.CreateDirectory(folder);
                return new DirectoryInfo(folder).EnumerateFiles().Where(f => f.Length > 0 && extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => relative + "/" + Uri.EscapeDataString(f.Name)).ToArray();
            }
            result[kind] = new(Files("images", [".png"]), Files("sounds", [".mp3", ".wav", ".ogg", ".m4a", ".flac", ".opus", ".webm"]));
        }
        return result;
    }
}
