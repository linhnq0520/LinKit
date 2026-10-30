using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Globalization;

namespace LinKit.Json;

internal static class JsonValueConverter
{
    public static bool TryConvert<T>(JsonElement element, out T? result)
    {
        result = default;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return default(T) is null;
        }

        object? converted = null;
        var target = Target<T>.Type;

        if (target == typeof(JsonElement))
        {
            converted = element.Clone();
        }
        else if (target == typeof(JsonNode) || target == typeof(JsonObject) ||
                 target == typeof(JsonArray) || target == typeof(JsonValue))
        {
            var node = JsonNode.Parse(element.GetRawText());
            if (node is not null && target.IsInstanceOfType(node))
            {
                converted = node;
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            if (target == typeof(string))
            {
                converted = element.GetString();
            }
            else if (target == typeof(char))
            {
                var text = element.GetString()!;
                if (text.Length == 1)
                {
                    converted = text[0];
                }
            }
            else if (target == typeof(Guid) && element.TryGetGuid(out var guid))
            {
                converted = guid;
            }
            else if (target == typeof(DateTime) && element.TryGetDateTime(out var dateTime))
            {
                converted = dateTime;
            }
            else if (target == typeof(DateTimeOffset) &&
                     element.TryGetDateTimeOffset(out var dateTimeOffset))
            {
                converted = dateTimeOffset;
            }
            else if (target == typeof(TimeSpan) &&
                     TimeSpan.TryParseExact(
                         element.GetString(),
                         "c",
                         CultureInfo.InvariantCulture,
                         out var timeSpan))
            {
                converted = timeSpan;
            }
            else if (target == typeof(Uri) &&
                     Uri.TryCreate(element.GetString(), UriKind.RelativeOrAbsolute, out var uri))
            {
                converted = uri;
            }
        }
        else if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (target == typeof(bool))
            {
                converted = element.GetBoolean();
            }
        }
        else if (element.ValueKind == JsonValueKind.Number)
        {
            if (target == typeof(double) &&
                element.TryGetDouble(out var doubleValue) &&
                double.IsFinite(doubleValue))
            {
                converted = doubleValue;
            }
            else if (target == typeof(float) &&
                     element.TryGetSingle(out var singleValue) &&
                     float.IsFinite(singleValue))
            {
                converted = singleValue;
            }
            else if (target == typeof(decimal) && element.TryGetDecimal(out var decimalValue))
            {
                converted = decimalValue;
            }
            else if (Target<T>.IsInteger &&
                     JsonInteger.TryRead(element, out var negative, out var magnitude))
            {
                JsonInteger.TryConvert(target, negative, magnitude, out converted);
            }
        }

        if (converted is not T value)
        {
            return false;
        }

        result = value;
        return true;
    }

    public static bool TryConvert<T>(JsonNode? node, out T? result)
    {
        result = default;
        if (node is null)
        {
            return default(T) is null;
        }

        if (node is T typed)
        {
            result = typed;
            return true;
        }

        if (node is JsonValue scalar)
        {
            if (IsNonFiniteNumber(scalar))
            {
                return false;
            }

            if (scalar.TryGetValue<JsonElement>(out var element))
            {
                return TryConvert(element, out result);
            }
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        var converted = TryConvert<T>(document.RootElement, out result);

        if (converted &&
            (result is double doubleValue && !double.IsFinite(doubleValue) ||
             result is float floatValue && !float.IsFinite(floatValue)))
        {
            result = default;
            return false;
        }

        return converted;
    }

    public static bool IsNonFiniteNumber(JsonValue value) =>
        value.TryGetValue<double>(out var doubleValue) && !double.IsFinite(doubleValue) ||
        value.TryGetValue<float>(out var floatValue) && !float.IsFinite(floatValue);

    public static bool TryDeserialize<T>(
        JsonElement element,
        JsonTypeInfo<T> typeInfo,
        out T? result)
    {
        result = default;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return default(T) is null;
        }

        try
        {
            result = element.Deserialize(typeInfo);
            return result is not null || default(T) is null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryDeserialize<T>(
        JsonNode? node,
        JsonTypeInfo<T> typeInfo,
        out T? result)
    {
        result = default;
        if (node is null)
        {
            return default(T) is null;
        }

        try
        {
            result = node.Deserialize(typeInfo);
            return result is not null || default(T) is null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static T? Deserialize<T>(JsonElement element, JsonPath path, JsonTypeInfo<T> typeInfo)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (default(T) is null)
            {
                return default;
            }

            throw Failure<T>(path);
        }

        if (TryDeserialize(element, typeInfo, out T? result))
        {
            return result;
        }

        throw Failure<T>(path);
    }

    public static T? Deserialize<T>(JsonNode? node, JsonPath path, JsonTypeInfo<T> typeInfo)
    {
        if (node is null)
        {
            if (default(T) is null)
            {
                return default;
            }

            throw Failure<T>(path);
        }

        if (TryDeserialize(node, typeInfo, out T? result))
        {
            return result;
        }

        throw Failure<T>(path);
    }

    public static JsonException Failure<T>(JsonPath path) =>
        new($"The value at '{path}' could not be read as '{typeof(T)}'. For DTOs, supply JsonTypeInfo metadata.");

    private static class Target<T>
    {
        public static readonly Type Type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        public static readonly bool IsInteger =
            Type == typeof(byte) ||
            Type == typeof(sbyte) ||
            Type == typeof(short) ||
            Type == typeof(ushort) ||
            Type == typeof(int) ||
            Type == typeof(uint) ||
            Type == typeof(long) ||
            Type == typeof(ulong) ||
            Type == typeof(Int128) ||
            Type == typeof(UInt128);
    }
}
