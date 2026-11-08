using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Grpc.Core;

namespace LinKit.Grpc;

/// <summary>
/// Convenience wrappers that read headers from the ambient <see cref="GrpcContextAccessor.Current"/> instead of taking a
/// <see cref="ServerCallContext"/>, which keeps handler signatures free of transport types.
/// </summary>
public class GrpcUtils
{
    /// <summary>Returns the incoming metadata of the current call, or <see langword="null"/> when no call is ambient.</summary>
    public static Metadata? GetHeaders()
    {
        var context = GrpcContextAccessor.Current;
        return context?.GetHeaders();
    }

    /// <summary>Returns the raw string value of <paramref name="key"/> for the current call.</summary>
    /// <typeparam name="T">Unused; present for symmetry with the converting overloads.</typeparam>
    /// <param name="key">Header name; matched case-insensitively.</param>
    /// <returns>The raw value, or <see langword="null"/> when no call is ambient or the header is absent.</returns>
    public static string? GetHeaderValueString<T>(string key)
    {
        var context = GrpcContextAccessor.Current;
        if (context is null)
        {
            return default;
        }
        return context.GetHeaderValue(key);
    }

    /// <summary>
    /// Converts the header to <typeparamref name="T"/> using reflection-based JSON for reference types.
    /// </summary>
    /// <typeparam name="T">Target type.</typeparam>
    /// <param name="key">Header name; matched case-insensitively.</param>
    /// <returns>The converted value, or <see langword="default"/> when no call is ambient or the header is absent.</returns>
    [RequiresUnreferencedCode(
        "Reference-type headers are deserialized with reflection-based System.Text.Json. Use the JsonSerializerContext overload instead."
    )]
    [RequiresDynamicCode(
        "Reference-type headers are deserialized with reflection-based System.Text.Json. Use the JsonSerializerContext overload instead."
    )]
    public static T? GetHeaderValue<T>(string key)
    {
        var context = GrpcContextAccessor.Current;
        if (context is null)
        {
            return default;
        }
        return context.GetHeaderValue<T>(key);
    }

    /// <summary>Converts the header to <typeparamref name="T"/> with source-generated serialization metadata.</summary>
    /// <typeparam name="T">Target type.</typeparam>
    /// <param name="key">Header name; matched case-insensitively.</param>
    /// <param name="serializerContext">Serialization metadata used for reference types.</param>
    /// <returns>The converted value, or <see langword="default"/> when no call is ambient or the header is absent.</returns>
    public static T? GetHeaderValue<T>(string key, JsonSerializerContext serializerContext)
    {
        var context = GrpcContextAccessor.Current;
        if (context is null)
        {
            return default;
        }
        return context.GetHeaderValue<T>(key, serializerContext);
    }
}
