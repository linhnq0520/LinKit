using System.Globalization;
using System.Text;

namespace LinKit.Json;

/// <summary>An immutable, reusable path in LinKit.Json's JSONPath subset.</summary>
public sealed class JsonPath
{
    private readonly Segment[] _segments;

    private JsonPath(string expression, Segment[] segments)
    {
        Expression = expression;
        _segments = segments;
        IsSingular = true;
        foreach (var segment in segments)
        {
            if (segment.Kind == SegmentKind.Wildcard)
            {
                IsSingular = false;
                break;
            }
        }
    }

    /// <summary>The source expression used to create this path.</summary>
    public string Expression { get; }

    /// <summary>Whether this path contains no wildcard and can select at most one value.</summary>
    public bool IsSingular { get; }

    internal int Count => _segments.Length;

    internal Segment this[int index] => _segments[index];

    internal static void ValidateSingular(JsonPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!path.IsSingular)
        {
            throw new ArgumentException(
                "A single-value query cannot contain a wildcard. Use SelectMany instead.",
                nameof(path));
        }
    }

    /// <summary>Parses a path. Unsupported or malformed syntax throws <see cref="FormatException"/>.</summary>
    public static JsonPath Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new Parser(expression).Parse();
    }

    public override string ToString() => Expression;

    internal enum SegmentKind
    {
        Property,
        Index,
        Wildcard
    }

    internal readonly record struct Segment(SegmentKind Kind, string? Name = null, int Index = 0);

    private sealed class Parser(string text)
    {
        private int _position;

        public JsonPath Parse()
        {
            if (text.Length == 0 || text[0] != '$')
            {
                throw Error("A path must start with '$'.");
            }

            _position = 1;
            var segments = new List<Segment>();

            while (_position < text.Length)
            {
                switch (text[_position])
                {
                    case '.':
                        _position++;
                        if (_position < text.Length && text[_position] == '*')
                        {
                            _position++;
                            segments.Add(new Segment(SegmentKind.Wildcard));
                        }
                        else
                        {
                            segments.Add(ParseProperty());
                        }
                        break;

                    case '[':
                        segments.Add(ParseBracket());
                        break;

                    default:
                        throw Error("Expected '.' or '['.");
                }
            }

            return new JsonPath(text, segments.ToArray());
        }

        private Segment ParseProperty()
        {
            if (_position >= text.Length || !IsNameStart(text[_position]))
            {
                throw Error("Expected a property name.");
            }

            var start = _position++;
            while (_position < text.Length && IsNamePart(text[_position]))
            {
                _position++;
            }

            return new Segment(SegmentKind.Property, text[start.._position]);
        }

        private Segment ParseBracket()
        {
            _position++;
            if (_position >= text.Length)
            {
                throw Error("Unterminated bracket.");
            }

            Segment segment;
            if (text[_position] is '"' or '\'')
            {
                segment = new Segment(SegmentKind.Property, ParseQuotedName());
            }
            else if (text[_position] == '*')
            {
                _position++;
                segment = new Segment(SegmentKind.Wildcard);
            }
            else
            {
                var start = _position;
                if (text[_position] == '-')
                {
                    _position++;
                }

                var digitsStart = _position;
                while (_position < text.Length && char.IsAsciiDigit(text[_position]))
                {
                    _position++;
                }

                var digits = text.AsSpan(digitsStart, _position - digitsStart);
                if (digits.Length == 0 ||
                    (digits.Length > 1 && digits[0] == '0') ||
                    (text[start] == '-' && digits.Length == 1 && digits[0] == '0') ||
                    !int.TryParse(
                        text.AsSpan(start, _position - start),
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var index))
                {
                    throw Error("Expected an Int32 array index without leading zeros, '+' or negative zero.");
                }

                segment = new Segment(SegmentKind.Index, Index: index);
            }

            if (_position >= text.Length || text[_position] != ']')
            {
                throw Error("Expected ']'.");
            }

            _position++;
            return segment;
        }

        private string ParseQuotedName()
        {
            var quote = text[_position++];
            var result = new StringBuilder();

            while (_position < text.Length)
            {
                var character = text[_position++];
                if (character == quote)
                {
                    return result.ToString();
                }

                if (character < ' ')
                {
                    throw Error("Control characters must be escaped in quoted property names.");
                }

                if (character != '\\')
                {
                    result.Append(character);
                    continue;
                }

                if (_position >= text.Length)
                {
                    throw Error("Unterminated escape sequence.");
                }

                var escape = text[_position++];
                result.Append(escape switch
                {
                    '"' => '"',
                    '\'' => '\'',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' => ParseUnicodeEscape(),
                    _ => throw Error("Invalid escape sequence.")
                });
            }

            throw Error("Unterminated quoted property name.");
        }

        private char ParseUnicodeEscape()
        {
            if (text.Length - _position < 4 ||
                !ushort.TryParse(
                    text.AsSpan(_position, 4),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                throw Error("Expected four hexadecimal digits after '\\u'.");
            }

            _position += 4;
            return (char)value;
        }

        private static bool IsNameStart(char character) =>
            char.IsAsciiLetter(character) || character == '_';

        private static bool IsNamePart(char character) =>
            IsNameStart(character) || char.IsAsciiDigit(character);

        private FormatException Error(string message) =>
            new($"Invalid path at offset {_position}: {message}");
    }
}
