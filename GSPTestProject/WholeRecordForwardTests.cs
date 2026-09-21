using GenericSynthesisPatcher;
using GenericSynthesisPatcher.Helpers;
using GenericSynthesisPatcher.Rules;
using GSPTestProject.GameData.GlobalGame.Fixtures;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Newtonsoft.Json;
using NSubstitute;
using Xunit.Abstractions;
using Global = GenericSynthesisPatcher.Global;

namespace GSPTestProject;

[Collection("Sequential")]
public sealed class WholeRecordForwardTests(SkyrimSEFixture fixture, ITestOutputHelper output) : IClassFixture<SkyrimSEFixture>
{
    private static GSPRule Rule(string forward = "\"All\": []", string options = "")
        => Assert.IsType<GSPRule>(JsonConvert.DeserializeObject<GSPBase>(
            "{\"RecordTypes\":\"Armor\",\"Forward\":{" + forward + "}" + options + "}", Global.Game.SerializerSettings));

    [Theory]
    [InlineData("")]
    [InlineData(",\"ForwardOptions\":[\"Sort\"]")]
    [InlineData(",\"ForwardOptions\":[\"Default\",\"IndexedByField\"]")]
    public void WinningOverlayCopiesCompletelyAndResetsEarlierChanges(string options)
        => WithState((keys, patch) =>
        {
            var rule = Rule(options: options);
            Assert.True(rule.Validate());
            Assert.True(RulePreflightValidator.Validate([rule]));
            keys.SetRule(rule);
            Assert.Equal(1, rule.RunActions(keys));
            var result = Assert.Single(patch.Armors);
            Assert.Equal("Winner", result.EditorID);
            Assert.Null(result.WorldModel!.Female);
            Assert.Equal("winner.nif", result.WorldModel.Male!.Model!.File.GivenPath);
            result.EditorID = "earlier edit";
            result.WorldModel.Female = new ArmorModel { Model = new Model { File = "old.nif" } };
            result.Armature.Add(new FormLink<IArmorAddonGetter>(new FormKey(ModKey.FromNameAndExtension("base.esm"), 9)));
            Assert.Equal(1, rule.RunActions(keys));
            Assert.Same(result, keys.GetPatchRecord());
            Assert.Equal("Winner", result.EditorID);
            Assert.Null(result.WorldModel.Female);
            Assert.Empty(result.Armature);
            result.WorldModel.Male.Model.File = "mutated.nif";
            Assert.Equal("winner.nif", ((IArmorGetter)keys.Context.Record).WorldModel!.Male!.Model!.File.GivenPath);
            Assert.Equal(0, options.Length == 0 ? (int)rule.ForwardOptions : 0);
        });

    [Theory]
    [InlineData("[]", "Winner")]
    [InlineData("[\"base.esm\",\"winner.esp\"]", "Winner")]
    [InlineData("[\"!winner.esp\"]", "Origin")]
    [InlineData("[\"disabled.esp\",\"missing.esp\"]", null)]
    public void SourcesUseEnabledLoadOrder(string sources, string? editorId)
        => WithState((keys, patch) =>
        {
            var rule = Rule("\"aLl\":" + sources);
            keys.SetRule(rule);
            Assert.Equal(editorId is null ? -1 : 1, rule.RunActions(keys));
            if (editorId is null) Assert.Empty(patch.Armors);
            else Assert.Equal(editorId, Assert.Single(patch.Armors).EditorID);
        });

    [Fact]
    public void MasterOnlyRecordStillCreatesOverride()
        => WithState((keys, patch) =>
        {
            var rule = Rule();
            keys.SetRule(rule);
            Assert.Equal(1, rule.RunActions(keys));
            Assert.Equal("Origin", Assert.Single(patch.Armors).EditorID);
        }, originOnly: true);

    [Fact]
    public void DeletedWinnersStayOutsideRecordScan()
        => WithState((_, _) => Assert.Empty(Global.Game.GetRecords(IArmorGetter.StaticRegistration)), deletedWinner: true);

    [Fact]
    public void GroupFiltersStillPreventCopies()
        => WithState((keys, patch) =>
        {
            var group = Assert.IsType<GSPGroup>(JsonConvert.DeserializeObject<GSPBase>("""
                { "RecordTypes": "Armor", "rules": [
                  { "EditorID": "NeverMatches", "Forward": { "All": [] } }
                ] }
                """, Global.Game.SerializerSettings));
            Assert.True(group.Validate());
            keys.SetRule(group);
            Assert.Equal(-1, group.RunActions(keys));
            Assert.Empty(patch.Armors);
        });

