using GenericSynthesisPatcher;
using GenericSynthesisPatcher.Helpers;
using GenericSynthesisPatcher.Rules;
using GenericSynthesisPatcher.Rules.Operations;

using GSPTestProject.GameData.GlobalGame.Fixtures;

using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Noggog;

using NSubstitute;

using Xunit.Abstractions;

using Global = GenericSynthesisPatcher.Global;

namespace GSPTestProject
{
    [Collection("Sequential")]
    public sealed class DeepRecordHandlingTests : IClassFixture<SkyrimSEFixture>
    {
        public DeepRecordHandlingTests (SkyrimSEFixture fixture, ITestOutputHelper output)
        {
            _ = fixture;
            Global.Logger.Out = new Helpers.TestOutputTextWritter(output);
        }

        [Fact]
        public void TypesAndRecordTypesAreUnioned ()
        {
            const string json = """
                [
                  {
                    "Types": "QUST",
                    "RecordTypes": ["ANIO", "quest"]
                  }
                ]
                """;

            using var reader = new JsonTextReader(new StringReader(json));
            var rules = JsonSerializer.Create(Global.Game.SerializerSettings).Deserialize<List<GSPBase>>(reader);

            var rule = Assert.IsType<GSPRule>(Assert.Single(rules!));
            Assert.Equal(2, rule.Types.Count);
            Assert.Contains(IQuestGetter.StaticRegistration, rule.Types);
            Assert.Contains(IAnimatedObjectGetter.StaticRegistration, rule.Types);
        }

        [Fact]
        public void SkyrimRecordAndFieldAliasesResolveToOneDescriptor ()
        {
            var signature = Global.Game.GetRecordType("ANIO");
            Assert.Same(signature, Global.Game.GetRecordType("AnimationObject"));
            Assert.Same(signature, Global.Game.GetRecordType("animation-object"));
            Assert.Same(signature, Global.Game.GetRecordType("AnimatedObject"));

            var model = Global.Game.GetAction(signature!, "MODL");
            Assert.True(model.IsValid);
            Assert.Equal("Model", model.PropertyName);

            var conditions = Global.Game.GetAction(IQuestGetter.StaticRegistration, "Aliases.CTDA");
            Assert.True(conditions.IsValid);
            Assert.Equal("Aliases.Conditions", conditions.PropertyName);
            Assert.True(conditions.Descriptor!.HasCollectionBoundary);
            Assert.True(conditions.Descriptor.LeafIsCollection);

            Assert.Equal(IObjectEffectGetter.StaticRegistration, Global.Game.GetRecordType("Enchantment"));
            Assert.Equal(IImageSpaceAdapterGetter.StaticRegistration, Global.Game.GetRecordType("image-space modifier"));
            Assert.Equal(IMoveableStaticGetter.StaticRegistration, Global.Game.GetRecordType("Movable Static"));
        }

        [Fact]
        public void QuestTypeAndAllSafeFieldsAreForwardable ()
        {
            var type = Global.Game.GetAction(IQuestGetter.StaticRegistration, "Type");
            Assert.True(type.IsValid);
            Assert.True(type.Action.CanForward());

            var fields = Global.Game.GetForwardableProperties(IQuestGetter.StaticRegistration);
            Assert.Contains("Type", fields);
            Assert.Contains("Aliases", fields);
            Assert.DoesNotContain("FormKey", fields);
            Assert.DoesNotContain("EditorID", fields);
            Assert.DoesNotContain("StaticRegistration", fields);
            Assert.True(Global.Game.GetAction(IQuestGetter.StaticRegistration, "Aliases").Action.CanMerge());

            Assert.Equal(fields, GSPRule.ExpandDefaultForwardFields(IQuestGetter.StaticRegistration, []));
            Assert.Equal(new[] { "Type", "Aliases" }, GSPRule.ExpandDefaultForwardFields(IQuestGetter.StaticRegistration, new[] { "Type", "Aliases" }));
        }

