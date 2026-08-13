using System.Diagnostics.CodeAnalysis;

namespace GenericSynthesisPatcher.Games.Universal
{
    public readonly struct PropertyAliasMapping (Type? type, string propertyName, string? realPropertyName)
    {
        private readonly string normalizedPropertyName = Normalize(propertyName);
        private readonly int _hashcode = HashCode.Combine(type, Normalize(propertyName).GetHashCode(StringComparison.Ordinal));
        public string PropertyName { get; } = propertyName;
        public string? RealPropertyName { get; } = realPropertyName;
        public Type? Type { get; } = type;

        public static bool operator != (PropertyAliasMapping left, PropertyAliasMapping right) => !(left == right);

        public static bool operator == (PropertyAliasMapping left, PropertyAliasMapping right) => left.Equals(right);

        public override bool Equals ([NotNullWhen(true)] object? obj) => obj is PropertyAliasMapping p && Equals(p);

        public bool Equals (PropertyAliasMapping other) => Type == other.Type && normalizedPropertyName.Equals(other.normalizedPropertyName, StringComparison.Ordinal);

        public override int GetHashCode () => _hashcode;

        internal static string Normalize (string value)
            => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