    [Fact]
    public void ContainerRecordsDoNotCopyUnrelatedChildren()
        => WithState((_, patch) =>
        {
            var context = Global.Game.GetRecords(ICellGetter.StaticRegistration).First();
            var keys = new ProcessingKeys(context);
            var rule = Rule();
            rule.Types.Clear();
            rule.Types.Add(ICellGetter.StaticRegistration);
            Assert.True(RulePreflightValidator.Validate([rule]));
            keys.SetRule(rule);
            Assert.Equal(1, rule.RunActions(keys));
            var result = Assert.IsType<Cell>(keys.GetPatchRecord());
            Assert.Empty(result.Persistent);
            Assert.Equal("CellWinner", result.EditorID);
        });

    [Fact]
    public void NestedOverrideKeepsParentPlacementWithoutSiblings()
        => WithState((_, patch) =>
        {
            var context = Global.Game.GetRecords(IPlacedObjectGetter.StaticRegistration).First(x => x.Record.FormKey.ID == 0x802);
            var keys = new ProcessingKeys(context);
            var rule = Rule();
            rule.Types.Clear();
            rule.Types.Add(IPlacedObjectGetter.StaticRegistration);
            keys.SetRule(rule);
            Assert.Equal(1, rule.RunActions(keys));
            var cell = Assert.Single(Assert.Single(Assert.Single(patch.Cells).SubBlocks).Cells);
            Assert.Equal(0x802u, Assert.Single(cell.Persistent).FormKey.ID);
        });

    [Fact]
    public void GroupRunsWholeCopyBetweenEarlierAndLaterEdits()
        => WithState((keys, patch) =>
        {
            var group = Assert.IsType<GSPGroup>(JsonConvert.DeserializeObject<GSPBase>("""
                { "RecordTypes": "Armor", "rules": [
                  { "Fill": { "Value": 999 } },
                  { "Forward": { "All": [] }, "Fill": { "Weight": 3 } }
                ] }
                """, Global.Game.SerializerSettings));
            Assert.True(group.Validate());
            Assert.True(RulePreflightValidator.Validate([group]));
            keys.SetRule(group);
            Assert.True(group.RunActions(keys) > 0);
            var result = Assert.Single(patch.Armors);
            Assert.Equal(0u, result.Value);
            Assert.Equal(3f, result.Weight);
        });

    [Fact]
    public void WholeCopySupportsMutableRecordsInEveryGame()
    {
        var key = new FormKey(ModKey.FromNameAndExtension("base.esm"), 0x800);
        IMajorRecord[] sources = [
            new Armor(key, SkyrimRelease.SkyrimSE),
            new Mutagen.Bethesda.Fallout4.Armor(key, Mutagen.Bethesda.Fallout4.Fallout4Release.Fallout4),
            new Mutagen.Bethesda.Oblivion.Armor(key, Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion)
        ];
        IMajorRecord[] targets = [
            new Armor(key, SkyrimRelease.SkyrimSE),
            new Mutagen.Bethesda.Fallout4.Armor(key, Mutagen.Bethesda.Fallout4.Fallout4Release.Fallout4),
            new Mutagen.Bethesda.Oblivion.Armor(key, Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion)
        ];
        for (int i = 0; i < sources.Length; i++)
        {
            sources[i].EditorID = "Source";
            sources[i].MajorRecordFlagsRaw = 0x20;
            targets[i].EditorID = "Old";
            WholeRecordForward.Copy(sources[i], targets[i], WholeRecordForward.GetMask(sources[i].Registration)!);
            Assert.Equal("Source", targets[i].EditorID);
            Assert.Equal(0x20, targets[i].MajorRecordFlagsRaw);
            Assert.Equal(key, targets[i].FormKey);
            sources[i].EditorID = null;
            WholeRecordForward.Copy(sources[i], targets[i], WholeRecordForward.GetMask(sources[i].Registration)!);
            Assert.Null(targets[i].EditorID);
        }
    }

    [Fact]
    public void WholeCopyDoesNotSwallowSourceErrors()
    {
        var source = Substitute.For<IArmorGetter>();
        source.EditorID.Returns(_ => throw new InvalidDataException("Original source failure"));
        source.Registration.Returns(IArmorGetter.StaticRegistration);
        var target = new Armor(new FormKey(ModKey.FromNameAndExtension("base.esm"), 0x800), SkyrimRelease.SkyrimSE);
        var error = Assert.ThrowsAny<Exception>(() => WholeRecordForward.Copy(source, target,
            WholeRecordForward.GetMask(IArmorGetter.StaticRegistration)!));
        Assert.Contains("Original source failure", error.ToString());
    }

