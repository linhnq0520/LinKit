using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinKit.Data;

public sealed record ColumnModel(
    string Name,
    DbType Type,
    bool Nullable = false,
    bool PrimaryKey = false,
    bool Generated = false,
    bool Version = false,
    bool Tenant = false,
    bool SoftDelete = false,
    int Length = 0,
    byte Precision = 0,
    byte Scale = 0);

public sealed record TableModel(string Name, string? Schema, IReadOnlyList<ColumnModel> Columns)
{
    public ColumnModel Key => Columns.Single(column => column.PrimaryKey);
}

public sealed record SchemaSnapshot(IReadOnlyList<TableModel> Tables);

public sealed class DataMappingException(string message) : Exception(message);
public sealed class DataConcurrencyException(string message) : Exception(message);

public static class DbValues
{
    public static object? SerializeJson<T>(T value, JsonSerializerContext? context)
    {
        if (value is null) return null;
        ArgumentNullException.ThrowIfNull(context);
        var typeInfo = context.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"The JSON context does not contain metadata for '{typeof(T)}'.");
        return JsonSerializer.Serialize(value, typeInfo);
    }

    public static T Read<T>(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            if (default(T) is null)
                return default!;
            throw new DataMappingException($"Column '{reader.GetName(ordinal)}' is NULL but the result requires {typeof(T).Name}.");
        }
        return ConvertTo<T>(reader.GetValue(ordinal));
    }

    public static T ConvertTo<T>(object value)
    {
        if (value is null or DBNull) return default!;
        if (value is T exact) return exact;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(target);
            var num = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
            return (T)Enum.ToObject(target, num);
        }
        object converted = target == typeof(Guid) ? value is byte[] bytes ? new Guid(bytes) : Guid.Parse(value.ToString()!)
            : target == typeof(DateOnly) ? value is DateTime date ? DateOnly.FromDateTime(date) : DateOnly.Parse(value.ToString()!, CultureInfo.InvariantCulture)
            : target == typeof(TimeOnly) ? value is TimeSpan time ? TimeOnly.FromTimeSpan(time) : TimeOnly.Parse(value.ToString()!, CultureInfo.InvariantCulture)
            : target == typeof(DateTimeOffset) ? value is DateTime timestamp ? new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)) : DateTimeOffset.Parse(value.ToString()!, CultureInfo.InvariantCulture)
            : target == typeof(TimeSpan) ? TimeSpan.Parse(value.ToString()!, CultureInfo.InvariantCulture)
            : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        return (T)converted;
    }
}
