namespace LinKit.Json;

/// <summary>Describes why a required JSON path read failed.</summary>
public enum JsonPathReadError
{
    Missing,
    Null,
    TypeMismatch,
    InvalidNumber
}

/// <summary>The required value at a JSON path could not be read.</summary>
public sealed class JsonPathReadException(JsonPath path, JsonPathReadError error)
    : InvalidOperationException($"The value at '{path}' could not be read: {error}.")
{
    public JsonPath Path { get; } = path;

    public JsonPathReadError Error { get; } = error;
}
