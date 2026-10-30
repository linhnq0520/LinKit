using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace LinKit.Json;

/// <summary>Converts singular path matches without reflection-based serialization.</summary>
/// <remarks>JSON null succeeds only when <typeparamref name="T"/> accepts null. DTOs require explicit metadata.</remarks>
public static class JsonValuePathExtensions
{
    public static bool TryGetValueByPath<T>(this JsonElement root, string path, out T? value) =>
        root.TryGetValueByPath(JsonPath.Parse(path), out value);

    public static bool TryGetValueByPath<T>(this JsonElement root, JsonPath path, out T? value)
    {
        JsonPath.ValidateSingular(path);
        value = default;
        return root.TrySelect(path, out var selected) &&
               JsonValueConverter.TryConvert(selected, out value);
    }

    public static T? GetValueByPath<T>(this JsonElement root, string path) =>
        root.GetValueByPath<T>(JsonPath.Parse(path));

    public static T? GetValueByPath<T>(this JsonElement root, JsonPath path)
    {
        return root.TryGetValueByPath(path, out T? value)
            ? value
            : throw JsonValueConverter.Failure<T>(path);
    }

    public static bool TryGetValueByPath<T>(
        this JsonElement root,
        string path,
        JsonTypeInfo<T> typeInfo,
        out T? value) =>
        root.TryGetValueByPath(JsonPath.Parse(path), typeInfo, out value);

