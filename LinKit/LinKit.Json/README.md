# LinKit.Json

`LinKit.Json` is a small, dependency-free library for navigating JSON documents and reading
strongly typed values with `System.Text.Json`. It targets .NET 8, .NET 9, and .NET 10.

The library provides:

- Reusable, immutable JSON paths with a deliberately small, predictable JSONPath syntax.
- Navigation for `JsonElement`, `JsonDocument`, and `JsonNode`.
- Lazy wildcard selection in document order.
- Strict conversions for common JSON scalar values and exact integer values, including
  `Int128` and `UInt128`.
- DTO deserialization through `JsonTypeInfo<T>`, without requiring reflection-based
  serializer metadata.

## Installation

Install the package from NuGet:

```shell
dotnet add package LinKit.Json
```

Or add a package reference to your project file:

```xml
<PackageReference Include="LinKit.Json" Version="1.0.0" />
```

## Quick start

The extension methods are in the `LinKit.Json` namespace:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LinKit.Json;

const string json = """
    {
      "profile": { "display.name": "Ada", "active": true },
      "orders": [
        { "id": 101, "total": 24.50 },
        { "id": 102, "total": 18.00 }
      ]
    }
    """;

using var document = JsonDocument.Parse(json);
var root = document.RootElement;

string name = root.GetValueByPath<string>("$.profile['display.name']");
bool active = root.GetRequiredBoolean("$.profile.active");
int lastOrderId = root.GetValueByPath<int>("$.orders[-1].id");

IEnumerable<int> orderIds = root
    .SelectMany("$.orders[*].id")
    .Select(static value => value.GetInt32());
```

For code that queries the same path more than once, parse the path once and reuse it:

```csharp
var orderIdsPath = JsonPath.Parse("$.orders[*].id");
IEnumerable<JsonElement> ids = root.SelectMany(orderIdsPath);
```

`JsonPath` is immutable and can be shared. Reusing it avoids parsing the path expression
for every query.

## Supported path syntax

Every path starts at `$`, which selects the root value:

| Syntax | Description | Example |
| --- | --- | --- |
| `$` | The root value | `$` |
| `.name` | An unquoted property name | `$.profile.name` |
| `['name']` or `["name"]` | A quoted property name | `$.profile['display.name']` |
| `[0]` | A zero-based array index | `$.orders[0]` |
| `[-1]` | An index from the end of an array | `$.orders[-1]` |
| `[*]` or `.*` | Each element of an array | `$.orders[*].id` |

Unquoted property names use ASCII identifiers: the first character must be an ASCII letter
or `_`; subsequent characters can also include digits. Use quoted bracket notation for
names containing spaces, punctuation, non-ASCII characters, or reserved path characters.
Quoted names support JSON-style escapes (`\"`, `\\`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`,
and `\uXXXX`); single-quoted names may also escape a single quote.

Array indexes are signed 32-bit integers. Leading zeros, `+` prefixes, and negative zero
are rejected. Negative indexes count backward from the end (`-1` is the last element).
Indexes outside the array return no match.

This is a JSONPath **subset**, not a full JSONPath implementation. It does not support
recursive descent, filters, slices, unions, or wildcards over object properties. Wildcards
enumerate array elements only.

Malformed paths throw `FormatException`; a `null` path throws `ArgumentNullException`.
Single-value APIs reject wildcard paths with `ArgumentException`.

## Selecting JSON values

### Select one value

`TrySelect` returns `false` if the path does not match. JSON `null` is still a match:

```csharp
if (root.TrySelect("$.profile", out JsonElement profile))
{
    Console.WriteLine(profile.GetRawText());
}
```

With `JsonNode`, check the return value separately from the result: `true` with a
`null` result means the path selected JSON `null`.

```csharp
JsonNode? node = JsonNode.Parse("""{ "value": null }""");

if (node.TrySelect("$.value", out JsonNode? value))
{
    // The path exists, even though its JSON value is null.
}
```

### Select multiple values

Use `SelectMany` for paths containing wildcards. The sequence is lazy and preserves array
order; nested wildcards are traversed depth-first:

```csharp
IEnumerable<JsonElement> totals = root.SelectMany("$.orders[*].total");

foreach (JsonElement total in totals)
{
    Console.WriteLine(total.GetDecimal());
}
```

