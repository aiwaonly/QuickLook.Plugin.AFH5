using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QuickLook.Plugin.AFH5;

/// <summary>
/// A Scheme symbol, e.g. <c>velocity-inlet</c> or <c>.</c>. Renders bare.
/// Quoted strings are stored as plain <see cref="string"/>, so a
/// <see cref="SexpSymbol"/> is always an identifier or keyword.
/// </summary>
public sealed class SexpSymbol(string name)
{
    public string Name { get; } = name;

    public override string ToString() => Name;

    public override bool Equals(object? obj) => obj is SexpSymbol other && other.Name == Name;

    public override int GetHashCode() => Name.GetHashCode();
}

/// <summary>
/// A Scheme pair <c>(car . cdr)</c>. A proper list is a chain of pairs
/// terminated by <see cref="SexpNull"/>.
/// </summary>
public sealed class SexpPair(object car, object cdr)
{
    public object Car { get; } = car;

    public object Cdr { get; } = cdr;

    public override string ToString() => $"({Car} . {Cdr})";
}

/// <summary>The empty list <c>()</c>, also used as a list terminator.</summary>
public sealed class SexpNull
{
    public static readonly SexpNull Instance = new();

    private SexpNull()
    {
    }

    public override string ToString() => "()";
}

/// <summary>
/// Raised when a Scheme document cannot be parsed. The offset is folded into the
/// message, which is all the caller ever uses.
/// </summary>
public sealed class SexpParseException(string message) : Exception(message);

/// <summary>
/// A self-contained S-expression reader for Fluent's Scheme documents:
///
/// <list type="bullet">
/// <item>symbols are bare (<c>air</c>), strings keep their quotes (<c>"air"</c>);</item>
/// <item>a proper list is a chain of <see cref="SexpPair"/> ending in
/// <see cref="SexpNull"/>, so iterating a dotted pair yields its tail atom as a
/// final element;</item>
/// <item><c>; line</c> and <c>#| block |#</c> comments are skipped.</item>
/// </list>
/// </summary>
public sealed class SexpReader
{
    private readonly string _text;
    private int _index;
    private int _line = 1;
    private int _lineStart;

    private SexpReader(string text) => _text = text;

    /// <summary>
    /// When true, <c>#t</c>/<c>#f</c> are read as booleans; by default they stay
    /// symbols, which is how Fluent writes them in the settings trees.
    /// </summary>
    public static bool AcceptBooleanLiterals { get; set; }

    /// <summary>Parse every top-level expression of a document.</summary>
    public static List<object> ParseAll(string text)
    {
        var reader = new SexpReader(text);
        var result = new List<object>();
        while (true)
        {
            reader.SkipAtmosphere();
            if (reader.AtEnd)
            {
                return result;
            }
            result.Add(reader.ReadExpression());
        }
    }

    /// <summary>Parse the first top-level expression of a document.</summary>
    public static object Parse(string text)
    {
        var reader = new SexpReader(text);
        reader.SkipAtmosphere();
        if (reader.AtEnd)
        {
            return SexpNull.Instance;
        }
        return reader.ReadExpression();
    }

    private bool AtEnd => _index >= _text.Length;

    private char Current => _text[_index];

    private SexpParseException Error(string message)
    {
        return new SexpParseException($"{message} (line {_line}, column {_index - _lineStart + 1})");
    }

    private void Advance()
    {
        if (_text[_index] == '\n')
        {
            _line++;
            _lineStart = _index + 1;
        }
        _index++;
    }

    /// <summary>Skip whitespace, <c>;</c> line comments and <c>#|...|#</c> blocks.</summary>
    private void SkipAtmosphere()
    {
        while (!AtEnd)
        {
            var c = Current;
            if (char.IsWhiteSpace(c))
            {
                Advance();
            }
            else if (c == ';')
            {
                while (!AtEnd && Current != '\n')
                {
                    Advance();
                }
            }
            else if (c == '#' && _index + 1 < _text.Length && _text[_index + 1] == '|')
            {
                Advance();
                Advance();
                while (!AtEnd)
                {
                    if (Current == '|' && _index + 1 < _text.Length && _text[_index + 1] == '#')
                    {
                        Advance();
                        Advance();
                        break;
                    }
                    Advance();
                }
            }
            else
            {
                return;
            }
        }
    }

    private object ReadExpression()
    {
        SkipAtmosphere();
        if (AtEnd)
        {
            throw Error("unexpected end of input");
        }

        if (TryReadAbbreviation(out var abbreviated))
        {
            return abbreviated;
        }

