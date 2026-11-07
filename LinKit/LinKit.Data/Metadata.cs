using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
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
    byte Scale = 0
);

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
        if (value is null)
            return null;
        ArgumentNullException.ThrowIfNull(context);
        var typeInfo =
            context.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException(
                $"The JSON context does not contain metadata for '{typeof(T)}'."
            );
        return JsonSerializer.Serialize(value, typeInfo);
    }

    public static T Read<T>(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            if (default(T) is null)
                return default!;
            throw new DataMappingException(
                $"Column '{reader.GetName(ordinal)}' is NULL but the result requires {typeof(T).Name}."
            );
        }

        var requested = typeof(T);
        if (requested == typeof(string))
            return (T)(object)ReadString(reader, ordinal);
        if (requested == typeof(byte[]))
            return (T)(object)ReadBytes(reader, ordinal);

        var underlying = Nullable.GetUnderlyingType(requested);
        if (underlying is null)
        {
            // Non-nullable value types take the non-boxing path: the typed accessor is called and the resulting
            // value is reinterpreted as T, which the JIT folds away for the matching instantiation.
            if (requested == typeof(int))
                return Reinterpret<int, T>(ReadInt32(reader, ordinal));
            if (requested == typeof(long))
                return Reinterpret<long, T>(ReadInt64(reader, ordinal));
            if (requested == typeof(short))
                return Reinterpret<short, T>(ReadInt16(reader, ordinal));
            if (requested == typeof(byte))
                return Reinterpret<byte, T>(ReadByte(reader, ordinal));
            if (requested == typeof(decimal))
                return Reinterpret<decimal, T>(ReadDecimal(reader, ordinal));
            if (requested == typeof(double))
                return Reinterpret<double, T>(ReadDouble(reader, ordinal));
            if (requested == typeof(float))
                return Reinterpret<float, T>(ReadSingle(reader, ordinal));
            if (requested == typeof(bool))
                return Reinterpret<bool, T>(ReadBoolean(reader, ordinal));
            if (requested == typeof(char))
                return Reinterpret<char, T>(ReadChar(reader, ordinal));
            if (requested == typeof(Guid))
                return Reinterpret<Guid, T>(ReadGuid(reader, ordinal));
            if (requested == typeof(DateTime))
                return Reinterpret<DateTime, T>(ReadDateTime(reader, ordinal));
            if (requested == typeof(DateTimeOffset))
                return Reinterpret<DateTimeOffset, T>(ReadDateTimeOffset(reader, ordinal));
            if (requested == typeof(DateOnly))
                return Reinterpret<DateOnly, T>(ReadDateOnly(reader, ordinal));
            if (requested == typeof(TimeOnly))
                return Reinterpret<TimeOnly, T>(ReadTimeOnly(reader, ordinal));
            if (requested == typeof(TimeSpan))
                return Reinterpret<TimeSpan, T>(ReadTimeSpan(reader, ordinal));
        }
        else
        {
            // Nullable<T> is not layout-compatible with T, so the box below cannot be avoided here. Generated
            // mappers call the non-generic accessors directly and only reach this from custom SQL projections.
            if (underlying == typeof(int))
                return (T)(object)ReadInt32(reader, ordinal);
            if (underlying == typeof(long))
                return (T)(object)ReadInt64(reader, ordinal);
            if (underlying == typeof(short))
                return (T)(object)ReadInt16(reader, ordinal);
            if (underlying == typeof(byte))
                return (T)(object)ReadByte(reader, ordinal);
            if (underlying == typeof(decimal))
                return (T)(object)ReadDecimal(reader, ordinal);
            if (underlying == typeof(double))
                return (T)(object)ReadDouble(reader, ordinal);
            if (underlying == typeof(float))
                return (T)(object)ReadSingle(reader, ordinal);
            if (underlying == typeof(bool))
                return (T)(object)ReadBoolean(reader, ordinal);
            if (underlying == typeof(char))
                return (T)(object)ReadChar(reader, ordinal);
            if (underlying == typeof(Guid))
                return (T)(object)ReadGuid(reader, ordinal);
            if (underlying == typeof(DateTime))
                return (T)(object)ReadDateTime(reader, ordinal);
            if (underlying == typeof(DateTimeOffset))
                return (T)(object)ReadDateTimeOffset(reader, ordinal);
            if (underlying == typeof(DateOnly))
                return (T)(object)ReadDateOnly(reader, ordinal);
            if (underlying == typeof(TimeOnly))
                return (T)(object)ReadTimeOnly(reader, ordinal);
            if (underlying == typeof(TimeSpan))
                return (T)(object)ReadTimeSpan(reader, ordinal);
        }

        return ConvertTo<T>(reader.GetValue(ordinal));
    }

    private static TTo Reinterpret<TFrom, TTo>(TFrom value) => Unsafe.As<TFrom, TTo>(ref value);

    /// <summary>Reads a non-null string with <see cref="DbDataReader.GetString"/>.</summary>
    public static string ReadString(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetString(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
                ?? string.Empty;
        }
    }

    /// <summary>Reads a non-null Int32 with the provider's typed accessor.</summary>
    public static int ReadInt32(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetInt32(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }

    public static long ReadInt64(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetInt64(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }

    public static short ReadInt16(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetInt16(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToInt16(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }

    public static byte ReadByte(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetByte(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToByte(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }

    public static sbyte ReadSByte(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldValue<sbyte>(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToSByte(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }

    public static ushort ReadUInt16(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldValue<ushort>(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToUInt16(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }

    public static uint ReadUInt32(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldValue<uint>(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToUInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }

    public static ulong ReadUInt64(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldValue<ulong>(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToUInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }

    public static decimal ReadDecimal(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetDecimal(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }

    public static double ReadDouble(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetDouble(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }

    public static float ReadSingle(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFloat(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            return Convert.ToSingle(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }

    public static bool ReadBoolean(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetBoolean(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            var value = reader.GetValue(ordinal);
            return value is string text
                ? bool.Parse(text)
                : Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        }
    }

    public static char ReadChar(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetChar(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            var text = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(text) ? '\0' : text![0];
        }
    }

    public static Guid ReadGuid(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetGuid(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            var value = reader.GetValue(ordinal);
            return value is byte[] bytes && bytes.Length == 16
                ? new Guid(bytes)
                : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        }
    }

    public static DateTime ReadDateTime(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetDateTime(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            var value = reader.GetValue(ordinal);
            return value switch
            {
                long ticks => new DateTime(ticks, DateTimeKind.Utc),
                string text => DateTime.Parse(text, CultureInfo.InvariantCulture),
                _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
            };
        }
    }

    public static DateTimeOffset ReadDateTimeOffset(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldValue<DateTimeOffset>(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            var value = reader.GetValue(ordinal);
            return value switch
            {
                DateTime timestamp => new DateTimeOffset(timestamp),
                string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
                _ => new DateTimeOffset(Convert.ToDateTime(value, CultureInfo.InvariantCulture)),
            };
        }
    }

    public static DateOnly ReadDateOnly(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldValue<DateOnly>(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            var value = reader.GetValue(ordinal);
            return value switch
            {
                DateTime timestamp => DateOnly.FromDateTime(timestamp),
                string text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
                _ => DateOnly.FromDateTime(Convert.ToDateTime(value, CultureInfo.InvariantCulture)),
            };
        }
    }

    public static TimeOnly ReadTimeOnly(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldValue<TimeOnly>(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            var value = reader.GetValue(ordinal);
            return value switch
            {
                TimeSpan span => TimeOnly.FromTimeSpan(span),
                DateTime timestamp => TimeOnly.FromDateTime(timestamp),
                string text => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
                _ => TimeOnly.FromTimeSpan(
                    TimeSpan.Parse(
                        Convert.ToString(value, CultureInfo.InvariantCulture)!,
                        CultureInfo.InvariantCulture
                    )
                ),
            };
        }
    }

    public static TimeSpan ReadTimeSpan(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldValue<TimeSpan>(ordinal);
        }
        catch (Exception error) when (IsConversionFailure(error))
        {
            var value = reader.GetValue(ordinal);
            return value switch
            {
                long ticks => TimeSpan.FromTicks(ticks),
                TimeOnly time => time.ToTimeSpan(),
                _ => TimeSpan.Parse(
                    Convert.ToString(value, CultureInfo.InvariantCulture)!,
                    CultureInfo.InvariantCulture
                ),
            };
        }
    }

    public static byte[] ReadBytes(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            byte[] bytes => bytes,
            string text => Convert.FromBase64String(text),
            _ => throw new DataMappingException(
                $"Column '{reader.GetName(ordinal)}' cannot be read as binary; the provider returned {value?.GetType().Name ?? "null"}."
            ),
        };
    }

    public static T ReadEnum<T>(DbDataReader reader, int ordinal)
        where T : struct, Enum
    {
        var value = reader.GetValue(ordinal);
        if (value is T exact)
            return exact;
        if (value is string text)
            return Enum.Parse<T>(text, ignoreCase: true);
        var underlying = Enum.GetUnderlyingType(typeof(T));
        return (T)
            Enum.ToObject(
                typeof(T),
                Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture)
            );
    }

    private static bool IsConversionFailure(Exception error) =>
        error is InvalidCastException or NotSupportedException or FormatException;

    /// <summary>
    /// Converts an already-materialised <see cref="object"/> from a custom SQL statement. Generated mappers use the
    /// typed accessors above and never reach this path.
    /// </summary>
    public static T ConvertTo<T>(object value)
    {
        if (value is null or DBNull)
            return default!;
        if (value is T exact)
            return exact;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(target);
            var num = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
            return (T)Enum.ToObject(target, num);
        }
        object converted =
            target == typeof(int) ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : target == typeof(long) ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : target == typeof(short) ? Convert.ToInt16(value, CultureInfo.InvariantCulture)
            : target == typeof(byte) ? Convert.ToByte(value, CultureInfo.InvariantCulture)
            : target == typeof(string) ? Convert.ToString(value, CultureInfo.InvariantCulture)!
            : target == typeof(decimal) ? Convert.ToDecimal(value, CultureInfo.InvariantCulture)
            : target == typeof(double) ? Convert.ToDouble(value, CultureInfo.InvariantCulture)
            : target == typeof(float) ? Convert.ToSingle(value, CultureInfo.InvariantCulture)
            : target == typeof(bool)
                ? value is string flag ? bool.Parse(flag)
                    : Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0
            : target == typeof(Guid)
                ? value is byte[] bytes ? new Guid(bytes)
                    : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!)
            : target == typeof(DateTime)
                ? value is long ticks ? new DateTime(ticks, DateTimeKind.Utc)
                    : Convert.ToDateTime(value, CultureInfo.InvariantCulture)
            : target == typeof(DateOnly)
                ? value is DateTime date ? DateOnly.FromDateTime(date)
                    : DateOnly.Parse(
                        Convert.ToString(value, CultureInfo.InvariantCulture)!,
                        CultureInfo.InvariantCulture
                    )
            : target == typeof(TimeOnly)
                ? value is TimeSpan time ? TimeOnly.FromTimeSpan(time)
                    : TimeOnly.Parse(
                        Convert.ToString(value, CultureInfo.InvariantCulture)!,
                        CultureInfo.InvariantCulture
                    )
            : target == typeof(DateTimeOffset)
                ? value is DateTime timestamp
                        ? new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc))
                    : DateTimeOffset.Parse(
                        Convert.ToString(value, CultureInfo.InvariantCulture)!,
                        CultureInfo.InvariantCulture
                    )
            : target == typeof(TimeSpan)
                ? TimeSpan.Parse(
                    Convert.ToString(value, CultureInfo.InvariantCulture)!,
                    CultureInfo.InvariantCulture
                )
            : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        return (T)converted;
    }
}