If a wildcard is applied to a missing value or a non-array, it produces no matches.
`JsonElement` results refer to the original document, so keep its `JsonDocument` alive
while enumerating or using those results. `JsonNode` results are returned by reference;
do not mutate the node tree while enumerating.

## Reading typed values

`GetValueByPath<T>` and `TryGetValueByPath<T>` are available on `JsonElement`,
`JsonDocument`, `JsonNode?`, and JSON strings:

```csharp
int id = root.GetValueByPath<int>("$.orders[0].id");

if (json.TryGetValueByPath<bool>("$.profile.active", out bool isActive))
{
    Console.WriteLine(isActive);
}
```

Supported scalar target types are:

- `string`, `char`, and `bool`
- `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `Int128`, and
  `UInt128`
- `float`, `double`, and `decimal`
- `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, and `Uri`
- `JsonElement`, `JsonNode`, `JsonObject`, `JsonArray`, and `JsonValue`

Conversions are strict: a JSON string is not converted to a number, a JSON number is not
converted to a string, and booleans are not converted to numeric values. Integer
conversions preserve the exact JSON number instead of routing it through floating point;
fractional values and values outside the target type's range are rejected. Non-finite
floating-point values are not accepted as JSON numbers.

Date/time values are read from JSON strings using `System.Text.Json`'s ISO-8601 parsing;
`DateTimeOffset` values are normalized to UTC. `TimeSpan` strings use the invariant
constant (`c`) format. `Uri` values may be relative or absolute.

When the selected value is JSON `null`, `TryGetValueByPath<T>` succeeds only when `T`
accepts null (for example, `string?` or `int?`). Otherwise it returns `false`.

### Required primitive values

Use `GetRequiredString`, `GetRequiredBoolean`, `GetRequiredInt32`,
`GetRequiredInt64`, `GetRequiredDecimal`, and `GetRequiredDouble` when the value is
required:

```csharp
string name = root.GetRequiredString("$.profile.name");
int count = root.GetRequiredInt32("$.orders[0].id");
```

These methods require the expected JSON kind and do not coerce between kinds. A failed
read throws `JsonPathReadException`, whose `Path` and `Error` properties identify the
path and the reason:

- `Missing`: the path does not exist.
- `Null`: the selected JSON value is `null`.
- `TypeMismatch`: the value has the wrong JSON kind.
- `InvalidNumber`: the number cannot be represented by the requested type, is fractional
  where an integer is required, or is outside the finite `double` range.

`JsonPathReadException` derives from `InvalidOperationException`.

### Deserializing DTOs with source-generated metadata

DTOs and other custom types require explicit `JsonTypeInfo<T>` metadata. This avoids
reflection-based metadata discovery and works well with trimming and Native AOT:

```csharp
using System.Text.Json.Serialization;

const string json = """{ "settings": { "theme": "dark", "notificationsEnabled": true } }""";
AppSettings settings = json.GetValueByPath("$.settings", AppJsonContext.Default.AppSettings)!;

[JsonSerializable(typeof(AppSettings))]
internal partial class AppJsonContext : JsonSerializerContext
{
}

internal sealed record AppSettings(string Theme, bool NotificationsEnabled);
```

Use the corresponding `JsonTypeInfo<T>` overloads on `JsonElement`, `JsonDocument`,
`JsonNode?`, or a JSON string. `TryGetValueByPath` returns `false` for a missing value,
an incompatible JSON value, or a JSON deserialization error. `GetValueByPath` throws
`JsonException` when it cannot supply the requested value.

## Error behavior

| API | No matching value | Invalid JSON input | Invalid path |
| --- | --- | --- | --- |
| `TrySelect` / `TryGetValueByPath` on a JSON value | Returns `false` | Not applicable | Throws |
| `GetValueByPath` on a JSON value | Throws `JsonException` | Not applicable | Throws |
| `TryGetValueByPath` on a JSON string | Returns `false` for `null` or blank input | Throws `JsonException` for malformed, nonblank JSON | Throws |
| `GetValueByPath` on a JSON string | Throws `JsonException` for `null` or blank input | Throws `JsonException` for malformed JSON | Throws |
| `GetRequired...` | Throws `JsonPathReadException` | Not applicable | Throws |

## Framework support

| Target framework | Supported |
| --- | --- |
| .NET 8 | Yes |
| .NET 9 | Yes |
| .NET 10 | Yes |

The library has no third-party package dependencies.
