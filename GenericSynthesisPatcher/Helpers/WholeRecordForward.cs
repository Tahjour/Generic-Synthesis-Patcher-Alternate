using System.Collections.Concurrent;
using System.Reflection;
using Common;
using GenericSynthesisPatcher.Exceptions;
using GenericSynthesisPatcher.Rules;
using GenericSynthesisPatcher.Rules.Operations;
using Loqui;
using Microsoft.Extensions.Logging;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Newtonsoft.Json.Linq;
using Noggog;

namespace GenericSynthesisPatcher.Helpers;

/// <summary>Complete overrides, deliberately separate from field forwarding.</summary>
internal static class WholeRecordForward
{
    internal const string UpdateName = "<WholeRecord>";
    private const int ClassLogCode = 0x22;
    private static readonly ConcurrentDictionary<Type, Lazy<MajorRecord.TranslationMask?>> Masks = new();

    internal static bool IsTarget(string name)
        => name.TrimStart('&', '^', '|', '!', '+', '-').Equals("All", StringComparison.OrdinalIgnoreCase);

    internal static MajorRecord.TranslationMask? GetMask(ILoquiRegistration registration)
        => Masks.GetOrAdd(registration.GetterType, _ => new Lazy<MajorRecord.TranslationMask?>(() =>
        {
            if (!TranslationMaskFactory.TryCreate(registration, true, [], out var mask)
                || mask is not MajorRecord.TranslationMask major) return null;
            // GRUP children are separate records, not serialized subrecords of their parent.
            foreach (var field in mask.GetAllFields())
            {
                var property = registration.ClassType.GetProperties().FirstOrDefault(p => p.Name == field.Name);
                if (property is not null && OwnsRecords(property.PropertyType, [])
                    && !mask.TrySetValue(field.Name, false)) return null;
            }
            return major;
        })).Value;

    private static bool OwnsRecords(Type type, HashSet<Type> visited)
    {
        if (typeof(IMajorRecordGetter).IsAssignableFrom(type)) return true;
        if (typeof(IFormLinkGetter).IsAssignableFrom(type) || !visited.Add(type)) return false;
        var element = Games.Universal.PropertyPathSegment.TryGetCollectionElementType(type);
        if (element is not null) return OwnsRecords(element, visited);
        if (!typeof(ILoquiObject).IsAssignableFrom(type)) return false;
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "Registration")
            .Any(p => OwnsRecords(p.PropertyType, visited));
    }

    internal static List<ModKeyListOperation> ReadSources(JToken token)
    {
        if (token is not JArray array || array.Any(x => x.Type != JTokenType.String))
            throw new ArgumentException("All requires an array of plugin names; [] selects all enabled sources.");
        var sources = new List<ModKeyListOperation>();
        foreach (var item in array)
        {
            var source = new ModKeyListOperation(item.Value<string>()!);
            if (source.Operation is not (ListLogic.Default or ListLogic.NOT) || source.Value == ModKey.Null)
                throw new ArgumentException("All source entries must be plugin names or !Plugin.esp exclusions.");
            sources.Add(source);
        }
        if (sources.Any(x => x.Operation == ListLogic.NOT) && sources.Any(x => x.Operation == ListLogic.Default))
            throw new ArgumentException("All cannot mix source inclusions and exclusions.");
        return sources;
    }

    internal static IEnumerable<string> Validate(GSPRule rule)
    {
        foreach (var error in rule.WholeRecordInputErrors) yield return error;
        var entries = rule.Forward.Where(x => IsTarget(x.Key.Value)).ToArray();
        if (entries.Length == 0) yield break;
        if (entries.Length != 1 || rule.Forward.Count != 1)
            yield return "All must be the only Forward key in its rule.";
        const ForwardOptions allowed = ForwardOptions.Default | ForwardOptions.Sort | ForwardOptions.IndexedByField;
        if ((rule.ForwardOptions & ~allowed) != 0)
            yield return "All accepts only Default, Sort, and IndexedByField; sorting is implicit.";
        if (rule.OnlyIfDefault) yield return "OnlyIfDefault is not supported with whole-record All forwarding.";
        foreach (var entry in entries)
        {
            if (!entry.Key.Value.Equals("All", StringComparison.OrdinalIgnoreCase) || entry.Key.Operation != FilterLogic.OR)
                yield return "All does not accept operation prefixes.";
            string? error = null;
            try { _ = ReadSources(entry.Value); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { error = ex.Message; }
            if (error is not null) yield return error;
        }
    }

    internal static int Run(ProcessingKeys keys, JToken token)
    {
        ModKey? selected = null;
        keys.ClearProperty();
        Global.Logger.CurrentPropertyName = UpdateName;
        try
        {
            var sources = ReadSources(token);
            bool excluded = sources.Any(x => x.Operation == ListLogic.NOT);
            var eligible = Global.Game.LoadOrder.ListedOrder.Where(x => x.Enabled && x.ModKey != Global.Game.State.PatchMod.ModKey)
                .Where(x => sources.Count == 0 || (excluded
                    ? !sources.Any(s => s.Value == x.ModKey)
                    : sources.Any(s => s.Value == x.ModKey)))
                .Select(x => x.ModKey).ToHashSet();
            var source = Global.Game.State.LinkCache.ResolveAllSimpleContexts(keys.Record.FormKey, keys.Type.GetterType)
                .Where(x => eligible.Contains(x.ModKey))
                .OrderByDescending(x => Global.Game.LoadOrder.IndexOf(x.ModKey)).FirstOrDefault();
            if (source is null)
            {
                Global.Logger.WriteLog(LogLevel.Trace, LogType.RecordProcessSkipped,
                    $"Whole-record forward skipped for {keys.Record.FormKey}: no eligible source contains this record.", ClassLogCode);
                return -1;
            }
            selected = source.ModKey;
            var mask = GetMask(keys.Type) ?? throw new InvalidOperationException("No complete record translation mask exists.");
            if (keys.GetPatchRecord() is not IMajorRecordInternal target)
                throw new InvalidOperationException("The output record does not support complete copying.");
            // No equality shortcut: an identical override is a useful staging record.
            Copy(source.Record, target, mask);
            Program.RecordUpdates.Add((keys.Type, keys.Record.FormKey, keys.Rule, UpdateName, 1));
            Global.Logger.WriteLog(LogLevel.Debug, LogType.RecordUpdated,
                $"Whole-record forward: {keys.Record.FormKey}; selected source: {selected}; action: CopyAsOverride", ClassLogCode);
            return 1;
        }
        catch (Exception ex)
        {
            throw new GSPActionException(keys, "Forward All", $"Field: {UpdateName}; source plugin: {selected?.ToString() ?? "<not selected>"}", ex);
        }
        finally { Global.Logger.CurrentPropertyName = null; }
    }

    internal static void Copy(IMajorRecordGetter source, IMajorRecord target, MajorRecord.TranslationMask mask)
    {
        ((IMajorRecordInternal)target).DeepCopyIn(source, out var errors, mask);
        if (errors.IsInError()) throw new InvalidOperationException($"Complete record copy failed: {errors}");
    }
}
