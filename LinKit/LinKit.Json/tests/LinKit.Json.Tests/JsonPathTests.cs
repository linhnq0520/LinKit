using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Globalization;
using LinKit.Json;
using Xunit;

namespace LinKit.Json.Tests;

public sealed partial class JsonPathTests
{
    [Fact]
    public void SelectsPropertiesAndNegativeIndexes()
    {
        using var document = JsonDocument.Parse(
            """{"store":{"items":[{"name":"first"},{"name":"last"}]}}""");

        Assert.Equal("last", document.RootElement.GetValueByPath<string>("$.store.items[-1].name"));
    }

    [Fact]
    public void RootPathSelectsRootValues()
    {
        using var document = JsonDocument.Parse("42");

        Assert.True(document.RootElement.TrySelect("$", out var selected));
        Assert.Equal(42, selected.GetInt32());
        Assert.Equal(42, document.RootElement.GetValueByPath<int>("$"));
    }

    [Fact]
    public void DefaultJsonElementDoesNotMatchRootPath()
    {
        var root = default(JsonElement);

        Assert.False(root.TrySelect("$", out _));
        Assert.False(root.TryGetValueByPath<JsonElement>("$", out _));
        Assert.Empty(root.SelectMany("$"));
    }

    [Fact]
    public void SelectManyTraversesWildcardsInArrayOrder()
    {
        using var document = JsonDocument.Parse(
            """{"items":[{"id":1},{"id":2},{"id":3}]}""");

        var ids = document.RootElement
            .SelectMany("$.items[*].id")
            .Select(static item => item.GetInt32());

        Assert.Equal([1, 2, 3], ids);
    }

    [Fact]
    public void SupportsBothWildcardSpellingsAndEmptyPropertyNames()
    {
        using var document = JsonDocument.Parse(
            """{"":{"items":[1,2]},"other":[3,4]}""");

        Assert.Equal(
            [1, 2],
            document.RootElement.SelectMany("$[''].items.*").Select(static item => item.GetInt32()));
        Assert.Equal(
            [3, 4],
            document.RootElement.SelectMany("$.other[*]").Select(static item => item.GetInt32()));
    }

    [Fact]
    public void SelectManyTraversesNestedWildcardsDepthFirst()
    {
        using var document = JsonDocument.Parse(
            """{"groups":[{"items":[1,2]},{"items":[3]}]}""");

        var values = document.RootElement
            .SelectMany("$.groups[*].items[*]")
            .Select(static item => item.GetInt32());

        Assert.Equal([1, 2, 3], values);
    }

    [Fact]
    public void WildcardSelectManyIsLazy()
    {
        var root = JsonNode.Parse("""{"items":[1]}""")
            ?? throw new InvalidOperationException("The test JSON root must be an object.");
        var matches = root.SelectMany("$.items[*]");
        root["items"]!.AsArray().Add(2);

        Assert.Equal([1, 2], matches.Select(static value => value!.GetValue<int>()));
    }

    [Fact]
    public void ParsesQuotedPropertyNamesAndEscapes()
    {
        using var document = JsonDocument.Parse("""{"quote'key":"value","line\nbreak":9}""");

        Assert.Equal("value", document.RootElement.GetValueByPath<string>("$['quote\\'key']"));
        Assert.Equal(9, document.RootElement.GetValueByPath<int>("$['line\\nbreak']"));
    }

