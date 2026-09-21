using GenericSynthesisPatcher;
using GenericSynthesisPatcher.Exceptions;
using GenericSynthesisPatcher.Games.Universal;
using GenericSynthesisPatcher.Games.Universal.Action;
using GenericSynthesisPatcher.Helpers;
using GenericSynthesisPatcher.Rules;
using GSPTestProject.GameData.GlobalGame.Fixtures;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NSubstitute;
using Xunit.Abstractions;
using Global = GenericSynthesisPatcher.Global;

namespace GSPTestProject;

[Collection("Sequential")]
public sealed class StructuredValueTests : IClassFixture<SkyrimSEFixture>
{
    private readonly ITestOutputHelper output;
    public StructuredValueTests(SkyrimSEFixture fixture, ITestOutputHelper output)
    {
        _ = fixture;
        this.output = output;
        Global.Logger.Out = new Helpers.TestOutputTextWritter(output);
    }

    private static Armor NewArmor(string male, string female)
        => new(new FormKey(ModKey.FromNameAndExtension("arnima.esm"), 0x11C28), SkyrimRelease.SkyrimSE)
        {
            WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel { Model = new Model { File = male } },
                new ArmorModel { Model = new Model { File = female } }),
        };

    [Fact]
    public void ArraysUseReplacementAndMergeWithoutListMutation()
    {
        var info = typeof(ArrayHolder).GetProperty(nameof(ArrayHolder.Items))!;
        var descriptor = new PropertyPathDescriptor(IArmorGetter.StaticRegistration, "Items", "Items",
            [new PropertyPathSegment(typeof(ArrayHolder), info)]);
        var property = new PropertyAction(IArmorGetter.StaticRegistration, [info], "Items", DeepPropertyAction.Instance, descriptor);
        var source = new ArrayHolder { Items = [1, 2] };
        var target = new ArrayHolder { Items = [3] };
        Assert.True(PropertyPathEngine.Forward(property, source, target) > 0);
        Assert.Equal(source.Items, target.Items);
        Assert.NotSame(source.Items, target.Items);
        Assert.Equal(0, PropertyPathEngine.Forward(property, source, target));
        source.Items = [2, 4];
        Assert.True(PropertyPathEngine.Merge(property, source, target) > 0);
        Assert.Equal(new[] { 1, 2, 4 }, target.Items);
    }

    private sealed class ArrayHolder { public int[] Items { get; set; } = []; }

    [Fact]
    public void ArmorAddonModelsAndSkinTexturesAreIndependentCopies()
    {
        var source = new ArmorAddon(NewArmor("", "").FormKey, SkyrimRelease.SkyrimSE)
        {
            WorldModel = new GenderedItem<Model?>(new Model { File = "male.nif" }, null),
            SkinTexture = new GenderedItem<IFormLinkNullableGetter<ITextureSetGetter>>(
                new FormLinkNullable<ITextureSetGetter>(new FormKey(ModKey.FromNameAndExtension("textures.esp"), 1)),
                new FormLinkNullable<ITextureSetGetter>()),
        };
        var target = new ArmorAddon(source.FormKey, SkyrimRelease.SkyrimSE);
        foreach (string path in new[] { "WorldModel", "SkinTexture" })
        {
            var property = Global.Game.GetAction(IArmorAddonGetter.StaticRegistration, path);
            Assert.True(PropertyPathEngine.Forward(property, source, target) > 0);
            Assert.True(PropertyPathEngine.Equals(property, source, target));
            Assert.Equal(0, PropertyPathEngine.Forward(property, source, target));
        }
        source.WorldModel!.Male!.File = "changed.nif";
        Assert.Equal("male.nif", target.WorldModel!.Male!.File.GivenPath);
        Assert.NotSame(source.SkinTexture!.Male, target.SkinTexture!.Male);
        Assert.Null(target.WorldModel.Female);
    }

    [Fact]
    public void FalloutAndOblivionGenderedValuesForwardWithoutAliasing()
    {
        var key = NewArmor("", "").FormKey;
        var fallout = new Mutagen.Bethesda.Fallout4.Race(key, Mutagen.Bethesda.Fallout4.Fallout4Release.Fallout4);
        var falloutTarget = new Mutagen.Bethesda.Fallout4.Race(key, Mutagen.Bethesda.Fallout4.Fallout4Release.Fallout4);
        fallout.Height.Male = 1.25f;
        fallout.Height.Female = 0.75f;
        var game = new GameData.Game(Mutagen.Bethesda.GameRelease.Fallout4).BaseGame;
        var property = game.GetAction(Mutagen.Bethesda.Fallout4.IRaceGetter.StaticRegistration, "Height");
        Assert.Equal(1, PropertyPathEngine.Forward(property, fallout, falloutTarget));
        Assert.Equal(1.25f, falloutTarget.Height.Male);
        fallout.Height.Male = 2;
        Assert.Equal(1.25f, falloutTarget.Height.Male);

        var oblivion = new Mutagen.Bethesda.Oblivion.Race(key, Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
        var oblivionTarget = new Mutagen.Bethesda.Oblivion.Race(key, Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
        oblivion.Voices = new GenderedItem<IFormLinkGetter<Mutagen.Bethesda.Oblivion.IRaceGetter>>(
            new FormLink<Mutagen.Bethesda.Oblivion.IRaceGetter>(key),
            new FormLink<Mutagen.Bethesda.Oblivion.IRaceGetter>());
        game = new GameData.Game(Mutagen.Bethesda.GameRelease.Oblivion).BaseGame;
        property = game.GetAction(Mutagen.Bethesda.Oblivion.IRaceGetter.StaticRegistration, "Voices.Male");
        Assert.Equal(1, PropertyPathEngine.Forward(property, oblivion, oblivionTarget));
        Assert.Equal(key, oblivionTarget.Voices!.Male.FormKey);
        Assert.NotSame(oblivion.Voices.Male, oblivionTarget.Voices.Male);
    }

    [Fact]
    public void ForwardingOtherFieldPreservesGenderedValuesDespiteMutagenMaskBug()
    {
        var a = NewArmor("a", "b");
        var b = NewArmor("c", "d");
        a.Value = 100;
        var property = Global.Game.GetAction(IArmorGetter.StaticRegistration, "Value");
        Assert.Equal(1, PropertyPathEngine.Forward(property, a, b));
        Assert.Equal(100u, b.Value);
        Assert.Equal("c", b.WorldModel!.Male!.Model!.File.GivenPath);
        Assert.Equal("d", b.WorldModel.Female!.Model!.File.GivenPath);
    }

    [Fact]
    public void NestedAlternateTexturesMergeWithoutChangingFemale()
    {
        var a = NewArmor("a.nif", "b.nif");
        var b = NewArmor("c.nif", "d.nif");
        a.WorldModel!.Male!.Model!.AlternateTextures = [new AlternateTexture { Name = "source", Index = 1 }];
        b.WorldModel!.Male!.Model!.AlternateTextures = [new AlternateTexture { Name = "target", Index = 2 }];
        var property = Global.Game.GetAction(IArmorGetter.StaticRegistration, "WorldModel.Male.Model.AlternateTextures");
        Assert.True(property.IsValid);
        Assert.True(property.Descriptor!.LeafIsCollection);
        Assert.Equal(1, PropertyPathEngine.Merge(property, a, b));
        Assert.Equal(new[] { "target", "source" }, b.WorldModel.Male.Model.AlternateTextures.Select(x => x.Name));
        Assert.Equal("c.nif", b.WorldModel.Male.Model.File.GivenPath);
        Assert.Equal("d.nif", b.WorldModel.Female!.Model!.File.GivenPath);
        a.WorldModel.Male.Model.AlternateTextures[0].Name = "changed";
        Assert.Equal("source", b.WorldModel.Male.Model.AlternateTextures[1].Name);
        Assert.True(RulePreflightValidator.Validate([RuleWithForwardMerge("WorldModel.Male.Model.AlternateTextures")]));
        Assert.False(RulePreflightValidator.Validate([RuleWithForwardMerge("WorldModel")]));
    }

    private static GSPRule RuleWithForwardMerge(string path)
    {
        var rule = new GSPRule { ForwardOptions = ForwardOptions.Merge };
        rule.Types.Add(IArmorGetter.StaticRegistration);
        rule.Forward.Add(new GenericSynthesisPatcher.Rules.Operations.FilterOperation(path), new Newtonsoft.Json.Linq.JArray());
        return rule;
    }

    [Fact]
    public void GroupedForwardRunsAgainstBinaryOverlayOverrides()
    {
        var previous = Global.Game.State;
        var settings = Global.Settings;
        var env = Synthesis.Bethesda.UnitTests.Common.Utility.SetupEnvironment(Mutagen.Bethesda.GameRelease.SkyrimSE);
        foreach (string name in new[] { "arnima.esm", "BRarmor.esp", "Arnima Tweaks.esp" })
        {
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(name), SkyrimRelease.SkyrimSE);
            mod.Armors.Add(NewArmor(name + "-m.nif", name + "-f.nif"));
            using var stream = new MemoryStream();
            mod.WriteToBinary(stream);
            env.FileSystem.File.WriteAllBytes(Path.Combine(env.DataFolder, name), stream.ToArray());
        }
        env.FileSystem.File.WriteAllText(env.PluginPath, "*arnima.esm\n*BRarmor.esp\n*Arnima Tweaks.esp\n");
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
            const string json = """
                [{ "recordTypes": "Armor", "rules": [{ "&PatchedBy": ["BRarmor.esp", "Arnima Tweaks.esp"],
                  "forward": { "BRarmor.esp": ["WorldModel", "Armature", "Race"] } }] }]
                """;
            var group = Assert.IsType<GSPGroup>(Newtonsoft.Json.JsonConvert.DeserializeObject<List<GSPBase>>(json, Global.Game.SerializerSettings)![0]);
            Assert.True(group.Validate());
            var context = state.LinkCache.ResolveAllContexts<ISkyrimMajorRecord, ISkyrimMajorRecordGetter>(NewArmor("", "").FormKey).First();
            var keys = new ProcessingKeys(context);
            keys.SetRule(group);
            Assert.True(group.RunActions(keys) > 0);
            var result = Assert.Single(state.PatchMod.Armors);
            Assert.Equal("BRarmor.esp-m.nif", result.WorldModel!.Male!.Model!.File.GivenPath);
            Assert.Equal("BRarmor.esp-f.nif", result.WorldModel.Female!.Model!.File.GivenPath);
            Assert.Equal(0, group.RunActions(keys));
        }
        finally { Global.Initialize(previous, settings); }
    }

    [Fact]
    public void GenderedValuesAreStructuresAndListsRemainLists()
    {
        Assert.Null(PropertyPathSegment.TryGetCollectionElementType(typeof(GenderedItem<ArmorModel>)));
        Assert.Null(PropertyPathSegment.TryGetCollectionElementType(typeof(IGenderedItem<IArmorModelGetter>)));
        Assert.Null(PropertyPathSegment.TryGetCollectionElementType(typeof(IEnumerable<int>)));
        Assert.Equal(typeof(int), PropertyPathSegment.TryGetCollectionElementType(typeof(List<int>)));
        Assert.Equal(typeof(int), PropertyPathSegment.TryGetCollectionElementType(typeof(int[])));
        foreach (string name in new[] { "WorldModel", "FirstPersonModel", "SkinTexture" })
        {
            var property = Global.Game.GetAction(IArmorAddonGetter.StaticRegistration, name);
            Assert.True(property.IsValid, name);
            Assert.False(property.Descriptor!.LeafIsCollection);
            Assert.IsType<DeepCopyInAction>(property.Action);
        }
    }

    [Theory]
    [InlineData("WorldModel", true, true)]
    [InlineData("WorldModel.Male", true, false)]
    [InlineData("WorldModel.Female", false, true)]
    [InlineData("WorldModel.Male.Model", true, false)]
    [InlineData("WorldModel.Female.Model.File", false, true)]
    public void ForwardCopiesOnlySelectedGenderAndIsIndependent(string path, bool male, bool female)
    {
        var source = NewArmor("source-m.nif", "source-f.nif");
        var target = NewArmor("target-m.nif", "target-f.nif");
        target.EditorID = "PreserveMe";
        var property = Global.Game.GetAction(IArmorGetter.StaticRegistration, path);
        Assert.True(property.IsValid, path);
        Assert.False(property.Descriptor!.HasCollectionBoundary);
        Assert.True(PropertyPathEngine.Forward(property, source, target) > 0);
        Assert.Equal(male ? "source-m.nif" : "target-m.nif", target.WorldModel!.Male!.Model!.File.GivenPath);
        Assert.Equal(female ? "source-f.nif" : "target-f.nif", target.WorldModel.Female!.Model!.File.GivenPath);
        Assert.Equal("PreserveMe", target.EditorID);
        Assert.Equal(0, PropertyPathEngine.Forward(property, source, target));
        source.WorldModel!.Male!.Model!.File = "changed.nif";
        Assert.NotEqual("changed.nif", target.WorldModel.Male.Model.File.GivenPath);
    }

    [Fact]
    public void MissingGenderIsCopiedOrClearedWithoutChangingOtherSide()
    {
        var source = NewArmor("source.nif", "female.nif");
        var target = NewArmor("target.nif", "keep.nif");
        source.WorldModel!.Male = null;
        var property = Global.Game.GetAction(IArmorGetter.StaticRegistration, "WorldModel.Male");
        Assert.Equal(1, PropertyPathEngine.Forward(property, source, target));
        Assert.Null(target.WorldModel!.Male);
        Assert.Equal("keep.nif", target.WorldModel.Female!.Model!.File.GivenPath);
        source.WorldModel.Male = new ArmorModel { Model = new Model { File = "restored.nif" } };
        Assert.Equal(1, PropertyPathEngine.Forward(property, source, target));
        Assert.Equal("restored.nif", target.WorldModel.Male!.Model!.File.GivenPath);
        target.WorldModel = null;
        Assert.True(PropertyPathEngine.Forward(property, source, target) > 0);
        Assert.NotNull(target.WorldModel!.Male);
        Assert.Null(target.WorldModel.Female);
    }

    [Theory]
    [InlineData("WorldModel")]
    [InlineData("WorldModel.Male")]
    public void GenderedHpuUsesContentHistory(string path)
    {
        var origin = NewArmor("origin", "female");
        var middle = NewArmor("unique", "female");
        var winner = NewArmor("origin", "female");
        IModContext<IMajorRecordGetter> Context(Armor armor, string name)
        {
            var result = Substitute.For<IModContext<IMajorRecordGetter>>();
            result.Record.Returns(armor);
            result.ModKey.Returns(ModKey.FromNameAndExtension(name));
            return result;
        }
        var keys = new ProcessingKeys(Context(origin, "arnima.esm"));
        keys.SetRule(new GSPRule { ForwardOptions = ForwardOptions.HPU | ForwardOptions.NonNull, OnlyIfDefault = true });
        keys.SetProperty(new GenericSynthesisPatcher.Rules.Operations.FilterOperation(path), path, 0);
        Assert.True(keys.Property.Action.MatchesOrigin(keys));
        var contexts = new[] { Context(winner, "Winner.esp"), Context(middle, "Middle.esp") };
        Assert.Same(contexts[1], PropertyPathEngine.FindHPU(keys, contexts, null));
        Assert.Same(contexts[0], PropertyPathEngine.FindHPU(keys, contexts, [contexts[0].ModKey]));
        Assert.True(PropertyPathEngine.Equals(keys.Property, origin, winner));
        Assert.False(PropertyPathEngine.Equals(keys.Property, origin, middle));
    }

    [Fact]
    public void RelatedGameGenderedFieldsResolve()
    {
        foreach (object[] entry in new GameData.Stateless.AllGames())
        {
            var game = ((GameData.Game)entry[0]).BaseGame;
            int genderedCount = 0;
            foreach (var registration in game.AllRecordTypes())
            foreach (var property in registration.ClassType.GetProperties())
            {
                if (!PropertyPathSegment.IsGendered(property.PropertyType)) continue;
                genderedCount++;
                var action = game.GetAction(registration, property.Name);
                Assert.True(action.IsValid, $"{registration.Name}.{property.Name}");
                Assert.False(action.Descriptor!.LeafIsCollection);
                Assert.False(action.Action.CanMerge());
                output.WriteLine($"Structured field: {registration.ClassType.FullName}.{property.Name}: {property.PropertyType}");
            }
            Assert.True(genderedCount > 0);
        }
    }

    [Fact]
    public void ExceptionsAreSafeForUnsetRuleAndGroupContexts()
    {
        var record = NewArmor("a.nif", "b.nif");
        var context = Substitute.For<IModContext<IMajorRecordGetter>>();
        context.Record.Returns(record);
        context.ModKey.Returns(ModKey.FromNameAndExtension("BRarmor.esp"));
        var keys = new ProcessingKeys(context);
        foreach (GSPBase? rule in new GSPBase?[] { null, new GSPGroup(), new GSPRule() })
        {
            if (rule is not null) keys.SetRule(rule);
            var inner = new InvalidOperationException("Original cause");
            Assert.Contains("BRarmor.esp", new GSPActionException(keys, "Forward").Message);
            Assert.Contains("detail", new GSPActionException(keys, "Forward", "detail").Message);
            Assert.Same(inner, new GSPActionException(keys, "Forward", inner).InnerException);
            Assert.Contains("Original cause", new GSPActionException(keys, "Forward", "detail", inner).ToString());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GroupFailurePreservesCauseAndHonorsContinue(bool wrapped, bool continueOnError)
    {
        var context = Substitute.For<IModContext<IMajorRecordGetter>>();
        context.Record.Returns(NewArmor("a", "b"));
        context.ModKey.Returns(ModKey.FromNameAndExtension("BRarmor.esp"));
        var child = new ThrowingRule(wrapped);
        var group = new GSPGroup { Rules = [child] };
        var keys = new ProcessingKeys(context);
        keys.SetRule(group);
        bool previous = Global.Settings.Logging.ContinueOnError;
        using var log = new StringWriter();
        Global.Logger.Out = log;
        try
        {
            Global.Settings.Logging.ContinueOnError = continueOnError;
            if (continueOnError) Assert.Equal(0, group.RunActions(keys));
            else
            {
                var error = Assert.Throws<GSPActionException>(() => group.RunActions(keys));
                if (wrapped) Assert.Same(child.Thrown, error);
                else Assert.Same(child.Thrown, error.InnerException);
                Assert.Contains("Actual failure", error.ToString());
            }
            Assert.Contains("Actual failure", log.ToString());
            Assert.DoesNotContain("Rule not currently set", log.ToString());
        }
        finally
        {
            Global.Settings.Logging.ContinueOnError = previous;
            Global.Logger.Out = new Helpers.TestOutputTextWritter(output);
        }
    }

    private sealed class ThrowingRule(bool wrapped) : GSPRule
    {
        public Exception? Thrown { get; private set; }
        public override bool Matches(ProcessingKeys keys) => true;
        public override int RunActions(ProcessingKeys keys)
        {
            var inner = new InvalidOperationException("Actual failure");
            Thrown = wrapped ? new GSPActionException(keys, "Forward", inner) : inner;
            throw Thrown;
        }
    }
}
