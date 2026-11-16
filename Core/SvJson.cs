using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SplatterVault.Core
{
    /// <summary>
    /// The SDK's JSON settings (Newtonsoft). Dates stay strings (<see cref="DateParseHandling.None"/>): the API sends
    /// ISO 8601 text and the SDK hands it back unchanged, in models and inside <see cref="JToken"/> values alike.
    /// Whole numbers read as <see cref="long"/> (balances and versions are at most 2^53 - 1); send 64-bit platform
    /// ids (Steam, Meta) as strings.
    /// </summary>
    public static class SvJson
    {
        /// <summary>Nesting the SDK reads. The API stores values at most 32 levels deep.</summary>
        public const int MaxDepth = 64;

        public static JsonSerializerSettings Settings { get; } = CreateSettings();

        public static JsonSerializer Serializer { get; } = JsonSerializer.Create(Settings);

        /// <summary>
        /// Writes game values (player data, item custom data): like <see cref="Serializer"/>, but null members are
        /// kept, so a value is stored as the game built it.
        /// </summary>
        public static JsonSerializer ValueSerializer { get; } = CreateValueSerializer();

        private static JsonSerializer CreateValueSerializer()
        {
            var settings = CreateSettings();
            settings.NullValueHandling = NullValueHandling.Include;
            return JsonSerializer.Create(settings);
        }

        public static JsonSerializerSettings CreateSettings()
        {
            return new JsonSerializerSettings
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double,
                NullValueHandling = NullValueHandling.Ignore,
                MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                TypeNameHandling = TypeNameHandling.None,
                MaxDepth = MaxDepth,
                Culture = CultureInfo.InvariantCulture
            };
        }

        /// <summary>Parses JSON text into a token, dates kept as strings. Throws <see cref="JsonException"/>.</summary>
        public static JToken Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            using (var reader = new JsonTextReader(new StringReader(json)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                reader.MaxDepth = MaxDepth;
                reader.Culture = CultureInfo.InvariantCulture;
                var token = JToken.ReadFrom(reader);
                // Anything after the value is not JSON this SDK sent or the API answers.
                while (reader.Read())
                    if (reader.TokenType != JsonToken.Comment)
                        throw new JsonReaderException("Additional text after the JSON value.");
                return token;
            }
        }

        /// <summary>Reads a model from JSON text.</summary>
        public static T Deserialize<T>(string json)
        {
            return ToObject<T>(Parse(json));
        }

        /// <summary>Converts a token to <typeparamref name="T"/> with the SDK's settings.</summary>
        public static T ToObject<T>(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return default(T);
            return token.ToObject<T>(Serializer);
        }

        /// <summary>A game value as a token (null members kept). Null becomes a JSON null.</summary>
        public static JToken FromObject(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is JToken t) return t.DeepClone();
            return JToken.FromObject(value, ValueSerializer);
        }

        /// <summary>Compact JSON text.</summary>
        public static string Serialize(JToken token)
        {
            return token == null ? "null" : token.ToString(Formatting.None);
        }
    }
}
