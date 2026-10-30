using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinKit.Json;

/// <summary>Extensions for selecting JSON values by <see cref="JsonPath"/>.</summary>
public static class JsonSelectionExtensions
{
    /// <summary>Selects a single value without cloning it.</summary>
    public static bool TrySelect(this JsonElement root, string path, out JsonElement result) =>
        root.TrySelect(JsonPath.Parse(path), out result);

    /// <summary>Selects a single value without cloning it.</summary>
    public static bool TrySelect(this JsonElement root, JsonPath path, out JsonElement result)
    {
        ArgumentNullException.ThrowIfNull(path);
        JsonPath.ValidateSingular(path);
        return JsonNavigator<JsonElement, JsonElementAccessor>.TrySelect(root, path, out result);
    }

    /// <summary>Selects a single node by reference. A true return with a null result means JSON null.</summary>
    public static bool TrySelect(this JsonNode? root, string path, out JsonNode? result) =>
        root.TrySelect(JsonPath.Parse(path), out result);

    /// <summary>Selects a single node by reference. A true return with a null result means JSON null.</summary>
    public static bool TrySelect(this JsonNode? root, JsonPath path, out JsonNode? result)
    {
        ArgumentNullException.ThrowIfNull(path);
        JsonPath.ValidateSingular(path);
        return JsonNavigator<JsonNode?, JsonNodeAccessor>.TrySelect(root, path, out result);
    }

    /// <summary>Selects values in array order. The owning document must remain alive.</summary>
    public static IEnumerable<JsonElement> SelectMany(this JsonElement root, string path) =>
        root.SelectMany(JsonPath.Parse(path));

    /// <summary>Selects values in array order. The owning document must remain alive.</summary>
    public static IEnumerable<JsonElement> SelectMany(this JsonElement root, JsonPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return JsonNavigator<JsonElement, JsonElementAccessor>.SelectMany(root, path);
    }

    /// <summary>Selects nodes by reference, including JSON null. Do not mutate during enumeration.</summary>
    public static IEnumerable<JsonNode?> SelectMany(this JsonNode? root, string path) =>
        root.SelectMany(JsonPath.Parse(path));

    /// <summary>Selects nodes by reference, including JSON null. Do not mutate during enumeration.</summary>
    public static IEnumerable<JsonNode?> SelectMany(this JsonNode? root, JsonPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return JsonNavigator<JsonNode?, JsonNodeAccessor>.SelectMany(root, path);
    }
}
