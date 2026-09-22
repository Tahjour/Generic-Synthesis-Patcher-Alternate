using GenericSynthesisPatcher.Helpers.Graph;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;

namespace GenericSynthesisPatcher.Helpers;

/// <summary>One record's history/topology. Mutable record values are never cached.</summary>
internal sealed class RecordProcessingState(IModContext<IMajorRecordGetter> context)
{
    private IModContext<IMajorRecordGetter>[]? contexts;
    private ForwardRecordGraph? graph;
    private bool graphBuilt;
    private IMajorRecord? observedPatch;
    private readonly Dictionary<string, IReadOnlyCollection<ModKey>?> endNodes = [];
    internal int HistoryResolutions { get; private set; }
    internal int GraphBuilds { get; private set; }
    internal int EndNodeBuilds { get; private set; }
    internal int Generation { get; private set; }

    internal IReadOnlyList<IModContext<IMajorRecordGetter>> Contexts
    {
        get
        {
            if (contexts is null)
            {
                contexts = Global.Game.State.LinkCache.ResolveAllSimpleContexts(context.Record.FormKey, context.Record.Registration.GetterType).ToArray();
                HistoryResolutions++;
            }
            return contexts;
        }
    }

    internal void PatchAvailable(IMajorRecord patch)
    {
        if (ReferenceEquals(observedPatch, patch)) return;
        observedPatch = patch;
        if (contexts is null || contexts.Any(x => ReferenceEquals(x.Record, patch))) return;
        contexts = null;
        graph = null;
        graphBuilt = false;
        endNodes.Clear();
        Generation++;
    }

    internal IReadOnlyCollection<ModKey>? GetEndNodes(ProcessingKeys keys, IEnumerable<ModKey> sources)
    {
        if (!graphBuilt)
        {
            graph = ForwardRecordGraph.Create(keys);
            graphBuilt = true;
            GraphBuilds++;
        }
        var selection = sources as SourceSelection ?? new SourceSelection(sources);
        var membership = Contexts.Select(x => x.ModKey).Where(selection.Contains).ToHashSet();
        string key = string.Join('\n', membership.Select(x => x.ToString().ToUpperInvariant()).Order(StringComparer.Ordinal));
        if (!endNodes.TryGetValue(key, out var result))
        {
            result = graph?.GetEndNodes(membership)?.ToArray();
            endNodes.Add(key, result);
            EndNodeBuilds++;
        }
        return result;
    }
}
