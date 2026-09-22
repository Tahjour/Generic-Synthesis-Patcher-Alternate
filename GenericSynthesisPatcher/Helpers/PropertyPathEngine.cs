using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using Common;

using GenericSynthesisPatcher.Games.Universal;
using GenericSynthesisPatcher.Rules;

using Loqui;

using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Strings;

namespace GenericSynthesisPatcher.Helpers
{
    /// <summary>
    ///     Collection-aware runtime for cached property-path descriptors. Root HPU comparisons
    ///     use generated Mutagen equality masks; deep collection leaves use bounded structural
    ///     normalization to preserve per-parent comparison semantics.
    /// </summary>
    internal static class PropertyPathEngine
    {
        private const char IdentitySeparator = '\u001f';
        private const int MaxCanonicalDepth = 64;
        private const int MaxCanonicalCollectionItems = 100_000;
        private static readonly ConcurrentDictionary<(Type RecordType, string Path), Lazy<MajorRecord.TranslationMask?>> HpuMasks = new();
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> CanonicalProperties = new();
        private static readonly ConcurrentDictionary<(Type RuntimeType, string Name), PropertyInfo?> RuntimeProperties = new();
        private static readonly ConcurrentDictionary<(Type Source, Type Target), MethodInfo?> DeepCopyMethods = new();

        public static bool Equals (PropertyAction property, object left, object right)
        {
            var descriptor = RequireDescriptor(property);
            if (!HasGenderedSegment(descriptor) && !descriptor.HasCollectionBoundary && left is IMajorRecordGetter lhsRecord
                && right is IMajorRecordGetter rhsRecord && TryGetHpuMask(descriptor, out var mask))
                return lhsRecord.Equals(rhsRecord, mask);
            var lhs = ReadLeaves(left, descriptor);
            var rhs = ReadLeaves(right, descriptor);

            return lhs.Count == rhs.Count
                && lhs.All(x => rhs.TryGetValue(x.Key, out var value) && Canonicalize(x.Value) == Canonicalize(value));
        }

        public static int Forward (ProcessingKeys proKeys, object source)
        {
            return Forward(proKeys.Property, source, proKeys.GetPatchRecord());
        }

        internal static int Forward (PropertyAction property, object source, object target)
        {
            var descriptor = RequireDescriptor(property);
            if (!HasGenderedSegment(descriptor) && !descriptor.HasCollectionBoundary && source is IMajorRecordGetter sourceRecord
                && target is IMajorRecordInternal targetRecord && TryGetHpuMask(descriptor, out var mask))
            {
                if (((IMajorRecordGetter)targetRecord).Equals(sourceRecord, mask))
                    return 0;
                // Mutagen 0.54.4 copies some gendered fields even when their mask is off.
                // Preserve those unselected fields around the generated copy operation.
                var preserved = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.CanWrite && PropertyPathSegment.IsGendered(p.PropertyType))
                    .Select(p => (Property: p, Value: p.GetValue(target))).ToArray();
                try { targetRecord.DeepCopyIn(sourceRecord, mask); }
                finally { foreach (var entry in preserved) entry.Property.SetValue(target, entry.Value); }
                return 1;
            }
            return ForwardRecursive(source, target, descriptor, 0, [], null, 0);
        }

        public static int ForwardHPU (ProcessingKeys proKeys, IEnumerable<IModContext<IMajorRecordGetter>> allRecordMods, IEnumerable<ModKey>? endNodes)
        {
            var descriptor = RequireDescriptor(proKeys.Property);
            var contexts = allRecordMods.ToList();
            var maps = contexts.ToDictionary(x => x.ModKey, x => ReadLeaves(x.Record, descriptor));
            var origin = ReadLeaves(proKeys.GetOriginRecord(), descriptor);
            var keys = maps.Values.SelectMany(x => x.Keys).Concat(origin.Keys).Distinct(StringComparer.Ordinal).ToList();
            bool nonNull = proKeys.Rule.HasForwardOption(ForwardOptions._nonNullMod);
            int changes = 0;

            foreach (string key in keys)
            {
                var history = new List<string>();
                history.Add(origin.TryGetValue(key, out var originValue) ? Canonicalize(originValue, descriptor.CanonicalPath) : "<missing>");

                IModContext<IMajorRecordGetter>? selected = null;
                int selectedHistory = -1;

                foreach (var context in contexts.AsEnumerable().Reverse())
                {
                    if (!maps[context.ModKey].TryGetValue(key, out var value) || (nonNull && Mod.IsNullOrEmpty(value)))
                        continue;

                    string canonical = Canonicalize(value, descriptor.CanonicalPath);
                    int historyIndex = history.IndexOf(canonical);
                    if (historyIndex < 0)
                    {
                        historyIndex = history.Count;
                        history.Add(canonical);
                    }

                    if ((endNodes is null || endNodes.Contains(context.ModKey)) && historyIndex >= selectedHistory)
                    {
                        selected = context;
                        selectedHistory = historyIndex;
                    }
                }

                if (selected is not null)
                {
                    string[] identities = key.Length == 0 ? [] : key.Split(IdentitySeparator);
                    changes += ForwardRecursive(selected.Record, proKeys.GetPatchRecord(), descriptor, 0, [], identities, 0);
                }
            }

            return changes;
        }

