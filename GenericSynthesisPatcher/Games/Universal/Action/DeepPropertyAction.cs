using System.Diagnostics.CodeAnalysis;

using Common;

using GenericSynthesisPatcher.Helpers;

using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;

namespace GenericSynthesisPatcher.Games.Universal.Action
{
    /// <summary>
    ///     Generic action for paths crossing one or more collection boundaries. Collection
    ///     parents are correlated by stable identity by <see cref="PropertyPathEngine" />.
    /// </summary>
    public sealed class DeepPropertyAction : IRecordAction
    {
        public static readonly DeepPropertyAction Instance = new();

        private DeepPropertyAction ()
        { }

        public bool AllowSubProperties => true;

        public bool CanFill () => false;

        public bool CanForward () => true;

        public bool CanForwardSelfOnly () => false;

        public bool CanMatch () => false;

        public bool CanMerge () => true;

        public int Fill (ProcessingKeys proKeys) => throw new NotSupportedException("Fill is not supported on an implicitly traversed collection path.");

        public IModContext<IMajorRecordGetter>? FindHPUIndex (ProcessingKeys proKeys, IEnumerable<IModContext<IMajorRecordGetter>> AllRecordMods, IEnumerable<ModKey>? endNodes)
            => Mod.FindHPUIndex(proKeys, AllRecordMods, endNodes, 0x20);

        public int Forward (ProcessingKeys proKeys, IModContext<IMajorRecordGetter> forwardContext)
            => PropertyPathEngine.Forward(proKeys, forwardContext.Record);

        public int ForwardSelfOnly (ProcessingKeys proKeys, IModContext<IMajorRecordGetter> forwardContext)
            => throw new NotSupportedException("SelfMasterOnly is not supported on an implicitly traversed collection path.");

        public bool IsNullOrEmpty (ProcessingKeys proKeys, IModContext<IMajorRecordGetter> recordContext)
            => PropertyPathEngine.IsNullOrEmpty(proKeys.Property, recordContext.Record);

        public bool MatchesOrigin (ProcessingKeys proKeys)
            => PropertyPathEngine.Equals(proKeys.Property, proKeys.Record, proKeys.GetOriginRecord());

        public bool MatchesOrigin (ProcessingKeys proKeys, IModContext<IMajorRecordGetter> recordContext)
            => recordContext.IsMaster() || PropertyPathEngine.Equals(proKeys.Property, recordContext.Record, proKeys.GetOriginRecord());

        public bool MatchesRule (ProcessingKeys proKeys) => throw new NotSupportedException("Matches is not supported on an implicitly traversed collection path.");

        public int Merge (ProcessingKeys proKeys) => PropertyPathEngine.MergeAll(proKeys);

        public bool TryGetDocumentation (Type propertyType, string propertyName, [NotNullWhen(true)] out string? description, [NotNullWhen(true)] out string? example)
        {
            description = "A writable value reached by correlating each parent collection entry by stable identity.";
            example = $"\"{propertyName}\": []";
            return true;
        }
    }
}
