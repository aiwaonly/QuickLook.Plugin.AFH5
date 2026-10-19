using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QuickLook.Plugin.AFH5;

/// <summary>
/// A tiny JSON value model with a reader and an indented writer.
///
/// The plugin ships its own because the host application loads an older
/// Newtonsoft.Json than the one the project would reference, which makes calls
/// such as <c>JToken.ToString(Formatting)</c> fail at runtime with
/// <see cref="MissingMethodException"/>. Everything the case viewer needs maps
/// onto <c>Dictionary&lt;string, JsonValue&gt;</c>, <c>List&lt;JsonValue&gt;</c>
/// and four scalar kinds.
/// </summary>
public sealed class JsonValue(JsonValue.ValueKind kind)
{
    public enum ValueKind
    {
        Null,
        Boolean,
        Number,
        String,
        Object,
        Array,
    }

    public ValueKind Kind { get; } = kind;

    /// <summary>Set for objects; preserves the order the keys were read in.</summary>
    public Dictionary<string, JsonValue>? Members { get; private init; }

    /// <summary>Set for arrays.</summary>
    public List<JsonValue>? Elements { get; private init; }

    /// <summary>Set for strings.</summary>
    public string? String { get; private init; }

    public double Number { get; private init; }

    public bool Boolean { get; private init; }

    public bool IsObject => Kind == ValueKind.Object;

    public bool IsArray => Kind == ValueKind.Array;

    /// <summary>Number of members for an object, elements for an array, else 0.</summary>
    public int Count => Kind switch
    {
        ValueKind.Object => Members?.Count ?? 0,
        ValueKind.Array => Elements?.Count ?? 0,
        _ => 0,
    };

    public static JsonValue NewNull() => new(ValueKind.Null);

    public static JsonValue NewString(string value) => new(ValueKind.String) { String = value };

    public static JsonValue NewNumber(double value) => new(ValueKind.Number) { Number = value };

    public static JsonValue NewBoolean(bool value) => new(ValueKind.Boolean) { Boolean = value };

    public static JsonValue NewObject(Dictionary<string, JsonValue> members) =>
        new(ValueKind.Object) { Members = members };

    public static JsonValue NewArray(List<JsonValue> elements) =>
        new(ValueKind.Array) { Elements = elements };