        [Fact]
        public void DefaultForwardEntriesPreserveDeclarationOrder ()
        {
            const string json = """
                [
                  {
                    "Types": "QUST",
                    "Forward": {
                      "First.esp": [],
                      "Second.esp": ["Type"]
                    }
                  }
                ]
                """;

            using var reader = new JsonTextReader(new StringReader(json));
            var rules = JsonSerializer.Create(Global.Game.SerializerSettings).Deserialize<List<GSPBase>>(reader);
            var rule = Assert.IsType<GSPRule>(Assert.Single(rules!));

            Assert.Equal(new[] { "First.esp", "Second.esp" }, rule.Forward.Keys.Select(x => x.Value));
            Assert.Equal((ForwardOptions)0, rule.ForwardOptions);
        }

        [Fact]
        public void CanonicalValuesNormalizeFormLinksAndOrderedLists ()
        {
            var first = new FormKey(ModKey.FromNameAndExtension("Links.esm"), 0x123).ToLinkGetter<IKeywordGetter>();
            var same = new FormKey(ModKey.FromNameAndExtension("Links.esm"), 0x123).ToLinkGetter<IKeywordGetter>();
            var second = new FormKey(ModKey.FromNameAndExtension("Links.esm"), 0x456).ToLinkGetter<IKeywordGetter>();

            Assert.Equal(PropertyPathEngine.Canonicalize(first), PropertyPathEngine.Canonicalize(same));
            Assert.Equal(PropertyPathEngine.Canonicalize(new[] { first, second }), PropertyPathEngine.Canonicalize(new[] { same, second }));
            Assert.NotEqual(PropertyPathEngine.Canonicalize(new[] { first, second }), PropertyPathEngine.Canonicalize(new[] { second, first }));
        }

        [Fact]
        public void ContainerObjectBoundsHpuUsesFieldMaskWithoutRecursion ()
        {
            var formKey = new FormKey(ModKey.FromNameAndExtension("BoundsMaster.esm"), 0x900);
            var origin = NewContainer(formKey, NewBounds(1, 2));
            var low = NewContainer(formKey, NewBounds(1, 2));
            var middle = NewContainer(formKey, NewBounds(10, 20));
            var high = NewContainer(formKey, NewBounds(1, 2));

            var property = Global.Game.GetAction(IContainerGetter.StaticRegistration, "ObjectBounds");
            Assert.True(PropertyPathEngine.EqualsByFieldMask(property, origin, low));
            Assert.False(PropertyPathEngine.EqualsByFieldMask(property, origin, middle));

            var rule = new GSPRule
            {
                ForwardOptions = ForwardOptions.Sort | ForwardOptions.HPU | ForwardOptions.NonNull,
            };
            rule.Types.Add(IContainerGetter.StaticRegistration);

            var keys = new ProcessingKeys(NewContext(origin, formKey.ModKey));
            Assert.True(keys.SetRule(rule));
            Assert.True(keys.SetProperty(new FilterOperation("ObjectBounds"), "ObjectBounds", 0));

            IModContext<IMajorRecordGetter>? selected = PropertyPathEngine.FindHPU(keys,
            [
                NewContext(high, ModKey.FromNameAndExtension("High.esp")),
                NewContext(middle, ModKey.FromNameAndExtension("Middle.esp")),
                NewContext(low, ModKey.FromNameAndExtension("Low.esp")),
            ], null);

            Assert.NotNull(selected);
            Assert.Equal("Middle.esp", selected.ModKey.FileName.String);
        }

