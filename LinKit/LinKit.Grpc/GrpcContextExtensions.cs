using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core;

namespace LinKit.Grpc;

/// <summary>
/// Reads incoming gRPC headers from a <see cref="ServerCallContext"/>.
/// </summary>
/// <remarks>
/// Header lookup is case-insensitive. Prefer the overload that takes a <see cref="JsonSerializerContext"/>: the
/// parameterless generic overload falls back to reflection-based JSON and is therefore flagged as trimming- and
/// AOT-unsafe.
/// </remarks>
public static class GrpcContextExtensions
{
    /// <summary>Returns the raw incoming metadata, or <see langword="null"/> when the call carried none.</summary>
    /// <param name="context">Server call context for the current request.</param>
    public static Metadata? GetHeaders(this ServerCallContext context)
    {
        Metadata? headers = context.RequestHeaders;
        return headers;
    }

    /// <summary>Returns the first header value matching <paramref name="key"/>, or <see langword="null"/> when absent.</summary>
    /// <param name="context">Server call context for the current request.</param>
    /// <param name="key">Header name; matched case-insensitively.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or whitespace.</exception>
    public static string? GetHeaderValue(this ServerCallContext context, string key)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Header key must be provided", nameof(key));
        }

        return context
            .RequestHeaders.FirstOrDefault(h =>
                string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase)
            )
            ?.Value;
    }

    /// <summary>
    /// Reads a header and converts it to <typeparamref name="T"/> using source-generated serialization metadata, which
    /// keeps the read trim- and AOT-safe.
    /// </summary>
    /// <typeparam name="T">Target type; value types are converted with <see cref="Convert.ChangeType(object, Type)"/>, other
    /// types are deserialized from JSON.</typeparam>
    /// <param name="context">Server call context for the current request.</param>
    /// <param name="key">Header name; matched case-insensitively.</param>
    /// <param name="serializerContext">Serialization metadata used for reference types.</param>
    /// <returns>The converted value, or <see langword="default"/> when the header is absent or empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidCastException">The header value cannot be converted or deserialized to <typeparamref name="T"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="serializerContext"/> is <see langword="null"/> and a
    /// reference type needs deserialization.</exception>
    public static T? GetHeaderValue<T>(
        this ServerCallContext context,
        string key,
        JsonSerializerContext serializerContext
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Header key must be provided", nameof(key));
        }

        var value = context
            .RequestHeaders.FirstOrDefault(h =>
                string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase)
            )
            ?.Value;

        if (string.IsNullOrEmpty(value))
        {
            return default;
        }

        var targetType = typeof(T);
        if (targetType == typeof(string))
        {
            return (T)(object)value;
        }

        if (targetType.IsValueType)
        {
            try
            {
                return (T)Convert.ChangeType(value, targetType);
            }
            catch (Exception ex)
            {
                throw new InvalidCastException(
                    $"Cannot convert header '{key}' with value '{value}' to type {targetType.Name}.",
                    ex
                );
            }
        }

        if (serializerContext is null)
        {
            throw new InvalidOperationException(
                $"A JsonSerializerContext is required to deserialize header '{key}' to {targetType.Name} in AOT mode."
            );
        }

        try
        {
            return (T?)JsonSerializer.Deserialize(value, targetType, serializerContext);
        }
        catch (JsonException ex)
        {
            throw new InvalidCastException(
                $"Cannot deserialize header '{key}' with value '{value}' to type {targetType.Name}.",
                ex
            );
        }
    }

    /// <summary>
    /// Reads a header and converts it to <typeparamref name="T"/> without requiring a
    /// <see cref="JsonSerializerContext"/>. Reference types fall back to reflection-based JSON, so this
    /// overload is flagged as trimming- and AOT-unsafe; prefer the overload that takes a
    /// <see cref="JsonSerializerContext"/> in Native AOT deployments.
    /// </summary>
    [RequiresUnreferencedCode(
        "Reference-type headers are deserialized with reflection-based System.Text.Json. Use the JsonSerializerContext overload instead."
    )]
    [RequiresDynamicCode(
        "Reference-type headers are deserialized with reflection-based System.Text.Json. Use the JsonSerializerContext overload instead."
    )]
    public static T? GetHeaderValue<T>(this ServerCallContext context, string key)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Header key must be provided", nameof(key));
        }

        var value = context
            .RequestHeaders.FirstOrDefault(h =>
                string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase)
            )
            ?.Value;

        if (string.IsNullOrEmpty(value))
        {
            return default;
        }

        var targetType = typeof(T);
        if (targetType == typeof(string))
        {
            return (T)(object)value;
        }

        if (targetType.IsValueType)
        {
            try
            {
                return (T)Convert.ChangeType(value, targetType);
            }
            catch (Exception ex)
            {
                throw new InvalidCastException(
                    $"Cannot convert header '{key}' with value '{value}' to type {targetType.Name}.",
                    ex
                );
            }
        }

        try
        {
            return (T?)JsonSerializer.Deserialize(value, targetType);
        }
        catch (JsonException ex)
        {
            throw new InvalidCastException(
                $"Cannot deserialize header '{key}' with value '{value}' to type {targetType.Name}.",
                ex
            );
        }
    }
}