        return Current switch
        {
            '(' => ReadList('(', ')'),
            '[' => ReadList('[', ']'),
            '"' => ReadString(),
            '|' => ReadDelimitedSymbol(),
            _ => ReadAtom(),
        };
    }

    private bool NextIs(char c) => _index + 1 < _text.Length && _text[_index + 1] == c;

    /// <summary>Single-character reader prefixes: <c>'</c>, <c>`</c>, <c>,</c>, <c>,@</c>.</summary>
    private bool TryReadAbbreviation(out object value)
    {
        value = SexpNull.Instance;
        var c = Current;
        string name;
        switch (c)
        {
            case '\'':
                name = "quote";
                break;
            case '`':
                name = "quasiquote";
                break;
            case ',':
                name = NextIs('@') ? "unquote-splicing" : "unquote";
                break;
            default:
                return false;
        }

        Advance();
        if (name == "unquote-splicing")
        {
            Advance();
        }

        var quoted = ReadExpression();
        value = new SexpPair(new SexpSymbol(name), new SexpPair(quoted, SexpNull.Instance));
        return true;
    }

    private object ReadList(char open, char close)
    {
        Advance(); // consume the opening delimiter
        var items = new List<object>();

        while (true)
        {
            SkipAtmosphere();
            if (AtEnd)
            {
                throw Error($"unterminated list starting with '{open}'");
            }

            if (Current == close)
            {
                Advance();
                break;
            }

            if (Current == ')' || Current == ']')
            {
                throw Error($"expected '{close}'");
            }

            // Dotted pair: a lone '.' token separates car from cdr.
            if (Current == '.' && IsDelimiterAt(_index + 1))
            {
                if (items.Count == 0)
                {
                    throw Error("unexpected '.' at the start of a list");
                }
                Advance(); // consume '.'
                var tail = ReadExpression();
                SkipAtmosphere();
                if (AtEnd || Current != close)
                {
                    throw Error($"expected '{close}' after dotted tail");
                }
                Advance();
                return BuildDotted(items, tail);
            }

            items.Add(ReadExpression());
        }

        return BuildProper(items);
    }