        public static bool IsNullOrEmpty (PropertyAction property, object record)
        {
            var leaves = ReadLeaves(record, RequireDescriptor(property));
            return leaves.Count == 0 || leaves.Values.All(Mod.IsNullOrEmpty);
        }

        public static int MergeAll (ProcessingKeys proKeys)
        {
            var contexts = proKeys.RecordContexts
                .Where(x => !x.ModKey.Equals(proKeys.Record.FormKey.ModKey));

            return MergeSelected(proKeys, contexts);
        }

        public static int MergeSelected (ProcessingKeys proKeys, IEnumerable<IModContext<IMajorRecordGetter>> contexts)
        {
            var descriptor = RequireDescriptor(proKeys.Property);
            if (!descriptor.LeafIsCollection)
                throw new InvalidOperationException($"Merge requires a list leaf, but '{descriptor.CanonicalPath}' ends in {descriptor.Segments[^1].Property.PropertyType.FullName}.");

            int changes = 0;
            foreach (var context in contexts.Reverse())
                changes += MergeRecursive(context.Record, proKeys.GetPatchRecord(), descriptor, 0);

            return changes;
        }

        internal static bool CanMergeFlags(PropertyPathDescriptor? descriptor)
        {
            if (descriptor is null || descriptor.HasCollectionBoundary || descriptor.LeafIsCollection) return false;
            var type = descriptor.Segments[^1].Property.PropertyType;
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsEnum && type.IsDefined(typeof(FlagsAttribute), false);
        }

