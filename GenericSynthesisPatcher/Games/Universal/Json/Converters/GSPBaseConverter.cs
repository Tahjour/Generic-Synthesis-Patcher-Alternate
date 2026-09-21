using GenericSynthesisPatcher.Rules;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GenericSynthesisPatcher.Games.Universal.Json.Converters
{
    public class GSPBaseConverter : JsonConverter<GSPBase>
    {
        public override GSPBase? ReadJson (JsonReader reader, Type objectType, GSPBase? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            // Load JObject from stream
            var jObject = JObject.Load(reader);

            // Create target object based on JObject
            var target = jObject.GetValue("rules", StringComparison.OrdinalIgnoreCase) is not null ? new GSPGroup() : (GSPBase)new GSPRule();

            if (target is GSPRule rule)
            {
                var keys = jObject.Properties().Where(p => p.Name.Equals("Forward", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(p => (p.Value as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                    .Where(p => Helpers.WholeRecordForward.IsTarget(p.Name)).ToArray();
                if (keys.Length > 1) rule.WholeRecordInputErrors.Add("Duplicate All targets (including case variants) are not allowed.");
                if (keys.Any(p => !p.Name.Equals("All", StringComparison.OrdinalIgnoreCase)))
                    rule.WholeRecordInputErrors.Add("All does not accept operation prefixes.");
                // Keep one entry so malformed duplicate input can reach aggregated preflight.
                foreach (var duplicate in keys.Skip(1)) duplicate.Remove();
            }

            // Populate only after preserving whole-record syntax errors.
            serializer.Populate(jObject.CreateReader(), target);

            return target;
        }

        public override void WriteJson (JsonWriter writer, GSPBase? value, JsonSerializer serializer) => throw new NotImplementedException();
    }
}
