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
