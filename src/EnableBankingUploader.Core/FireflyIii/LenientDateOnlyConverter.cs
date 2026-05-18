using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnableBankingUploader.Core.FireflyIii;

// Firefly III returns transaction dates as full ISO datetime strings with timezone
// offsets (e.g. "2018-09-17T00:00:00+02:00"), but the built-in DateOnly converter
// only accepts "yyyy-MM-dd". This converter handles both formats on read and always
// writes "yyyy-MM-dd" so the create-transaction write path stays compatible.
public sealed class LenientDateOnlyConverter : JsonConverter<DateOnly>
{
    private const string DateFormat = "yyyy-MM-dd";

    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString()
            ?? throw new JsonException("Expected a non-null date string.");

        if (DateOnly.TryParseExact(s, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return DateOnly.FromDateTime(dto.DateTime);

        throw new JsonException($"The JSON value '{s}' could not be converted to DateOnly.");
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(DateFormat, CultureInfo.InvariantCulture));
}
