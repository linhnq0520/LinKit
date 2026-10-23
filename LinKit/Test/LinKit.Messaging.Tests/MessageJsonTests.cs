using System.Text.Json;
using System.Text.Json.Serialization;
using LinKit.Core.Messaging;
using Xunit;

namespace LinKit.Messaging.Tests;

public partial class MessageJsonTests
{
    public record Sample(string Id, int N);

    [JsonSerializable(typeof(Sample))]
    private partial class SampleContext : JsonSerializerContext;

    [Fact]
    public void RoundTripsThroughRegisteredContext()
    {
        LinKitMessageJson.AddContext(SampleContext.Default);
        var bytes = LinKitMessageJson.SerializeToUtf8Bytes(new Sample("a", 2));
        var back = LinKitMessageJson.Deserialize<Sample>(bytes);
        Assert.Equal(new Sample("a", 2), back);
    }

    [Fact]
    public void JsonElementPayloadIsWrittenVerbatim()
    {
        using var doc = JsonDocument.Parse("""{"x":1}""");
        var bytes = LinKitMessageJson.SerializeToUtf8Bytes(doc.RootElement);
        Assert.Equal("""{"x":1}""", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void RegisteringSameContextTwiceIsIdempotent()
    {
        LinKitMessageJson.AddContext(SampleContext.Default);
        var first = LinKitMessageJson.Options;
        LinKitMessageJson.AddContext(SampleContext.Default);
        Assert.Same(first, LinKitMessageJson.Options);
    }
}
