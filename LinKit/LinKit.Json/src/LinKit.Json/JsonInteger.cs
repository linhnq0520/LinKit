using System.Globalization;
using System.Text.Json;

namespace LinKit.Json;

internal static class JsonInteger
{
    public static bool TryRead(JsonElement element, out bool negative, out UInt128 magnitude)
    {
        if (element.TryGetInt64(out var signed))
        {
            negative = signed < 0;
            magnitude = negative ? (UInt128)(-(Int128)signed) : (UInt128)signed;
            return true;
        }

        if (element.TryGetUInt64(out var unsigned))
        {
            negative = false;
            magnitude = unsigned;
            return true;
        }

        return TryRead(element.GetRawText(), out negative, out magnitude);
    }

    private static bool TryRead(string text, out bool negative, out UInt128 magnitude)
    {
        negative = false;
        magnitude = 0;

        if (text.Length == 0)
        {
            return false;
        }

        var exponentIndex = text.IndexOf('e');
        if (exponentIndex < 0)
        {
            exponentIndex = text.IndexOf('E');
        }
        var mantissaEnd = exponentIndex < 0 ? text.Length : exponentIndex;
        long exponent = 0;

        if (exponentIndex >= 0 &&
            !long.TryParse(
                text.AsSpan(exponentIndex + 1),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out exponent))
        {
            return false;
        }

        var offset = text[0] == '-' ? 1 : 0;
        negative = offset == 1;
        var decimalIndex = text.AsSpan(offset, mantissaEnd - offset).IndexOf('.');
        var integerEnd = decimalIndex < 0 ? mantissaEnd : offset + decimalIndex;
        var fractionStart = decimalIndex < 0 ? mantissaEnd : integerEnd + 1;
        var fractionalDigits = mantissaEnd - fractionStart;

        var digits = new char[mantissaEnd - offset - (decimalIndex < 0 ? 0 : 1)];
        var digitCount = 0;
        for (var index = offset; index < mantissaEnd; index++)
        {
            if (text[index] != '.')
            {
                digits[digitCount++] = text[index];
            }
        }

        var first = 0;
        while (first < digitCount && digits[first] == '0')
        {
            first++;
        }

        if (first == digitCount)
        {
            negative = false;
            return true;
        }

        var last = digitCount - 1;
        while (last > first && digits[last] == '0')
        {
            last--;
        }

        var trailingZeros = digitCount - last - 1;
        long scale;
        try
        {
            scale = checked(exponent - fractionalDigits + trailingZeros);
        }
        catch (OverflowException)
        {
            return false;
        }

        var significantDigits = last - first + 1;
        if (scale < 0 || scale > 39 || significantDigits > 39 - scale)
        {
            return false;
        }

        for (var index = first; index <= last; index++)
        {
            if (!TryAppend(ref magnitude, (uint)(digits[index] - '0')))
            {
                return false;
            }
        }

        for (long index = 0; index < scale; index++)
        {
            if (!TryAppend(ref magnitude, 0))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryAppend(ref UInt128 magnitude, uint digit)
    {
        if (magnitude > (UInt128.MaxValue - digit) / 10)
        {
            return false;
        }

        magnitude = magnitude * 10 + digit;
        return true;
    }

    public static bool TryConvert(Type target, bool negative, UInt128 magnitude, out object? result)
    {
        result = null;

        if (target == typeof(byte) ||
            target == typeof(ushort) ||
            target == typeof(uint) ||
            target == typeof(ulong) ||
            target == typeof(UInt128))
        {
            if (negative)
            {
                return false;
            }

            if (target == typeof(byte))
            {
                if (magnitude > byte.MaxValue) return false;
                result = (byte)magnitude;
            }
            else if (target == typeof(ushort))
            {
                if (magnitude > ushort.MaxValue) return false;
                result = (ushort)magnitude;
            }
            else if (target == typeof(uint))
            {
                if (magnitude > uint.MaxValue) return false;
                result = (uint)magnitude;
            }
            else if (target == typeof(ulong))
            {
                if (magnitude > ulong.MaxValue) return false;
                result = (ulong)magnitude;
            }
            else
            {
                result = magnitude;
            }

            return true;
        }

        if (target != typeof(sbyte) &&
            target != typeof(short) &&
            target != typeof(int) &&
            target != typeof(long) &&
            target != typeof(Int128))
        {
            return false;
        }

        var maximumMagnitude = (UInt128)Int128.MaxValue + (negative ? 1u : 0u);
        if (magnitude > maximumMagnitude)
        {
            return false;
        }

        var signedValue = negative
            ? magnitude == (UInt128)Int128.MaxValue + 1
                ? Int128.MinValue
                : -(Int128)magnitude
            : (Int128)magnitude;

        if (target == typeof(sbyte))
        {
            if (signedValue < sbyte.MinValue || signedValue > sbyte.MaxValue) return false;
            result = (sbyte)signedValue;
        }
        else if (target == typeof(short))
        {
            if (signedValue < short.MinValue || signedValue > short.MaxValue) return false;
            result = (short)signedValue;
        }
        else if (target == typeof(int))
        {
            if (signedValue < int.MinValue || signedValue > int.MaxValue) return false;
            result = (int)signedValue;
        }
        else if (target == typeof(long))
        {
            if (signedValue < long.MinValue || signedValue > long.MaxValue) return false;
            result = (long)signedValue;
        }
        else
        {
            result = signedValue;
        }

        return true;
    }
}
