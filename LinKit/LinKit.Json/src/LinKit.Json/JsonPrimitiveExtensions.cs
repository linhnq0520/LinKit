using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinKit.Json;

/// <summary>Strict required-value readers. Strings, booleans and numbers are never coerced across JSON kinds.</summary>
/// <remarks>
/// Every reader throws <see cref="JsonPathReadException"/> when the path is missing, the value is JSON null, or the
/// value is a different JSON kind than requested; nothing is silently defaulted. Use
/// <see cref="JsonValuePathExtensions.TryGetValueByPath{T}(JsonElement,JsonPath,out T)"/> when absence is expected.
/// </remarks>
public static class JsonPrimitiveExtensions
{
    /// <inheritdoc cref="GetRequiredString(JsonElement, JsonPath)"/>
    public static string GetRequiredString(this JsonElement root, string path) =>
        root.GetRequiredString(JsonPath.Parse(path));

    /// <summary>Reads a required JSON string.</summary>
    /// <param name="root">Document or element to read from.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The string value, never <see langword="null"/>.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, or does not hold a string.</exception>
    public static string GetRequiredString(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.String,
            static value => value.GetString()!
        );

    /// <inheritdoc cref="GetRequiredInt32(JsonElement, JsonPath)"/>
    public static int GetRequiredInt32(this JsonElement root, string path) =>
        root.GetRequiredInt32(JsonPath.Parse(path));

    /// <summary>Reads a required 32-bit signed integer.</summary>
    /// <param name="root">Document or element to read from.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The numeric value.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, is not a number, or is out of range.</exception>
    public static int GetRequiredInt32(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.Number,
            static value => value.GetInt32()
        );

    /// <inheritdoc cref="GetRequiredInt64(JsonElement, JsonPath)"/>
    public static long GetRequiredInt64(this JsonElement root, string path) =>
        root.GetRequiredInt64(JsonPath.Parse(path));

    /// <summary>Reads a required 64-bit signed integer.</summary>
    /// <param name="root">Document or element to read from.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The numeric value.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, is not a number, or is out of range.</exception>
    public static long GetRequiredInt64(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.Number,
            static value => value.GetInt64()
        );

    /// <inheritdoc cref="GetRequiredDecimal(JsonElement, JsonPath)"/>
    public static decimal GetRequiredDecimal(this JsonElement root, string path) =>
        root.GetRequiredDecimal(JsonPath.Parse(path));

    /// <summary>Reads a required <see cref="decimal"/> without the rounding a binary floating-point read would introduce.</summary>
    /// <param name="root">Document or element to read from.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The numeric value.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, is not a number, or is out of range.</exception>
    public static decimal GetRequiredDecimal(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.Number,
            static value => value.GetDecimal()
        );

    /// <inheritdoc cref="GetRequiredDouble(JsonElement, JsonPath)"/>
    public static double GetRequiredDouble(this JsonElement root, string path) =>
        root.GetRequiredDouble(JsonPath.Parse(path));

    /// <summary>Reads a required <see cref="double"/>, rejecting non-finite values.</summary>
    /// <param name="root">Document or element to read from.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The numeric value, guaranteed to be finite.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, is not a number, or is not finite.</exception>
    public static double GetRequiredDouble(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.Number,
            static value =>
            {
                var number = value.GetDouble();
                return double.IsFinite(number)
                    ? number
                    : throw new FormatException("JSON number is outside the finite double range.");
            }
        );

    /// <inheritdoc cref="GetRequiredBoolean(JsonElement, JsonPath)"/>
    public static bool GetRequiredBoolean(this JsonElement root, string path) =>
        root.GetRequiredBoolean(JsonPath.Parse(path));

    /// <summary>Reads a required boolean.</summary>
    /// <param name="root">Document or element to read from.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The boolean value.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, or is not a boolean.</exception>
    public static bool GetRequiredBoolean(this JsonElement root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.True,
            static value => value.GetBoolean()
        );

