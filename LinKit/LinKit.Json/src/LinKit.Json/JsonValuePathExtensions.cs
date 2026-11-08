using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace LinKit.Json;

/// <summary>Converts singular path matches without reflection-based serialization.</summary>
/// <remarks>
/// JSON null succeeds only when the requested type accepts null. DTOs require explicit metadata.
/// Every overload rejects wildcard paths with <see cref="ArgumentException"/>: use
/// <see cref="JsonSelectionExtensions.SelectMany(JsonElement, JsonPath)"/> for multi-value queries. Paths are
/// parsed from a string unless a <see cref="JsonPath"/> instance is supplied, so parse once and reuse it in hot loops.
/// </remarks>
public static class JsonValuePathExtensions
{
    /// <inheritdoc cref="TryGetValueByPath{T}(JsonElement, JsonPath, out T)"/>
    public static bool TryGetValueByPath<T>(this JsonElement root, string path, out T? value) =>
        root.TryGetValueByPath(JsonPath.Parse(path), out value);

    /// <summary>
    /// Attempts to read the single value at <paramref name="path"/> and convert it to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target type; primitives, <see cref="Guid"/>, <see cref="DateTime"/>, <see cref="Uri"/> and
    /// the JSON DOM types are supported without metadata.</typeparam>
    /// <param name="root">Element to read from.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="value">Receives the converted value when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the path resolved and the value converted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    public static bool TryGetValueByPath<T>(this JsonElement root, JsonPath path, out T? value)
    {
        JsonPath.ValidateSingular(path);
        value = default;
        return root.TrySelect(path, out var selected)
            && JsonValueConverter.TryConvert(selected, out value);
    }

    /// <inheritdoc cref="GetValueByPath{T}(JsonElement, JsonPath)"/>
    public static T? GetValueByPath<T>(this JsonElement root, string path) =>
        root.GetValueByPath<T>(JsonPath.Parse(path));

    /// <summary>
    /// Reads the single value at <paramref name="path"/> and converts it to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target type; may be a nullable or reference type.</typeparam>
    /// <param name="root">Element to read from.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <returns>The converted value; <see langword="null"/> when the value is JSON null and <typeparamref name="T"/> accepts null.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="JsonException">
    /// The path is missing, the value cannot be represented as <typeparamref name="T"/>, or <typeparamref name="T"/> is a
    /// DTO and no <see cref="JsonTypeInfo{T}"/> overload was used.
    /// </exception>
    public static T? GetValueByPath<T>(this JsonElement root, JsonPath path)
    {
        return root.TryGetValueByPath(path, out T? value)
            ? value
            : throw JsonValueConverter.Failure<T>(path);
    }

    /// <inheritdoc cref="TryGetValueByPath{T}(JsonElement, JsonPath, JsonTypeInfo{T}, out T)"/>
    public static bool TryGetValueByPath<T>(
        this JsonElement root,
        string path,
        JsonTypeInfo<T> typeInfo,
        out T? value
    ) => root.TryGetValueByPath(JsonPath.Parse(path), typeInfo, out value);

