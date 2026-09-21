using System.Diagnostics.CodeAnalysis;
using Common;

using GenericSynthesisPatcher.Helpers;

using Microsoft.Extensions.Logging;

using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;

namespace GenericSynthesisPatcher.Games.Universal.Action
{
    /// <summary>
    ///     This is the default action for editable properties that are not assigned any other action.
    ///
    ///     Uses field-scoped Mutagen copies and structured-value adapters for Forward.
    /// </summary>
    public class DeepCopyInAction : IRecordAction
    {
        public static readonly DeepCopyInAction Instance = new();
        private const int ClassLogCode = 0x14;

        protected DeepCopyInAction ()
        {
        }

        // <inheritdoc />
        public bool AllowSubProperties => true;

        /// <inheritdoc />
        public virtual bool CanFill () => false;

        /// <inheritdoc />
        public virtual bool CanForward () => true;

        /// <inheritdoc />
        public virtual bool CanForwardSelfOnly () => false;

        /// <inheritdoc />
        public virtual bool CanMatch () => false;

        /// <inheritdoc />
        public virtual bool CanMerge () => false;

        /// <inheritdoc />
        public virtual int Fill (ProcessingKeys proKeys) => throw new NotImplementedException();

        /// <inheritdoc />
        public IModContext<IMajorRecordGetter>? FindHPUIndex (ProcessingKeys proKeys, IEnumerable<IModContext<IMajorRecordGetter>> AllRecordMods, IEnumerable<ModKey>? endNodes) => Mod.FindHPUIndex(proKeys, AllRecordMods, endNodes, ClassLogCode);

        /// <inheritdoc />
        public virtual int Forward (ProcessingKeys proKeys, IModContext<IMajorRecordGetter> forwardContext)
        {
            if (PropertyPathEngine.Equals(proKeys.Property, proKeys.Record, forwardContext.Record))
            {
                Global.Logger.WriteLog(LogLevel.Trace, LogType.NoUpdateAlreadyMatches, LogWriter.PropertyIsEqual, ClassLogCode);
                return 0;
            }

            Global.Logger.LogAction("Copying selected structured property.", ClassLogCode);
            try
            {
                return PropertyPathEngine.Forward(proKeys, forwardContext.Record);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Forwarding '{proKeys.Property.PropertyName}' from '{forwardContext.ModKey}' failed: {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public virtual int ForwardSelfOnly (ProcessingKeys proKeys, IModContext<IMajorRecordGetter> forwardContext) => throw new NotImplementedException();

        /// <inheritdoc />
        public virtual bool IsNullOrEmpty (ProcessingKeys proKeys, IModContext<IMajorRecordGetter> recordContext)
                    => !Mod.TryGetProperty(recordContext.Record, proKeys.Property.PropertyName, out object? curValue, ClassLogCode) || Mod.IsNullOrEmpty(curValue);

        /// <inheritdoc />
        public bool MatchesOrigin (ProcessingKeys proKeys)
            => PropertyPathEngine.Equals(proKeys.Property, proKeys.Record, proKeys.GetOriginRecord());

        /// <inheritdoc />
        public virtual bool MatchesOrigin (ProcessingKeys proKeys, IModContext<IMajorRecordGetter> recordContext)
            => recordContext.IsMaster()
            || PropertyPathEngine.Equals(proKeys.Property, recordContext.Record, proKeys.GetOriginRecord());

        /// <inheritdoc />
        public virtual bool MatchesRule (ProcessingKeys proKeys) => throw new NotImplementedException();

        /// <inheritdoc />
        public virtual int Merge (ProcessingKeys proKeys) => throw new NotImplementedException();

        // <inheritdoc />
        public virtual bool TryGetDocumentation (Type propertyType, string propertyName, [NotNullWhen(true)] out string? description, [NotNullWhen(true)] out string? example)
        {
            description = PropertyPathSegment.IsGendered(propertyType)
                ? "Forward the complete Male/Female structure. Select Male or Female explicitly for one side; Merge requires a nested list leaf."
                : string.Empty;
            example = PropertyPathSegment.IsGendered(propertyType)
                ? $"\"Forward\": {{ \"Source.esp\": [\"{propertyName}\"] }}" : string.Empty;

            return description is not null && example is not null;
        }
    }
}
