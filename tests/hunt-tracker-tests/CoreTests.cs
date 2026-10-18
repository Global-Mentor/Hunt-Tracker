using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace HuntTracker.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HuntTrackerTests-" + Guid.NewGuid().ToString("N"));
    public CoreTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private static CombatEvent Event(double at = 0, string mode = "trades", string match = "one", int epoch = 1, bool available = true, int generation = 1) => new("event", at, mode, epoch, available, generation, match);
    private static CombatResult Resolve(CombatEvent first, List<CombatEvent> pending, List<DeathSignal> signals, bool healthy = true, bool caughtUp = true, int generation = 1, int epoch = 1) => Combat.Resolve(first, pending, signals, healthy, caughtUp, generation, epoch);

    [Fact]
    public async Task RelativeApplicationRootResolvesConfiguredSources()
    {
        new AppConfig { GameFolder = "./game", NvidiaLogs = "./sdk" }.Save(root);
        var engine = new TrackerEngine(Path.GetRelativePath(Environment.CurrentDirectory, root), "NoSuchRootFixture");
        engine.Stop();
        await engine.RunAsync();
        Assert.Equal(AtomicFile.Protect(Path.Combine(root, "game/USER/Game.log")).Replace('\\', '/'), engine.Latest!.GameLog);
    }

    [Fact]
    public void LongPartialLineAllocationsStayBounded()
    {
        var path = Path.Combine(root, "long.log");
        File.WriteAllText(path, new string('x', 1048576));
        var tail = new LogTail(path, false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Empty(tail.Read());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8 * 1048576, $"Reading a 1 MiB partial line allocated {allocated} bytes");
        File.AppendAllText(path, "overflow\nvalid\r\n\n");
        Assert.Equal(new[] { "valid", "" }, tail.Read());
    }

    [Fact]
    public void PartialLineSurvivesMultipleReadsAndCrLfBoundary()
    {
        var path = Path.Combine(root, "partial.log");
        File.WriteAllText(path, "first");
        var tail = new LogTail(path, false);
        Assert.Empty(tail.Read());
        File.AppendAllText(path, " second\r");
        Assert.Empty(tail.Read());
        File.AppendAllText(path, "\nthird\n");
        Assert.Equal(new[] { "first second", "third" }, tail.Read());
    }

    [Fact]
    public void IdleMetadataCheckStillReadsAppendsAndForcedCheckDetectsLocks()
    {
        var path = Path.Combine(root, "idle.log");
        File.WriteAllText(path, "old\n");
        var tail = new LogTail(path, true);
        Assert.Empty(tail.Read(false));
        File.AppendAllText(path, "new\n");
        Assert.Equal(new[] { "new" }, tail.Read(false));
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(tail.Read());
            Assert.False(tail.Healthy);
        }
        Assert.Empty(tail.Read(false));
        Assert.True(tail.Healthy);
    }

    [Fact]
    public void ForcedCheckDetectsSameSizeRewriteWithPreservedTimestamp()
    {
        var path = Path.Combine(root, "rewrite.log");
        File.WriteAllText(path, "old\n");
        var tail = new LogTail(path, true);
        tail.Read(false);
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "new\n");
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(new[] { "new" }, tail.Read());
        Assert.Equal(1, tail.Generation);
    }

    [Fact]
    public async Task IdleVerificationEventuallyDetectsPreservedTimestampRewrite()
    {
        var path = Path.Combine(root, "idle-rewrite.log");
        File.WriteAllText(path, "old\n");
        var tail = new LogTail(path, true);
        tail.Read(false);
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "new\n");
        File.SetLastWriteTimeUtc(path, stamp);
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "new" }, tail.Read(false));
        Assert.Equal(1, tail.Generation);
    }

    [Fact]
    public async Task HighlightForcesGameAccessCheckEvenWhenItsMetadataIsUnchanged()
    {
        var game = Path.Combine(root, "game/USER/Game.log");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        Directory.CreateDirectory(Path.Combine(root, "sdk"));
        File.WriteAllText(game, "baseline\n");
        new AppConfig { GameFolder = "./game", NvidiaLogs = "./sdk", DetectTrades = false }.Save(root);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var engine = new TrackerEngine(root, process.ProcessName);
        var worker = Task.Run(engine.RunAsync, TestContext.Current.CancellationToken);
        async Task Until(Func<bool> condition)
        {
            var timeout = DateTime.UtcNow.AddSeconds(8);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < timeout, "Timed out during access check");
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }
        try
        {
            await Until(() => engine.Latest?.GameReadable == true && engine.Latest.GamePid > 0);
            var sdk = Path.Combine(root, "sdk", $"HuntGame_{engine.Latest!.GamePid}.log");
            using (File.Open(game, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                File.WriteAllText(sdk, "Posted Request HLSetVideo[locked]\n");
                await Until(() =>
                {
                    try { return AtomicFile.Read(Path.Combine(root, "_runtime/alerts.js")).Contains("\"id\":1"); }
                    catch (IOException) { return false; }
                });
                await Until(() => engine.Latest?.GameReadable == false);
            }
            await Task.Delay(700, TestContext.Current.CancellationToken);
            Assert.Equal(0, engine.Latest!.Statistics.Kills);
            Assert.Equal(0, engine.Latest.Statistics.Deaths);
            Assert.Contains("\"type\":\"unknown\"", AtomicFile.Read(Assert.Single(Directory.GetFiles(Path.Combine(root, "logs")))));
        }
        finally { engine.Stop(); await worker; }
    }

    [Fact]
    public async Task ChangingPathsContinuesSessionWithoutReplayingOldSignals()
    {
        var config = new AppConfig { GameFolder = "./old-game", NvidiaLogs = "./old-sdk", DetectTrades = false };
        config.Save(root);
        var stats = new Statistics();
        stats.Start(true);
        stats.Current!.Loaded = true;
        stats.Combat("kill", stats.Current.Id);
        new StatisticsStore(root).Save(stats);
        var engine = new TrackerEngine(root, System.Diagnostics.Process.GetCurrentProcess().ProcessName) { PanelVisible = true };
        var worker = Task.Run(engine.RunAsync, TestContext.Current.CancellationToken);
        async Task Until(Func<bool> condition)
        {
            var timeout = DateTime.UtcNow.AddSeconds(8);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < timeout, "Timed out waiting for switched log sources");
                await Task.Delay(30, TestContext.Current.CancellationToken);
            }
        }
        try
        {
            await Until(() => engine.Latest?.GamePid > 0);
            var game = Path.Combine(root, "new-game", "USER", "Game.log");
            var sdk = Path.Combine(root, "new-sdk", $"HuntGame_{engine.Latest!.GamePid}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(game)!);
            Directory.CreateDirectory(Path.GetDirectoryName(sdk)!);
            File.WriteAllText(game, "Starting UI state machine 'death/StateMachine'\n");
            File.WriteAllText(sdk, "Posted Request HLSetVideo[old]\n");
            config.GameFolder = "./new-game";
            config.NvidiaLogs = "./new-sdk";
            await engine.ApplyConfig(config);
            await Until(() => engine.Latest?.GameLog == AtomicFile.Protect(game).Replace('\\', '/') && engine.Latest.SdkLog == AtomicFile.Protect(sdk).Replace('\\', '/') && engine.Latest.GameReadable && engine.Latest.SdkReadable);
            await Task.Delay(650, TestContext.Current.CancellationToken);
            Assert.Equal(stats.Id, engine.Latest!.Statistics.Id);
            Assert.Equal(1, engine.Latest.Statistics.Kills);
            Assert.Equal(0, engine.Latest.Statistics.Deaths);
            File.AppendAllText(sdk, "Posted Request HLSetVideo[new]\n");
            await Until(() => engine.Latest!.Statistics.Kills == 2);
            Assert.Equal(0, engine.Latest!.Statistics.Deaths);
            Assert.Equal(config.GameFolder, AppConfig.Load(root).GameFolder);
        }
        finally { engine.Stop(); await worker; }
    }

    [Fact]
    public void TradeDetectionDefaultsOnButRespectsSavedChoice()
    {
        Assert.True(new AppConfig().DetectTrades);
        var config = new AppConfig { DetectTrades = false };
        Assert.False(AppConfig.Parse(JsonSerializer.Serialize(config, AppConfig.Json)).DetectTrades);
    }

    [Theory]
    [InlineData("kills", 0, false, false)]
    [InlineData("kills-deaths", 300, true, false)]
    [InlineData("trades", 500, true, true)]
    public void ModesHaveIndependentDelaysAndFilters(string mode, int delay, bool death, bool trade)
    {
        Assert.Equal(delay, Combat.Delay(mode));
        Assert.True(Combat.Enabled(mode, "kill"));
        Assert.Equal(death, Combat.Enabled(mode, "death"));
        Assert.Equal(trade, Combat.Enabled(mode, "trade"));
        Assert.False(Combat.Enabled(mode, "unknown"));
    }
    [Theory]
    [InlineData(500, 400, true)]
    [InlineData(501, 400, false)]
    [InlineData(100, -450, false)]
    public void TradeWindowIncludesOverallSpan(double secondAt, double screenAt, bool paired)
    {
        var first = Event();
        var second = Event(secondAt);
        Assert.Equal(paired, Resolve(first, [first, second], [new(screenAt, 1)]).Kind == "trade");
    }
    [Theory]
    [InlineData("other", 1, true, 1)]
    [InlineData("one", 2, true, 1)]
    [InlineData("one", 1, false, 1)]
    [InlineData("one", 1, true, 2)]
    public void TradesCannotCrossMatchOrReadGap(string match, int epoch, bool available, int generation)
    {
        var first = Event();
        var second = Event(100, match: match, epoch: epoch, available: available, generation: generation);
        Assert.NotEqual("trade", Resolve(first, [first, second], [new(200, 1)]).Kind);
    }
    [Fact]
    public void AmbiguousBurstNeverBecomesTradeAfterRemoval()
    {
        var a = Event();
        var b = Event(100);
        var c = Event(200);
        List<CombatEvent> pending = [a, b, c];
        List<DeathSignal> screens = [new(150, 1)];
        Assert.NotEqual("trade", Resolve(a, pending, screens).Kind);
        pending.Remove(a);
        Assert.NotEqual("trade", Resolve(b, pending, screens).Kind);
    }
    [Fact]
    public void TwoDeathScreensBlockTrade()
    {
        var a = Event();
        var b = Event(200);
        Assert.NotEqual("trade", Resolve(a, [a, b], [new(100, 1), new(200, 1)]).Kind);
        Assert.True(a.TradeBlocked && b.TradeBlocked);
    }
    [Fact]
    public void BleedKillAfterDeathIsCounted()
    {
        var a = Event(0, "kills-deaths");
        var b = Event(260, "kills-deaths");
        List<DeathSignal> screens = [new(122, 1)];
        Assert.Equal("death", Resolve(a, [a, b], screens).Kind);
        Assert.Equal("kill", Resolve(b, [b], screens).Kind);
    }
    [Fact]
    public void SameBatchIsConservativeAndOneScreenCountsOnce()
    {
        var a = Event(0, "kills-deaths");
        var b = Event(0, "kills-deaths");
        List<DeathSignal> screens = [new(120, 1)];
        Assert.Equal("unknown", Resolve(a, [a, b], screens).Kind);
        Assert.Equal("death", Resolve(b, [b], screens).Kind);
    }
    [Theory]
    [InlineData(false, true, 1, 1)]
    [InlineData(true, false, 1, 1)]
    [InlineData(true, true, 2, 1)]
    [InlineData(true, true, 1, 2)]
    public void MissingOrReplacedLogCannotProveKill(bool healthy, bool caughtUp, int generation, int epoch)
    {
        var e = Event(mode: "kills-deaths");
        Assert.Equal("unknown", Resolve(e, [e], [], healthy, caughtUp, generation, epoch).Kind);
    }
    [Fact]
    public void MatchLifecyclePreservesLateCombatAndOutcomePolicy()
    {
        var s = new Statistics();
        Assert.False(s.ProcessLine("unrelated"));
        s.ProcessLine("Starting UI state machine 'loading/StateMachine'");
        s.ProcessLine("CGameLobby::OnGameStarting()");
        s.ProcessLine("PrepareLevel 'levels/cemetery' (client)");
        s.ProcessLine("PrepareLevel 'levels/cemetery' (client)");
        s.ProcessLine("Client_OnDisconnect(cause=11, desc='Nub destroyed', reasonType='Unknown')");
        Assert.Equal(1, s.Games);
        Assert.Equal(1, s.Maps["cemetery"]);
        Assert.Equal("loading", s.Current!.Status);
        s.ProcessLine("transition from state(eLS_Loading) to (eLS_Game)");
        var first = s.Current.Id;
        s.Combat("kill", first);
        s.Combat("death", first);
        s.Combat("unknown", first);
        s.ProcessLine("Starting UI state machine 'death/StateMachine'");
        s.ProcessLine("Stopping UI state machine 'death/StateMachine'");
        s.ProcessLine("Client_OnDisconnect(cause=0, reasonType='MissionRequested')");
        s.ProcessLine("Client_OnDisconnect(cause=0, reasonType='MissionRequested')");
        Assert.Equal(1, s.Survived);
        Assert.Single(s.Recent);
        s.Start(true);
        s.Combat("kill", first);
        Assert.Equal(0, s.Current!.Kills);
        Assert.Equal(new[] { "kill", "death", "kill" }, s.Recent[0].CombatEvents);
        s.Complete("tracker_stopped");
        Assert.Equal(1, s.Lost);
        Assert.Equal(1, s.Deaths);
        s.ProcessLine("PrepareLevel 'levels/creek' (client)");
        s.ProcessLine("transition from state(eLS_Loading) to (eLS_Game)");
        s.Complete("MissionRequested");
        Assert.Equal(2, s.Lost);
        Assert.Equal(1, s.Survived);
    }
    [Fact]
    public void StorageIsIncrementalAndFailedCommitKeepsPreviousSnapshot()
    {
        var store = new StatisticsStore(root);
        var stats = new Statistics();
        for (var i = 0; i < 60; i++)
        {
            stats.Start(true);
            stats.Combat("kill", stats.Current!.Id);
            stats.Complete("MissionRequested");
        }
        store.Save(stats);
        var files = Directory.GetFiles(Path.Combine(root, "data/matches", stats.Id));
        var stamps = files.ToDictionary(f => f, File.GetLastWriteTimeUtc);
        stats.Start(true);
        stats.Combat("kill", stats.Current!.Id);
        store.Save(stats);
        Assert.All(stamps, item => Assert.Equal(item.Value, File.GetLastWriteTimeUtc(item.Key)));
        var before = AtomicFile.Read(store.CurrentFile);
        File.Move(store.CurrentFile, store.CurrentFile + ".held");
        Directory.CreateDirectory(store.CurrentFile);
        stats.Combat("kill", stats.Recent[0].Id);
        Assert.ThrowsAny<IOException>(() => store.Save(stats));
        Directory.Delete(store.CurrentFile);
        File.Move(store.CurrentFile + ".held", store.CurrentFile);
        Assert.Equal(before, AtomicFile.Read(store.CurrentFile));
        Assert.Equal(1, StatisticsStore.Read(store.CurrentFile).Recent[0].Kills);
        store.Save(stats);
        var read = StatisticsStore.Read(store.CurrentFile);
        Assert.Equal(2, read.Recent[0].Kills);
        Assert.Equal(62, read.Kills);
        Assert.Empty(StatisticsStore.Read(store.CurrentFile, true).Recent);
    }
    [Fact]
    public void LegacyHistoryMigrationKeepsCountersAndOriginalFile()
    {
        var stats = new Statistics();
        stats.Start(true);
        stats.Combat("kill", stats.Current!.Id);
        stats.Complete("MissionRequested");
        var store = new StatisticsStore(root);
        Directory.CreateDirectory(store.DataFolder);
        var node = JsonSerializer.SerializeToNode(stats, Statistics.Json)!;
        node["padding"] = new string('x', 4194304);
        node["recent"]![0]!.AsObject().Remove("combat_events");
        node["current"] = null;
        AtomicFile.Write(store.CurrentFile, node.ToJsonString());
        var read = store.Load();
        Assert.Equal(1, read.Kills);
        Assert.Empty(read.Recent[0].CombatEvents);
        store.Save(read);
        Assert.True(File.Exists(Path.Combine(root, "data/migrations", stats.Id + ".json")));
        Assert.Equal(1, store.Load().Kills);
    }
    [Fact]
    public void RecoveryRestoresNewestStateAndNeverRollsBack()
    {
        var store = new StatisticsStore(root);
        var s = new Statistics();
        store.Save(s);
        s.Combat("kill", null);
        store.SaveRecovery(s);
        var recovery = Path.Combine(root, "_runtime/statistics-recovery.json");
        var old = AtomicFile.Read(recovery);
        s = store.Load();
        Assert.Equal(1, s.Kills);
        store.Save(s);
        Assert.False(File.Exists(recovery));
        s.Combat("kill", null);
        store.Save(s);
        AtomicFile.Write(recovery, old);
        Assert.Equal(2, store.Load().Kills);
        store.Save(s);
        store.SaveRecovery(s);
        AtomicFile.Write(store.CurrentFile, "{bad");
        s = store.Load();
        store.Save(s);
        Assert.Equal(2, s.Kills);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "data/recovery")));
        store.SaveRecovery(s);
        var next = store.NewSession(s);
        Assert.Equal(0, next.Kills);
        Assert.False(File.Exists(recovery));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "data/sessions")));
    }
    [Fact]
    public void CorruptRecoveryCannotHideHealthyPrimaryOrStartEmptySession()
    {
        var store = new StatisticsStore(root);
        var stats = new Statistics();
        stats.Combat("kill", null);
        store.Save(stats);
        var recovery = Path.Combine(root, "_runtime/statistics-recovery.json");
        Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
        File.WriteAllText(recovery, "{incomplete");
        Assert.Equal(1, store.Load().Kills);
        Assert.NotEmpty(store.RecoveryWarning);
        Assert.Equal("{incomplete", File.ReadAllText(recovery));
        File.Delete(store.CurrentFile);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.False(File.Exists(store.CurrentFile));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":\"bad\"}")]
    public void RecoveryPreservesInvalidManifestBeforeReplacingIt(string invalid)
    {
        var store = new StatisticsStore(root);
        var stats = new Statistics();
        stats.Combat("kill", null);
        store.Save(stats);
        store.SaveRecovery(stats);
        File.WriteAllText(store.CurrentFile, invalid);
        var restored = store.Load();
        store.Save(restored);
        Assert.Equal(1, store.Load().Kills);
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(root, "data/recovery")));
        Assert.Equal(invalid, File.ReadAllText(backup));
    }
    [Fact]
    public void SnapshotCopiesAreIndependentAndKeepCurrentMatchIdentity()
    {
        var stats = new Statistics();
        stats.Start(true);
        stats.Combat("kill", stats.Current!.Id);
        stats.Complete("MissionRequested");
        var copy = stats.Copy();
        Assert.Same(copy.Current, Assert.Single(copy.Recent));
        Assert.NotSame(stats.Current, copy.Current);
        Assert.Equal(stats.Current.Revision, copy.Current!.Revision);
        copy.Current.CombatEvents.Add("death");
        copy.Maps["cemetery"] = 9;
        Assert.Single(stats.Current.CombatEvents);
        Assert.Equal(0, stats.Maps["cemetery"]);
        var summary = stats.Copy(includeHistory: false);
        Assert.Empty(summary.Recent);
        Assert.NotSame(stats.Current, summary.Current);
        Assert.Equal(stats.Current.Id, summary.Current!.Id);
    }
    [Fact]
    public void FailedArchiveDoesNotChangeLiveMatch()
    {
        var store = new StatisticsStore(root);
        var stats = new Statistics();
        stats.Start(true);
        store.Save(stats);
        File.WriteAllText(Path.Combine(root, "data/sessions"), "blocked");
        Assert.ThrowsAny<IOException>(() => store.NewSession(stats));
        Assert.Equal("loading", stats.Current!.Status);
    }
    [Theory]
    [InlineData("null")]
    [InlineData("\"bad\"")]
    public void CorruptManifestIsRejected(string value)
    {
        var store = new StatisticsStore(root);
        store.Save(new());
        var node = JsonNode.Parse(AtomicFile.Read(store.CurrentFile))!;
        node["recent"] = JsonNode.Parse(value);
        AtomicFile.Write(store.CurrentFile, node.ToJsonString());
        Assert.Throws<InvalidDataException>(() => store.Load());
    }
    [Fact]
    public void ChangedMatchContentsFailChecksum()
    {
        var store = new StatisticsStore(root);
        var stats = new Statistics();
        stats.Start(true);
        stats.Complete("MissionRequested");
        store.Save(stats);
        var file = Directory.GetFiles(Path.Combine(root, "data/matches", stats.Id))[0];
        File.AppendAllText(file, " ");
        Assert.Throws<InvalidDataException>(() => store.Load());
    }
    [Fact]
    public void LogTailHandlesFirstCreationLocksPartialUtf8AndRotation()
    {
        var path = Path.Combine(root, "game.log");
        var tail = new LogTail(path, true);
        Assert.Empty(tail.Read());
        File.WriteAllText(path, "new\n");
        Assert.Equal(new[] { "new" }, tail.Read());
        Assert.Equal(0, tail.Generation);
        using (var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            tail = new(path, true);
            Assert.False(tail.Healthy);
        }
        Assert.Empty(tail.Read());
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var bytes = Encoding.UTF8.GetBytes("雪\n");
            stream.Write(bytes, 0, 2);
            stream.Flush();
            Assert.Empty(tail.Read());
            stream.Write(bytes, 2, bytes.Length - 2);
            stream.Flush();
            Assert.Equal(new[] { "雪" }, tail.Read());
        }
        var oldGeneration = tail.Generation;
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "changed\n");
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(new[] { "changed" }, tail.Read());
        Assert.Equal(oldGeneration + 1, tail.Generation);
        File.Move(path, path + ".old");
        File.WriteAllText(path, "rotated\n");
        Assert.Equal(new[] { "rotated" }, tail.Read());
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Fact]
    public void LongLineCannotEmitFalseTrailingEvent()
    {
        var path = Path.Combine(root, "huge.log");
        File.WriteAllText(path, new string('x', 1060000) + "Posted Request HLSetVideo[false]\nvalid\n");
        var tail = new LogTail(path, false);
        Assert.Equal(new[] { "valid" }, tail.Read().Concat(tail.Read()));
    }
    [Theory]
    [InlineData("volume", "true")]
    [InlineData("volume", "\"0.5\"")]
    [InlineData("durationMs", "299")]
    [InlineData("alertsEnabled", "\"false\"")]
    [InlineData("statisticsRefreshMs", "250")]
    public void InvalidSettingsAreRejected(string key, string value)
    {
        var node = JsonSerializer.SerializeToNode(new AppConfig(), AppConfig.Json)!;
        node[key] = JsonNode.Parse(value);
        Assert.ThrowsAny<Exception>(() => AppConfig.Parse(node.ToJsonString()));
    }
    [Fact]
    public void OlderConfigKeepsAllWidgetFields()
    {
        var node = JsonSerializer.SerializeToNode(new AppConfig(), AppConfig.Json)!.AsObject();
        node.Remove("display");
        Assert.Equal(AppConfig.FieldNames, AppConfig.Parse(node.ToJsonString()).Display.Fields);
    }
    [Fact]
    public void ConfigMigrationPreservesMuteAndIndependentTrades()
    {
        var node = JsonSerializer.SerializeToNode(new AppConfig(), AppConfig.Json)!;
        node["volume"] = "";
        node["alertMode"] = "trades";
        node.AsObject().Remove("detectTrades");
        var config = AppConfig.Parse(node.ToJsonString());
        Assert.Equal(.5, config.Volume);
        Assert.True(config.DetectTrades);
        node["volume"] = 0;
        node["detectTrades"] = false;
        config = AppConfig.Parse(node.ToJsonString());
        Assert.Equal(0, config.Volume);
        Assert.False(config.DetectTrades);
    }
    [Fact]
    public void MediaFoldersAreIndependentAndEncodeNames()
    {
        var media = MediaCatalog.Read(root);
        Assert.All(media.Values, m => Assert.Empty(m.Images));
        File.WriteAllText(Path.Combine(root, "source/sounds/kill/voice #1.mp3"), "data");
        File.WriteAllText(Path.Combine(root, "source/images/death/skull.png"), "data");
        media = MediaCatalog.Read(root);
        Assert.EndsWith("voice%20%231.mp3", Assert.Single(media["kill"].Sounds));
        Assert.Empty(media["death"].Sounds);
        Assert.Single(media["death"].Images);
    }
    [Fact]
    public void ExportIgnoresOnlyIncompleteLastLine()
    {
        var source = Path.Combine(root, "events.jsonl");
        File.WriteAllText(source, "{\"at\":\"now\",\"message\":\"complete\"}\n{\"unfinished\":");
        var output = Journal.Export(source);
        Assert.Equal("[now] complete", File.ReadAllText(output).Trim());
        Assert.Empty(Directory.GetFiles(root, "*.tmp*"));
        File.WriteAllText(source, "bad\n");
        Assert.ThrowsAny<JsonException>(() => Journal.Export(source));
        Assert.Equal("[now] complete", File.ReadAllText(output).Trim());
    }
}
