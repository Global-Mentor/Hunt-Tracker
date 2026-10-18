using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HuntTracker;

public sealed record HistoryBlock(int Count, string Previous, Match[] Matches);
public sealed record HistoryPage(Match[] Matches, int Total);

public static partial class HistoryBlocks
{
    public const int BlockSize = 128;
    public const int HotHistoryLimit = BlockSize * 2;
    public const int PageSize = 100;
    private const int MaximumBlockBytes = 16 * 1024 * 1024;

    public static string Write(string dataFolder, string session, int count, string previous, IEnumerable<Match> matches)
    {
        var block = new HistoryBlock(count, previous, matches.ToArray());
        var json = JsonSerializer.Serialize(block, Statistics.Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBlockBytes)
            throw new InvalidDataException("Блок истории превышает допустимый размер.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var reference = $"history/{session}/{count}-{hash}.json";
        var path = Path.Combine(dataFolder, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
            AtomicFile.Write(path, json);
        return reference;
    }

    public static void ValidateReference(string session, string reference, int count)
    {
        if (count == 0 && reference == "")
            return;
        if (reference is null || count <= 0 || count % BlockSize != 0 || !ReferencePattern().IsMatch(reference) ||
            !reference.StartsWith($"history/{session}/{count}-", StringComparison.Ordinal))
            throw new InvalidDataException("Некорректная ссылка на блок истории.");
    }

    public static HistoryBlock Read(string dataFolder, string session, string reference, int count)
    {
        ValidateReference(session, reference, count);
        var json = AtomicFile.Read(Path.Combine(dataFolder, reference), MaximumBlockBytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if (!reference.EndsWith("-" + hash + ".json", StringComparison.Ordinal))
            throw new InvalidDataException("Повреждён блок истории.");
        var block = JsonSerializer.Deserialize<HistoryBlock>(json, Statistics.Json) ?? throw new InvalidDataException("Пустой блок истории.");
        if (block.Count != count || block.Matches is null || block.Matches.Length != BlockSize || block.Previous is null)
            throw new InvalidDataException("Некорректный блок истории.");
        ValidateReference(session, block.Previous, count - BlockSize);
        var ids = new HashSet<string>();
        foreach (var match in block.Matches)
        {
            StatisticsStore.ValidateMatch(match);
            if (match.Status != "complete" || !ids.Add(match.Id))
                throw new InvalidDataException("Повторяющийся или незавершённый матч в блоке.");
        }
        return block;
    }

    public static IEnumerable<Match> Enumerate(Statistics stats, string dataFolder, CancellationToken cancellation = default)
    {
        if (stats.Current is { Status: not "complete" } current)
            yield return current;
        for (var i = stats.Recent.Count - 1; i >= 0; i--)
        {
            cancellation.ThrowIfCancellationRequested();
            yield return stats.Recent[i];
        }
        var reference = stats.HistoryReference;
        for (var count = stats.HistoryCount; count > 0; count -= BlockSize)
        {
            cancellation.ThrowIfCancellationRequested();
            var block = Read(dataFolder, stats.Id, reference, count);
            for (var i = block.Matches.Length - 1; i >= 0; i--)
                yield return block.Matches[i];
            reference = block.Previous;
        }
    }

    public static HistoryPage Page(Statistics stats, string dataFolder, string sort = "Number", bool descending = true,
        Match? boundary = null, bool previous = false, CancellationToken cancellation = default)
    {
        int Compare(Match a, Match b)
        {
            var order = sort switch
            {
                "MapName" => StringComparer.CurrentCulture.Compare(a.MapName, b.MapName),
                "Kills" => a.Kills.CompareTo(b.Kills),
                "Deaths" => a.Deaths.CompareTo(b.Deaths),
                "Result" => StringComparer.CurrentCulture.Compare(a.Result, b.Result),
                "Started" => a.Started.CompareTo(b.Started),
                _ => a.Number.CompareTo(b.Number)
            };
            if (order == 0) order = a.Number.CompareTo(b.Number);
            if (order == 0) order = StringComparer.Ordinal.Compare(a.Id, b.Id);
            return descending ^ previous ? -Math.Sign(order) : Math.Sign(order);
        }
        var comparer = Comparer<Match>.Create(Compare);
        var total = checked(stats.HistoryCount + stats.Recent.Count + (stats.Current is { Status: not "complete" } ? 1 : 0));
        if (sort == "Number" && descending && !previous)
            return new(Enumerate(stats, dataFolder, cancellation)
                .Where(match => boundary is null || Compare(match, boundary) > 0).Take(PageSize).ToArray(), total);
        var retained = new PriorityQueue<Match, Match>(Comparer<Match>.Create((a, b) => Compare(b, a)));
        foreach (var match in Enumerate(stats, dataFolder, cancellation))
        {
            if (boundary is not null && Compare(match, boundary) <= 0)
                continue;
            retained.Enqueue(match, match);
            if (retained.Count > PageSize)
                retained.Dequeue();
        }
        var rows = retained.UnorderedItems.Select(item => item.Element).Order(comparer).ToArray();
        if (previous) Array.Reverse(rows);
        return new(rows, total);
    }

    [GeneratedRegex(@"\Ahistory/[a-f0-9]{32}/[1-9][0-9]*-[a-f0-9]{64}\.json\z")]
    private static partial Regex ReferencePattern();
}
