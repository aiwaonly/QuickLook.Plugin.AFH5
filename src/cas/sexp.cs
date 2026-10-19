using System;
using System.Collections.Generic;

namespace QuickLook.Plugin.AFH5;

/// <summary>
/// Convenience helpers over the S-expressions produced by
/// <see cref="SexpReader"/>.
///
/// The reader keeps three value shapes:
/// <list type="bullet">
/// <item><see cref="SexpSymbol"/> — a bare identifier such as <c>velocity-inlet</c>;</item>
/// <item><see cref="string"/> — the contents of a quoted string, quotes removed;</item>
/// <item><see cref="SexpPair"/> — a pair; a proper list is a chain terminated by
/// <see cref="SexpNull"/>.</item>
/// </list>
///
/// <see cref="Elements"/> flattens a dotted pair so readers written against
/// Scheme's dotted-pair syntax can index without special cases.
/// </summary>
public static class Sexp
{
    /// <summary>Every top-level expression of a document.</summary>
    public static List<object> ParseDocument(string text) => SexpReader.ParseAll(text);

    /// <summary>The first top-level expression of a document.</summary>
    public static object ParseDocumentFirst(string text) => SexpReader.Parse(text);

    /// <summary>
    /// Elements of a list. A dotted pair contributes its <c>.</c> marker and its
    /// tail as two extra elements — and because Fluent writes many lists fully
    /// dotted, this recurses through the tail:
    /// <c>(a . (b . (c . ())))</c> yields <c>[a, ., b, ., c, .]</c>.
    ///
    /// Keeping the marker in the sequence is what lets index-based readers
    /// (<c>eq[4]</c>, <c>parts[2]</c>) work unchanged, and it is also how the
    /// original Scheme text reads.
    /// </summary>
    public static List<object> Elements(object? value)
    {
        var items = new List<object>();
        if (value is SexpNull or null)
        {
            return items;
        }

        if (value is SexpPair)
        {
            var cursor = value;
            while (cursor is SexpPair pair)
            {
                items.Add(pair.Car);
                cursor = pair.Cdr;
            }

            // A proper list ends in the empty list; only a real dotted tail adds
            // the marker. Fluent nests these, hence the recursion.
            if (cursor is SexpNull)
            {
                return items;
            }

            items.Add(Dot);
            items.AddRange(Elements(cursor));
            return items;
        }

        items.Add(value);
        return items;
    }

    /// <summary>
    /// The raw second element of a pair chain, bypassing the dotted-tail
    /// expansion of <see cref="Elements"/>. For
    /// <c>(report-definition . flux-massflow)</c> this is <c>flux-massflow</c>,
    /// where <see cref="Elements"/> would place the <c>.</c> marker at index 1.
    /// </summary>
    public static object? Second(object? value)
    {
        return value is SexpPair pair ? pair.Cdr : null;
    }

    /// <summary>
    /// Strip a <c>(quote ...)</c> wrapper, which the reader produces for the
    /// <c>'expr</c> shorthand Scheme uses inside Fluent surface definitions.
    /// </summary>
    public static object? Unquote(object? value)
    {
        if (value is SexpPair pair
            && pair.Car is SexpSymbol symbol
            && symbol.Name is "quote" or "quasiquote"
            && Second(value) is SexpPair inner)
        {
            return inner.Car;
        }

        return value;
    }

    /// <summary>
    /// Flatten a (possibly nested) dotted list into its scalar sequence, so
    /// <c>(-0.5 . (0.12 . (-10 . ())))</c> yields <c>[-0.5, 0.12, -10]</c>.
    /// Used for Fluent surface coordinates, which are written this way.
    /// </summary>
    public static List<object> Flatten(object? value)
    {
        var items = new List<object>();
        if (value is SexpNull or null)
        {
            return items;
        }

        if (value is List<object> list)
        {
            foreach (var item in list)
            {
                items.AddRange(Flatten(item));
            }
            return items;
        }

        if (value is SexpPair)
        {
            var cursor = value;
            while (cursor is SexpPair pair)
            {
                if (pair.Car is SexpPair or SexpNull or List<object>)
                {
                    items.AddRange(Flatten(pair.Car));
                }
                else
                {
                    items.Add(pair.Car);
                }
                cursor = pair.Cdr;
            }

            if (cursor is not SexpNull)
            {
                items.AddRange(Flatten(cursor));
            }

            return items;
        }

        items.Add(value);
        return items;
    }

    /// <summary>
    /// The <paramref name="index"/>-th value of a list, counting only real
    /// elements and skipping the <c>.</c> markers that <see cref="Elements"/>
    /// inserts. <c>(0 . (2948 . (v . ())))</c> therefore has values 0, 2948, v.
    /// </summary>
    public static object? ValueAt(object? value, int index)
    {
        var seen = 0;
        foreach (var item in Elements(value))
        {
            if (IsDot(item))
            {
                continue;
            }

            if (seen == index)
            {
                return item;
            }
            seen++;
        }

        return null;
    }

