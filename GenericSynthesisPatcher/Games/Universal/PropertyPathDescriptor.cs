using System.Collections;
using System.Reflection;

using Loqui;
using Common;
using Mutagen.Bethesda.Plugins.Records;

namespace GenericSynthesisPatcher.Games.Universal
{
    /// <summary>
    ///     Cached description of a configured property path. The descriptor is built from the
    ///     writable record model and is also usable against getter/overlay instances because
    ///     runtime access is resolved by canonical segment name.
    /// </summary>
    public sealed class PropertyPathDescriptor
    {
        public PropertyPathDescriptor (ILoquiRegistration recordType, string suppliedPath, string canonicalPath, IReadOnlyList<PropertyPathSegment> segments)
        {
            RecordType = recordType;
            SuppliedPath = suppliedPath;
            CanonicalPath = canonicalPath;
            Segments = segments;
        }

        public string CanonicalPath { get; }

        public bool HasCollectionBoundary => Segments.Take(Math.Max(0, Segments.Count - 1)).Any(x => x.IsCollection);

        public bool LeafIsCollection => Segments.Count != 0 && Segments[^1].IsCollection;

        public ILoquiRegistration RecordType { get; }

        public IReadOnlyList<PropertyPathSegment> Segments { get; }

        public string SuppliedPath { get; }
    }

    public sealed class PropertyPathSegment
    {
        public PropertyPathSegment (Type ownerType, PropertyInfo property)
        {
            OwnerType = ownerType;
            Property = property;
            ElementType = TryGetCollectionElementType(property.PropertyType);
        }

        public Type? ElementType { get; }

        public bool IsCollection => ElementType is not null;

        public Type OwnerType { get; }

        public PropertyInfo Property { get; }

        public static Type? TryGetCollectionElementType (Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string) || type == typeof(byte[]) || IsGendered(type))
                return null;

            if (type.IsArray)
                return type.GetElementType();

            Type? enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? type
                : type.GetInterfaces().FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));

            var element = enumerable?.GetGenericArguments()[0];
            if (element is null)
                return null;
            // Descriptors use the mutable model: enumeration alone is not a mutation strategy.
            return type.GetInterfaces().Append(type).Any(x => x.IsGenericType
                && x.GetGenericTypeDefinition() == typeof(IList<>))
                ? element : null;
        }

        public static bool IsGendered (Type type)
            => type.GetInterfaces().Append(type).Any(x => x.IsGenericType
                && (x.GetGenericTypeDefinition() == typeof(GenderedItem<>)
                    || x.GetGenericTypeDefinition() == typeof(IGenderedItem<>)
                    || x.GetGenericTypeDefinition() == typeof(IGenderedItemGetter<>)));
    }
}
