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

/// <summary>
/// Describes a single column of the physical schema.
/// </summary>
/// <param name="Name">Physical column name.</param>
/// <param name="Type">Provider-independent database type.</param>
/// <param name="Nullable">Whether the column accepts <c>NULL</c>.</param>
/// <param name="PrimaryKey">Whether the column is part of the primary key.</param>
/// <param name="Generated">Whether the database generates the value on insert.</param>
/// <param name="Version">Whether the column is an optimistic concurrency token.</param>
/// <param name="Tenant">Whether the column carries the tenant discriminator.</param>
/// <param name="SoftDelete">Whether the column carries the soft-delete marker.</param>
/// <param name="Length">Declared length for string or binary columns; zero when unbounded.</param>
/// <param name="Precision">Declared numeric precision; zero when not applicable.</param>
/// <param name="Scale">Declared numeric scale; zero when not applicable.</param>
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

/// <summary>
/// Describes a table of the physical schema together with its columns.
/// </summary>
/// <param name="Name">Physical table name.</param>
/// <param name="Schema">Owner or schema the table belongs to; <see langword="null"/> for the provider default.</param>
/// <param name="Columns">Columns in declaration order.</param>
public sealed record TableModel(string Name, string? Schema, IReadOnlyList<ColumnModel> Columns)
{
    /// <summary>Returns the primary key column.</summary>
    /// <exception cref="InvalidOperationException">The table has zero or several primary key columns.</exception>
    public ColumnModel Key => Columns.Single(column => column.PrimaryKey);
}

/// <summary>
/// Point-in-time snapshot of every table reported by a dialect, used by schema synchronization.
/// </summary>
/// <param name="Tables">Tables of the snapshot.</param>
public sealed record SchemaSnapshot(IReadOnlyList<TableModel> Tables);

/// <summary>
/// Thrown when a database value cannot be mapped to the requested CLR type, typically because the column type and the
/// property type disagree.
/// </summary>
public sealed class DataMappingException(string message) : Exception(message);

/// <summary>
/// Thrown when an optimistic concurrency check affects no row, which means another writer changed or removed the row
/// between the read and the write.
/// </summary>
public sealed class DataConcurrencyException(string message) : Exception(message);

/// <summary>
/// Helpers used by generated mappers to read provider values and to convert them to CLR types.
/// </summary>
/// <remarks>
/// Every typed accessor first tries the provider's strongly typed getter and falls back to <see cref="Convert"/> under
/// <see cref="CultureInfo.InvariantCulture"/> when the provider reports an incompatible representation.
/// </remarks>
public static class DbValues
{
    /// <summary>Serializes a value through a source-generated <see cref="JsonSerializerContext"/> so the mapping stays Native AOT safe.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="value">Value to serialize; <see langword="null"/> returns <see langword="null"/>.</param>
    /// <param name="context">Source-generated context that must contain metadata for <typeparamref name="T"/>.</param>
    /// <returns>The JSON payload, or <see langword="null"/> when <paramref name="value"/> is <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is not <see langword="null"/> and <paramref name="context"/> is.</exception>
    /// <exception cref="InvalidOperationException">The context has no metadata for <typeparamref name="T"/>.</exception>
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

    /// <summary>Reads a column and converts it to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Requested type.</typeparam>
    /// <param name="reader">Reader positioned on the row to read.</param>
    /// <param name="ordinal">Zero-based column ordinal.</param>
    /// <returns>The column value.</returns>
    /// <exception cref="DataMappingException">The column is <c>NULL</c> and <typeparamref name="T"/> cannot hold <see langword="null"/>.</exception>
    /// <remarks>
    /// Non-nullable value types are read through a non-boxing reinterpretation of the typed accessor; <see cref="Nullable{T}"/>
    /// takes the boxing path because its layout differs, and any other type falls back to <see cref="ConvertTo{T}"/>.
    /// </remarks>
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

    /// <summary>Reads a non-null Int64 with the provider's typed accessor.</summary>
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

    /// <summary>Reads a non-null Int16 with the provider's typed accessor.</summary>
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

    /// <summary>Reads a non-null unsigned byte with the provider's typed accessor.</summary>
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

    /// <summary>Reads a non-null signed byte.</summary>
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

    /// <summary>Reads a non-null unsigned 16-bit integer.</summary>
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

    /// <summary>Reads a non-null unsigned 32-bit integer.</summary>
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

    /// <summary>Reads a non-null unsigned 64-bit integer.</summary>
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

    /// <summary>Reads a non-null decimal with the provider's typed accessor.</summary>
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

    /// <summary>Reads a non-null double with the provider's typed accessor.</summary>
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

    /// <summary>Reads a non-null single-precision number with the provider's typed accessor.</summary>
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

    /// <summary>Reads a non-null boolean, accepting text and numeric representations such as the ones SQLite returns.</summary>
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

    /// <summary>Reads a non-null character from a single-character column, returning <c>\0</c> for an empty string.</summary>
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

    /// <summary>Reads a non-null GUID from a native GUID column, a 16-byte binary column or a textual representation.</summary>
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

    /// <summary>Reads a non-null timestamp, accepting ticks and textual representations as a fallback.</summary>
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

    /// <summary>Reads a non-null offset timestamp, accepting <see cref="DateTime"/> and textual representations as a fallback.</summary>
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

    /// <summary>Reads a non-null date, accepting <see cref="DateTime"/> and textual representations as a fallback.</summary>
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

    /// <summary>Reads a non-null time of day, accepting span, timestamp and textual representations as a fallback.</summary>
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

    /// <summary>Reads a non-null duration, accepting ticks, <see cref="TimeOnly"/> and textual representations as a fallback.</summary>
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

    /// <summary>Reads a non-null binary value from a binary column or from a Base64 string.</summary>
    /// <exception cref="DataMappingException">The provider returned a type that cannot be read as binary.</exception>
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

    /// <summary>Reads a non-null enum from its stored name or numeric value.</summary>
    /// <typeparam name="T">Enum type.</typeparam>
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