    [Theory]
    [InlineData("\"All\":[],\"Value\":[]", "")]
    [InlineData("\"All\":[],\"all\":[]", "")]
    [InlineData("\"|All\":[]", "")]
    [InlineData("\"&All\":[]", "")]
    [InlineData("\"All\":null", "")]
    [InlineData("\"All\":[12]", "")]
    [InlineData("\"All\":[\"!winner.esp\",\"base.esm\"]", "")]
    [InlineData("\"All\":[\"bad name\"]", "")]
    [InlineData("\"All\":[]", ",\"OnlyIfDefault\":true")]
    [InlineData("\"All\":[]", ",\"ForwardOptions\":[\"Merge\"]")]
    [InlineData("\"All\":[]", ",\"ForwardOptions\":[\"HPU\"]")]
    [InlineData("\"All\":[]", ",\"ForwardOptions\":[\"Random\"]")]
    [InlineData("\"All\":[]", ",\"ForwardOptions\":[\"NonNull\"]")]
    [InlineData("\"All\":[]", ",\"ForwardOptions\":[\"NonDefault\"]")]
    [InlineData("\"All\":[]", ",\"ForwardOptions\":[\"SelfMasterOnly\"]")]
    public void InvalidWholeRecordConfigurationsFailPreflight(string forward, string options)
    {
        _ = fixture;
        Global.Logger.Out = new Helpers.TestOutputTextWritter(output);
        Assert.False(RulePreflightValidator.Validate([Rule(forward, options)]));
    }

    [Fact]
    public void EverySupportedRegistrationHasCompleteMask()
    {
        foreach (object[] entry in new GameData.Stateless.AllGames())
        foreach (var registration in ((GameData.Game)entry[0]).BaseGame.AllRecordTypes())
        {
            Assert.NotNull(WholeRecordForward.GetMask(registration));
        }
    }

    [Fact]
    public void LegacyDefaultIsStillModIndexed()
    {
        var rule = Rule("\"winner.esp\":[\"Value\"]");
        Assert.Equal((ForwardOptions)0, rule.ForwardOptions);
        Assert.True(RulePreflightValidator.Validate([rule]));
    }

    private void WithState(Action<ProcessingKeys, ISkyrimMod> test, bool originOnly = false, bool deletedWinner = false)
    {
        _ = fixture;
        var previous = Global.Game.State;
        var settings = Global.Settings;
        var env = Synthesis.Bethesda.UnitTests.Common.Utility.SetupEnvironment(Mutagen.Bethesda.GameRelease.SkyrimSE);
        var key = new FormKey(ModKey.FromNameAndExtension("base.esm"), 0x800);
        foreach (string name in new[] { "base.esm", "winner.esp", "disabled.esp" })
        {
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(name), SkyrimRelease.SkyrimSE);
            mod.Armors.Add(new Armor(key, SkyrimRelease.SkyrimSE)
            {
                EditorID = name == "base.esm" ? "Origin" : "Winner",
                IsDeleted = deletedWinner && name == "winner.esp",
                WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel { Model = new Model { File = "winner.nif" } }, null),
            });
            var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
            var subBlock = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
            var cell = new Cell(new FormKey(key.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "CellWinner" };
            cell.Persistent.Add(new PlacedObject(new FormKey(key.ModKey, 0x802), SkyrimRelease.SkyrimSE));
            cell.Persistent.Add(new PlacedObject(new FormKey(key.ModKey, 0x803), SkyrimRelease.SkyrimSE));
            subBlock.Cells.Add(cell);
            block.SubBlocks.Add(subBlock);
            mod.Cells.Add(block);
            using var stream = new MemoryStream();
            mod.WriteToBinary(stream);
            env.FileSystem.File.WriteAllBytes(Path.Combine(env.DataFolder, name), stream.ToArray());
        }
        env.FileSystem.File.WriteAllText(env.PluginPath, originOnly ? "*base.esm\ndisabled.esp\n" : "*base.esm\n*winner.esp\ndisabled.esp\n");
        using var state = env.GetStateFactory().ToState<ISkyrimMod, ISkyrimModGetter>(
            new Mutagen.Bethesda.Synthesis.CLI.RunSynthesisMutagenPatcher
            {
                DataFolderPath = env.DataFolder, GameRelease = Mutagen.Bethesda.GameRelease.SkyrimSE,
                OutputPath = Path.Combine(env.DataFolder, "TestPatch.esp"), LoadOrderFilePath = env.PluginPath,
            }, new Mutagen.Bethesda.Synthesis.PatcherPreferences(), ModKey.FromNameAndExtension("TestPatch.esp"));
        try
        {
            Global.Initialize(state, new GSPSettings());
            Global.Logger.Out = new Helpers.TestOutputTextWritter(output);
            var context = state.LinkCache.ResolveAllContexts<ISkyrimMajorRecord, ISkyrimMajorRecordGetter>(key).First();
            test(new ProcessingKeys(context), state.PatchMod);
        }
        finally { Global.Initialize(previous, settings); }
    }
}