    [Fact]
    public void ParsesAllSupportedJsonStringEscapesInPropertyNames()
    {
        using var document = JsonDocument.Parse(
            """{"double\"quote":1,"slash\\key":2,"tab\tkey":3,"unicode\u263A":4}""");

        Assert.Equal(1, document.RootElement.GetValueByPath<int>("$[\"double\\\"quote\"]"));
        Assert.Equal(2, document.RootElement.GetValueByPath<int>("$['slash\\\\key']"));
        Assert.Equal(3, document.RootElement.GetValueByPath<int>("$['tab\\tkey']"));
        Assert.Equal(4, document.RootElement.GetValueByPath<int>("$['unicode\\u263A']"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("items")]
    [InlineData("$.items[01]")]
    [InlineData("$.items[-0]")]
    [InlineData("$.items[+1]")]
    [InlineData("$.items[")]
    [InlineData("$.items[]")]
    [InlineData("$.items[1.0]")]
    [InlineData("$.items[1]tail")]
    [InlineData("$..items")]
    [InlineData("$['unfinished]")]
    [InlineData("$['bad\\qescape']")]
    [InlineData("$['bad\nline']")]
    [InlineData("$['bad\\u12xz']")]
    [InlineData("$.9items")]
    public void RejectsMalformedPaths(string path)
    {
        Assert.Throws<FormatException>(() => JsonPath.Parse(path));
    }

    [Fact]
    public void SingleValueSelectionRejectsWildcards()
    {
        using var document = JsonDocument.Parse("""{"items":[1]}""");

        Assert.Throws<ArgumentException>(() =>
            document.RootElement.TryGetValueByPath<int>("$.items[*]", out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("-2")]
    public void SupportsInRangePositiveAndNegativeArrayIndexes(string index)
    {
        using var document = JsonDocument.Parse("""{"items":[10,20,30]}""");
        var actualIndex = int.Parse(index, CultureInfo.InvariantCulture);

        Assert.True(document.RootElement.TrySelect($"$.items[{index}]", out var selected));
        var expectedIndex = actualIndex < 0 ? actualIndex + 3 : actualIndex;
        Assert.Equal(document.RootElement.GetProperty("items")[expectedIndex].GetInt32(), selected.GetInt32());
    }

    [Theory]
    [InlineData("3")]
    [InlineData("-4")]
    [InlineData("2147483647")]
    [InlineData("-2147483648")]
    public void OutOfRangeArrayIndexesReturnNoMatch(string index)
    {
        using var document = JsonDocument.Parse("""{"items":[10,20,30]}""");

        Assert.False(document.RootElement.TrySelect($"$.items[{index}]", out _));
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public void RejectsArrayIndexesOutsideInt32(string index)
    {
        Assert.Throws<FormatException>(() => JsonPath.Parse($"$.items[{index}]"));
    }

    [Fact]
    public void ConvertsExactLargeIntegersWithoutFloatingPointRounding()
    {
        const string json = """{"large":170141183460469231731687303715884105727,"scaled":1.2300e2}""";

        Assert.Equal(
            Int128.MaxValue,
            json.GetValueByPath<Int128>("$.large"));
        Assert.Equal(123, json.GetValueByPath<int>("$.scaled"));
    }

    [Fact]
    public void ConvertsIntegerExtremesExactly()
    {
        const string json =
            """{"signedMin":-170141183460469231731687303715884105728,"unsignedMax":340282366920938463463374607431768211455}""";

        Assert.Equal(Int128.MinValue, json.GetValueByPath<Int128>("$.signedMin"));
        Assert.Equal(UInt128.MaxValue, json.GetValueByPath<UInt128>("$.unsignedMax"));
        Assert.False(json.TryGetValueByPath<Int128>("$.unsignedMax", out _));
        Assert.False(json.TryGetValueByPath<UInt128>("$.signedMin", out _));
    }

    [Fact]
    public void NumericAndStringConversionsDoNotCoerceAcrossKinds()
    {
        using var document = JsonDocument.Parse("""{"text":"42","number":42,"boolean":true}""");

        Assert.False(document.RootElement.TryGetValueByPath<int>("$.text", out _));
        Assert.False(document.RootElement.TryGetValueByPath<string>("$.number", out _));
        Assert.False(document.RootElement.TryGetValueByPath<int>("$.boolean", out _));
        Assert.Equal(42d, document.RootElement.GetValueByPath<double>("$.number"));
    }

    [Fact]
    public void ConvertsSupportedScalarTypes()
    {
        const string json =
            """{"char":"x","guid":"d85b1407-351d-4694-9392-03acc5870eb1","date":"2025-04-03T02:01:00Z","offset":"2025-04-03T09:01:00+07:00","duration":"1.02:03:04.005","uri":"../resource","float":1.5,"decimal":123.45}""";
        using var document = JsonDocument.Parse(json);

        Assert.Equal('x', document.RootElement.GetValueByPath<char>("$.char"));
        Assert.Equal(
            Guid.Parse("d85b1407-351d-4694-9392-03acc5870eb1"),
            document.RootElement.GetValueByPath<Guid>("$.guid"));
        Assert.Equal(
            DateTime.Parse(
                "2025-04-03T02:01:00Z",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            document.RootElement.GetValueByPath<DateTime>("$.date"));
        Assert.Equal(
            DateTimeOffset.Parse("2025-04-03T09:01:00+07:00", CultureInfo.InvariantCulture).UtcTicks,
            document.RootElement.GetValueByPath<DateTimeOffset>("$.offset").UtcTicks);
        Assert.Equal(
            TimeSpan.FromDays(1) + new TimeSpan(2, 3, 4) + TimeSpan.FromMilliseconds(5),
            document.RootElement.GetValueByPath<TimeSpan>("$.duration"));
        Assert.Equal(
            new Uri("../resource", UriKind.Relative),
            document.RootElement.GetValueByPath<Uri>("$.uri"));
        Assert.Equal(1.5f, document.RootElement.GetValueByPath<float>("$.float"));
        Assert.Equal(123.45m, document.RootElement.GetValueByPath<decimal>("$.decimal"));
    }

    [Fact]
    public void RejectsIntegerOverflowAndFractionalValues()
    {
        const string json = """{"tooLarge":340282366920938463463374607431768211456,"fraction":1.25}""";

        Assert.False(json.TryGetValueByPath<UInt128>("$.tooLarge", out _));
        Assert.False(json.TryGetValueByPath<int>("$.fraction", out _));
    }

    [Theory]
    [InlineData("128", typeof(sbyte), false)]
    [InlineData("-129", typeof(sbyte), false)]
    [InlineData("32768", typeof(short), false)]
    [InlineData("-32769", typeof(short), false)]
    [InlineData("2147483648", typeof(int), false)]
    [InlineData("-2147483649", typeof(int), false)]
    [InlineData("9223372036854775808", typeof(long), false)]
    [InlineData("-9223372036854775809", typeof(long), false)]
    [InlineData("-1", typeof(byte), false)]
    [InlineData("-1", typeof(ulong), false)]
    [InlineData("127", typeof(sbyte), true)]
    [InlineData("-128", typeof(sbyte), true)]
    [InlineData("18446744073709551615", typeof(ulong), true)]
    [InlineData("-170141183460469231731687303715884105728", typeof(Int128), true)]
    public void ConvertsIntegerValuesAtTargetBoundaries(string number, Type targetType, bool expected)
    {
        using var document = JsonDocument.Parse(number);
        Assert.Equal(expected, TryConvertInteger(document.RootElement, targetType));
    }

    [Fact]
    public void RequiredReadersDistinguishMissingNullAndWrongKind()
    {
        using var document = JsonDocument.Parse("""{"nullValue":null,"text":"1"}""");

        Assert.Equal(
            JsonPathReadError.Missing,
            Assert.Throws<JsonPathReadException>(() =>
                document.RootElement.GetRequiredInt32("$.missing")).Error);
        Assert.Equal(
            JsonPathReadError.Null,
            Assert.Throws<JsonPathReadException>(() =>
                document.RootElement.GetRequiredInt32("$.nullValue")).Error);
        Assert.Equal(
            JsonPathReadError.TypeMismatch,
            Assert.Throws<JsonPathReadException>(() =>
                document.RootElement.GetRequiredInt32("$.text")).Error);
    }

    [Fact]
    public void RequiredDoubleRejectsValuesOutsideTheFiniteRange()
    {
        using var document = JsonDocument.Parse("""{"number":1e400}""");

        Assert.Equal(
            JsonPathReadError.InvalidNumber,
            Assert.Throws<JsonPathReadException>(() =>
                document.RootElement.GetRequiredDouble("$.number")).Error);
    }

    [Fact]
    public void SupportsJsonNodeAndPreservesJsonNullMatches()
    {
        var root = JsonNode.Parse("""{"values":[null,{"id":7}]}""");

        var values = root!.SelectMany("$.values[*]").ToArray();

        Assert.Equal(2, values.Length);
        Assert.Null(values[0]);
        Assert.Equal(7, values[1]!.GetValueByPath<int>("$.id"));
        Assert.True(root.TrySelect("$.values[0]", out var nullValue));
        Assert.Null(nullValue);
    }

    [Fact]
    public void WildcardOnNonArrayOrMissingPropertyReturnsEmptyResults()
    {
        using var document = JsonDocument.Parse("""{"items":{},"empty":[]}""");

        Assert.Empty(document.RootElement.SelectMany("$.items[*]"));
        Assert.Empty(document.RootElement.SelectMany("$.missing[*]"));
        Assert.Empty(document.RootElement.SelectMany("$.empty[*]"));
    }

    [Fact]
    public void JsonNodeSelectionHandlesProgrammaticallyConstructedValues()
    {
        var root = new JsonObject
        {
            ["number"] = JsonValue.Create(42L),
            ["items"] = new JsonArray(JsonValue.Create("first"), JsonValue.Create("last"))
        };

        Assert.Equal(42, root.GetValueByPath<int>("$.number"));
        Assert.Equal("last", root.GetValueByPath<string>("$.items[-1]"));
    }

    [Fact]
    public void TryReadersRejectProgrammaticNonFiniteNumbers()
    {
        var root = new JsonObject
        {
            ["number"] = JsonValue.Create(double.NaN)
        };

        Assert.False(root.TryGetValueByPath<double>("$.number", out _));
        Assert.Equal(
            JsonPathReadError.InvalidNumber,
            Assert.Throws<JsonPathReadException>(() =>
                root.GetRequiredDouble("$.number")).Error);
    }

    [Fact]
    public void UsesExplicitMetadataForDtoDeserialization()
    {
        const string json = """{"profile":{"Name":"Ada","Age":37}}""";

        var profile = json.GetValueByPath("$.profile", TestJsonContext.Default.Profile);

        Assert.Equal(new Profile("Ada", 37), profile);
    }

    [Fact]
    public void DeserializesDtoUsingMetadataFromJsonNodeAndDocument()
    {
        var node = JsonNode.Parse("""{"profile":{"Name":"Ada","Age":37}}""");
        using var document = JsonDocument.Parse("""{"profile":{"Name":"Ada","Age":37}}""");

        Assert.Equal(
            new Profile("Ada", 37),
            node.GetValueByPath("$.profile", TestJsonContext.Default.Profile));
        Assert.Equal(
            new Profile("Ada", 37),
            document.GetValueByPath("$.profile", TestJsonContext.Default.Profile));
    }

    [Fact]
    public void MetadataDeserializationReportsTypeMismatch()
    {
        const string json = """{"profile":{"Name":"Ada","Age":"not-a-number"}}""";

        Assert.False(json.TryGetValueByPath("$.profile", TestJsonContext.Default.Profile, out _));
        Assert.Throws<JsonException>(() =>
            json.GetValueByPath("$.profile", TestJsonContext.Default.Profile));
    }

    [Fact]
    public void JsonElementResultsRemainValidAfterDocumentDisposal()
    {
        JsonElement selected;
        using (var document = JsonDocument.Parse("""{"value":{"id":7}}"""))
        {
            selected = document.RootElement.GetValueByPath<JsonElement>("$.value");
        }

        Assert.Equal(7, selected.GetProperty("id").GetInt32());
    }

    [Fact]
    public void ConvertsJsonNodeScalarsAndKeepsNullableValues()
    {
        var root = JsonNode.Parse("""{"count":4,"text":"ok","nothing":null}""");

        Assert.Equal(4, root.GetValueByPath<int>("$.count"));
        Assert.Equal("ok", root.GetValueByPath<string>("$.text"));
        Assert.Null(root.GetValueByPath<string?>("$.nothing"));
        Assert.False(root.TryGetValueByPath<int>("$.nothing", out _));
    }

    [Fact]
    public void TimeSpanConversionUsesInvariantConstantFormat()
    {
        using var document = JsonDocument.Parse("""{"valid":"1.02:03:04.005","localized":"00:00:00,5"}""");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.Equal(
                TimeSpan.FromDays(1) + new TimeSpan(2, 3, 4) + TimeSpan.FromMilliseconds(5),
                document.RootElement.GetValueByPath<TimeSpan>("$.valid"));
            Assert.False(document.RootElement.TryGetValueByPath<TimeSpan>("$.localized", out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void RequiredReadersAcceptFalseAndRejectIntegerOverflow()
    {
        using var document = JsonDocument.Parse("""{"enabled":false,"large":2147483648}""");

        Assert.False(document.RootElement.GetRequiredBoolean("$.enabled"));
        Assert.Equal(
            JsonPathReadError.InvalidNumber,
            Assert.Throws<JsonPathReadException>(() =>
                document.RootElement.GetRequiredInt32("$.large")).Error);
    }

    [Fact]
    public void BlankInputReturnsFalseAndMalformedJsonStillThrows()
    {
        Assert.False(((string?)" ").TryGetValueByPath<int>("$.id", out _));
        Assert.ThrowsAny<JsonException>(() => """{"id":""".GetValueByPath<int>("$.id"));
    }

    [JsonSerializable(typeof(Profile))]
    private partial class TestJsonContext : JsonSerializerContext
    {
    }

    public sealed record Profile(string Name, int Age);

    private static bool TryConvertInteger(JsonElement value, Type targetType)
    {
        if (targetType == typeof(sbyte)) return value.TryGetValueByPath("$", out sbyte _);
        if (targetType == typeof(byte)) return value.TryGetValueByPath("$", out byte _);
        if (targetType == typeof(short)) return value.TryGetValueByPath("$", out short _);
        if (targetType == typeof(ushort)) return value.TryGetValueByPath("$", out ushort _);
        if (targetType == typeof(int)) return value.TryGetValueByPath("$", out int _);
        if (targetType == typeof(uint)) return value.TryGetValueByPath("$", out uint _);
        if (targetType == typeof(long)) return value.TryGetValueByPath("$", out long _);
        if (targetType == typeof(ulong)) return value.TryGetValueByPath("$", out ulong _);
        if (targetType == typeof(Int128)) return value.TryGetValueByPath("$", out Int128 _);
        if (targetType == typeof(UInt128)) return value.TryGetValueByPath("$", out UInt128 _);
        throw new ArgumentOutOfRangeException(nameof(targetType));
    }
}
