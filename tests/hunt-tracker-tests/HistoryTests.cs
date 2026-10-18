using System.Text.Json;
using Xunit;

namespace HuntTracker.Tests;

public sealed class HistoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HuntHistoryTests-" + Guid.NewGuid().ToString("N"));
    public HistoryTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private static void Add(Statistics stats, int count)
    {
        for (var i = 0; i < count; i++)
        {
            stats.Start(true);
            stats.Current!.Map = i % 2 == 0 ? "creek" : "cemetery";
            for (var kill = 0; kill < stats.Games % 7; kill++) stats.Combat("kill", stats.Current.Id);
            stats.Complete("test");
        }
    }

    [Fact]
    public void TenThousandMatchesKeepHotHistoryAndManifestBounded()
    {
        var store = new StatisticsStore(root);
        var stats = new Statistics();
        for (var i = 0; i < 40; i++)
        {
            Add(stats, 250);
            store.Save(stats);
            Assert.InRange(stats.Recent.Count, 128, 256);
            Assert.True(new FileInfo(store.CurrentFile).Length < 65000);
        }
        var restored = StatisticsStore.Read(store.CurrentFile, boundedHistory: true);
        Assert.Equal(10000, restored.HistoryCount + restored.Recent.Count);
        Assert.Equal(stats.Kills, restored.Kills);
        var all = HistoryBlocks.Enumerate(restored, store.DataFolder, TestContext.Current.CancellationToken).ToArray();
        Assert.Equal(10000, all.Length);
        Assert.Equal(10000, all.Select(m => m.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 10000).Reverse(), all.Select(m => m.Number));
        Assert.Equal(stats.Kills, all.Sum(m => m.Kills));
        Assert.Equal(10000, StatisticsStore.Read(store.CurrentFile).Recent.Count);
    }

    [Theory]
    [InlineData("Number", true)]
    [InlineData("Number", false)]
    [InlineData("Kills", true)]
    [InlineData("Kills", false)]
    public void PagingHasStableGlobalOrderWithoutMissingOrRepeatedRows(string sort, bool descending)
    {
        var store = new StatisticsStore(root);
        var stats = new Statistics();
        Add(stats, 529);
        store.Save(stats);
        var expected = HistoryBlocks.Enumerate(stats, store.DataFolder, TestContext.Current.CancellationToken)
            .OrderBy(m => sort == "Kills" ? m.Kills : m.Number).ThenBy(m => m.Number).ToArray();
        if (descending) Array.Reverse(expected);
        var actual = new List<Match>();
        Match? boundary = null;
        Match[]? preceding = null;
        while (true)
        {
            var page = HistoryBlocks.Page(stats, store.DataFolder, sort, descending, boundary, cancellation: TestContext.Current.CancellationToken);
            Assert.Equal(529, page.Total);
            if (page.Matches.Length == 0) break;
            if (preceding is not null)
            {
                var back = HistoryBlocks.Page(stats, store.DataFolder, sort, descending, page.Matches[0], true, TestContext.Current.CancellationToken);
                Assert.Equal(preceding.Select(m => m.Id), back.Matches.Select(m => m.Id));
            }
            actual.AddRange(page.Matches);
            boundary = page.Matches[^1];
            preceding = page.Matches;
        }
        Assert.Equal(expected.Select(m => m.Id), actual.Select(m => m.Id));
    }

    [Fact]
    public void FailedManifestCommitDoesNotDiscardHistoryOrChangeCommittedData()
    {
        var store = new StatisticsStore(root);
        var stats = new Statistics();
        Add(stats, 200);
        store.Save(stats);
        var revision = stats.WriteRevision;
        Add(stats, 100);
        using (File.Open(store.CurrentFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => store.Save(stats));
        Assert.Equal(300, stats.Recent.Count);
        Assert.Equal(0, stats.HistoryCount);
        Assert.Equal(revision, stats.WriteRevision);
        Assert.Equal(200, StatisticsStore.Read(store.CurrentFile).Recent.Count);
        store.Save(stats);
        Assert.Equal(300, StatisticsStore.Read(store.CurrentFile).Recent.Count);
    }

    [Fact]
    public void RecoveryRemainsStandaloneAndCanBeSealedAgain()
    {
        var store = new StatisticsStore(root);
        var stats = new Statistics();
        Add(stats, 529);
        store.Save(stats);
        Add(stats, 10);
        store.SaveRecovery(stats);
        var restoredRoot = Path.Combine(root, "restored");
        Directory.CreateDirectory(Path.Combine(restoredRoot, "_runtime"));
        File.Copy(Path.Combine(root, "_runtime/statistics-recovery.json"), Path.Combine(restoredRoot, "_runtime/statistics-recovery.json"));
        var recoveredStore = new StatisticsStore(restoredRoot);
        var recovered = recoveredStore.Load(boundedHistory: true);
        Assert.Equal(Enumerable.Range(1, 539), recovered.Recent.Select(m => m.Number));
        recoveredStore.Save(recovered);
        Assert.Equal(539, StatisticsStore.Read(recoveredStore.CurrentFile).Recent.Count);
        Assert.Equal(stats.Kills, recovered.Kills);
        Assert.Equal(539, recovered.Recent[^1].Number);
    }

    [Fact]
    public void LegacyMigrationAndArchivePreserveEveryMatch()
    {
        var store = new StatisticsStore(root);
        var stats = new Statistics();
        Add(stats, 530);
        Directory.CreateDirectory(store.DataFolder);
        File.WriteAllText(store.CurrentFile, JsonSerializer.Serialize(stats, Statistics.Json));
        var loaded = store.Load(boundedHistory: true);
        store.Save(loaded);
        Assert.Single(Directory.GetFiles(Path.Combine(store.DataFolder, "migrations")));
        var next = store.NewSession(loaded);
        Assert.Equal(0, next.Games);
        var archived = StatisticsStore.Read(Path.Combine(store.DataFolder, "sessions", loaded.Id + ".json"));
        Assert.Equal(530, archived.Recent.Count);
        Assert.Equal(stats.Kills, archived.Kills);
    }

    [Fact]
    public void CorruptedBlocksFailWithoutRewritingHistory()
    {
        var store = new StatisticsStore(root);
        var stats = new Statistics();
        Add(stats, 529);
        store.Save(stats);
        var block = Path.Combine(store.DataFolder, stats.HistoryReference);
        File.AppendAllText(block, " ");
        Assert.Throws<InvalidDataException>(() => HistoryBlocks.Enumerate(stats, store.DataFolder, TestContext.Current.CancellationToken).ToArray());
        Assert.True(File.ReadAllText(block).EndsWith(' '));
        Assert.Throws<InvalidDataException>(() => HistoryBlocks.ValidateReference(stats.Id, "../outside.json", 128));
        Assert.Throws<InvalidDataException>(() => HistoryBlocks.ValidateReference(stats.Id, null!, 128));
    }

    [Fact]
    public async Task CommandFloodIsBoundedAndDoesNotPreventShutdown()
    {
        new AppConfig { GameFolder = "./game", NvidiaLogs = "./sdk" }.Save(root);
        var engine = new TrackerEngine(root, "NoSuchHistoryFixture");
        var requests = Enumerable.Range(0, 1000).Select(_ => engine.CurrentHistory()).ToArray();
        Assert.Equal(64, requests.Count(task => !task.IsCompleted));
        engine.Stop();
        await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try { await Task.WhenAll(requests); } catch (InvalidOperationException) { }
        Assert.All(requests, task => Assert.True(task.IsCompleted));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CurrentHistory());
    }

    [Fact]
    public void LargeMatchKeepsCountersAndOnlyRecentCombatOrder()
    {
        var stats = new Statistics();
        stats.Start(true);
        for (var i = 0; i < 10000; i++) stats.Combat(i % 2 == 0 ? "kill" : "death", stats.Current!.Id);
        Assert.Equal(5000, stats.Kills);
        Assert.Equal(5000, stats.Deaths);
        Assert.Equal(5000, stats.Current!.Kills);
        Assert.Equal(Match.CombatHistoryLimit, stats.Current.CombatEvents.Count);
        Assert.Equal("death", stats.Current.CombatEvents[^1]);
        stats.Survived = stats.Lost = int.MaxValue;
        Assert.Equal("50%", stats.Survival);
    }

    [Fact]
    public async Task SignalFloodDoesNotInventKillsAndCollectionRecovers()
    {
        var game = Path.Combine(root, "game/USER/Game.log");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        Directory.CreateDirectory(Path.Combine(root, "sdk"));
        File.WriteAllText(game, "baseline\n");
        new AppConfig { GameFolder = "./game", NvidiaLogs = "./sdk", AlertMode = "trades", DetectTrades = true }.Save(root);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var engine = new TrackerEngine(root, process.ProcessName);
        var worker = Task.Run(engine.RunAsync, TestContext.Current.CancellationToken);
        async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out during signal flood");
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }
        try
        {
            await Until(() => engine.Latest?.GamePid > 0 && engine.Latest.GameReadable);
            var sdk = Path.Combine(root, "sdk", $"HuntGame_{engine.Latest!.GamePid}.log");
            File.WriteAllText(sdk, string.Concat(Enumerable.Range(0, 10000).Select(i => $"Posted Request HLSetVideo[flood{i}]\n")));
            await Until(() => engine.Latest!.Warning.Contains("Слишком много"));
            await Task.Delay(1700, TestContext.Current.CancellationToken);
            Assert.Equal(0, engine.Latest!.Statistics.Kills);
            Assert.Equal(0, engine.Latest.Statistics.Deaths);
            File.AppendAllText(sdk, "Posted Request HLSetVideo[afterFlood]\n");
            await Until(() => engine.Latest!.Statistics.Kills == 1);
        }
        finally
        {
            engine.Stop();
            await worker.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }
}
