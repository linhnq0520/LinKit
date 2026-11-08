namespace LinKit.Json;

/// <summary>Describes why a required JSON path read failed.</summary>
public enum JsonPathReadError
{
    /// <summary>The path does not exist in the document.</summary>
    Missing,

    /// <summary>The path exists but holds JSON null.</summary>
    Null,

    /// <summary>The value holds a different JSON kind than the reader requires.</summary>
    TypeMismatch,

    /// <summary>The value is a number but is not representable by the requested numeric type.</summary>
    InvalidNumber,
}

/// <summary>The required value at a JSON path could not be read.</summary>
public sealed class JsonPathReadException(JsonPath path, JsonPathReadError error)
    : InvalidOperationException($"The value at '{path}' could not be read: {error}.")
{
    /// <summary>The path that failed to resolve.</summary>
    public JsonPath Path { get; } = path;

    /// <summary>The specific failure reason, which lets callers distinguish a missing value from a type mismatch.</summary>
    public JsonPathReadError Error { get; } = error;
}