    /// <summary>
    /// Attempts to deserialize the value at <paramref name="path"/> using source-generated metadata, which keeps DTO
    /// reads trim- and AOT-safe.
    /// </summary>
    /// <typeparam name="T">Type of the DTO to materialize.</typeparam>
    /// <param name="root">Element to read from.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a <c>JsonSerializerContext</c>.</param>
    /// <param name="value">Receives the deserialized object when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the path resolved and the payload deserialized.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    public static bool TryGetValueByPath<T>(
        this JsonElement root,
        JsonPath path,
        JsonTypeInfo<T> typeInfo,
        out T? value
    )
    {
        JsonPath.ValidateSingular(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        value = default;
        return root.TrySelect(path, out var selected)
            && JsonValueConverter.TryDeserialize(selected, typeInfo, out value);
    }

    /// <inheritdoc cref="GetValueByPath{T}(JsonElement, JsonPath, JsonTypeInfo{T})"/>
    public static T? GetValueByPath<T>(
        this JsonElement root,
        string path,
        JsonTypeInfo<T> typeInfo
    ) => root.GetValueByPath(JsonPath.Parse(path), typeInfo);

    /// <summary>
    /// Deserializes the value at <paramref name="path"/> using source-generated metadata and throws when the value is
    /// missing or cannot be materialized.
    /// </summary>
    /// <typeparam name="T">Type of the DTO to materialize.</typeparam>
    /// <param name="root">Element to read from.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a <c>JsonSerializerContext</c>.</param>
    /// <returns>The deserialized object, or <see langword="null"/> for JSON null when <typeparamref name="T"/> accepts null.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The path is missing or the payload is not a valid <typeparamref name="T"/>.</exception>
    public static T? GetValueByPath<T>(
        this JsonElement root,
        JsonPath path,
        JsonTypeInfo<T> typeInfo
    )
    {
        JsonPath.ValidateSingular(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (root.TrySelect(path, out var selected))
        {
            return JsonValueConverter.Deserialize(selected, path, typeInfo);
        }

        throw JsonValueConverter.Failure<T>(path);
    }

    /// <inheritdoc cref="TryGetValueByPath{T}(JsonNode, JsonPath, out T)"/>
    public static bool TryGetValueByPath<T>(this JsonNode? root, string path, out T? value) =>
        root.TryGetValueByPath(JsonPath.Parse(path), out value);

    /// <summary>
    /// Attempts to read the single value at <paramref name="path"/> from an already-parsed node tree.
    /// </summary>
    /// <typeparam name="T">Target type; primitives, <see cref="Guid"/>, <see cref="DateTime"/>, <see cref="Uri"/> and
    /// the JSON DOM types are supported without metadata.</typeparam>
    /// <param name="root">Root node to read from; <see langword="null"/> resolves nothing.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="value">Receives the converted value when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the path resolved and the value converted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    public static bool TryGetValueByPath<T>(this JsonNode? root, JsonPath path, out T? value)
    {
        JsonPath.ValidateSingular(path);
        value = default;
        return root.TrySelect(path, out var selected)
            && JsonValueConverter.TryConvert(selected, out value);
    }

    /// <inheritdoc cref="GetValueByPath{T}(JsonNode, JsonPath)"/>
    public static T? GetValueByPath<T>(this JsonNode? root, string path) =>
        root.GetValueByPath<T>(JsonPath.Parse(path));

    /// <summary>
    /// Reads the single value at <paramref name="path"/> from an already-parsed node tree and converts it to
    /// <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target type; may be a nullable or reference type.</typeparam>
    /// <param name="root">Root node to read from; <see langword="null"/> is treated as a missing value.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <returns>The converted value; <see langword="null"/> when the value is JSON null and <typeparamref name="T"/> accepts null.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="JsonException">
    /// The path is missing, the value cannot be represented as <typeparamref name="T"/>, or <typeparamref name="T"/> is a
    /// DTO and no <see cref="JsonTypeInfo{T}"/> overload was used.
    /// </exception>
    public static T? GetValueByPath<T>(this JsonNode? root, JsonPath path) =>
        root.TryGetValueByPath(path, out T? value)
            ? value
            : throw JsonValueConverter.Failure<T>(path);

    /// <inheritdoc cref="TryGetValueByPath{T}(JsonNode, JsonPath, JsonTypeInfo{T}, out T)"/>
    public static bool TryGetValueByPath<T>(
        this JsonNode? root,
        string path,
        JsonTypeInfo<T> typeInfo,
        out T? value
    ) => root.TryGetValueByPath(JsonPath.Parse(path), typeInfo, out value);

    /// <summary>
    /// Attempts to deserialize the node at <paramref name="path"/> using source-generated metadata, which keeps DTO
    /// reads trim- and AOT-safe.
    /// </summary>
    /// <typeparam name="T">Type of the DTO to materialize.</typeparam>
    /// <param name="root">Root node to read from; <see langword="null"/> resolves nothing.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a <c>JsonSerializerContext</c>.</param>
    /// <param name="value">Receives the deserialized object when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the path resolved and the payload deserialized.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    public static bool TryGetValueByPath<T>(
        this JsonNode? root,
        JsonPath path,
        JsonTypeInfo<T> typeInfo,
        out T? value
    )
    {
        JsonPath.ValidateSingular(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        value = default;
        return root.TrySelect(path, out var selected)
            && JsonValueConverter.TryDeserialize(selected, typeInfo, out value);
    }

    /// <inheritdoc cref="GetValueByPath{T}(JsonNode, JsonPath, JsonTypeInfo{T})"/>
    public static T? GetValueByPath<T>(
        this JsonNode? root,
        string path,
        JsonTypeInfo<T> typeInfo
    ) => root.GetValueByPath(JsonPath.Parse(path), typeInfo);

    /// <summary>
    /// Deserializes the node at <paramref name="path"/> using source-generated metadata and throws when the value is
    /// missing or cannot be materialized.
    /// </summary>
    /// <typeparam name="T">Type of the DTO to materialize.</typeparam>
    /// <param name="root">Root node to read from; <see langword="null"/> is treated as a missing value.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a <c>JsonSerializerContext</c>.</param>
    /// <returns>The deserialized object, or <see langword="null"/> for JSON null when <typeparamref name="T"/> accepts null.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The path is missing or the payload is not a valid <typeparamref name="T"/>.</exception>
    public static T? GetValueByPath<T>(this JsonNode? root, JsonPath path, JsonTypeInfo<T> typeInfo)
    {
        JsonPath.ValidateSingular(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (root.TrySelect(path, out var selected))
        {
            return JsonValueConverter.Deserialize(selected, path, typeInfo);
        }

        throw JsonValueConverter.Failure<T>(path);
    }

    /// <inheritdoc cref="TryGetValueByPath{T}(JsonDocument, JsonPath, out T)"/>
    public static bool TryGetValueByPath<T>(this JsonDocument root, string path, out T? value) =>
        root.TryGetValueByPath(JsonPath.Parse(path), out value);

    /// <summary>
    /// Attempts to read the single value at <paramref name="path"/> from a parsed <see cref="JsonDocument"/>.
    /// </summary>
    /// <typeparam name="T">Target type; primitives, <see cref="Guid"/>, <see cref="DateTime"/>, <see cref="Uri"/> and
    /// the JSON DOM types are supported without metadata.</typeparam>
    /// <param name="root">Document to read from; it must outlive the returned values.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="value">Receives the converted value when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the path resolved and the value converted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    public static bool TryGetValueByPath<T>(this JsonDocument root, JsonPath path, out T? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.RootElement.TryGetValueByPath(path, out value);
    }

    /// <inheritdoc cref="GetValueByPath{T}(JsonDocument, JsonPath)"/>
    public static T? GetValueByPath<T>(this JsonDocument root, string path) =>
        root.GetValueByPath<T>(JsonPath.Parse(path));

    /// <summary>
    /// Reads the single value at <paramref name="path"/> from a parsed <see cref="JsonDocument"/> and converts it.
    /// </summary>
    /// <typeparam name="T">Target type; may be a nullable or reference type.</typeparam>
    /// <param name="root">Document to read from; it must outlive the returned values.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <returns>The converted value; <see langword="null"/> when the value is JSON null and <typeparamref name="T"/> accepts null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="JsonException">
    /// The path is missing, the value cannot be represented as <typeparamref name="T"/>, or <typeparamref name="T"/> is a
    /// DTO and no <see cref="JsonTypeInfo{T}"/> overload was used.
    /// </exception>
    public static T? GetValueByPath<T>(this JsonDocument root, JsonPath path)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.RootElement.GetValueByPath<T>(path);
    }

    /// <inheritdoc cref="TryGetValueByPath{T}(JsonDocument, JsonPath, JsonTypeInfo{T}, out T)"/>
    public static bool TryGetValueByPath<T>(
        this JsonDocument root,
        string path,
        JsonTypeInfo<T> typeInfo,
        out T? value
    ) => root.TryGetValueByPath(JsonPath.Parse(path), typeInfo, out value);

    /// <summary>
    /// Attempts to deserialize the value at <paramref name="path"/> using source-generated metadata, which keeps DTO
    /// reads trim- and AOT-safe.
    /// </summary>
    /// <typeparam name="T">Type of the DTO to materialize.</typeparam>
    /// <param name="root">Document to read from; it must outlive the returned object.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a <c>JsonSerializerContext</c>.</param>
    /// <param name="value">Receives the deserialized object when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the path resolved and the payload deserialized.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    public static bool TryGetValueByPath<T>(
        this JsonDocument root,
        JsonPath path,
        JsonTypeInfo<T> typeInfo,
        out T? value
    )
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.RootElement.TryGetValueByPath(path, typeInfo, out value);
    }

    /// <inheritdoc cref="GetValueByPath{T}(JsonDocument, JsonPath, JsonTypeInfo{T})"/>
    public static T? GetValueByPath<T>(
        this JsonDocument root,
        string path,
        JsonTypeInfo<T> typeInfo
    ) => root.GetValueByPath(JsonPath.Parse(path), typeInfo);

    /// <summary>
    /// Deserializes the value at <paramref name="path"/> using source-generated metadata and throws when the value is
    /// missing or cannot be materialized.
    /// </summary>
    /// <typeparam name="T">Type of the DTO to materialize.</typeparam>
    /// <param name="root">Document to read from; it must outlive the returned object.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a <c>JsonSerializerContext</c>.</param>
    /// <returns>The deserialized object, or <see langword="null"/> for JSON null when <typeparamref name="T"/> accepts null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="JsonException">The path is missing or the payload is not a valid <typeparamref name="T"/>.</exception>
    public static T? GetValueByPath<T>(
        this JsonDocument root,
        JsonPath path,
        JsonTypeInfo<T> typeInfo
    )
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.RootElement.GetValueByPath(path, typeInfo);
    }

    /// <inheritdoc cref="TryGetValueByPath{T}(string, JsonPath, out T)"/>
    public static bool TryGetValueByPath<T>(this string? json, string path, out T? value) =>
        json.TryGetValueByPath(JsonPath.Parse(path), out value);

    /// <summary>
    /// Parses <paramref name="json"/> and attempts to read the single value at <paramref name="path"/>.
    /// </summary>
    /// <typeparam name="T">Target type; primitives, <see cref="Guid"/>, <see cref="DateTime"/>, <see cref="Uri"/> and
    /// the JSON DOM types are supported without metadata.</typeparam>
    /// <param name="json">Raw JSON text. <see langword="null"/> or whitespace returns <see langword="false"/>.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="value">Receives the converted value when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the path resolved and the value converted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="JsonException">Non-blank <paramref name="json"/> is not valid JSON.</exception>
    public static bool TryGetValueByPath<T>(this string? json, JsonPath path, out T? value)
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

    /// <inheritdoc cref="GetValueByPath{T}(string, JsonPath)"/>
    public static T? GetValueByPath<T>(this string? json, string path) =>
        json.GetValueByPath<T>(JsonPath.Parse(path));

    /// <summary>Parses <paramref name="json"/> and reads the single value at <paramref name="path"/>.</summary>
    /// <typeparam name="T">Target type; may be a nullable or reference type.</typeparam>
    /// <param name="json">Raw JSON text; <see langword="null"/> or whitespace is treated as a missing value.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <returns>The converted value; <see langword="null"/> when the value is JSON null and <typeparamref name="T"/> accepts null.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="JsonException">
    /// <paramref name="json"/> is not valid JSON, the path is missing, or the value cannot be represented as
    /// <typeparamref name="T"/>.
    /// </exception>
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

    /// <inheritdoc cref="TryGetValueByPath{T}(string, JsonPath, JsonTypeInfo{T}, out T)"/>
    public static bool TryGetValueByPath<T>(
        this string? json,
        string path,
        JsonTypeInfo<T> typeInfo,
        out T? value
    ) => json.TryGetValueByPath(JsonPath.Parse(path), typeInfo, out value);

    /// <summary>
    /// Parses <paramref name="json"/> and attempts to deserialize the value at <paramref name="path"/> using
    /// source-generated metadata, which keeps DTO reads trim- and AOT-safe.
    /// </summary>
    /// <typeparam name="T">Type of the DTO to materialize.</typeparam>
    /// <param name="json">Raw JSON text. <see langword="null"/> or whitespace returns <see langword="false"/>.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a <c>JsonSerializerContext</c>.</param>
    /// <param name="value">Receives the deserialized object when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the path resolved and the payload deserialized.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">Non-blank <paramref name="json"/> is not valid JSON.</exception>
    public static bool TryGetValueByPath<T>(
        this string? json,
        JsonPath path,
        JsonTypeInfo<T> typeInfo,
        out T? value
    )
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

    /// <inheritdoc cref="GetValueByPath{T}(string, JsonPath, JsonTypeInfo{T})"/>
    public static T? GetValueByPath<T>(this string? json, string path, JsonTypeInfo<T> typeInfo) =>
        json.GetValueByPath(JsonPath.Parse(path), typeInfo);

    /// <summary>
    /// Parses <paramref name="json"/> and deserializes the value at <paramref name="path"/> using source-generated
    /// metadata, throwing when the value is missing or cannot be materialized.
    /// </summary>
    /// <typeparam name="T">Type of the DTO to materialize.</typeparam>
    /// <param name="json">Raw JSON text; <see langword="null"/> or whitespace is treated as a missing value.</param>
    /// <param name="path">Singular path to the value.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, usually from a <c>JsonSerializerContext</c>.</param>
    /// <returns>The deserialized object, or <see langword="null"/> for JSON null when <typeparamref name="T"/> accepts null.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> contains a wildcard.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">
    /// <paramref name="json"/> is not valid JSON, the path is missing, or the payload is not a valid
    /// <typeparamref name="T"/>.
    /// </exception>
    public static T? GetValueByPath<T>(this string? json, JsonPath path, JsonTypeInfo<T> typeInfo)
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