    public static bool TryGetValueByPath<T>(
        this JsonElement root,
        JsonPath path,
        JsonTypeInfo<T> typeInfo,
        out T? value)
    {
        JsonPath.ValidateSingular(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        value = default;
        return root.TrySelect(path, out var selected) &&
               JsonValueConverter.TryDeserialize(selected, typeInfo, out value);
    }

    public static T? GetValueByPath<T>(
        this JsonElement root,
        string path,
        JsonTypeInfo<T> typeInfo) =>
        root.GetValueByPath(JsonPath.Parse(path), typeInfo);

    public static T? GetValueByPath<T>(
        this JsonElement root,
        JsonPath path,
        JsonTypeInfo<T> typeInfo)
    {
        JsonPath.ValidateSingular(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (root.TrySelect(path, out var selected))
        {
            return JsonValueConverter.Deserialize(selected, path, typeInfo);
        }

        throw JsonValueConverter.Failure<T>(path);
    }

    public static bool TryGetValueByPath<T>(this JsonNode? root, string path, out T? value) =>
        root.TryGetValueByPath(JsonPath.Parse(path), out value);

    public static bool TryGetValueByPath<T>(this JsonNode? root, JsonPath path, out T? value)
    {
        JsonPath.ValidateSingular(path);
        value = default;
        return root.TrySelect(path, out var selected) &&
               JsonValueConverter.TryConvert(selected, out value);
    }

    public static T? GetValueByPath<T>(this JsonNode? root, string path) =>
        root.GetValueByPath<T>(JsonPath.Parse(path));

    public static T? GetValueByPath<T>(this JsonNode? root, JsonPath path) =>
        root.TryGetValueByPath(path, out T? value)
            ? value
            : throw JsonValueConverter.Failure<T>(path);

    public static bool TryGetValueByPath<T>(
        this JsonNode? root,
        string path,
        JsonTypeInfo<T> typeInfo,
        out T? value) =>
        root.TryGetValueByPath(JsonPath.Parse(path), typeInfo, out value);

    public static bool TryGetValueByPath<T>(
        this JsonNode? root,
        JsonPath path,
        JsonTypeInfo<T> typeInfo,
        out T? value)
    {
        JsonPath.ValidateSingular(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        value = default;
        return root.TrySelect(path, out var selected) &&
               JsonValueConverter.TryDeserialize(selected, typeInfo, out value);
    }

    public static T? GetValueByPath<T>(
        this JsonNode? root,
        string path,
        JsonTypeInfo<T> typeInfo) =>
        root.GetValueByPath(JsonPath.Parse(path), typeInfo);

    public static T? GetValueByPath<T>(
        this JsonNode? root,
        JsonPath path,
        JsonTypeInfo<T> typeInfo)
    {
        JsonPath.ValidateSingular(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (root.TrySelect(path, out var selected))
        {
            return JsonValueConverter.Deserialize(selected, path, typeInfo);
        }

        throw JsonValueConverter.Failure<T>(path);
    }

    public static bool TryGetValueByPath<T>(
        this JsonDocument root,
        string path,
        out T? value) =>
        root.TryGetValueByPath(JsonPath.Parse(path), out value);

    public static bool TryGetValueByPath<T>(
        this JsonDocument root,
        JsonPath path,
        out T? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.RootElement.TryGetValueByPath(path, out value);
    }

    public static T? GetValueByPath<T>(this JsonDocument root, string path) =>
        root.GetValueByPath<T>(JsonPath.Parse(path));

    public static T? GetValueByPath<T>(this JsonDocument root, JsonPath path)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.RootElement.GetValueByPath<T>(path);
    }

    public static bool TryGetValueByPath<T>(
        this JsonDocument root,
        string path,
        JsonTypeInfo<T> typeInfo,
        out T? value) =>
        root.TryGetValueByPath(JsonPath.Parse(path), typeInfo, out value);

    public static bool TryGetValueByPath<T>(
        this JsonDocument root,
        JsonPath path,
        JsonTypeInfo<T> typeInfo,
        out T? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.RootElement.TryGetValueByPath(path, typeInfo, out value);
    }

    public static T? GetValueByPath<T>(
        this JsonDocument root,
        string path,
        JsonTypeInfo<T> typeInfo) =>
        root.GetValueByPath(JsonPath.Parse(path), typeInfo);

    public static T? GetValueByPath<T>(
        this JsonDocument root,
        JsonPath path,
        JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.RootElement.GetValueByPath(path, typeInfo);
    }

    /// <summary>Null or blank JSON returns false; malformed nonblank JSON throws <see cref="JsonException"/>.</summary>
    public static bool TryGetValueByPath<T>(
        this string? json,
        string path,
        out T? value) =>
        json.TryGetValueByPath(JsonPath.Parse(path), out value);

    public static bool TryGetValueByPath<T>(
        this string? json,
        JsonPath path,
        out T? value)
    {
        JsonPath.ValidateSingular(path);
        value = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetValueByPath(path, out value);
    }

    public static T? GetValueByPath<T>(this string? json, string path) =>
        json.GetValueByPath<T>(JsonPath.Parse(path));

    public static T? GetValueByPath<T>(this string? json, JsonPath path)
    {
        JsonPath.ValidateSingular(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw JsonValueConverter.Failure<T>(path);
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetValueByPath<T>(path);
    }

    public static bool TryGetValueByPath<T>(
        this string? json,
        string path,
        JsonTypeInfo<T> typeInfo,
        out T? value) =>
        json.TryGetValueByPath(JsonPath.Parse(path), typeInfo, out value);

    public static bool TryGetValueByPath<T>(
        this string? json,
        JsonPath path,
        JsonTypeInfo<T> typeInfo,
        out T? value)
    {
        JsonPath.ValidateSingular(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        value = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetValueByPath(path, typeInfo, out value);
    }

    public static T? GetValueByPath<T>(
        this string? json,
        string path,
        JsonTypeInfo<T> typeInfo) =>
        json.GetValueByPath(JsonPath.Parse(path), typeInfo);

    public static T? GetValueByPath<T>(
        this string? json,
        JsonPath path,
        JsonTypeInfo<T> typeInfo)
    {
        JsonPath.ValidateSingular(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw JsonValueConverter.Failure<T>(path);
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetValueByPath(path, typeInfo);
    }
}
