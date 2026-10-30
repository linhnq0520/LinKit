using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LinKit.Core.Messaging;

/// <summary>
/// Central JSON contract for broker messages. Register your source-generated contexts once at startup
/// (<c>LinKitMessageJson.AddContext(MyJsonContext.Default)</c>) so serialization is reflection-free and Native AOT safe.
/// Types without a registered context fall back to reflection only when it is enabled; otherwise a clear error is thrown.
/// </summary>
public static class LinKitMessageJson
{
    private static readonly object Gate = new();
    private static volatile JsonSerializerOptions _options = Create(Array.Empty<IJsonTypeInfoResolver>());
    private static IJsonTypeInfoResolver[] _resolvers = Array.Empty<IJsonTypeInfoResolver>();

    public static JsonSerializerOptions Options => _options;

    public static void AddContext(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (Gate)
        {
            if (Array.IndexOf(_resolvers, context) >= 0) return;
            var next = new IJsonTypeInfoResolver[_resolvers.Length + 1];
            _resolvers.CopyTo(next, 0);
            next[^1] = context;
            _resolvers = next;
            _options = Create(next);
        }
    }

    private static JsonSerializerOptions Create(IJsonTypeInfoResolver[] resolvers)
    {
        // Default (PascalCase) naming preserved for wire compatibility with messages produced before this contract existed.
        var options = new JsonSerializerOptions();
        foreach (var r in resolvers)
            options.TypeInfoResolverChain.Add(r);
        AddReflectionFallbackIfAvailable(options);
        return options;
    }

    // Reflection fallback is only wired up when the runtime actually allows it (never under Native AOT / trimming switches).
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Guarded by JsonSerializer.IsReflectionEnabledByDefault.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Guarded by RuntimeFeature.IsDynamicCodeSupported.")]
    private static void AddReflectionFallbackIfAvailable(JsonSerializerOptions options)
    {
        if (JsonSerializer.IsReflectionEnabledByDefault && RuntimeFeature.IsDynamicCodeSupported)
            options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
    }

    public static byte[] SerializeToUtf8Bytes(object message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Payloads already in JSON form (e.g. from the Outbox or raw bytes/strings) need no type metadata.
        if (message is byte[] raw)
            return raw;
        if (message is ReadOnlyMemory<byte> memory)
            return memory.ToArray();
        if (message is string rawJson)
            return Encoding.UTF8.GetBytes(rawJson);
        if (message is JsonElement element)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
                element.WriteTo(writer);
            return buffer.WrittenSpan.ToArray();
        }

        var info = _options.GetTypeInfo(message.GetType());
        return JsonSerializer.SerializeToUtf8Bytes(message, info);
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        var info = (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(utf8Json, info);
    }
}
