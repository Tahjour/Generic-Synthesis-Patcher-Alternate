using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GenericSynthesisPatcher;
using GenericSynthesisPatcher.Helpers;
using GenericSynthesisPatcher.Helpers.Graph;
using GenericSynthesisPatcher.Rules;
using GSPTestProject.GameData.GlobalGame.Fixtures;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Newtonsoft.Json;
using Xunit.Abstractions;
using Global = GenericSynthesisPatcher.Global;

namespace GSPTestProject;

[Collection("Sequential")]
public class OptimizationTests(SkyrimSEFixture fixture, ITestOutputHelper output) : IClassFixture<SkyrimSEFixture>
{
    [Flags] private enum Signed8 : sbyte { }
    [Flags] private enum Signed16 : short { }
    [Flags] private enum Signed32 : int { }
    [Flags] private enum Signed64 : long { }
    [Flags] private enum Unsigned8 : byte { }
    [Flags] private enum Unsigned16 : ushort { }
    [Flags] private enum Unsigned32 : uint { }
    [Flags] private enum Unsigned64 : ulong { }

    [Theory]
    [InlineData(typeof(Signed8), 0x80ul)]
    [InlineData(typeof(Signed16), 0x8000ul)]
    [InlineData(typeof(Signed32), 0x80000000ul)]
    [InlineData(typeof(Signed64), 0x8000000000000000ul)]
    [InlineData(typeof(Unsigned8), 0x80ul)]
    [InlineData(typeof(Unsigned16), 0x8000ul)]
    [InlineData(typeof(Unsigned32), 0x80000000ul)]
    [InlineData(typeof(Unsigned64), 0x8000000000000000ul)]
    public void AdditiveFlagsPreserveWidthsUnnamedBitsAndNull(Type type, ulong highBit)
    {
        object high = Enum.ToObject(type, highBit), one = Enum.ToObject(type, 1), zero = Enum.ToObject(type, 0);
        Assert.Equal(Enum.ToObject(type, highBit | 1), PropertyPathEngine.CombineFlags(type, high, [null, one, one, zero]));
        Assert.Null(PropertyPathEngine.CombineFlags(typeof(Nullable<>).MakeGenericType(type), null, [null]));
        Assert.Equal(zero, PropertyPathEngine.CombineFlags(type, null, [zero]));
        Assert.Equal(high, PropertyPathEngine.CombineFlags(type, high, []));
    }

    [Fact]
    public void IndexedSelectionKeepsDuplicatesAndFirstRanks()
    {
        var a = ModKey.FromNameAndExtension("A.esp");
        var b = ModKey.FromNameAndExtension("B.esp");
        var selection = new SourceSelection([b, a, b]);
        Assert.Equal(new[] { b, a, b }, selection);
        Assert.Equal(0, selection.Rank(b));
        Assert.Equal(1, selection.Rank(a));
        Assert.False(selection.Contains(ModKey.Null));
    }

    private static GSPRule FlagsRule(string sources, bool onlyDefault = false) => Assert.IsType<GSPRule>(
        JsonConvert.DeserializeObject<GSPBase>("{\"RecordTypes\":[\"Worldspace\",\"EncounterZone\"],\"ForwardOptions\":\"Merge\",\"OnlyIfDefault\":"
            + (onlyDefault ? "true" : "false") + ",\"Forward\":{\"Flags\":" + sources + "}}", Global.Game.SerializerSettings));

    [Fact]
    public void SuppliedWorldspaceConfigurationLoadsAndRunsUnchanged()
        => WithWorldState(keys =>
        {
            using var stream = typeof(OptimizationTests).Assembly.GetManifestResourceStream("GSPTestProject.Files.WorldspaceMergeHpu.json")!;
            using var reader = new StreamReader(stream);
            var rules = JsonConvert.DeserializeObject<List<GSPBase>>(reader.ReadToEnd(), Global.Game.SerializerSettings)!;
            Assert.All(rules, rule => Assert.True(rule.Validate()));
            Assert.True(RulePreflightValidator.Validate(rules));
            foreach (var key in keys)
            {
                key.SetRule(rules[0]);
                rules[0].RunActions(key);
                Assert.Equal(7, Convert.ToInt32(key.Record is IWorldspaceGetter world ? (object)world.Flags : ((IEncounterZoneGetter)key.Record).Flags));
            }
        });