    private static object BuildProper(List<object> items)
    {
        object result = SexpNull.Instance;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            result = new SexpPair(items[i], result);
        }
        return result;
    }

    private static object BuildDotted(List<object> items, object tail)
    {
        var result = tail;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            result = new SexpPair(items[i], result);
        }
        return result;
    }

    private bool IsDelimiterAt(int index)
    {
        if (index >= _text.Length)
        {
            return true;
        }
        var c = _text[index];
        return char.IsWhiteSpace(c) || c is '(' or ')' or '[' or ']' or '"' or ';';
    }

    private object ReadString()
    {
        Advance(); // opening quote
        var builder = new StringBuilder();
        while (true)
        {
            if (AtEnd)
            {
                throw Error("unterminated string");
            }
            var c = Current;
            if (c == '"')
            {
                Advance();
                return builder.ToString();
            }
            if (c == '\\')
            {
                Advance();
                if (AtEnd)
                {
                    throw Error("unterminated string escape");
                }
                builder.Append(ReadEscape());
                continue;
            }
            builder.Append(c);
            Advance();
        }
    }

    private string ReadEscape()
    {
        var c = Current;
        Advance();
        switch (c)
        {
            case 'n': return "\n";
            case 'r': return "\r";
            case 't': return "\t";
            case 'a': return "\a";
            case 'b': return "\b";
            case 'f': return "\f";
            case 'v': return "\v";
            case '0': return "\0";
            case '\\': return "\\";
            case '"': return "\"";
            case '\'': return "'";
            case 'x':
                {
                    var hex = ReadHexDigits(2);
                    return hex.Length == 0 ? "x" : ((char)Convert.ToInt32(hex, 16)).ToString();
                }
            case 'u':
                {
                    var hex = ReadHexDigits(4);
                    return hex.Length == 0 ? "u" : ((char)Convert.ToInt32(hex, 16)).ToString();
                }
            default:
                return c.ToString();
        }
    }

    private string ReadHexDigits(int count)
    {
        var start = _index;
        var taken = 0;
        while (taken < count && !AtEnd && Uri.IsHexDigit(Current))
        {
            Advance();
            taken++;
        }
        return _text.Substring(start, _index - start);
    }

    private object ReadDelimitedSymbol()
    {
        Advance(); // opening '|'
        var builder = new StringBuilder();
        while (true)
        {
            if (AtEnd)
            {
                throw Error("unterminated |symbol|");
            }
            var c = Current;
            if (c == '|')
            {
                Advance();
                return new SexpSymbol(builder.ToString());
            }
            if (c == '\\')
            {
                Advance();
                if (AtEnd)
                {
                    throw Error("unterminated |symbol| escape");
                }
                builder.Append(ReadEscape());
                continue;
            }
            builder.Append(c);
            Advance();
        }
    }

    /// <summary>Read a number, boolean/nil literal or a bare symbol.</summary>
    private object ReadAtom()
    {
        var start = _index;
        while (!AtEnd && !IsDelimiterAt(_index))
        {
            Advance();
        }
        var token = _text.Substring(start, _index - start);
        if (token.Length == 0)
        {
            throw Error($"unexpected character '{Current}'");
        }

        return token switch
        {
            "#t" => BooleanOrSymbol(token, true),
            "#f" => BooleanOrSymbol(token, false),
            "#true" => BooleanOrSymbol(token, true),
            "#false" => BooleanOrSymbol(token, false),
            "#nil" => SexpNull.Instance,
            _ => TryReadNumber(token, out var number) ? number : new SexpSymbol(token),
        };
    }

    private static object BooleanOrSymbol(string token, bool value)
    {
        return AcceptBooleanLiterals ? value : new SexpSymbol(token);
    }

    /// <summary>
    /// Parse the numeric token shapes Fluent emits. Integers become
    /// <see cref="long"/> and everything else <see cref="double"/>, so that
    /// rendering numbers textually reproduces the original literal (including
    /// the trailing dot of <c>1.</c>) while still allowing numeric comparison.
    /// </summary>
    private static bool TryReadNumber(string token, out object number)
    {
        number = 0L;

        var body = token;
        var negative = false;
        if (body.StartsWith("+", StringComparison.Ordinal))
        {
            body = body.Substring(1);
        }
        else if (body.StartsWith("-", StringComparison.Ordinal))
        {
            negative = true;
            body = body.Substring(1);
        }

        if (body.Length == 0)
        {
            return false;
        }

        // #x / #o / #b / #d radix prefixes.
        var radix = 10;
        if (body.Length > 2 && body[0] == '#')
        {
            radix = char.ToLowerInvariant(body[1]) switch
            {
                'x' => 16,
                'o' => 8,
                'b' => 2,
                'd' => 10,
                _ => 0,
            };
            if (radix == 0)
            {
                return false;
            }
            body = body.Substring(2);
            if (body.Length == 0)
            {
                return false;
            }
        }

        if (radix != 10)
        {
            try
            {
                var value = System.Convert.ToInt64(body, radix);
                number = negative ? -value : value;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Reject symbol-looking tokens early (`+`, `-`, `...`, `1+2`).
        var digits = 0;
        var dots = 0;
        var exponents = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c >= '0' && c <= '9')
            {
                digits++;
            }
            else if (c == '.')
            {
                dots++;
            }
            else if (c == 'e' || c == 'E')
            {
                exponents++;
                if (i + 1 < body.Length && (body[i + 1] == '+' || body[i + 1] == '-'))
                {
                    i++;
                }
            }
            else
            {
                return false;
            }
        }

        if (digits == 0 || dots > 1 || exponents > 1 || (body.StartsWith(".", StringComparison.Ordinal) && body.Length == 1))
        {
            return false;
        }

        var text = negative ? "-" + body : body;
        if (dots == 0 && exponents == 0)
        {
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            {
                number = integer;
                return true;
            }
            // Fall through to double for out-of-range integers.
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
        {
            return false;
        }

        number = real;
        return true;
    }
}

/// <summary>Rendering helpers shared by the S-expression value types.</summary>
public static class SexpText
{
    /// <summary>Wrap a decoded string in quotes, escaping what Scheme requires.</summary>
    public static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
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
                default: builder.Append(c); break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>Render a scalar value as text: symbols bare, numbers by literal.</summary>
    public static string Display(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            SexpSymbol s => s.Name,
            SexpNull => string.Empty,
            bool b => b ? "#t" : "#f",
            long l => l.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            double d => FormatDouble(d),
            float f => FormatDouble(f),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// Format a whole double with a decimal point (<c>3758000.0</c>), matching
    /// how python renders a float literal.
    /// </summary>
    public static string FormatDouble(double value)
    {
        if (double.IsNaN(value))
        {
            return "+nan.0";
        }
        if (double.IsPositiveInfinity(value))
        {
            return "+inf.0";
        }
        if (double.IsNegativeInfinity(value))
        {
            return "-inf.0";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.IndexOf('E') >= 0)
        {
            text = text.Replace("E", "e");
        }

        if (text.IndexOf('.') < 0 && text.IndexOf('e') < 0)
        {
            text += ".0";
        }

        return text;
    }
}
