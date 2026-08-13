using System.Reflection;
using System.Text;
using System.Globalization;

using GenericSynthesisPatcher.Games.Universal;
using GenericSynthesisPatcher.Helpers;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json.Linq;

namespace GenericSynthesisPatcher.Rules
{
    /// <summary>
    ///     Validates all expanded type/action/path combinations before any record scan begins.
    ///     Errors are accumulated into one report so a configuration can be fixed in one pass.
    /// </summary>
    internal static class RulePreflightValidator
    {
        private const int ClassLogCode = 0x21;

        public static bool Validate (IEnumerable<GSPBase> roots, IEnumerable<string>? initialErrors = null)
        {
            var failures = new List<string>();
            if (initialErrors is not null)
                failures.AddRange(initialErrors);

            foreach (GSPRule rule in Flatten(roots))
                ValidateRule(rule, failures);

            if (failures.Count == 0)
                return true;

            var report = new StringBuilder()
                .Append("Configuration preflight failed with ")
                .Append(failures.Count)
                .Append(" error(s). No records will be scanned or added to the patch.")
                .AppendLine();

            for (int i = 0; i < failures.Count; i++)
                report.Append(i + 1).Append(". ").AppendLine(failures[i]);

            Global.Logger.WriteLog(LogLevel.Critical, LogType.GeneralConfigFailure, report.ToString().TrimEnd(), ClassLogCode);
            return false;
        }

        public static IEnumerable<string> FindUnknownRecordTypes (JToken document, string filename)
        {
            int rootIndex = 0;
            foreach (var token in document.Children())
            {
                rootIndex++;
                foreach (var error in FindUnknownRecordTypesRecursive(token, filename, rootIndex.ToString(CultureInfo.InvariantCulture), null))
                    yield return error;
            }
        }

