using GenericSynthesisPatcher;
using GenericSynthesisPatcher.Helpers;
using GenericSynthesisPatcher.Rules;
using GenericSynthesisPatcher.Rules.Operations;

using GSPTestProject.GameData.GlobalGame.Fixtures;

using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

        private static ConditionFloat NewCondition (float value)
            => new() { Data = new GetLevelConditionData(), ComparisonValue = value };
    }
}