    /// <summary>
    /// Recursively convert raw S-expression values into plain JSON-friendly ones:
    /// dotted chains become nested lists of strings, pairs become lists, symbols
    /// and numbers become strings. Dictionaries and lists pass through.
    /// </summary>
    public static object? Convert(object? value)
    {
        switch (value)
        {
            case null:
            case SexpNull:
                return new List<object>();
            case SexpPair:
                return Stringify(value);
            case string s:
                return s;
            case SexpSymbol symbol:
                return symbol.Name;
            case Dictionary<string, object> dictionary:
                {
                    var converted = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var entry in dictionary)
                    {
                        converted[entry.Key] = Convert(entry.Value) ?? string.Empty;
                    }
                    return converted;
                }
            case List<object> list:
                return list.ConvertAll(item => Convert(item) ?? string.Empty);
            default:
                return SexpText.Display(value);
        }
    }

    /// <summary>Element at <paramref name="index"/>, or <c>null</c> when out of range.</summary>
    public static object? Element(object? value, int index)
    {
        var items = Elements(value);
        return index >= 0 && index < items.Count ? items[index] : null;
    }

    /// <summary>The dotted-pair separator recognised by <see cref="Elements"/>.</summary>
    public static readonly SexpSymbol Dot = new(".");

    /// <summary>True when the value is the <c>.</c> separator.</summary>
    public static bool IsDot(object? value) => value is SexpSymbol symbol && symbol.Name == ".";

    /// <summary>
    /// The value bound by a <c>(key . value)</c> or <c>(key value)</c> entry:
    /// <c>(name . "x")</c> yields the element after the <c>.</c>, while
    /// <c>(zone-ids 12 14)</c> yields <c>12</c>.
    /// </summary>
    public static object? ValueOf(object? value)
    {
        var items = Elements(value);
        if (items.Count >= 3 && IsDot(items[1]))
        {
            return items[2];
        }
        return items.Count >= 2 ? items[1] : null;
    }

    /// <summary>True when the entry is a <c>(key . value)</c> binding.</summary>
    public static bool IsBinding(object? value)
    {
        var items = Elements(value);
        return items.Count >= 3 && IsDot(items[1]);
    }

    /// <summary>True when the value is a scalar (not a list or pair).</summary>
    public static bool IsScalar(object? value) => value is SexpSymbol or string or bool or long or int or double;

    /// <summary>
    /// Plain text of a value: a string yields its contents, a symbol its name, a
    /// number its literal text, and a list its elements joined by spaces.
    /// </summary>
    public static string Text(object? value)
    {
        return value switch
        {
            null => string.Empty,
            SexpNull => string.Empty,
            string s => s,
            SexpSymbol symbol => symbol.Name,
            List<object> list => string.Join(" ", list.ConvertAll(Text)),
            _ => SexpText.Display(value),
        };
    }

    /// <summary>Recursively convert a value into nested lists of strings.</summary>
    public static List<object> Stringify(object? value)
    {
        var result = new List<object>();
        foreach (var item in Elements(value))
        {
            if (item is SexpPair or SexpNull)
            {
                result.Add(Stringify(item));
            }
            else if (item is List<object> nested)
            {
                result.Add(Stringify(nested));
            }
            else
            {
                result.Add(Text(item));
            }
        }

        return result;
    }

    /// <summary>Render a value like python's <c>repr</c>, for diagnostics and tests.</summary>
    public static string Format(object? value)
    {
        return value switch
        {
            List<object> list => "[" + string.Join(", ", list.ConvertAll(Format)) + "]",
            SexpNull => "[]",
            _ => $"'{Text(value)}'",
        };
    }
}

/// <summary>Regex helpers matching the semantics of python's <c>re.search</c>.</summary>
public static class RegexHelper
{
    /// <summary>First capturing group of the first match, or an empty string when unmatched.</summary>
    public static string Group(string input, string pattern, int group = 1, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
    {
        var match = System.Text.RegularExpressions.Regex.Match(input, pattern, options);
        return match.Success ? match.Groups[group].Value : string.Empty;
    }

    /// <summary>The whole first match, or an empty string when unmatched.</summary>
    public static string Match(string input, string pattern, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
    {
        var match = System.Text.RegularExpressions.Regex.Match(input, pattern, options);
        return match.Success ? match.Value : string.Empty;
    }

    /// <summary>True when the pattern matches anywhere in the input.</summary>
    public static bool IsMatch(string input, string pattern, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(input, pattern, options);
    }

    /// <summary>First capture of every match, in order.</summary>
    public static List<string> Groups(string input, string pattern, int group = 1, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
    {
        var result = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(input, pattern, options))
        {
            result.Add(match.Groups[group].Value);
        }
        return result;
    }

    /// <summary>Every match as <c>(group1, group2)</c> pairs.</summary>
    public static List<(string, string)> Pairs(string input, string pattern, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
    {
        var result = new List<(string, string)>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(input, pattern, options))
        {
            result.Add((match.Groups[1].Value, match.Groups[2].Value));
        }
        return result;
    }
}