        [Fact]
        public void ObjectBoundsMaskComparesBothCornersAndOnlySelectedField ()
        {
            var formKey = new FormKey(ModKey.FromNameAndExtension("Bounds.esm"), 0x901);
            var first = NewContainer(formKey, NewBounds(1, 2));
            var same = NewContainer(formKey, NewBounds(1, 2));
            var differentFirst = NewContainer(formKey, NewBounds(3, 2));
            var differentSecond = NewContainer(formKey, NewBounds(1, 4));
            first.EditorID = "First";
            same.EditorID = "Different infrastructure value";

            var property = Global.Game.GetAction(IContainerGetter.StaticRegistration, "ObjectBounds");
            Assert.True(PropertyPathEngine.EqualsByFieldMask(property, first, same));
            Assert.False(PropertyPathEngine.EqualsByFieldMask(property, first, differentFirst));
            Assert.False(PropertyPathEngine.EqualsByFieldMask(property, first, differentSecond));
        }

        [Fact]
        public void NullableFormLinkFieldsUseFieldMasks ()
        {
            var formKey = new FormKey(ModKey.FromNameAndExtension("Links.esm"), 0x902);
            var link = new FormKey(ModKey.FromNameAndExtension("Links.esm"), 0x100);
            var otherLink = new FormKey(ModKey.FromNameAndExtension("Links.esm"), 0x101);
            var left = new Mutagen.Bethesda.Skyrim.Activator(formKey, SkyrimRelease.SkyrimSE);
            var right = new Mutagen.Bethesda.Skyrim.Activator(formKey, SkyrimRelease.SkyrimSE);

            Assert.True(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "ActivationSound"), left, right));
            left.ActivationSound.SetTo(link);
            Assert.False(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "ActivationSound"), left, right));
            right.ActivationSound.SetTo(link);
            Assert.True(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "ActivationSound"), left, right));
            right.ActivationSound.SetTo(otherLink);
            Assert.False(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "ActivationSound"), left, right));

            Assert.True(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "WaterType"), left, right));
            left.WaterType.SetTo(link);
            Assert.False(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "WaterType"), left, right));
            right.WaterType.SetTo(link);
            Assert.True(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "WaterType"), left, right));
            right.WaterType.SetTo(otherLink);
            Assert.False(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "WaterType"), left, right));

            Assert.True(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "InteractionKeyword"), left, right));
            left.InteractionKeyword.SetTo(link);
            Assert.False(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "InteractionKeyword"), left, right));
            right.InteractionKeyword.SetTo(link);
            Assert.True(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "InteractionKeyword"), left, right));
            right.InteractionKeyword.SetTo(otherLink);
            Assert.False(PropertyPathEngine.EqualsByFieldMask(Global.Game.GetAction(IActivatorGetter.StaticRegistration, "InteractionKeyword"), left, right));

            AssertNullableFormLinkHpu("ActivationSound", (record, value) => record.ActivationSound.SetTo(value));
            AssertNullableFormLinkHpu("WaterType", (record, value) => record.WaterType.SetTo(value));
            AssertNullableFormLinkHpu("InteractionKeyword", (record, value) => record.InteractionKeyword.SetTo(value));
        }

        [Fact]
        public void OrderedWholeListHpuMaskPreservesOrder ()
        {
            var formKey = new FormKey(ModKey.FromNameAndExtension("Lists.esm"), 0x903);
            var itemOne = new FormKey(ModKey.FromNameAndExtension("Lists.esm"), 0x110);
            var itemTwo = new FormKey(ModKey.FromNameAndExtension("Lists.esm"), 0x111);
            var left = NewContainer(formKey, NewBounds(1, 2));
            var same = NewContainer(formKey, NewBounds(1, 2));
            var reversed = NewContainer(formKey, NewBounds(1, 2));
            left.Items!.Add(NewContainerEntry(itemOne));
            left.Items.Add(NewContainerEntry(itemTwo));
            same.Items!.Add(NewContainerEntry(itemOne));
            same.Items.Add(NewContainerEntry(itemTwo));
            reversed.Items!.Add(NewContainerEntry(itemTwo));
            reversed.Items.Add(NewContainerEntry(itemOne));

            var property = Global.Game.GetAction(IContainerGetter.StaticRegistration, "Items");
            Assert.True(PropertyPathEngine.EqualsByFieldMask(property, left, same));
            Assert.False(PropertyPathEngine.EqualsByFieldMask(property, left, reversed));
        }

        [Fact]
        public void NestedAliasConditionComparisonRemainsStructuralPerParent ()
        {
            var left = NewQuest(0x904);
            var same = NewQuest(0x905);
            var different = NewQuest(0x906);
            left.Aliases.Add(new QuestAlias { ID = 7, Conditions = { NewCondition(1) } });
            same.Aliases.Add(new QuestAlias { ID = 7, Conditions = { NewCondition(1) } });
            different.Aliases.Add(new QuestAlias { ID = 7, Conditions = { NewCondition(2) } });

            var property = Global.Game.GetAction(IQuestGetter.StaticRegistration, "Aliases.Conditions");
            Assert.True(PropertyPathEngine.Equals(property, left, same));
            Assert.False(PropertyPathEngine.Equals(property, left, different));
        }

        [Fact]
        public void StructuralCanonicalizationBoundsCyclesAndDepth ()
        {
            var cycleError = Assert.Throws<InvalidDataException>(() => PropertyPathEngine.Canonicalize(new SelfEnumerable(), "SelfEnumerable"));
            Assert.Contains("reference cycle", cycleError.Message, StringComparison.Ordinal);
            Assert.Contains("SelfEnumerable", cycleError.Message, StringComparison.Ordinal);

            var root = new RecursiveValue();
            var current = root;
            for (int index = 0; index < 66; index++)
            {
                current.Child = new RecursiveValue();
                current = current.Child;
            }

            var error = Assert.Throws<InvalidDataException>(() => PropertyPathEngine.Canonicalize(root, "HostileValue"));
            Assert.Contains("maximum depth of 64", error.Message, StringComparison.Ordinal);
            Assert.Contains("HostileValue", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void PreflightRejectsMergeCombinedWithHpu ()
        {
            var rule = new GSPRule
            {
                ConfigFile = 1,
                ConfigRule = 1,
                ForwardOptions = ForwardOptions.Merge | ForwardOptions.HPU,
                Forward = { [new FilterOperation("Aliases.Conditions")] = new JArray() },
            };
            rule.Types.Add(IQuestGetter.StaticRegistration);

            Assert.False(RulePreflightValidator.Validate([rule]));
        }

        [Fact]
        public void NestedForwardCorrelatesAliasesAndClonesMissingParents ()
        {
            var source = NewQuest(0x800);
            var target = NewQuest(0x801);

            source.Aliases.Add(new QuestAlias
            {
                ID = 1,
                Name = "Source alias",
                Conditions = { NewCondition(1) },
            });
            source.Aliases.Add(new QuestAlias
            {
                ID = 2,
                Name = "Missing alias",
                Conditions = { NewCondition(1) },
            });
            target.Aliases.Add(new QuestAlias
            {
                ID = 1,
                Name = "Winning alias",
            });

            var property = Global.Game.GetAction(IQuestGetter.StaticRegistration, "Aliases.Conditions");
            int changes = PropertyPathEngine.Forward(property, source, target);

            Assert.True(changes > 0);
            Assert.Equal("Winning alias", target.Aliases.Single(x => x.ID == 1).Name);
            Assert.Single(target.Aliases.Single(x => x.ID == 1).Conditions);
            Assert.Equal("Missing alias", target.Aliases.Single(x => x.ID == 2).Name);
            Assert.Single(target.Aliases.Single(x => x.ID == 2).Conditions);
        }

        [Fact]
        public void NestedMergePreservesWinnerOrderAndAddsUniqueConditions ()
        {
            var source = NewQuest(0x802);
            var target = NewQuest(0x803);
            source.Aliases.Add(new QuestAlias
            {
                ID = 7,
                Conditions = { NewCondition(2) },
            });
            target.Aliases.Add(new QuestAlias
            {
                ID = 7,
                Conditions = { NewCondition(1) },
            });

            var property = Global.Game.GetAction(IQuestGetter.StaticRegistration, "Aliases.Conditions");
            int changes = PropertyPathEngine.Merge(property, source, target);

            Assert.Equal(1, changes);
            Assert.Equal(2, target.Aliases[0].Conditions.Count);
            Assert.Equal(1, Assert.IsType<ConditionFloat>(target.Aliases[0].Conditions[0]).ComparisonValue);
            Assert.Equal(2, Assert.IsType<ConditionFloat>(target.Aliases[0].Conditions[1]).ComparisonValue);
        }

        private static Quest NewQuest (uint id)
            => new(new FormKey(ModKey.FromNameAndExtension("DeepRecordTests.esp"), id), SkyrimRelease.SkyrimSE);

        private static Container NewContainer (FormKey formKey, ObjectBounds bounds)
            => new(formKey, SkyrimRelease.SkyrimSE) { ObjectBounds = bounds, Items = [] };

        private static ObjectBounds NewBounds (short first, short second)
            => new()
            {
                First = new P3Int16(first, first, first),
                Second = new P3Int16(second, second, second),
            };

        private static ContainerEntry NewContainerEntry (FormKey item)
        {
            var entry = new ContainerEntry();
            entry.Item.Item.FormKey = item;
            entry.Item.Count = 1;
            return entry;
        }

        private static void AssertNullableFormLinkHpu (string field, Action<Mutagen.Bethesda.Skyrim.Activator, FormKey> setValue)
        {
            var formKey = new FormKey(ModKey.FromNameAndExtension("LinkMaster.esm"), 0x920);
            var originalValue = new FormKey(ModKey.FromNameAndExtension("LinkMaster.esm"), 0x120);
            var uniqueValue = new FormKey(ModKey.FromNameAndExtension("LinkMaster.esm"), 0x121);
            var origin = new Mutagen.Bethesda.Skyrim.Activator(formKey, SkyrimRelease.SkyrimSE);
            var low = new Mutagen.Bethesda.Skyrim.Activator(formKey, SkyrimRelease.SkyrimSE);
            var middle = new Mutagen.Bethesda.Skyrim.Activator(formKey, SkyrimRelease.SkyrimSE);
            var high = new Mutagen.Bethesda.Skyrim.Activator(formKey, SkyrimRelease.SkyrimSE);
            setValue(origin, originalValue);
            setValue(low, originalValue);
            setValue(middle, uniqueValue);
            setValue(high, originalValue);

            var rule = new GSPRule
            {
                ForwardOptions = ForwardOptions.Sort | ForwardOptions.HPU | ForwardOptions.NonNull,
            };
            rule.Types.Add(IActivatorGetter.StaticRegistration);

            var keys = new ProcessingKeys(NewContext(origin, formKey.ModKey));
            Assert.True(keys.SetRule(rule));
            Assert.True(keys.SetProperty(new FilterOperation(field), field, 0));

            IModContext<IMajorRecordGetter>? selected = PropertyPathEngine.FindHPU(keys,
            [
                NewContext(high, ModKey.FromNameAndExtension("High.esp")),
                NewContext(middle, ModKey.FromNameAndExtension("Middle.esp")),
                NewContext(low, ModKey.FromNameAndExtension("Low.esp")),
            ], null);

            Assert.NotNull(selected);
            Assert.Equal("Middle.esp", selected.ModKey.FileName.String);
        }

        private static IModContext<IMajorRecordGetter> NewContext (IMajorRecordGetter record, ModKey modKey)
        {
            var context = Substitute.For<IModContext<IMajorRecordGetter>>();
            context.Record.Returns(record);
            context.ModKey.Returns(modKey);
            return context;
        }

        private static ConditionFloat NewCondition (float value)
            => new() { Data = new GetLevelConditionData(), ComparisonValue = value };

        private sealed class RecursiveValue
        {
            public RecursiveValue? Child { get; set; }
        }

        private sealed class SelfEnumerable : IEnumerable<object>
        {
            public IEnumerator<object> GetEnumerator ()
            {
                yield return this;
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator () => GetEnumerator();
        }
    }
}