    [Theory]
    [InlineData("Quest", "Type", "Merge")]
    [InlineData("Quest", "Aliases.Flags", "Merge")]
    [InlineData("Armor", "WorldModel", "Merge")]
    [InlineData("Worldspace", "Flags", "Merge,HPU")]
    [InlineData("Worldspace", "Flags", "Merge,Random")]
    [InlineData("Worldspace", "Flags", "Merge,SelfMasterOnly")]
    [InlineData("Worldspace", "Flags", "Merge,DefaultThenSelfMasterOnly")]
    public void UnsupportedSelectedMergePreflightsWithoutCreatingRecords(string type, string path, string options)
        => WithWorldState(keys =>
        {
            var rule = JsonConvert.DeserializeObject<GSPBase>(
                "{\"RecordTypes\":\"" + type + "\",\"ForwardOptions\":[\"" + options.Replace(",", "\",\"")
                + "\"],\"Forward\":{\"" + path + "\":[]}}", Global.Game.SerializerSettings)!;
            Assert.False(RulePreflightValidator.Validate([rule]));
            Assert.All(keys, key => Assert.False(key.HasPatchRecord));
        });

    private sealed class SourceProbe : GSPRule
    {
        internal IEnumerable<IModContext<IMajorRecordGetter>> Select(ProcessingKeys keys, ModKey[] sources)
            => getAvailableMods(keys, sources, keys.Record.FormKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomEnumerationAndNumberConsumptionRemainUncached(bool sort)
        => WithWorldState(keys =>
        {
            var rule = new SourceProbe { ForwardOptions = ForwardOptions.Random | (sort ? ForwardOptions.Sort : 0) };
            var actual = keys[0];
            var expected = new ProcessingKeys(actual.Context);
            foreach (var key in new[] { actual, expected })
            {
                key.SetRule(rule);
                Assert.True(key.SetProperty(new("Flags"), "Flags", -1));
            }
            var sources = Global.Game.LoadOrder.ListedOrder.Select(x => x.ModKey).ToArray();
            var selected = rule.Select(actual, sources);
            var history = Global.Game.State.LinkCache.ResolveAllSimpleContexts(expected.Record.FormKey, expected.Type.GetterType)
                .Where(x => sources.Contains(x.ModKey));
            var legacy = sort ? history.OrderBy(x => Array.IndexOf(sources, x.ModKey)) : history.OrderBy(_ => expected.GetRandom().Next());
            for (int i = 0; i < 3; i++)
                Assert.Equal(legacy.Select(x => x.ModKey).ToArray(), selected.Select(x => x.ModKey).ToArray());
            Assert.Equal(expected.GetRandom().Next(), actual.GetRandom().Next());
            Assert.Equal(0, actual.RecordState.HistoryResolutions);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedEndNodesMatchFreshTopologyIncludingDynamicAndSeedRecords(bool dynamic)
        => WithWorldState(keys =>
        {
            if (dynamic) Global.Settings.DynamicMods.Add(ModKey.FromNameAndExtension("Winner.esp"));
            var seeded = (Worldspace)keys[0].GetPatchRecord();
            var root = new ProcessingKeys(keys[0].Context);
            root.SetRule(FlagsRule("[]"));
            Assert.Contains(root.RecordContexts, x => ReferenceEquals(x.Record, seeded));
            root.GetPatchRecord();
            Assert.Equal(0, root.RecordState.Generation);
            ModKey[][] sourceSets = [[], [ModKey.FromNameAndExtension("Lux.esp")],
                [ModKey.FromNameAndExtension("Winner.esp"), ModKey.FromNameAndExtension("Base.esm")],
                Global.Game.State.LinkCache.ListedOrder.Select(x => x.ModKey).ToArray()];
            foreach (var set in sourceSets)
            {
                var fresh = ForwardRecordGraph.Create(root)?.GetEndNodes(set)?.ToArray();
                var cached = root.RecordState.GetEndNodes(root, set);
                Assert.Equal(fresh, cached);
                Assert.Same(cached, root.RecordState.GetEndNodes(root, set.Reverse()));
            }
            Assert.Equal(1, root.RecordState.HistoryResolutions);
            Assert.Equal(1, root.RecordState.GraphBuilds);
            Assert.Equal(4, root.RecordState.EndNodeBuilds);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReinitializedRunsRefreshRanksAndRuleSelections(bool wholeRecord)
    {
        GSPRule? rule = null;
        RunSourceIndex? previousIndex = null;
        foreach (bool reverse in new[] { false, true })
            WithWorldState(keys =>
            {
                var index = Global.Game.Sources;
                Assert.NotSame(previousIndex, index);
                previousIndex = index;
                var expectedWinner = ModKey.FromNameAndExtension(reverse ? "Lux.esp" : "Winner.esp");
                Assert.Equal(3, index.Rank(expectedWinner));
                Assert.Equal(Global.Game.State.LinkCache.ListedOrder.Select(x => x.ModKey).ToList().IndexOf(expectedWinner), index.LinkRank(expectedWinner));
                rule ??= Assert.IsType<GSPRule>(JsonConvert.DeserializeObject<GSPBase>(wholeRecord
                    ? "{\"RecordTypes\":\"Worldspace\",\"Forward\":{\"All\":[]}}"
                    : "{\"RecordTypes\":\"Worldspace\",\"ForwardOptions\":[\"Sort\",\"IndexedByField\"],\"Forward\":{\"Flags\":[\"Lux.esp\",\"Winner.esp\"]}}",
                    Global.Game.SerializerSettings));
                var key = keys[0];
                ((Worldspace)key.GetPatchRecord()).Flags = (Worldspace.Flag)128;
                key.SetRule(rule);
                rule.RunActions(key);
                Assert.Equal((Worldspace.Flag)(reverse ? 1 : 4), ((IWorldspaceGetter)key.Record).Flags);
            }, reverse);
    }

    [Fact]
    public void HpuRecomputesFieldHistoryAfterPatchedValueReverts()
        => WithWorldState(keys =>
        {
            var key = keys[0];
            key.SetRule(new GSPRule { ForwardOptions = ForwardOptions.Sort | ForwardOptions.HPU });
            Assert.True(key.SetProperty(new("Flags"), "Flags", -1));
            var patch = (Worldspace)key.GetPatchRecord();
            patch.Flags = (Worldspace.Flag)128;
            var sources = Global.Game.State.LinkCache.ListedOrder.Select(x => x.ModKey).ToArray();
            var endNodes = key.RecordState.GetEndNodes(key, sources);
            Assert.Same(patch, PropertyPathEngine.FindHPU(key, key.RecordContexts, endNodes)!.Record);
            patch.Flags = 0; // A reversion must change HPU selection without rebuilding topology.
            Assert.Same(endNodes, key.RecordState.GetEndNodes(key, sources));
            Assert.Equal(ModKey.FromNameAndExtension("Lux Orbis.esp"), PropertyPathEngine.FindHPU(key, key.RecordContexts, endNodes)!.ModKey);
            Assert.Equal(1, key.RecordState.GraphBuilds);
        });

    [Theory]
    [InlineData("[\"Lux.esp\",\"Lux Orbis.esp\"]", 7)]
    [InlineData("[\"!Lux.esp\"]", 6)]
    [InlineData("[\"missing.esp\",\"disabled.esp\"]", 4)]
    [InlineData("[\"Winner.esp\",\"Lux.esp\",\"Lux.esp\"]", 5)]
    [InlineData("[]", 7)]
    public void SelectedFlagsUseCurrentWinnerAndEligibleSources(string sources, int expected)
        => WithWorldState(keys =>
        {
            var rule = FlagsRule(sources);
            Assert.True(rule.Validate());
            Assert.True(RulePreflightValidator.Validate([rule]));
            foreach (var key in keys)
            {
                key.SetRule(rule);
                rule.RunActions(key);
                Assert.Equal(expected, Convert.ToInt32(key.Record is IWorldspaceGetter world ? (object)world.Flags : ((IEncounterZoneGetter)key.Record).Flags));
                Assert.Equal(expected != 4, key.HasPatchRecord);
                int updates = Program.RecordUpdates.Count;
                rule.RunActions(key);
                Assert.Equal(updates, Program.RecordUpdates.Count);
            }
            Assert.Equal(expected == 4 ? 0 : 2, Program.RecordUpdates.Count);
        });

    [Fact]
    public void FlagsPreservePriorPatchEditsAndRespectOnlyIfDefault()
        => WithWorldState(keys =>
        {
            var key = keys[0];
            var patch = (Worldspace)key.GetPatchRecord();
            patch.Flags = (Worldspace.Flag)8;
            var rule = FlagsRule("[\"Lux.esp\",\"Lux Orbis.esp\"]");
            key.SetRule(rule);
            Assert.Equal(1, rule.RunActions(key));
            Assert.Equal((Worldspace.Flag)11, patch.Flags);
            patch.Flags = (Worldspace.Flag)8;
            rule = FlagsRule("[\"Lux Orbis.esp\"]", true);
            key.SetRule(rule);
            rule.RunActions(key);
            Assert.Equal((Worldspace.Flag)8, patch.Flags);
            patch.Flags = (Worldspace.Flag)0; // Origin value, despite a different winning value.
            rule.RunActions(key);
            Assert.Equal((Worldspace.Flag)2, patch.Flags);
        });

    [Fact]
    public void SelectedFlagsPreserveUndiscoveredSeedOverride()
        => WithWorldState(keys =>
        {
            var seed = (Worldspace)keys[0].GetPatchRecord();
            seed.Flags = (Worldspace.Flag)8;
            var key = new ProcessingKeys(keys[0].Context);
            var rule = FlagsRule("[\"Lux.esp\",\"Lux Orbis.esp\"]");
            key.SetRule(rule);
            Assert.False(key.HasPatchRecord);
            Assert.Equal(1, rule.RunActions(key));
            Assert.Equal((Worldspace.Flag)11, seed.Flags);
            key = new ProcessingKeys(keys[0].Context);
            key.SetRule(rule);
            Assert.Equal(0, rule.RunActions(key));
            Assert.False(key.HasPatchRecord);
            seed.Flags = 0;
            key.SetRule(FlagsRule("[\"Lux.esp\"]", true));
            Assert.Equal(1, key.Rule.RunActions(key));
            Assert.Equal((Worldspace.Flag)1, seed.Flags);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MasterOnlySelectedFlagsRemainLazyAndPreserveSeed(bool seed)
        => WithWorldState(keys =>
        {
            if (seed) ((Worldspace)keys[0].GetPatchRecord()).Flags = (Worldspace.Flag)2;
            var key = new ProcessingKeys(keys[0].Context);
            key.SetRule(FlagsRule("[\"Base.esm\"]"));
            Assert.Equal(seed ? 1 : 0, key.Rule.RunActions(key));
            Assert.Equal((Worldspace.Flag)(seed ? 3 : 1), ((IWorldspaceGetter)key.Record).Flags);
            Assert.Equal(seed, key.HasPatchRecord);
        }, originOnly: true);

    [Fact]
    public void GroupChildrenShareHistoryButObservePatchEditsAndInvalidateTopology()
        => WithWorldState(keys =>
        {
            var root = keys[0];
            var rule = FlagsRule("[]");
            root.SetRule(rule);
            var all = new SourceSelection(Global.Game.LoadOrder.ListedOrder.Select(x => x.ModKey));
            var state = root.RecordState;
            var first = state.GetEndNodes(root, all);
            Assert.Same(first, state.GetEndNodes(root, all.Reverse()));
            Assert.Equal(1, state.HistoryResolutions);
            Assert.Equal(1, state.GraphBuilds);
            Assert.Equal(1, state.EndNodeBuilds);
            var child = new ProcessingKeys(root.Context, root);
            child.SetRule(rule);
            Assert.Same(state, child.RecordState);
            var patch = (Worldspace)child.GetPatchRecord();
            Assert.Equal(1, state.Generation);
            state.GetEndNodes(child, all);
            Assert.Equal(2, state.HistoryResolutions);
            Assert.Equal(2, state.GraphBuilds);
            var captured = child.RecordContexts.Single(x => x.ModKey == Global.Game.State.PatchMod.ModKey);
            patch.Flags = (Worldspace.Flag)32;
            Assert.Equal((Worldspace.Flag)32, ((IWorldspaceGetter)captured.Record).Flags);
            child.GetPatchRecord();
            Assert.Equal(1, state.Generation);
            state.GetEndNodes(child, [ModKey.FromNameAndExtension("Lux.esp")]);
            Assert.Equal(3, state.EndNodeBuilds);
            Assert.Equal(2, state.GraphBuilds);
        });

    private void WithWorldState(Action<ProcessingKeys[]> test, bool reverse = false, bool originOnly = false)
    {
        _ = fixture;
        var previous = Global.Game.State;
        var settings = Global.Settings;
        var env = Synthesis.Bethesda.UnitTests.Common.Utility.SetupEnvironment(GameRelease.SkyrimSE);
        var origin = ModKey.FromNameAndExtension("Base.esm");
        string[] names = ["Base.esm", "Lux.esp", "Lux Orbis.esp", "Winner.esp", "disabled.esp"];
        for (int i = 0; i < names.Length; i++)
        {
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(names[i]), SkyrimRelease.SkyrimSE);
            int flags = i == 0 ? (originOnly ? 1 : 0) : 1 << (i - 1);
            mod.Worldspaces.Add(new Worldspace(new FormKey(origin, 0x800), SkyrimRelease.SkyrimSE) { Flags = (Worldspace.Flag)flags });
            mod.EncounterZones.Add(new EncounterZone(new FormKey(origin, 0x801), SkyrimRelease.SkyrimSE) { Flags = (EncounterZone.Flag)flags });
            using var stream = new MemoryStream();
            mod.WriteToBinary(stream);
            env.FileSystem.File.WriteAllBytes(Path.Combine(env.DataFolder, names[i]), stream.ToArray());
        }
        var orderedNames = reverse ? new[] { "Base.esm", "Winner.esp", "Lux Orbis.esp", "Lux.esp", "disabled.esp" } : names;
        env.FileSystem.File.WriteAllText(env.PluginPath, originOnly ? "*Base.esm" : string.Join('\n', orderedNames.Select(x => x == "disabled.esp" ? x : "*" + x)));
        using var state = env.GetStateFactory().ToState<ISkyrimMod, ISkyrimModGetter>(
            new Mutagen.Bethesda.Synthesis.CLI.RunSynthesisMutagenPatcher
            {
                DataFolderPath = env.DataFolder, GameRelease = GameRelease.SkyrimSE,
                OutputPath = Path.Combine(env.DataFolder, "TestPatch.esp"), LoadOrderFilePath = env.PluginPath,
            }, new Mutagen.Bethesda.Synthesis.PatcherPreferences(), ModKey.FromNameAndExtension("TestPatch.esp"));
        try
        {
            Global.Initialize(state, new GSPSettings());
            Global.Logger.Out = new Helpers.TestOutputTextWritter(output);
            Program.RecordUpdates.Clear();
            test(new[] { IWorldspaceGetter.StaticRegistration, IEncounterZoneGetter.StaticRegistration }
                .SelectMany(r => Global.Game.GetRecords(r)).Select(c => new ProcessingKeys(c)).ToArray());
        }
        finally { Program.RecordUpdates.Clear(); Global.Initialize(previous, settings); }
    }

    [Fact]
    public void CellWorkloadBaselineAndBenchmark()
    {
        _ = fixture;
        int plugins = int.TryParse(Environment.GetEnvironmentVariable("GSP_BENCHMARK_PLUGINS"), out int count) ? count : 16;
        var previous = Global.Game.State;
        var settings = Global.Settings;
        var env = Synthesis.Bethesda.UnitTests.Common.Utility.SetupEnvironment(GameRelease.SkyrimSE);
        var origin = ModKey.FromNameAndExtension("Base.esm");
        var names = new List<string>();
        for (int i = 0; i < plugins; i++)
        {
            var name = i == 0 ? "Base.esm" : $"Plugin{i:D4}.esp";
            names.Add("*" + name);
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(name), SkyrimRelease.SkyrimSE);
            if (i == 0 || i >= plugins - 5)
            {
                int version = i == 0 ? 0 : i - plugins + 6;
                var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
                var subBlock = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
                for (uint id = 0x800; id < 0x810; id++)
                    subBlock.Cells.Add(new Cell(new FormKey(origin, id), SkyrimRelease.SkyrimSE)
                    {
                        EditorID = "Cell" + id + "V" + (version % 3),
                        WaterHeight = version % 3,
                        Flags = (Cell.Flag)(version % 2 == 0 ? 1 : 3),
                    });
                block.SubBlocks.Add(subBlock);
                mod.Cells.Add(block);
            }
            using var stream = new MemoryStream();
            mod.WriteToBinary(stream);
            env.FileSystem.File.WriteAllBytes(Path.Combine(env.DataFolder, name), stream.ToArray());
        }
        env.FileSystem.File.WriteAllText(env.PluginPath, string.Join('\n', names));
        using var state = env.GetStateFactory().ToState<ISkyrimMod, ISkyrimModGetter>(
            new Mutagen.Bethesda.Synthesis.CLI.RunSynthesisMutagenPatcher
            {
                DataFolderPath = env.DataFolder, GameRelease = GameRelease.SkyrimSE,
                OutputPath = Path.Combine(env.DataFolder, "TestPatch.esp"), LoadOrderFilePath = env.PluginPath,
            }, new Mutagen.Bethesda.Synthesis.PatcherPreferences(), ModKey.FromNameAndExtension("TestPatch.esp"));
        try
        {
            Global.Initialize(state, new GSPSettings());
            Global.Logger.Out = TextWriter.Null;
            string? expected = null;
            for (int sample = 0; sample < 6; sample++)
            {
                state.PatchMod.Cells.Clear();
                Program.RecordUpdates.Clear();
                var rule = Assert.IsType<GSPRule>(JsonConvert.DeserializeObject<GSPBase>("""
                    { "RecordTypes":"Cell", "Merge":["Flags","Regions"], "ForwardOptions":["Sort","HPU"],
                      "Forward":{"MaxHeightData":[],"WaterHeight":[],"EditorID":[],"Grid":[],"Water":[],
                      "Location":[],"EncounterZone":[],"Music":[],"SkyAndWeatherFromRegion":[]} }
                    """, Global.Game.SerializerSettings));
                Assert.True(RulePreflightValidator.Validate([rule]));
                var records = Global.Game.GetRecords(ICellGetter.StaticRegistration).ToArray();
                int histories = 0, topologies = 0, endNodeBuilds = 0;
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                foreach (var context in records)
                {
                    var keys = new ProcessingKeys(context);
                    keys.SetRule(rule);
                    rule.RunActions(keys);
                    histories += keys.RecordState.HistoryResolutions;
                    topologies += keys.RecordState.GraphBuilds;
                    endNodeBuilds += keys.RecordState.EndNodeBuilds;
                }
                timer.Stop();
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert.Equal(32, histories);
                Assert.Equal(16, topologies);
                Assert.Equal(16, endNodeBuilds);
                var normalized = state.PatchMod.Cells.SelectMany(b => b.SubBlocks).SelectMany(b => b.Cells)
                    .OrderBy(c => c.FormKey.ID).Select(c => PropertyPathEngine.Canonicalize(c));
                var updates = Program.RecordUpdates.Select(x => $"{x.FormKey}|{x.Property}|{x.Changes}");
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', normalized.Concat(updates)))));
                expected ??= hash;
                Assert.Equal(expected, hash);
                Assert.Equal("B47836E643CA969B0287A1C4482FD87A58DD6BC143BAA34B91C430A9EF59D816", hash);
                output.WriteLine($"plugins={plugins} sample={sample} ms={timer.Elapsed.TotalMilliseconds:F3} bytes={allocated} histories={histories} topologies={topologies} endNodes={endNodeBuilds} updates={Program.RecordUpdates.Count} sha256={hash}");
            }
        }
        finally { Program.RecordUpdates.Clear(); Global.Initialize(previous, settings); }
    }
}
