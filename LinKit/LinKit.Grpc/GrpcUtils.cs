using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Grpc.Core;

namespace LinKit.Grpc;

public class GrpcUtils
{
    public static Metadata? GetHeaders()
    {
        var context = GrpcContextAccessor.Current;
        return context?.GetHeaders();
    }

    public static string? GetHeaderValueString<T>(string key)
    {
        var context = GrpcContextAccessor.Current;
        if (context is null)
        {
            return default;
        }
        return context.GetHeaderValue(key);
    }

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