        private static IEnumerable<string> FindUnknownRecordTypesRecursive (JToken token, string filename, string ruleId, JToken? inheritedTypes)
        {
            if (token is not JObject obj)
                yield break;

            JToken? localTypes = GetProperty(obj, "Types");
            JToken? recordTypes = GetProperty(obj, "RecordTypes");
            foreach (string selector in ReadStrings(localTypes).Concat(ReadStrings(recordTypes)))
            {
                if (Global.Game.GetRecordType(selector) is null)
                    yield return $"Config: {filename}; rule/group: {ruleId}; supplied record type: '{selector}'; reason: unknown record type; close suggestions: {SuggestRecordTypes(selector)}.";
            }

            JToken? effectiveTypes = localTypes ?? recordTypes ?? inheritedTypes;
            JToken? rules = GetProperty(obj, "Rules");
            if (rules is JArray children)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    foreach (string error in FindUnknownRecordTypesRecursive(children[i], filename, $"{ruleId}.{i + 1}", effectiveTypes))
                        yield return error;
                }
            }
        }

        private static void ValidateRule (GSPRule rule, List<string> failures)
        {
            string prefix = $"Config: {rule.SourceFile ?? $"#{rule.ConfigFile}"}; group/rule: {rule.GetLogRuleID()}";
            if (rule.Types.Count == 0)
            {
                failures.Add($"{prefix}; reason: no valid record types remain after group inheritance.");
                return;
            }

            if (rule.Types.Any(x => x is null))
                failures.Add($"{prefix}; reason: one or more supplied record types could not be resolved.");

            if (rule.HasForwardOption(ForwardOptions._merge))
            {
                var incompatible = new List<string>();
                if (rule.HasForwardOption(ForwardOptions._hpu)) incompatible.Add(nameof(ForwardOptions.HPU));
                if (rule.HasForwardOption(ForwardOptions._randomMod)) incompatible.Add(nameof(ForwardOptions.Random));
                if (rule.HasForwardOption(ForwardOptions.SelfMasterOnly)) incompatible.Add(nameof(ForwardOptions.SelfMasterOnly));

                if (incompatible.Count != 0)
                    failures.Add($"{prefix}; requested options: {rule.ForwardOptions}; reason: Merge is incompatible with {string.Join(", ", incompatible.Distinct())}.");
            }

            foreach (var recordType in rule.Types.Where(x => x is not null))
            {
                foreach (var key in rule.Match.Keys)
                    ValidatePath(rule, recordType, key.Value, "Matches", x => x.CanMatch(), false, failures);

                foreach (var key in rule.Fill.Keys)
                    ValidatePath(rule, recordType, key.Value, "Fill", x => x.CanFill(), false, failures);

                foreach (var key in rule.Merge.Keys)
                    ValidatePath(rule, recordType, key.Value, "Merge", x => x.CanMerge(), false, failures);

                if (rule.ForwardOptions.HasFlag(ForwardOptions.IndexedByField))
                {
                    foreach (var key in rule.Forward.Keys)
                        ValidatePath(rule, recordType, key.Value, rule.HasForwardOption(ForwardOptions._merge) ? "Forward/Merge" : "Forward", x => x.CanForward(), rule.HasForwardOption(ForwardOptions._merge), failures);
                }
                else
                {
                    foreach (var entry in rule.Forward)
                    {
                        var fields = ReadStrings(entry.Value).ToList();
                        if (fields.Count == 0 && entry.Value.Type == JTokenType.Array)
                            fields.AddRange(Global.Game.GetForwardableProperties(recordType));

                        foreach (string field in fields)
                            ValidatePath(rule, recordType, field, "Forward", x => x.CanForward(), false, failures);
                    }
                }
            }
        }

        private static void ValidatePath (GSPRule rule, Loqui.ILoquiRegistration recordType, string suppliedPath, string actionName, Func<Games.Universal.Action.IRecordAction, bool> supportsAction, bool requiresListLeaf, List<string> failures)
        {
            PropertyAction property = Global.Game.GetAction(recordType, suppliedPath);
            string canonical = property.PropertyName;
            if (!property.IsValid)
            {
                failures.Add(BuildPathFailure(rule, recordType, suppliedPath, canonical, property, actionName, "field does not exist, is computed/read-only, or has no safe copy adapter"));
                return;
            }

            if (!supportsAction(property.Action))
            {
                failures.Add(BuildPathFailure(rule, recordType, suppliedPath, canonical, property, actionName, $"{actionName} is not supported by {property.Action.GetType().Name}"));
                return;
            }

            if (requiresListLeaf && property.Descriptor?.LeafIsCollection != true)
                failures.Add(BuildPathFailure(rule, recordType, suppliedPath, canonical, property, actionName, "Merge operates only on list leaves"));

            if (property.Descriptor?.HasCollectionBoundary == true && rule.HasForwardOption(ForwardOptions.SelfMasterOnly))
                failures.Add(BuildPathFailure(rule, recordType, suppliedPath, canonical, property, actionName, "SelfMasterOnly cannot be applied through an implicit collection traversal"));
        }

        private static string BuildPathFailure (GSPRule rule, Loqui.ILoquiRegistration recordType, string suppliedPath, string canonicalPath, PropertyAction property, string actionName, string reason)
        {
            int failingIndex = Array.FindIndex(property.Properties, x => x is null);
            string[] suppliedSegments = suppliedPath.Split('.');
            string failingSegment = failingIndex >= 0 && failingIndex < suppliedSegments.Length ? suppliedSegments[failingIndex] : suppliedSegments.LastOrDefault() ?? suppliedPath;
            Type owner = recordType.ClassType;
            for (int i = 0; i < property.Properties.Length && i < (failingIndex < 0 ? property.Properties.Length - 1 : failingIndex); i++)
            {
                if (property.Properties[i] is null)
                    break;
                owner = PropertyPathSegment.TryGetCollectionElementType(property.Properties[i].PropertyType) ?? Nullable.GetUnderlyingType(property.Properties[i].PropertyType) ?? property.Properties[i].PropertyType;
            }

            string compatible = property.IsValid
                ? string.Join(", ", new[] {
                    property.Action.CanMatch() ? "Matches" : null,
                    property.Action.CanFill() ? "Fill" : null,
                    property.Action.CanForward() ? "Forward/HPU" : null,
                    property.Action.CanMerge() ? "Merge" : null,
                }.Where(x => x is not null))
                : "none";

            string suggestions = string.Join(", ", owner.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(x => x.Name)
                .OrderBy(x => Levenshtein(PropertyAliasMapping.Normalize(x), PropertyAliasMapping.Normalize(failingSegment)))
                .ThenBy(x => x, StringComparer.Ordinal)
                .Take(3));

            return $"Config: {rule.SourceFile ?? $"#{rule.ConfigFile}"}; group/rule: {rule.GetLogRuleID()}; supplied record type/path: '{recordType.Name}'/'{suppliedPath}'; canonical record type/path: '{recordType.Name}'/'{canonicalPath}'; failing segment: '{failingSegment}'; owning CLR type: {owner.FullName}; requested action/options: {actionName}/{rule.ForwardOptions}; reason: {reason}; compatible actions: {compatible}; close suggestions: {suggestions}.";
        }

        private static IEnumerable<GSPRule> Flatten (IEnumerable<GSPBase> roots)
        {
            foreach (GSPBase root in roots)
            {
                if (root is GSPRule rule)
                    yield return rule;
                else if (root is GSPGroup group)
                {
                    foreach (GSPRule child in group.Rules)
                        yield return child;
                }
            }
        }

        private static IEnumerable<string> ReadStrings (JToken? token)
        {
            if (token is null || token.Type == JTokenType.Null)
                yield break;

            if (token.Type == JTokenType.Array)
            {
                foreach (JToken child in token.Children())
                {
                    if (child.Type == JTokenType.String)
                        yield return child.Value<string>()!;
                }
            }
            else if (token.Type == JTokenType.String)
            {
                yield return token.Value<string>()!;
            }
        }

        private static JToken? GetProperty (JObject obj, string name)
            => obj.Properties().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

        private static string SuggestRecordTypes (string supplied)
            => string.Join(", ", Global.Game.RecordTypes.Keys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => Levenshtein(PropertyAliasMapping.Normalize(x), PropertyAliasMapping.Normalize(supplied)))
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(3));

        private static int Levenshtein (string left, string right)
        {
            var previous = Enumerable.Range(0, right.Length + 1).ToArray();
            for (int i = 1; i <= left.Length; i++)
            {
                var current = new int[right.Length + 1];
                current[0] = i;
                for (int j = 1; j <= right.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                previous = current;
            }
            return previous[^1];
        }
    }
}