        internal static object? CombineFlags(Type enumType, object? current, IEnumerable<object?> sources)
        {
            enumType = Nullable.GetUnderlyingType(enumType) ?? enumType;
            var code = Type.GetTypeCode(Enum.GetUnderlyingType(enumType));
            bool signed = code is TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64;
            ulong bits = 0;
            bool hasValue = false;
            foreach (var value in sources.Prepend(current))
            {
                if (value is null) continue;
                hasValue = true;
                bits |= signed ? unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture))
                    : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            return hasValue ? Enum.ToObject(enumType, bits) : null;
        }

        internal static int MergeSelectedFlags(ProcessingKeys keys, IEnumerable<IModContext<IMajorRecordGetter>> contexts)
        {
            var descriptor = RequireDescriptor(keys.Property);
            if (!CanMergeFlags(descriptor)) throw new InvalidOperationException("Selected-source flags merging requires a non-collection [Flags] enum path.");
            object? current = ReadLeaves(SelectedFlagsTarget(keys), descriptor).Values.FirstOrDefault();
            object? combined = CombineFlags(descriptor.Segments[^1].Property.PropertyType, current,
                contexts.SelectMany(x => ReadLeaves(x.Record, descriptor).Values));
            if (object.Equals(current, combined)) return 0;
            if (!Mod.TrySetProperty(keys.GetPatchRecord(), descriptor.CanonicalPath, combined, -1))
                throw new InvalidOperationException($"Could not write selected-source flags to '{descriptor.CanonicalPath}'.");
            return 1;
        }

        // A seed/output override may already exist before these processing keys discover it.
        // Looking it up in the shared snapshot must not create an override on a no-op.
        internal static IMajorRecordGetter SelectedFlagsTarget(ProcessingKeys keys)
            => keys.HasPatchRecord ? keys.Record
                : keys.RecordContexts.FirstOrDefault(x => x.ModKey == Global.Game.State.PatchMod.ModKey)?.Record ?? keys.Record;

        internal static int Merge (PropertyAction property, object source, object target)
        {
            var descriptor = RequireDescriptor(property);
            if (!descriptor.LeafIsCollection)
                throw new InvalidOperationException($"Merge requires a list leaf, but '{descriptor.CanonicalPath}' is not a list.");
            return MergeRecursive(source, target, descriptor, 0);
        }

        public static IModContext<IMajorRecordGetter>? FindHPU (ProcessingKeys proKeys, IEnumerable<IModContext<IMajorRecordGetter>> allRecordMods, IEnumerable<ModKey>? endNodes)
        {
            var descriptor = RequireDescriptor(proKeys.Property);
            if (!HasGenderedSegment(descriptor) && !descriptor.HasCollectionBoundary && TryGetHpuMask(descriptor, out var mask))
                return FindHPUWithMask(proKeys, allRecordMods, endNodes, descriptor, mask);

            bool nonNull = proKeys.Rule.HasForwardOption(ForwardOptions._nonNullMod);
            if (!TryReadSingleValue(proKeys.GetOriginRecord(), descriptor, out var defaultValue))
                return null;

            var history = new List<string> { Canonicalize(defaultValue, descriptor.CanonicalPath) };
            IModContext<IMajorRecordGetter>? hpu = null;
            int hpuHistory = -1;

            foreach (var context in allRecordMods.Reverse())
            {
                if (!TryReadSingleValue(context.Record, descriptor, out var current)
                    || (nonNull && Mod.IsNullOrEmpty(current)))
                {
                    continue;
                }

                string canonical = Canonicalize(current, descriptor.CanonicalPath);
                int historyIndex = history.IndexOf(canonical);
                if (historyIndex < 0)
                {
                    historyIndex = history.Count;
                    history.Add(canonical);
                }

                if ((endNodes is null || endNodes.Contains(context.ModKey)) && hpuHistory <= historyIndex)
                {
                    hpu = context;
                    hpuHistory = historyIndex;
                }
            }

            return hpu;
        }

        private static IModContext<IMajorRecordGetter>? FindHPUWithMask (ProcessingKeys proKeys, IEnumerable<IModContext<IMajorRecordGetter>> allRecordMods, IEnumerable<ModKey>? endNodes, PropertyPathDescriptor descriptor, MajorRecord.TranslationMask mask)
        {
            bool nonNull = proKeys.Rule.HasForwardOption(ForwardOptions._nonNullMod);
            var history = new List<IMajorRecordGetter> { proKeys.GetOriginRecord() };
            IModContext<IMajorRecordGetter>? hpu = null;
            int hpuHistory = -1;

            foreach (var context in allRecordMods.Reverse())
            {
                if (nonNull
                    && (!TryReadSingleValue(context.Record, descriptor, out var current) || Mod.IsNullOrEmpty(current)))
                {
                    continue;
                }

                int historyIndex = history.FindIndex(record => record.Equals(context.Record, mask));
                if (historyIndex < 0)
                {
                    historyIndex = history.Count;
                    history.Add(context.Record);
                }

                if ((endNodes is null || endNodes.Contains(context.ModKey)) && hpuHistory <= historyIndex)
                {
                    hpu = context;
                    hpuHistory = historyIndex;
                }
            }

            return hpu;
        }

        internal static bool EqualsByFieldMask (PropertyAction property, IMajorRecordGetter left, IMajorRecordGetter right)
        {
            var descriptor = RequireDescriptor(property);
            if (HasGenderedSegment(descriptor)) return Equals(property, left, right);
            return !descriptor.HasCollectionBoundary
                && TryGetHpuMask(descriptor, out var mask)
                && left.Equals(right, mask);
        }

        internal static string Canonicalize (object? value, string path = "$value")
        {
            var builder = new StringBuilder();
            AppendCanonical(builder, value, new HashSet<object>(ReferenceEqualityComparer.Instance), path, 0);
            return builder.ToString();
        }

        private static void AppendCanonical (StringBuilder builder, object? value, HashSet<object> visited, string path, int depth)
        {
            if (depth > MaxCanonicalDepth)
                throw new InvalidDataException($"Structural comparison exceeded the maximum depth of {MaxCanonicalDepth} at '{path}' ({value?.GetType().FullName ?? "null"}).");

            if (value is null)
            {
                builder.Append("null");
                return;
            }

            Type type = value.GetType();
            if (value is IAssetLinkGetter asset)
            {
                builder.Append("Asset:").Append(asset.GivenPath.Length).Append(':').Append(asset.GivenPath);
                return;
            }
            if (value is string text)
            {
                builder.Append("string:").Append(text.Length).Append(':').Append(text);
                return;
            }

            if (value is ITranslatedStringGetter translatedString)
            {
                builder.Append("TranslatedString:");
                AppendCanonical(builder, translatedString.String, visited, path + ".String", depth + 1);
                return;
            }

            if (type.IsEnum || type.IsPrimitive || value is decimal || value is Guid || value is DateTime || value is TimeSpan || value is FormKey || value is ModKey)
            {
                builder.Append(type.FullName).Append(':').Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            PropertyInfo? formKeyProperty = GetRuntimeProperty(type, "FormKey");
            if (formKeyProperty?.PropertyType == typeof(FormKey))
            {
                builder.Append("FormLink:");
                AppendCanonical(builder, formKeyProperty.GetValue(value), visited, path + ".FormKey", depth + 1);
                return;
            }

            bool tracked = false;
            if (!type.IsValueType)
            {
                if (!visited.Add(value))
                    throw new InvalidDataException($"Structural comparison detected a reference cycle at '{path}' ({type.FullName}).");
                tracked = true;
            }

            try
            {
                if (PropertyPathSegment.IsGendered(type))
                {
                    builder.Append("Gendered{");
                    foreach (string side in new[] { "Male", "Female" })
                    {
                        builder.Append(side).Append('=');
                        AppendCanonical(builder, GetRuntimeProperty(type, side)!.GetValue(value), visited, path + "." + side, depth + 1);
                        builder.Append(';');
                    }
                    builder.Append('}');
                    return;
                }
                if (value is IEnumerable enumerable)
                {
                    builder.Append('[');
                    int index = 0;
                    foreach (object? item in enumerable)
                    {
                        if (index >= MaxCanonicalCollectionItems)
                            throw new InvalidDataException($"Structural comparison exceeded the maximum collection size of {MaxCanonicalCollectionItems} at '{path}' ({type.FullName}).");

                        AppendCanonical(builder, item, visited, $"{path}[{index}]", depth + 1);
                        builder.Append(';');
                        index++;
                    }
                    builder.Append(']');
                    return;
                }

                Type dataType = value is ILoquiObject loqui ? loqui.Registration.ClassType : type;
                builder.Append('{').Append(dataType.FullName).Append('|');
                foreach (var property in CanonicalProperties.GetOrAdd(dataType, static runtimeType => runtimeType
                             .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(IsEligibleCanonicalProperty)
                             .OrderBy(x => x.Name, StringComparer.Ordinal)
                             .ToArray()))
                {
                    builder.Append(property.Name).Append('=');
                    object? propertyValue;
                    try
                    {
                        propertyValue = GetRuntimeProperty(type, property.Name)!.GetValue(value);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidDataException($"Structural comparison could not read '{path}.{property.Name}' on {type.FullName}.", ex);
                    }

                    AppendCanonical(builder, propertyValue, visited, path + "." + property.Name, depth + 1);
                    builder.Append(';');
                }
                builder.Append('}');
            }
            finally
            {
                if (tracked)
                    visited.Remove(value);
            }
        }

        private static int ForwardRecursive (object? source, object? target, PropertyPathDescriptor descriptor, int segmentIndex, List<string> identityPath, string[]? desiredPath, int identityDepth)
        {
            if (source is null || target is null)
                return 0;

            var segment = descriptor.Segments[segmentIndex];
            PropertyInfo? sourceProperty = GetRuntimeProperty(source.GetType(), segment.Property.Name);
            PropertyInfo? targetProperty = GetRuntimeProperty(target.GetType(), segment.Property.Name);
            if (sourceProperty is null || targetProperty is null)
                throw new MissingMemberException($"Unable to access canonical segment '{segment.Property.Name}' on {source.GetType().FullName} or {target.GetType().FullName}.");

            object? sourceValue = sourceProperty.GetValue(source);
            bool leaf = segmentIndex == descriptor.Segments.Count - 1;
            if (leaf)
            {
                if (desiredPath is not null && identityDepth != desiredPath.Length)
                    return 0;

                return segment.IsCollection
                    ? ReplaceList(sourceValue, target, targetProperty, segment.ElementType ?? typeof(object))
                    : SetScalar(sourceValue, target, targetProperty);
            }

            if (segment.IsCollection)
            {
                object? targetValue = targetProperty.GetValue(target);
                if (sourceValue is not IEnumerable sourceItems)
                    return 0;

                var targetItems = Enumerate(targetValue).ToList();
                var targetByIdentity = BuildIdentityMap(targetItems, descriptor.CanonicalPath, segment.Property.Name);
                int changes = 0;
                var sourceList = Enumerate(sourceItems).ToList();
                _ = BuildIdentityMap(sourceList, descriptor.CanonicalPath, segment.Property.Name);

                for (int index = 0; index < sourceList.Count; index++)
                {
                    object? sourceItem = sourceList[index];
                    string identity = GetIdentity(sourceItem, index);
                    if (desiredPath is not null && (identityDepth >= desiredPath.Length || desiredPath[identityDepth] != identity))
                        continue;

                    if (!targetByIdentity.TryGetValue(identity, out object? targetItem))
                    {
                        object? clone = CloneValue(sourceItem, segment.ElementType ?? sourceItem?.GetType() ?? typeof(object));
                        AddToList(targetValue, clone);
                        targetByIdentity[identity] = clone;
                        changes++;
                    }
                    else
                    {
                        identityPath.Add(identity);
                        changes += ForwardRecursive(sourceItem, targetItem, descriptor, segmentIndex + 1, identityPath, desiredPath, identityDepth + 1);
                        identityPath.RemoveAt(identityPath.Count - 1);
                    }
                }

                return changes;
            }

            object? targetValueNonCollection = targetProperty.GetValue(target);
            if (sourceValue is null)
                return 0;

            if (targetValueNonCollection is null)
            {
                if (PropertyPathSegment.IsGendered(targetProperty.PropertyType))
                {
                    Type pairType = typeof(GenderedItem<>).MakeGenericType(targetProperty.PropertyType.GetGenericArguments()[0]);
                    var pair = Activator.CreateInstance(pairType, new object?[] { null, null });
                    targetProperty.SetValue(target, pair);
                    return ForwardRecursive(sourceValue, pair, descriptor, segmentIndex + 1, identityPath, desiredPath, identityDepth);
                }
                object? clone = CloneValue(sourceValue, targetProperty.PropertyType);
                if (!targetProperty.CanWrite)
                    throw new InvalidOperationException($"Cannot create missing parent '{segment.Property.Name}' because {target.GetType().FullName}.{targetProperty.Name} is read-only.");
                targetProperty.SetValue(target, clone);
                return 1;
            }

            return ForwardRecursive(sourceValue, targetValueNonCollection, descriptor, segmentIndex + 1, identityPath, desiredPath, identityDepth);
        }

        private static int MergeRecursive (object? source, object? target, PropertyPathDescriptor descriptor, int segmentIndex)
        {
            if (source is null || target is null)
                return 0;

            var segment = descriptor.Segments[segmentIndex];
            PropertyInfo? sourceProperty = GetRuntimeProperty(source.GetType(), segment.Property.Name);
            PropertyInfo? targetProperty = GetRuntimeProperty(target.GetType(), segment.Property.Name);
            if (sourceProperty is null || targetProperty is null)
                throw new MissingMemberException($"Unable to access canonical segment '{segment.Property.Name}'.");

            object? sourceValue = sourceProperty.GetValue(source);
            bool leaf = segmentIndex == descriptor.Segments.Count - 1;
            if (leaf)
            {
                if (targetProperty.PropertyType.IsArray)
                {
                    var items = Enumerate(targetProperty.GetValue(target)).ToList();
                    var seen = items.Select(x => Canonicalize(x)).ToHashSet(StringComparer.Ordinal);
                    foreach (var item in Enumerate(sourceValue))
                        if (seen.Add(Canonicalize(item))) items.Add(item);
                    return ReplaceList(items, target, targetProperty, segment.ElementType ?? typeof(object));
                }
                if (targetProperty.GetValue(target) is null)
                    return ReplaceList(sourceValue, target, targetProperty, segment.ElementType ?? typeof(object));
                return MergeList(sourceValue, targetProperty.GetValue(target), segment.ElementType ?? typeof(object));
            }

            if (segment.IsCollection)
            {
                object? targetValue = targetProperty.GetValue(target);
                var targetItems = Enumerate(targetValue).ToList();
                var targetByIdentity = BuildIdentityMap(targetItems, descriptor.CanonicalPath, segment.Property.Name);
                var sourceList = Enumerate(sourceValue).ToList();
                _ = BuildIdentityMap(sourceList, descriptor.CanonicalPath, segment.Property.Name);
                int changes = 0;

                for (int index = 0; index < sourceList.Count; index++)
                {
                    object? sourceItem = sourceList[index];
                    string identity = GetIdentity(sourceItem, index);
                    if (!targetByIdentity.TryGetValue(identity, out object? targetItem))
                    {
                        object? clone = CloneValue(sourceItem, segment.ElementType ?? sourceItem?.GetType() ?? typeof(object));
                        AddToList(targetValue, clone);
                        targetByIdentity[identity] = clone;
                        changes++;
                    }
                    else
                    {
                        changes += MergeRecursive(sourceItem, targetItem, descriptor, segmentIndex + 1);
                    }
                }

                return changes;
            }

            if (sourceValue is not null && targetProperty.GetValue(target) is null)
            {
                if (!targetProperty.CanWrite)
                    throw new InvalidOperationException($"Cannot create missing parent '{segment.Property.Name}' in '{descriptor.CanonicalPath}'.");
                if (PropertyPathSegment.IsGendered(targetProperty.PropertyType))
                {
                    Type pairType = typeof(GenderedItem<>).MakeGenericType(targetProperty.PropertyType.GetGenericArguments()[0]);
                    var pair = Activator.CreateInstance(pairType, new object?[] { null, null });
                    targetProperty.SetValue(target, pair);
                    return MergeRecursive(sourceValue, pair, descriptor, segmentIndex + 1);
                }
                targetProperty.SetValue(target, CloneValue(sourceValue, targetProperty.PropertyType));
                return 1;
            }
            return MergeRecursive(sourceValue, targetProperty.GetValue(target), descriptor, segmentIndex + 1);
        }

        private static Dictionary<string, object?> ReadLeaves (object root, PropertyPathDescriptor descriptor)
        {
            var output = new Dictionary<string, object?>(StringComparer.Ordinal);
            ReadLeavesRecursive(root, descriptor, 0, [], output);
            return output;
        }

        private static void ReadLeavesRecursive (object? current, PropertyPathDescriptor descriptor, int segmentIndex, List<string> identities, Dictionary<string, object?> output)
        {
            if (current is null)
                return;

            var segment = descriptor.Segments[segmentIndex];
            PropertyInfo? property = GetRuntimeProperty(current.GetType(), segment.Property.Name);
            if (property is null)
                throw new MissingMemberException(current.GetType().FullName, segment.Property.Name);

            object? value = property.GetValue(current);
            if (segmentIndex == descriptor.Segments.Count - 1)
            {
                string key = string.Join(IdentitySeparator, identities);
                if (!output.TryAdd(key, value))
                    throw new InvalidDataException($"Duplicate correlated parent identity '{key}' while reading '{descriptor.CanonicalPath}'.");
                return;
            }

            if (segment.IsCollection)
            {
                var items = Enumerate(value).ToList();
                _ = BuildIdentityMap(items, descriptor.CanonicalPath, segment.Property.Name);
                for (int index = 0; index < items.Count; index++)
                {
                    identities.Add(GetIdentity(items[index], index));
                    ReadLeavesRecursive(items[index], descriptor, segmentIndex + 1, identities, output);
                    identities.RemoveAt(identities.Count - 1);
                }
            }
            else
            {
                ReadLeavesRecursive(value, descriptor, segmentIndex + 1, identities, output);
            }
        }

        private static bool TryReadSingleValue (object record, PropertyPathDescriptor descriptor, out object? value)
        {
            var leaves = ReadLeaves(record, descriptor);
            if (leaves.Count == 1 && leaves.TryGetValue(string.Empty, out value))
                return true;

            value = null;
            return false;
        }

        private static Dictionary<string, object?> BuildIdentityMap (List<object?> items, string path, string segment)
        {
            var output = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int index = 0; index < items.Count; index++)
            {
                string identity = GetIdentity(items[index], index);
                if (!output.TryAdd(identity, items[index]))
                    throw new InvalidDataException($"Duplicate parent identity '{identity}' in collection segment '{segment}' of '{path}'.");
            }
            return output;
        }

        private static string GetIdentity (object? item, int index)
        {
            if (item is null)
                return $"ordinal:{index}";

            Type type = item.GetType();
            if (type.IsPrimitive || type.IsEnum || item is string || item is FormKey || item is ModKey)
                return "value:" + Canonicalize(item);

            foreach (string name in new[] { "FormKey", "ID", "AliasID", "Index" })
            {
                PropertyInfo? property = GetRuntimeProperty(type, name);
                if (property is not null && property.CanRead)
                {
                    object? value = property.GetValue(item);
                    if (value is not null)
                        return name + ':' + Canonicalize(value);
                }
            }

            return $"ordinal:{index}";
        }

        private static int ReplaceList (object? sourceValue, object target, PropertyInfo targetProperty, Type elementType)
        {
            object? targetValue = targetProperty.GetValue(target);
            if (targetProperty.PropertyType.IsArray)
            {
                var items = Enumerate(sourceValue).ToList();
                if (Canonicalize(sourceValue) == Canonicalize(targetValue)) return 0;
                if (!targetProperty.CanWrite)
                    throw new InvalidOperationException($"Array '{targetProperty.Name}' requires a writable property for replacement.");
                if (sourceValue is null)
                    targetProperty.SetValue(target, null);
                else
                {
                    var replacement = Array.CreateInstance(elementType, items.Count);
                    for (int i = 0; i < items.Count; i++) replacement.SetValue(CloneValue(items[i], elementType), i);
                    targetProperty.SetValue(target, replacement);
                }
                return Math.Max(1, items.Count);
            }
            if (targetValue is null)
            {
                if (!targetProperty.CanWrite)
                    throw new InvalidOperationException($"List property {target.GetType().FullName}.{targetProperty.Name} is null and read-only.");
                targetValue = Activator.CreateInstance(targetProperty.PropertyType);
                targetProperty.SetValue(target, targetValue);
            }

            var sourceItems = Enumerate(sourceValue).ToList();
            var targetItems = Enumerate(targetValue).ToList();
            if (targetItems.Select(item => Canonicalize(item)).SequenceEqual(sourceItems.Select(item => Canonicalize(item)), StringComparer.Ordinal))
                return 0;

            int changes = targetItems.Count;
            ClearList(targetValue!);
            foreach (object? item in sourceItems)
            {
                AddToList(targetValue, CloneValue(item, elementType));
                changes++;
            }
            return changes;
        }

        private static int MergeList (object? sourceValue, object? targetValue, Type elementType)
        {
            if (targetValue is null)
                throw new InvalidOperationException("Target list is null and cannot be merged.");

            var existing = new HashSet<string>(Enumerate(targetValue).Select(item => Canonicalize(item)), StringComparer.Ordinal);
            int changes = 0;
            foreach (object? item in Enumerate(sourceValue))
            {
                if (existing.Add(Canonicalize(item)))
                {
                    AddToList(targetValue, CloneValue(item, elementType));
                    changes++;
                }
            }
            return changes;
        }

        private static int SetScalar (object? sourceValue, object target, PropertyInfo targetProperty)
        {
            object? current = targetProperty.GetValue(target);
            if (Canonicalize(current) == Canonicalize(sourceValue))
                return 0;

            PropertyInfo? sourceFormKey = sourceValue is null ? null : GetRuntimeProperty(sourceValue.GetType(), "FormKey");
            PropertyInfo? currentFormKey = current is null ? null : GetRuntimeProperty(current.GetType(), "FormKey");
            if (current is not null && currentFormKey?.PropertyType == typeof(FormKey))
            {
                if (sourceValue is null || (sourceFormKey?.GetValue(sourceValue) is FormKey fk && fk.IsNull))
                {
                    MethodInfo? setNull = current.GetType().GetMethod("SetToNull", Type.EmptyTypes);
                    if (setNull is not null)
                    {
                        setNull.Invoke(current, null);
                        return 1;
                    }
                }
                else if (sourceFormKey?.GetValue(sourceValue) is FormKey formKey)
                {
                    MethodInfo? setTo = current.GetType().GetMethod("SetTo", [typeof(FormKey)]);
                    if (setTo is not null)
                    {
                        setTo.Invoke(current, [formKey]);
                        return 1;
                    }
                }
            }

            if (!targetProperty.CanWrite)
                throw new InvalidOperationException($"Leaf property {target.GetType().FullName}.{targetProperty.Name} is read-only.");

            targetProperty.SetValue(target, CloneValue(sourceValue, targetProperty.PropertyType));
            return 1;
        }

        private static object? CloneValue (object? source, Type targetType)
        {
            if (source is null)
                return null;

            Type sourceType = source.GetType();
            Type unwrappedTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (sourceType.IsValueType || source is string || source is FormKey || source is ModKey)
                return source;
            if (source is IAssetLinkGetter asset)
                return Activator.CreateInstance(unwrappedTarget, asset.GivenPath);
            if (source is IFormLinkGetter link && unwrappedTarget.IsGenericType)
            {
                Type definition = unwrappedTarget.Name.Contains("Nullable", StringComparison.Ordinal)
                    ? typeof(FormLinkNullable<>) : typeof(FormLink<>);
                return Activator.CreateInstance(definition.MakeGenericType(unwrappedTarget.GetGenericArguments()[0]), link.FormKey);
            }

            MethodInfo? method = DeepCopyMethods.GetOrAdd((sourceType, unwrappedTarget), static pair => FindDeepCopyMethod(pair.Source, pair.Target));
            if (method is not null)
            {
                var parameters = method.GetParameters();
                object?[] args = parameters.Length == 1 ? [source] : [source, null];
                object? copy = method.Invoke(null, args);
                if (copy is not null)
                    return copy;
            }

            if (PropertyPathSegment.IsGendered(sourceType))
            {
                Type entryType = unwrappedTarget.GetGenericArguments()[0];
                Type pairType = typeof(GenderedItem<>).MakeGenericType(entryType);
                return Activator.CreateInstance(pairType,
                    CloneValue(GetRuntimeProperty(sourceType, "Male")!.GetValue(source), entryType),
                    CloneValue(GetRuntimeProperty(sourceType, "Female")!.GetValue(source), entryType));
            }

            ConstructorInfo? constructor = unwrappedTarget.GetConstructors()
                .FirstOrDefault(x => x.GetParameters() is [{ } parameter] && parameter.ParameterType.IsInstanceOfType(source));
            if (constructor is not null)
                return constructor.Invoke([source]);

            throw new InvalidOperationException($"No safe copy strategy exists from {sourceType.FullName} to {targetType.FullName}.");
        }

        private static MethodInfo? FindDeepCopyMethod (Type sourceType, Type targetType)
        {
            foreach (Type type in sourceType.Assembly.GetTypes().Where(x => x.IsAbstract && x.IsSealed && x.Name.EndsWith("MixIn", StringComparison.Ordinal)))
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(x => x.Name == "DeepCopy" && targetType.IsAssignableFrom(x.ReturnType)))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length is < 1 or > 2 || !parameters[0].ParameterType.IsAssignableFrom(sourceType))
                        continue;

                    if (parameters.Length == 1 || parameters[1].HasDefaultValue || parameters[1].ParameterType.Name.Contains("TranslationMask", StringComparison.Ordinal))
                        return method;
                }
            }
            return null;
        }

        private static IEnumerable<object?> Enumerate (object? value)
        {
            if (value is not IEnumerable enumerable || value is string)
                yield break;

            foreach (object? item in enumerable)
                yield return item;
        }

        private static void AddToList (object? list, object? value)
        {
            if (list is null)
                throw new InvalidOperationException("Target collection is null.");
            if (list is IList nonGeneric)
            {
                _ = nonGeneric.Add(value);
                return;
            }

            MethodInfo? add = list.GetType().GetMethods().FirstOrDefault(x => x.Name == "Add" && x.GetParameters().Length == 1);
            if (add is null)
                throw new InvalidOperationException($"Collection type {list.GetType().FullName} does not expose Add.");
            add.Invoke(list, [value]);
        }

        private static void ClearList (object list)
        {
            if (list is IList nonGeneric)
            {
                nonGeneric.Clear();
                return;
            }

            MethodInfo? clear = list.GetType().GetMethod("Clear", Type.EmptyTypes);
            if (clear is null)
                throw new InvalidOperationException($"Collection type {list.GetType().FullName} does not expose Clear.");
            clear.Invoke(list, null);
        }

        private static PropertyInfo? GetRuntimeProperty (Type type, string name)
            => RuntimeProperties.GetOrAdd((type, name), static key => key.RuntimeType.GetProperty(key.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));

        private static bool TryGetHpuMask (PropertyPathDescriptor descriptor, out MajorRecord.TranslationMask mask)
        {
            var lazy = HpuMasks.GetOrAdd(
                (descriptor.RecordType.GetterType, descriptor.CanonicalPath),
                _ => new Lazy<MajorRecord.TranslationMask?>(() => CreateHpuMask(descriptor), LazyThreadSafetyMode.ExecutionAndPublication));

            mask = lazy.Value!;
            return mask is not null;
        }

        private static MajorRecord.TranslationMask? CreateHpuMask (PropertyPathDescriptor descriptor)
        {
            if (!TranslationMaskFactory.TryCreate(descriptor.RecordType, false, [], out var rootMask)
                || rootMask is not MajorRecord.TranslationMask majorRecordMask)
                return null;

            return ConfigureMask(rootMask, descriptor, 0) ? majorRecordMask : null;
        }

        private static bool ConfigureMask (ITranslationMask currentMask, PropertyPathDescriptor descriptor, int index)
        {
            {
                string segmentName = descriptor.Segments[index].Property.Name;
                bool leaf = index == descriptor.Segments.Count - 1;
                if (leaf)
                    return currentMask.TrySetValue(segmentName, true, StringComparison.OrdinalIgnoreCase);

                if (!currentMask.TryGetMaskField(segmentName, StringComparison.OrdinalIgnoreCase, out var field))
                    return false;

                if (PropertyPathSegment.IsGendered(field.FieldType))
                {
                    string side = descriptor.Segments[index + 1].Property.Name;
                    if (side is not ("Male" or "Female"))
                        return false;
                    Type entryType = field.FieldType.GetGenericArguments()[0];
                    bool sideIsLeaf = index + 1 == descriptor.Segments.Count - 1;
                    object selected;
                    object unselected;
                    if (entryType == typeof(bool))
                    {
                        if (!sideIsLeaf) return false;
                        selected = true;
                        unselected = false;
                    }
                    else
                    {
                        if (!TranslationMaskFactory.TryCreate(entryType, sideIsLeaf, true, out var selectedMask)
                            || !TranslationMaskFactory.TryCreate(entryType, false, false, out var unselectedMask)
                            || (!sideIsLeaf && !ConfigureMask(selectedMask, descriptor, index + 2)))
                            return false;
                        selected = selectedMask;
                        unselected = unselectedMask;
                    }
                    Type pairType = typeof(GenderedItem<>).MakeGenericType(entryType);
                    field.SetValue(currentMask, Activator.CreateInstance(pairType,
                        side == "Male" ? new[] { selected, unselected } : new[] { unselected, selected }));
                    return true;
                }

                if (!field.FieldType.IsAssignableTo(typeof(ITranslationMask))
                    || !TranslationMaskFactory.TryCreate(field.FieldType, false, out var childMask)
                    || !currentMask.TrySetValue(segmentName, childMask, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return ConfigureMask(childMask, descriptor, index + 1);
            }
        }

        internal static bool CanUseFieldMask (PropertyPathDescriptor descriptor)
            => !descriptor.HasCollectionBoundary && TryGetHpuMask(descriptor, out _);

        private static bool HasGenderedSegment (PropertyPathDescriptor descriptor)
            => descriptor.Segments.Any(x => PropertyPathSegment.IsGendered(x.Property.PropertyType));

        private static PropertyPathDescriptor RequireDescriptor (PropertyAction property)
            => property.Descriptor ?? throw new InvalidOperationException($"No property-path descriptor was built for '{property.PropertyName}'.");

        private static bool IsEligibleCanonicalProperty (PropertyInfo property)
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0 || IsInfrastructureProperty(property.Name))
                return false;

            Type propertyType = property.PropertyType;
            return propertyType != typeof(Type)
                && !typeof(MemberInfo).IsAssignableFrom(propertyType)
                && !typeof(Delegate).IsAssignableFrom(propertyType)
                && !typeof(ILoquiRegistration).IsAssignableFrom(propertyType)
                && !typeof(ITranslationMask).IsAssignableFrom(propertyType);
        }

        private static bool IsInfrastructureProperty (string name)
            => name is "Registration" or "StaticRegistration" or "CommonInstance" or "GameRelease"
                or "GetterType" or "SetterType" or "ClassType";

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();
            public new bool Equals (object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode (object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