    /// <inheritdoc cref="GetRequiredString(JsonNode, JsonPath)"/>
    public static string GetRequiredString(this JsonNode? root, string path) =>
        root.GetRequiredString(JsonPath.Parse(path));

    /// <summary>Reads a required JSON string from an already-parsed node tree.</summary>
    /// <param name="root">Root node to read from; <see langword="null"/> is treated as a missing value.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The string value, never <see langword="null"/>.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, or does not hold a string.</exception>
    public static string GetRequiredString(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.String,
            static value => value.GetString()!
        );

    /// <inheritdoc cref="GetRequiredInt32(JsonNode, JsonPath)"/>
    public static int GetRequiredInt32(this JsonNode? root, string path) =>
        root.GetRequiredInt32(JsonPath.Parse(path));

    /// <summary>Reads a required 32-bit signed integer from an already-parsed node tree.</summary>
    /// <param name="root">Root node to read from; <see langword="null"/> is treated as a missing value.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The numeric value.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, is not a number, or is out of range.</exception>
    public static int GetRequiredInt32(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.Number,
            static value => value.GetInt32()
        );

    /// <inheritdoc cref="GetRequiredInt64(JsonNode, JsonPath)"/>
    public static long GetRequiredInt64(this JsonNode? root, string path) =>
        root.GetRequiredInt64(JsonPath.Parse(path));

    /// <summary>Reads a required 64-bit signed integer from an already-parsed node tree.</summary>
    /// <param name="root">Root node to read from; <see langword="null"/> is treated as a missing value.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The numeric value.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, is not a number, or is out of range.</exception>
    public static long GetRequiredInt64(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.Number,
            static value => value.GetInt64()
        );

    /// <inheritdoc cref="GetRequiredDecimal(JsonNode, JsonPath)"/>
    public static decimal GetRequiredDecimal(this JsonNode? root, string path) =>
        root.GetRequiredDecimal(JsonPath.Parse(path));

    /// <summary>Reads a required <see cref="decimal"/> from an already-parsed node tree.</summary>
    /// <param name="root">Root node to read from; <see langword="null"/> is treated as a missing value.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The numeric value.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, is not a number, or is out of range.</exception>
    public static decimal GetRequiredDecimal(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.Number,
            static value => value.GetDecimal()
        );

    /// <inheritdoc cref="GetRequiredDouble(JsonNode, JsonPath)"/>
    public static double GetRequiredDouble(this JsonNode? root, string path) =>
        root.GetRequiredDouble(JsonPath.Parse(path));

    /// <summary>Reads a required <see cref="double"/> from an already-parsed node tree, rejecting non-finite values.</summary>
    /// <param name="root">Root node to read from; <see langword="null"/> is treated as a missing value.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The numeric value, guaranteed to be finite.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, is not a number, or is not finite.</exception>
    public static double GetRequiredDouble(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.Number,
            static value =>
            {
                var number = value.GetDouble();
                return double.IsFinite(number)
                    ? number
                    : throw new FormatException("JSON number is outside the finite double range.");
            }
        );

    /// <inheritdoc cref="GetRequiredBoolean(JsonNode, JsonPath)"/>
    public static bool GetRequiredBoolean(this JsonNode? root, string path) =>
        root.GetRequiredBoolean(JsonPath.Parse(path));

    /// <summary>Reads a required boolean from an already-parsed node tree.</summary>
    /// <param name="root">Root node to read from; <see langword="null"/> is treated as a missing value.</param>
    /// <param name="path">Pre-parsed path to the value.</param>
    /// <returns>The boolean value.</returns>
    /// <exception cref="JsonPathReadException">The path is missing, is JSON null, or is not a boolean.</exception>
    public static bool GetRequiredBoolean(this JsonNode? root, JsonPath path) =>
        JsonPrimitiveReader.Read(
            root,
            path,
            JsonValueKind.True,
            static value => value.GetBoolean()
        );
}
