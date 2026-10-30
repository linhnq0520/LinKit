using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinKit.Json;

internal static class JsonPrimitiveReader
{
    public static T Read<T>(
        JsonElement root,
        JsonPath path,
        JsonValueKind expectedKind,
        Func<JsonElement, T> read)
    {
        JsonPath.ValidateSingular(path);
        if (!JsonNavigator<JsonElement, JsonElementAccessor>.TrySelect(root, path, out var value))
        {
            throw new JsonPathReadException(path, JsonPathReadError.Missing);
        }

        return ReadValue(value, path, expectedKind, read);
    }

    public static T Read<T>(
        JsonNode? root,
        JsonPath path,
        JsonValueKind expectedKind,
        Func<JsonElement, T> read)
    {
        JsonPath.ValidateSingular(path);
        if (!JsonNavigator<JsonNode?, JsonNodeAccessor>.TrySelect(root, path, out var value))
        {
            throw new JsonPathReadException(path, JsonPathReadError.Missing);
        }

        if (value is null)
        {
            throw new JsonPathReadException(path, JsonPathReadError.Null);
        }

        if (value is JsonValue scalar && JsonValueConverter.IsNonFiniteNumber(scalar))
        {
            throw new JsonPathReadException(path, JsonPathReadError.InvalidNumber);
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<JsonElement>(out var element))
        {
            return ReadValue(element, path, expectedKind, read);
        }

        using var document = JsonDocument.Parse(value.ToJsonString());
        return ReadValue(document.RootElement, path, expectedKind, read);
    }

    private static T ReadValue<T>(
        JsonElement value,
        JsonPath path,
        JsonValueKind expectedKind,
        Func<JsonElement, T> read)
    {
        ValidateKind(value.ValueKind, expectedKind, path);

        try
        {
            return read(value);
        }
        catch (FormatException)
        {
            throw new JsonPathReadException(path, JsonPathReadError.InvalidNumber);
        }
    }

    private static void ValidateKind(JsonValueKind actual, JsonValueKind expected, JsonPath path)
    {
        if (actual == JsonValueKind.Undefined)
        {
            throw new JsonPathReadException(path, JsonPathReadError.Missing);
        }

        if (actual == JsonValueKind.Null)
        {
            throw new JsonPathReadException(path, JsonPathReadError.Null);
        }

        if (actual != expected &&
            !(expected == JsonValueKind.True && actual == JsonValueKind.False))
        {
            throw new JsonPathReadException(path, JsonPathReadError.TypeMismatch);
        }
    }
}