    /// <summary>Iterates an object's members in order.</summary>
    public IEnumerable<KeyValuePair<string, JsonValue>> Enumerate()
    {
        return Members ?? [];
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Wrap a reader-produced tree the way <c>JToken</c> wrapped a deserialized
    /// case: numbers and booleans become their text, so every leaf the viewers
    /// show is a string or a list of strings.
    /// </summary>
    public JsonValue AsDisplayValue()
    {
        switch (Kind)
        {
            case ValueKind.Object:
                {
                    var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                    foreach (var member in Enumerate())
                    {
                        members[member.Key] = member.Value.AsDisplayValue();
                    }
                    return NewObject(members);
                }
            case ValueKind.Array:
                {
                    var elements = new List<JsonValue>();
                    foreach (var element in Elements!)
                    {
                        elements.Add(element.AsDisplayValue());
                    }
                    return NewArray(elements);
                }
            case ValueKind.String:
                return this;
            default:
                return NewString(ToPlainText());
        }
    }

    /// <summary>A scalar's text, without JSON quoting.</summary>
    public string ToPlainText()
    {
        return Kind switch
        {
            ValueKind.Null => string.Empty,
            ValueKind.String => String ?? string.Empty,
            ValueKind.Boolean => Boolean ? "true" : "false",
            ValueKind.Number => FormatNumber(Number),
            _ => string.Empty,
        };
    }

    /// <summary>Indented JSON, four spaces per level, used for the detail pane.</summary>
    public string ToIndentedJson()
    {
        var builder = new StringBuilder();
        Write(builder, this, 0);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonValue value, int depth)
    {
        switch (value.Kind)
        {
            case ValueKind.Object:
                {
                    var members = value.Members!;
                    if (members.Count == 0)
                    {
                        builder.Append("{}");
                        return;
                    }

                    builder.Append('{');
                    var first = true;
                    foreach (var member in members)
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }
                        first = false;
                        builder.Append('\n');
                        Indent(builder, depth + 1);
                        WriteString(builder, member.Key);
                        builder.Append(": ");
                        Write(builder, member.Value, depth + 1);
                    }
                    builder.Append('\n');
                    Indent(builder, depth);
                    builder.Append('}');
                    return;
                }
            case ValueKind.Array:
                {
                    var elements = value.Elements!;
                    if (elements.Count == 0)
                    {
                        builder.Append("[]");
                        return;
                    }

                    builder.Append('[');
                    for (var i = 0; i < elements.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }
                        builder.Append('\n');
                        Indent(builder, depth + 1);
                        Write(builder, elements[i], depth + 1);
                    }
                    builder.Append('\n');
                    Indent(builder, depth);
                    builder.Append(']');
                    return;
                }
            case ValueKind.String:
                WriteString(builder, value.String ?? string.Empty);
                return;
            case ValueKind.Number:
                builder.Append(FormatNumber(value.Number));
                return;
            case ValueKind.Boolean:
                builder.Append(value.Boolean ? "true" : "false");
                return;
            default:
                builder.Append("null");
                return;
        }
    }

    private static void Indent(StringBuilder builder, int depth)
    {
        builder.Append(' ', depth * 4);
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        builder.Append('"');
    }

    /// <summary>Render a number the way JSON does: integral values without a decimal point.</summary>
    private static string FormatNumber(double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Parses a JSON document.</summary>
    public static JsonValue Parse(string text)
    {
        var reader = new Reader(text);
        reader.SkipWhitespace();
        var value = reader.ReadValue();
        reader.SkipWhitespace();
        if (!reader.AtEnd)
        {
            throw new FormatException($"Unexpected trailing content at offset {reader.Position}.");
        }
        return value;
    }

    private sealed class Reader(string text)
    {
        private readonly string _text = text;
        private int _index;

        public int Position => _index;

        public bool AtEnd => _index >= _text.Length;

        private char Current => _text[_index];

        public void SkipWhitespace()
        {
            while (!AtEnd && (Current == ' ' || Current == '\t' || Current == '\n' || Current == '\r'))
            {
                _index++;
            }
        }

        public JsonValue ReadValue()
        {
            if (AtEnd)
            {
                throw new FormatException("Unexpected end of JSON input.");
            }

            return Current switch
            {
                '{' => ReadObject(),
                '[' => ReadArray(),
                '"' => NewString(ReadString()),
                't' => ReadLiteral("true", NewBoolean(true)),
                'f' => ReadLiteral("false", NewBoolean(false)),
                'n' => ReadLiteral("null", NewNull()),
                _ => NewNumber(ReadNumber()),
            };
        }

        private JsonValue ReadLiteral(string literal, JsonValue value)
        {
            if (_index + literal.Length > _text.Length
                || string.CompareOrdinal(_text, _index, literal, 0, literal.Length) != 0)
            {
                throw new FormatException($"Invalid literal at offset {_index}.");
            }

            _index += literal.Length;
            return value;
        }

        private JsonValue ReadObject()
        {
            _index++; // '{'
            var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            SkipWhitespace();
            if (!AtEnd && Current == '}')
            {
                _index++;
                return NewObject(members);
            }

            while (true)
            {
                SkipWhitespace();
                if (AtEnd || Current != '"')
                {
                    throw new FormatException($"Expected a key at offset {_index}.");
                }

                var key = ReadString();
                SkipWhitespace();
                if (AtEnd || Current != ':')
                {
                    throw new FormatException($"Expected ':' at offset {_index}.");
                }

                _index++;
                SkipWhitespace();
                members[key] = ReadValue();
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new FormatException("Unterminated object.");
                }

                if (Current == ',')
                {
                    _index++;
                    continue;
                }

                if (Current == '}')
                {
                    _index++;
                    return NewObject(members);
                }

                throw new FormatException($"Expected ',' or '}}' at offset {_index}.");
            }
        }

        private JsonValue ReadArray()
        {
            _index++; // '['
            var elements = new List<JsonValue>();
            SkipWhitespace();
            if (!AtEnd && Current == ']')
            {
                _index++;
                return NewArray(elements);
            }

            while (true)
            {
                SkipWhitespace();
                elements.Add(ReadValue());
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new FormatException("Unterminated array.");
                }

                if (Current == ',')
                {
                    _index++;
                    continue;
                }

                if (Current == ']')
                {
                    _index++;
                    return NewArray(elements);
                }

                throw new FormatException($"Expected ',' or ']' at offset {_index}.");
            }
        }

        private string ReadString()
        {
            _index++; // opening quote
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new FormatException("Unterminated string.");
                }

                var c = Current;
                if (c == '"')
                {
                    _index++;
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    _index++;
                    continue;
                }

                _index++;
                if (AtEnd)
                {
                    throw new FormatException("Unterminated escape.");
                }

                var escape = Current;
                _index++;
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        {
                            var code = ReadHex4();
                            // A surrogate pair is read as the second escape that follows.
                            if (char.IsHighSurrogate(code) && _index + 1 < _text.Length
                                && _text[_index] == '\\' && _text[_index + 1] == 'u')
                            {
                                _index += 2;
                                var low = ReadHex4();
                                builder.Append(code).Append(low);
                            }
                            else
                            {
                                builder.Append(code);
                            }
                            break;
                        }
                    default:
                        throw new FormatException($"Unknown escape '\\{escape}' at offset {_index - 2}.");
                }
            }
        }

        private char ReadHex4()
        {
            if (_index + 4 > _text.Length)
            {
                throw new FormatException("Truncated \\u escape.");
            }

            var hex = _text.Substring(_index, 4);
            _index += 4;
            return (char)ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private double ReadNumber()
        {
            var start = _index;
            if (!AtEnd && (Current == '-' || Current == '+'))
            {
                _index++;
            }

            while (!AtEnd && (char.IsDigit(Current) || Current == '.'
                              || Current == 'e' || Current == 'E' || Current == '+' || Current == '-'))
            {
                _index++;
            }

            var token = _text.Substring(start, _index - start);
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new FormatException($"Invalid number '{token}' at offset {start}.");
            }
            return value;
        }
    }
}
