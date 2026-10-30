using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinKit.Json;

/// <summary>Strict required-value readers. Strings, booleans and numbers are never coerced across JSON kinds.</summary>
public static class JsonPrimitiveExtensions
{
    public static string GetRequiredString(this JsonElement root, string path) =>
        root.GetRequiredString(JsonPath.Parse(path));

    public static string GetRequiredString(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.String, static value => value.GetString()!);

    public static int GetRequiredInt32(this JsonElement root, string path) =>
        root.GetRequiredInt32(JsonPath.Parse(path));

    public static int GetRequiredInt32(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.Number, static value => value.GetInt32());

    public static long GetRequiredInt64(this JsonElement root, string path) =>
        root.GetRequiredInt64(JsonPath.Parse(path));

    public static long GetRequiredInt64(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.Number, static value => value.GetInt64());

    public static decimal GetRequiredDecimal(this JsonElement root, string path) =>
        root.GetRequiredDecimal(JsonPath.Parse(path));

    public static decimal GetRequiredDecimal(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.Number, static value => value.GetDecimal());

    public static double GetRequiredDouble(this JsonElement root, string path) =>
        root.GetRequiredDouble(JsonPath.Parse(path));

    public static double GetRequiredDouble(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.Number, static value =>
        {
            var number = value.GetDouble();
            return double.IsFinite(number)
                ? number
                : throw new FormatException("JSON number is outside the finite double range.");
        });

    public static bool GetRequiredBoolean(this JsonElement root, string path) =>
        root.GetRequiredBoolean(JsonPath.Parse(path));

    public static bool GetRequiredBoolean(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.True, static value => value.GetBoolean());

    public static string GetRequiredString(this JsonNode? root, string path) =>
        root.GetRequiredString(JsonPath.Parse(path));

    public static string GetRequiredString(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.String, static value => value.GetString()!);

    public static int GetRequiredInt32(this JsonNode? root, string path) =>
        root.GetRequiredInt32(JsonPath.Parse(path));

    public static int GetRequiredInt32(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.Number, static value => value.GetInt32());

    public static long GetRequiredInt64(this JsonNode? root, string path) =>
        root.GetRequiredInt64(JsonPath.Parse(path));

    public static long GetRequiredInt64(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.Number, static value => value.GetInt64());

    public static decimal GetRequiredDecimal(this JsonNode? root, string path) =>
        root.GetRequiredDecimal(JsonPath.Parse(path));

    public static decimal GetRequiredDecimal(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.Number, static value => value.GetDecimal());

    public static double GetRequiredDouble(this JsonNode? root, string path) =>
        root.GetRequiredDouble(JsonPath.Parse(path));

    public static double GetRequiredDouble(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.Number, static value =>
        {
            var number = value.GetDouble();
            return double.IsFinite(number)
                ? number
                : throw new FormatException("JSON number is outside the finite double range.");
        });

    public static bool GetRequiredBoolean(this JsonNode? root, string path) =>
        root.GetRequiredBoolean(JsonPath.Parse(path));

    public static bool GetRequiredBoolean(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(root, path, JsonValueKind.True, static value => value.GetBoolean());
}
