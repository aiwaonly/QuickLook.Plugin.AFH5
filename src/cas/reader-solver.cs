using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace QuickLook.Plugin.AFH5;

/// <summary>
/// Reader for the scalar/solver/materials sections of a Fluent <c>.cas.h5</c> case.
/// </summary>
public static partial class CaseReader
{
    // ------------------------------------------------------------ shared helpers

    /// <summary>
    /// Parse the <c>(case-config ...)</c> block into a <c>name -&gt; value</c> map.
    /// </summary>
    private static Dictionary<string, string> ParseCaseConfig(string general)
    {
        var block = ParseCaseConfigBlock(general);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in RegexHelper.Pairs(block, @"\(([^()\s]+)\s+\.\s+([^()\s]+)\)"))
        {
            result[name] = value;
        }
        return result;
    }

    /// <summary>Combine the <c>-sel</c> and <c>-expr</c> values of a setting into <c>sel/expr</c>.</summary>
    private static string SelectorExpression(string general, string name)
    {
        var sel = RegexHelper.Group(general, $"\\({Regex.Escape(name)}-sel\\s+\"([^\"]+)\"\\)");
        var expr = RegexHelper.Group(general, $"\\({Regex.Escape(name)}-expr\\s+\"([^\"]+)\"\\)");
        return $"{sel}/{expr}";
    }

    /// <summary>
    /// Get the <c>sel/expr</c> pair out of a selection list. The parser keeps the
    /// dotted-pair marker, so <c>(constant . 1.225)</c> arrives as
    /// <c>[constant, ., 1.225]</c>.
    /// </summary>
    private static string SelectionExpression(object? value, bool returnExpression = false)
    {
        var items = Sexp.Elements(value);

        if (items.Count >= 3 && Sexp.IsDot(items[1]))
        {
            return returnExpression
                ? Sexp.Text(items[2])
                : $"{Sexp.Text(items[0])}/{Sexp.Text(items[2])}";
        }

        switch (items.Count)
        {
            case 0:
                return string.Empty;
            case 1:
                return Sexp.Text(items[0]);
            case 2:
                return returnExpression
                    ? Sexp.Text(items[1])
                    : $"{Sexp.Text(items[0])}/{Sexp.Text(items[1])}";
            default:
                {
                    // [prefix, [sel, '.', expr], ...]
                    if (items[1] is SexpPair or SexpNull)
                    {
                        var inner = Sexp.Elements(items[1]);
                        if (inner.Count >= 3 && Sexp.IsDot(inner[1]))
                        {
                            return returnExpression
                                ? Sexp.Text(inner[2])
                                : $"{Sexp.Text(items[0])}/{Sexp.Text(inner[0])}/{Sexp.Text(inner[2])}";
                        }
                    }

                    // [sel, rest...] - join the remainder the way python's
                    // str(list).strip('[]') does.
                    var rest = items.GetRange(1, items.Count - 1).ConvertAll(Sexp.Text);
                    var joined = string.Join(", ", rest);
                    return returnExpression ? joined : $"{Sexp.Text(items[0])}/{joined}";
                }
        }
    }

    // ------------------------------------------------------------------- solver

    private static readonly (string Key, string Value)[] TurbModelKeys =
    [
        ("rp-lam?", "lam"), ("rp-ke?", "ke"), ("rp-kw?", "kw"), ("rp-sa?", "sa"),
        ("sg-rsm?", "rsm"), ("rp-les?", "les"), ("rp-des?", "des"),
        ("rp-kklw", "kklw"), ("rp-v2f?", "v2f"),
    ];

    private static readonly (string Key, string Value)[] RadiationModelKeys =
    [
        ("sg-rosseland?", "rosseland"), ("sg-p1?", "p1"), ("sg-dtrm?", "dtrm"),
        ("sg-s2s?", "s2s"), ("sg-disco?", "disco"),
    ];

    private static string GetTurbModel(Dictionary<string, string> config)
    {
        if (Get(config, "rp-visc?") == "#f")
        {
            return "inviscid";
        }

        foreach (var (key, value) in TurbModelKeys)
        {
            if (Get(config, key) == "#t")
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string GetRadiationModel(Dictionary<string, string> config)
    {
        foreach (var (key, value) in RadiationModelKeys)
        {
            if (Get(config, key) != "#f")
            {
                return value;
            }
        }

        return "off";
    }

    private static string GetMultiPhaseModel(Dictionary<string, string> config)
    {
        var multiPhase = Get(config, "sg-mphase?");
        if (multiPhase != "#f")
        {
            return multiPhase switch
            {
                "vof" => "vof",
                "drift-flux" => "mixture",
                "multi-fluid" => "eulerian",
                _ => multiPhase,
            };
        }

        return Get(config, "sg-wetsteam?") != "#f" ? "wetsteam" : "off";
    }

    private static string GetSolverTime(Dictionary<string, string> config)
    {
        return Get(config, "rp-unsteady?") == "#t" ? "transient" : "steady";
    }

    private static object GetGravity(string general, string dimension)
    {
        var gravity = RegexHelper.Group(general, @"\(gravity\?\s+([^)\s]+)\)");
        if (gravity != "#t")
        {
            return "Off";
        }

        var axes = dimension == "3d" ? new[] { "x", "y", "z" } : ["x", "y"];
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var axis in axes)
        {
            result[axis] = SelectorExpression(general, $"gravity/{axis}");
        }
        return result;
    }

    private static Dictionary<string, object> GetOperatingConditions(string general)
    {
        var conditions = new List<string>
        {
            "operating-pressure",
            "pressure-reference/x",
            "pressure-reference/y",
            "pressure-reference/z",
            "operating-temperature",
        };

        if (RegexHelper.Group(general, @"\(use-operating-density\?\s+([^)\s]+)\)") == "#t")
        {
            conditions.Add("operating-density");
        }

        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var condition in conditions)
        {
            result[condition] = SelectorExpression(general, condition);
        }
        return result;
    }

    private static readonly string[] ReferenceValues =
    [
        "area", "depth", "density", "enthalpy", "length",
        "pressure", "temperature", "velocity", "viscosity",
        "gamma", "thread", "tol", "yplus",
    ];

    private static Dictionary<string, object> GetReferenceValues(string general)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var value in ReferenceValues)
        {
            result[value] = RegexHelper.Group(general, $"\\(reference-{value}\\s+([^)\\s]+)\\)");
        }
        return result;
    }

    /// <summary>Discretisation scheme codes, from Ansys Fluent <c>sg.h</c>.</summary>
    private static readonly Dictionary<string, string> DiscretizationScheme = new()
    {
        ["0"] = "First Order Upwind",
        ["1"] = "Second Order Upwind",
        ["2"] = "Power Law",
        ["3"] = "Central Difference",
        ["4"] = "Quick",
        ["5"] = "Modified HRIC",
        ["6"] = "Third-Order MUSCL",
        ["7"] = "Bounded Central Differencing",
        ["8"] = "CICSAM",
        ["9"] = "Low Diffusion Second Order",
        ["10"] = "Standard",
        ["11"] = "Linear",
        ["12"] = "Second Order",
        ["13"] = "Body Force Weighted",
        ["14"] = "PRESTO!",
        ["15"] = "Continuity Based",
        ["16"] = "Geo-Reconstruct",
        ["17"] = "Donor-Acceptor",
        ["18"] = "Modified Body Force Weighted",
        ["20"] = "SIMPLE",
        ["21"] = "SIMPLEC",
        ["22"] = "PISO",
        ["23"] = "Phase Coupled SIMPLE",
        ["24"] = "Coupled",
        ["25"] = "Fractional Step",
        ["26"] = "M_P_COUPLED",
        ["27"] = "M_P_FULL_COUPLED",
        ["28"] = "Compressive",
        ["29"] = "BGM",
        ["30"] = "Phase Coupled PISO",
        ["31"] = "Low Diffusion Central",
    };

    /// <summary>Numeric index of an equation's discretisation scheme, or empty.</summary>
    private static string SchemeIndex(string general, string equation)
    {
        // The setting sits outside (case-config ...). The lookbehind stops
        // 'pressure/scheme' matching 'face-pressure/scheme' and
        // 'temperature/scheme' matching 'granular-temperature/scheme'.
        return RegexHelper.Group(general, $"(?<![\\w-]){Regex.Escape(equation)}/scheme\\s+(\\d+)");
    }

    /// <summary>Label of an equation's discretisation scheme, or empty when unset.</summary>
    private static string SchemeLabel(string general, string equation)
    {
        var index = SchemeIndex(general, equation);
        return DiscretizationScheme.TryGetValue(index, out var label) ? label : string.Empty;
    }

    /// <summary>The raw <c>(case-config ...)</c> block.</summary>
    private static string ParseCaseConfigBlock(string general)
    {
        return RegexHelper.Match(general, "^(\\(case-config.*)", RegexOptions.Multiline);
    }

    private static Dictionary<string, object> ReadSolver(CaseTexts texts)
    {
        var general = texts.General;
        var config = ParseCaseConfig(general);
        var dimension = Get(config, "rp-3d?") == "#t" ? "3d" : "2d";

        var velocityFormulation = RegexHelper.Group(general, @"\(solve-absolute-velocities\?\s+([^)\s]+)\)") == "#t"
            ? "absolute"
            : "relative";

        var solver = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["type"] = Get(config, "rp-seg?") == "#t" ? "pbns" : "dbns",
            ["velocity-formulation"] = velocityFormulation,
            ["time"] = GetSolverTime(config),
            ["dimension"] = dimension,
            ["precision"] = Get(config, "rp-double?") == "#t" ? "double" : "single",
            ["axi"] = Get(config, "rp-axi?") == "#t" ? "true" : "false",
            ["init"] = Get(config, "hyb-init?") == "#t" ? "hybrid" : "standard",
            ["turb"] = GetTurbModel(config),
            ["energy"] = Get(config, "rf-energy?") == "#t" ? "true" : "false",
            ["radiation"] = GetRadiationModel(config),
            ["multi-phase"] = GetMultiPhaseModel(config),
            ["gravity"] = GetGravity(general, dimension),
            ["operating-conditions"] = GetOperatingConditions(general),
            ["reference-values"] = GetReferenceValues(general),
        };

        var result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["solver"] = solver,
        };
        return result;
    }

    private static string Get(Dictionary<string, string> map, string key)
    {
        return map.TryGetValue(key, out var value) ? value : string.Empty;
    }

    // ---------------------------------------------------------------- materials

    private static Dictionary<string, object> ReadMaterials(CaseTexts texts)
    {
        var general = texts.General;
        var materialsBlock = RegexHelper.Match(general, "^(\\(materials.*)", RegexOptions.Multiline);
        var materials = Sexp.Elements(Sexp.ParseDocumentFirst(materialsBlock));

        var data = new Dictionary<string, object>(StringComparer.Ordinal);

        // The document is (materials <material>...); index 0 is the 'materials' symbol.
        var list = materials.Count > 1 ? materials[1] : null;

        foreach (var material in Sexp.Elements(list))
        {
            var fields = Sexp.Elements(material);
            if (fields.Count < 3)
            {
                continue;
            }

            var name = Sexp.Text(fields[0]);
            var materialType = Sexp.Text(fields[1]);
            var properties = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var entry in fields.GetRange(2, fields.Count - 2))
            {
                var parts = Sexp.Elements(entry);
                if (parts.Count < 2)
                {
                    continue;
                }

                var propertyName = Sexp.Text(parts[0]);
                properties[propertyName] = ReadMaterialValue(parts.GetRange(1, parts.Count - 1));
            }

            if (!data.TryGetValue(materialType, out var bucket))
            {
                bucket = new Dictionary<string, object>(StringComparer.Ordinal);
                data[materialType] = bucket;
            }
            ((Dictionary<string, object>)bucket)[name] = properties;
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["materials"] = data };
    }

    /// <summary>Read a material property value from its parsed argument list.</summary>
    private static object ReadMaterialValue(List<object> parts)
    {
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        // (chemical-formula . al): the value itself is dotted.
        if (parts.Count >= 2 && Sexp.IsDot(parts[0]))
        {
            return Sexp.Text(parts[1]);
        }

        // ((constant . 1.225) (compressible-liquid . ...)): the leading selection
        // is the property value, the trailing ones are alternative models.
        if (parts[0] is SexpPair or SexpNull)
        {
            // Flatten, not Elements: a selection such as
            // (polynomial . (807.163 . (3.58 . ()))) must expand to its scalars.
            var selection = Sexp.Flatten(parts[0]);
            if (selection.Count >= 2 && Sexp.IsDot(selection[0]))
            {
                return Sexp.Text(selection[1]);
            }

            var head = selection.Count > 0 ? Sexp.Text(selection[0]) : string.Empty;
            var tail = selection.Count > 1 ? selection.GetRange(1, selection.Count - 1) : [];

            return head switch
            {
                // (polynomial piecewise-linear (pairs...)) and the nasa variants get
                // their own nested key.
                "polynomial" when tail.Count > 0
                                  && Sexp.Text(tail[0]) is "piecewise-linear" or "piecewise-polynomial" or "nasa-9-piecewise-polynomial" =>
                    ReadPolynomial(Sexp.Text(tail[0]), tail.GetRange(1, tail.Count - 1)),
                "orthotropic" => ReadOrthotropic(tail),
                // A single-value selection such as (constant . 2719) or (none . #f).
                _ when tail.Count == 1 => $"{head}/{Sexp.Text(tail[0])}",
                // Anything else keeps its selector as the key, e.g.
                // (polynomial . (807.163 . (3.58 . ()))) -> {"polynomial": [..]}.
                _ => new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [head] = tail.ConvertAll(Sexp.Text),
                },
            };
        }

        // (constant 1.225) and similar flat selections.
        if (parts.Count >= 2 && Sexp.IsScalar(parts[1]))
        {
            return $"{Sexp.Text(parts[0])}/{Sexp.Text(parts[1])}";
        }

        var name = Sexp.Text(parts[0]);
        var values = string.Join(" ", parts.GetRange(1, parts.Count - 1).ConvertAll(Sexp.Text));
        return $"{name}/{values}";
    }

    private static object ReadPolynomial(string kind, List<object> values)
    {
        // piecewise-linear pairs are (x y); others are flat coefficient lists.
        if (kind == "piecewise-linear")
        {
            var pairs = new List<object>();
            foreach (var value in values)
            {
                var points = Sexp.Elements(value);
                if (points.Count >= 3)
                {
                    pairs.Add($"{Sexp.Text(points[0])}, {Sexp.Text(points[2])}");
                }
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["polynomial/piecewise-linear"] = pairs,
            };
        }

        // Fluent sometimes wraps the coefficients in one extra list level.
        var coefficients = values.Count == 1 && values[0] is SexpPair or SexpNull
            ? Sexp.Elements(values[0])
            : values;

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [$"polynomial/{kind}"] = coefficients.ConvertAll(Sexp.Text),
        };
    }

    private static object ReadOrthotropic(List<object> properties)
    {
        var value = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            var fields = Sexp.Elements(property);
            if (fields.Count < 2)
            {
                continue;
            }

            var name = Sexp.Text(fields[0]);
            if (name is "direction-0" or "direction-1" or "direction-2")
            {
                var directions = fields.GetRange(1, fields.Count - 1).ConvertAll(Sexp.Text);
                value[name] = string.Join(" ", directions);
            }
            else if (name is "k0" or "k1" or "k2")
            {
                value[name] = SelectionExpression(fields[1]);
            }
        }
        return value;
    }
}
