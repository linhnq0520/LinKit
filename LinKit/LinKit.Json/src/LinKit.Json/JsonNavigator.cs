using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinKit.Json;

internal interface IJsonAccessor<T>
{
    static abstract bool IsDefined(T value);

    static abstract bool TryGetProperty(T value, string name, out T result);

    static abstract bool TryGetIndex(T value, int index, out T result);

    static abstract IEnumerable<T> EnumerateArray(T value);
}

internal readonly struct JsonElementAccessor : IJsonAccessor<JsonElement>
{
    public static bool IsDefined(JsonElement value) => value.ValueKind != JsonValueKind.Undefined;

    public static bool TryGetProperty(JsonElement value, string name, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out result))
        {
            return true;
        }

        result = default;
        return false;
    }

    public static bool TryGetIndex(JsonElement value, int index, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var length = value.GetArrayLength();
            if (index < 0)
            {
                index += length;
            }

            if ((uint)index < (uint)length)
            {
                result = value[index];
                return true;
            }
        }

        result = default;
        return false;
    }

    public static IEnumerable<JsonElement> EnumerateArray(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                yield return item;
            }
        }
    }
}

internal readonly struct JsonNodeAccessor : IJsonAccessor<JsonNode?>
{
    public static bool IsDefined(JsonNode? value) => true;

    public static bool TryGetProperty(JsonNode? value, string name, out JsonNode? result)
    {
        if (value is JsonObject obj && obj.TryGetPropertyValue(name, out result))
        {
            return true;
        }

        result = null;
        return false;
    }

    public static bool TryGetIndex(JsonNode? value, int index, out JsonNode? result)
    {
        if (value is JsonArray array)
        {
            if (index < 0)
            {
                index += array.Count;
            }

            if ((uint)index < (uint)array.Count)
            {
                result = array[index];
                return true;
            }
        }

        result = null;
        return false;
    }

    public static IEnumerable<JsonNode?> EnumerateArray(JsonNode? value)
    {
        if (value is JsonArray array)
        {
            foreach (var item in array)
            {
                yield return item;
            }
        }
    }
}

internal static class JsonNavigator<T, TAccessor>
    where TAccessor : struct, IJsonAccessor<T>
{
    public static bool TrySelect(T root, JsonPath path, out T result)
    {
        result = root;
        if (!TAccessor.IsDefined(root))
        {
            result = default!;
            return false;
        }

        if (!path.IsSingular)
        {
            result = default!;
            return false;
        }

        var current = root;
        for (var index = 0; index < path.Count; index++)
        {
            if (!TryStep(current, path[index], out var next))
            {
                result = default!;
                return false;
            }

            current = next;
        }

        result = current;
        return true;
    }

    public static IEnumerable<T> SelectMany(T root, JsonPath path)
    {
        var branches = new Stack<Branch>();
        var current = root;
        var position = 0;
        var hasCurrent = TAccessor.IsDefined(root);

        try
        {
            while (true)
            {
                if (hasCurrent)
                {
                    if (position == path.Count)
                    {
                        yield return current;
                        hasCurrent = false;
                    }
                    else
                    {
                        var segment = path[position];
                        if (segment.Kind == JsonPath.SegmentKind.Wildcard)
                        {
                            branches.Push(new Branch(
                                TAccessor.EnumerateArray(current).GetEnumerator(),
                                position + 1));
                            hasCurrent = false;
                        }
                        else if (TryStep(current, segment, out var next))
                        {
                            current = next;
                            position++;
                        }
                        else
                        {
                            hasCurrent = false;
                        }
                    }
                }

                if (hasCurrent)
                {
                    continue;
                }

                if (branches.Count == 0)
                {
                    yield break;
                }

                var branch = branches.Peek();
                if (branch.Items.MoveNext())
                {
                    current = branch.Items.Current;
                    position = branch.NextPosition;
                    hasCurrent = true;
                }
                else
                {
                    branch.Items.Dispose();
                    branches.Pop();
                }
            }
        }
        finally
        {
            while (branches.TryPop(out var branch))
            {
                branch.Items.Dispose();
            }
        }
    }

    private static bool TryStep(T value, JsonPath.Segment segment, out T result) =>
        segment.Kind switch
        {
            JsonPath.SegmentKind.Property => TAccessor.TryGetProperty(value, segment.Name!, out result),
            JsonPath.SegmentKind.Index => TAccessor.TryGetIndex(value, segment.Index, out result),
            _ => Fail(out result)
        };

    private static bool Fail(out T result)
    {
        result = default!;
        return false;
    }

    private sealed record Branch(IEnumerator<T> Items, int NextPosition);
}
