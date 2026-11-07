using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core;

namespace LinKit.Grpc;

public static class GrpcContextExtensions
{
    public static Metadata? GetHeaders(this ServerCallContext context)
    {
        Metadata? headers = context.RequestHeaders;
        return headers;
    }

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
