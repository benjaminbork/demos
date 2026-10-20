using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioBlog.Core.Json;

/// <summary>
/// One JSON convention for everything we store in Cosmos DB and return from the API:
/// camelCase, enums as camelCase strings, nulls omitted, timestamps as fixed-width UTC ISO 8601.
/// </summary>
public static class AppJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new UtcTimestampConverter());
        return options;
    }

    public static T DeepClone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options)!;
}

/// <summary>
/// Writes DateTimeOffset as "yyyy-MM-ddTHH:mm:ss.fffffffZ" (the format Cosmos DB recommends).
/// Fixed width matters: we compare and ORDER BY these strings inside Cosmos DB queries
/// (createdAt for the list, dueAt for the scheduler), and variable-width fractions would sort wrongly.
/// </summary>
public sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public static string ToCosmosString(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToCosmosString(value));
}
