using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MonolithSample;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Modules.Catalog.Models.ProductDto))]
public partial class MonolithDataJsonContext : JsonSerializerContext
{
}
