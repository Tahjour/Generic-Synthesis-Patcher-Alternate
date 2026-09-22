using System.Collections;
using GenericSynthesisPatcher.Games.Universal;
using Mutagen.Bethesda.Plugins;

namespace GenericSynthesisPatcher.Helpers;

/// <summary>Metadata only; scoped to a single initialized game/run.</summary>
internal sealed class RunSourceIndex
{
    private readonly Dictionary<ModKey, int> ranks = [];
    private readonly Dictionary<ModKey, int> linkRanks = [];
    private readonly Dictionary<ModKey, ModKey[]> masters = [];
    internal readonly HashSet<ModKey> Enabled = [];
    internal readonly HashSet<ModKey> Dynamic = [];

    internal RunSourceIndex(BaseGame game)
    {
        int rank = 0;
        foreach (var listing in game.LoadOrder.ListedOrder)
        {
            ranks.TryAdd(listing.ModKey, rank++);
            if (listing.Enabled) Enabled.Add(listing.ModKey);
        }
        rank = 0;
        foreach (var mod in game.State.LinkCache.ListedOrder)
        {
            linkRanks.TryAdd(mod.ModKey, rank++);
            masters.TryAdd(mod.ModKey, mod.MasterReferences.Select(x => x.Master).ToArray());
        }
        Dynamic.UnionWith(Global.Settings.DynamicMods);
    }

    internal int Rank(ModKey key) => ranks.GetValueOrDefault(key, -1);
    internal int LinkRank(ModKey key) => linkRanks.GetValueOrDefault(key, -1);
    internal IReadOnlyList<ModKey> Masters(ModKey key) => masters[key];
}

/// <summary>Retains source order and duplicates, with indexed membership and first rank.</summary>
internal sealed class SourceSelection : IEnumerable<ModKey>
{
    private readonly ModKey[] ordered;
    private readonly Dictionary<ModKey, int> ranks = [];
    internal SourceSelection(IEnumerable<ModKey> sources)
    {
        ordered = sources.ToArray();
        for (int i = 0; i < ordered.Length; i++) ranks.TryAdd(ordered[i], i);
    }
    internal bool Contains(ModKey key) => ranks.ContainsKey(key);
    internal int Rank(ModKey key) => ranks.GetValueOrDefault(key, -1);
    public IEnumerator<ModKey> GetEnumerator() => ((IEnumerable<ModKey>)ordered).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
