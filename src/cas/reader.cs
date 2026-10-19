using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using PureHDF;

namespace QuickLook.Plugin.AFH5;

/// <summary>
/// Raw Scheme documents of a case file, decoded once and shared by every reader.
/// </summary>
public readonly struct CaseTexts
{
    public required string General { get; init; }

    public required string Boundary { get; init; }

    public required string Cortex { get; init; }
}

/// <summary>
/// Reads a Fluent <c>.cas.h5</c> case file into its sections: zone settings,
/// solver configuration, reports and graphics, keyed and nested the way the
/// case file itself expresses them.
///
/// The mesh group is deliberately not read.
/// </summary>
public static partial class CaseReader
{
    /// <summary>Every case section, rendered as indented JSON.</summary>
    public static string Read(string path)
    {
        return ToJson(ReadCase(path)).ToIndentedJson();
    }

    /// <summary>The section readers, in output order.</summary>
    private static readonly (string Key, Func<CaseTexts, Dictionary<string, object>> Read)[] Sections =
    [
        ("solver", ReadSolver),
        ("mat", ReadMaterials),
        ("bd", ReadBoundary),
        ("interfaces", ReadInterfaces),
        ("cff", ReadCustomFieldFunctions),
        ("units", ReadUnits),
        ("ne", ReadNamedExpressions),
        ("solution", ReadSolution),
        ("rd", ReadReportDefinitions),
        ("residuals", ReadResiduals),
        ("monitorsets", ReadMonitorsets),
        ("plotsets", ReadPlotsets),
        ("convergencesets", ReadConvergencesets),
        ("cell", ReadCellRegisters),
        ("iter", ReadIter),
        ("surfaces", ReadSurfaces),
        ("mesh", texts => ReadGraphics(texts, "mesh")),
        ("contours", texts => ReadGraphics(texts, "contours")),
        ("vectors", texts => ReadGraphics(texts, "vectors")),
        ("pathlines", texts => ReadGraphics(texts, "pathlines")),
        ("xy_plot", texts => ReadGraphics(texts, "xy_plot")),
        ("scene", texts => ReadGraphics(texts, "scene")),
        ("parameters", ReadParameters),
    ];

    /// <summary>
    /// Adapt the reader output to the JSON model. Every value the readers produce
    /// is already a string, a list or a string-keyed map.
    /// </summary>
    public static JsonValue ToJson(object? value)
    {
        switch (value)
        {
            case null:
                return JsonValue.NewNull();
            case string text:
                return JsonValue.NewString(text);
            case Dictionary<string, object> map:
                {
                    var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                    foreach (var entry in map)
                    {
                        members[entry.Key] = ToJson(entry.Value);
                    }
                    return JsonValue.NewObject(members);
                }
            case System.Collections.IEnumerable sequence:
                {
                    var elements = new List<JsonValue>();
                    foreach (var item in sequence)
                    {
                        elements.Add(ToJson(item));
                    }
                    return JsonValue.NewArray(elements);
                }
            default:
                return JsonValue.NewString(
                    Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }
    }

    /// <summary>Read the requested sections; with no flags, read them all.</summary>
    public static Dictionary<string, object> ReadCase(string path)
    {
        using var file = H5File.OpenRead(path);
        var settings = file.Group("/settings");

        var texts = new CaseTexts
        {
            General = settings.Dataset("Rampant Variables").Read<string>(),
            Boundary = settings.Dataset("Thread Variables").Read<string>(),
            Cortex = settings.Dataset("Cortex Variables").Read<string>(),
        };

        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, read) in Sections)
        {
            foreach (var entry in read(texts))
            {
                data[entry.Key] = entry.Value;
            }
        }
        return data;
    }

    // ---------------------------------------------------------------- regex bits

    /// <summary>The block starting at a line that matches <paramref name="pattern"/>.</summary>
    private static string Block(string text, string pattern)
    {
        return RegexHelper.Match(text, pattern, RegexOptions.Multiline);
    }

    // ------------------------------------------------------------------ interfaces

    private static Dictionary<string, object> ReadInterfaces(CaseTexts texts)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);

        // (sliding-interfaces (<name> (<property> . <value>) ...) ...)
        var root = Sexp.ParseDocumentFirst(Block(texts.General, @"(\(sliding-interfaces\s+.*)"));
        var list = Sexp.Element(root, 1);

        foreach (var interfaceEntry in Sexp.Elements(list))
        {
            var fields = Sexp.Elements(interfaceEntry);
            if (fields.Count == 0)
            {
                continue;
            }

            var name = Sexp.Text(fields[0]);
            var properties = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in fields.Skip(1))
            {
                var parts = Sexp.Elements(property);
                if (parts.Count == 0)
                {
                    continue;
                }

                var value = Sexp.ValueOf(property);
                if (value is not null && Sexp.IsScalar(value))
                {
                    properties[Sexp.Text(parts[0])] = Sexp.Text(value);
                }
            }
            data[name] = properties;
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["interfaces"] = data };
    }

    // ---------------------------------------------------- custom field functions

    private static Dictionary<string, object> ReadCustomFieldFunctions(CaseTexts texts)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var block = Block(texts.Cortex, @"(\(cell-function-defs\s+.*)");
        if (block.Length == 0)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { ["custom-field-functions"] = data };
        }

        var names = RegexHelper.Groups(block, @"\(name\s([^)]+)\)");
        var displays = RegexHelper.Groups(block, @"\(display\s([^)]+)\)");
        for (var i = 0; i < Math.Min(names.Count, displays.Count); i++)
        {
            data[names[i]] = displays[i].Trim('"');
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["custom-field-functions"] = data };
    }

    // ---------------------------------------------------------------------- units

    private static Dictionary<string, object> ReadUnits(CaseTexts texts)
    {
        var block = Block(texts.Cortex, @"(\(unit-table\s+.*?\)\s*\))");
        if (block.Length == 0)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { ["units"] = "Default SI units" };
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(block, @"\(([^()\s]+)\s+([^()\s]+)\s+([^()\s]+)\s+([^()\s]+)\)"))
        {
            data[match.Groups[1].Value] = new List<object>
            {
                match.Groups[2].Value,
                match.Groups[3].Value,
                match.Groups[4].Value,
            };
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["units"] = data };
    }

    // --------------------------------------------------------- named expressions

    private static Dictionary<string, object> ReadNamedExpressions(CaseTexts texts)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var root = Sexp.ParseDocumentFirst(Block(texts.General, @"(\(named-expressions.*)"));

        foreach (var entry in Sexp.Elements(Sexp.Element(root, 1)))
        {
            var properties = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in Sexp.Elements(entry))
            {
                var parts = Sexp.Elements(property);
                if (parts.Count == 0)
                {
                    continue;
                }

                properties[Sexp.Text(parts[0])] = Sexp.Text(Sexp.ValueOf(property));
            }

            if (properties.TryGetValue("name", out var name))
            {
                data[name as string ?? string.Empty] = properties;
            }
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["named-expressions"] = data };
    }

    // --------------------------------------------------- solution methods/controls

    private static Dictionary<string, object> ReadSolution(CaseTexts texts)
    {
        var general = texts.General;
        var config = ParseCaseConfig(general);

        var methods = new Dictionary<string, object>(StringComparer.Ordinal);
        var controls = new Dictionary<string, object>(StringComparer.Ordinal);

        var cellLsq = RegexHelper.Group(general, @"\(recon/cell-lsq\?\s+([^)]+)\)");
        var nodeLsq = RegexHelper.Group(general, @"\(recon/node-lsq\?\s+([^)]+)\)");
        methods["Gradient"] = cellLsq == "#t"
            ? "Least Squares Cell-Based"
            : nodeLsq == "#t"
                ? "Green-Gauss Node-Based"
                : "Green-Gauss Cell-Based";

        foreach (var equation in new[] { "flow", "pressure", "mom", "temperature", "k", "omega", "epsilon", "disco" })
        {
            methods[equation] = SchemeLabel(general, equation);
        }

        var fluxAuto = RegexHelper.Group(general, @"\(pbs/flux-auto-select\?\s+([^)]+)\)");
        methods["Flux Type"] = fluxAuto == "#t"
            ? "Auto Select"
            : RegexHelper.Group(general, @"\(pbs/flux-index\s+([\d.]+)\)") == "0"
                ? "Rhie-Chow: distance based"
                : "Rhie-Chow: momentum based";

        var pseudoTimeMethod = GetPseudoTimeMethod(general, methods);
        methods["Pseudo Time Method"] = pseudoTimeMethod;

        methods["Wraped-Face Gradient Correction"] =
            RegexHelper.Group(general, @"\(recon/cell-lsf\?\s+([^)]+)\)") == "#t" ? "On" : "Off";

        var relax = RegexHelper.Group(general, @"\(recon/relax/relax\?\s+([^)]+)\)");
        if (relax == "#t")
        {
            var relaxLimitMode = RegexHelper.Group(general, @"\(recon/relax-limit-mode\s+([\d.]+)\)");
            var allVariables = RegexHelper.Group(general, @"\(recon/relax/all\?\s+([^)]+)\)");
            methods["High Order Term Relaxation"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Type"] = relaxLimitMode == "0" ? "Standard" : "Convection Only",
                ["Variables"] = allVariables == "#t" ? "All Variables" : "Flow Variables Only",
                ["Relaxation Factor"] = RegexHelper.Group(general, @"\(recon/relax/steady-urf\s+([\d.]+)\)"),
            };
        }
        else
        {
            methods["High Order Term Relaxation"] = "Off";
        }

        var flowScheme = methods["flow"] as string ?? string.Empty;
        if (flowScheme == "Coupled")
        {
            if (pseudoTimeMethod != "Off")
            {
                methods["Time Scale Factor"] = SelectorExpression(general, "pseudo-auto-time-step-scale-factor");
            }

            foreach (var equation in new[] { "pressure", "mom" })
            {
                controls[equation] = RegexHelper.Group(
                    general,
                    $"\\(pressure-coupled/{equation}/pseudo-explicit-relax\\s+([\\d.]+)\\)");
            }

            foreach (var equation in new[]
                     {
                         "density", "body-force", "temperature", "k", "omega", "epsilon",
                         "turb-viscosity", "disco",
                     })
            {
                controls[equation] = RegexHelper.Group(general, $"\\({equation}/pseudo-relax\\s+([\\d.]+)\\)");
            }
        }
        else
        {
            switch (flowScheme)
            {
                case "SIMPLEC":
                    methods["Skewness Correction"] = RegexHelper.Group(general, @"\(simplec/skew-iter\s+([\d.]+)\)");
                    break;
                case "PISO":
                    methods["Skewness Correction"] = RegexHelper.Group(general, @"\(piso/skew-iter\s+([\d.]+)\)");
                    methods["Neighbor Correction"] = RegexHelper.Group(general, @"\(piso/neighbor-iter\s+([\d.]+)\)");
                    methods["Skewness-Neighbor Coupling"] = RegexHelper.Group(general, @"\(piso/coupling\?\s+([^)]+)\)");
                    break;
            }

            var dualTime = pseudoTimeMethod != "Off";
            var implicitPrefix = dualTime ? "dual-ts-implicit-" : string.Empty;
            var explicitPrefix = dualTime ? "dual-ts-explicit-" : string.Empty;

            if (dualTime)
            {
                var courant = RegexHelper.Group(general, @"\(dual-ts/courant-number\s+([\d.]+)\)");
                if (courant.Length > 0)
                {
                    methods["pseudo-time-courant-number"] = courant;
                }
            }

            var implicitRelax = RelaxationFactors(general, implicitPrefix);
            foreach (var equation in new[] { "pressure", "mom", "temperature", "k", "omega", "epsilon", "turb-viscosity" })
            {
                controls[equation] = implicitRelax.TryGetValue(equation, out var value) ? value : string.Empty;
            }

            var explicitRelax = RelaxationFactors(general, explicitPrefix);
            foreach (var equation in new[] { "density", "body-force", "disco" })
            {
                controls[equation] = explicitRelax.TryGetValue(equation, out var value) ? value : string.Empty;
            }
        }

        var turbModel = GetTurbModel(config);
        if (turbModel is "lam" or "inviscid")
        {
            foreach (var equation in new[] { "k", "omega", "epsilon", "turb-viscosity" })
            {
                methods.Remove(equation);
                controls.Remove(equation);
            }
        }
        else if (turbModel == "kw")
        {
            methods.Remove("epsilon");
            controls.Remove("epsilon");
        }
        else if (turbModel == "ke")
        {
            methods.Remove("omega");
            controls.Remove("omega");
        }

        if (GetRadiationModel(config) != "disco")
        {
            methods.Remove("disco");
            controls.Remove("disco");
        }

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["solution"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["methods"] = methods,
                ["controls"] = controls,
            },
        };
    }

    private static Dictionary<string, string> RelaxationFactors(string general, string prefix)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(general, $@"\((.*)/{prefix}relax\s+([\d.]+)\)"))
        {
            result[match.Groups[1].Value] = match.Groups[2].Value;
        }
        return result;
    }

    /// <summary>
    /// The time-step method implied by the flow scheme and its dt-method code.
    /// </summary>
    private static string GetPseudoTimeMethod(string general, Dictionary<string, object> methods)
    {
        var flowScheme = methods["flow"] as string ?? string.Empty;
        var key = flowScheme == "Coupled"
            ? "pseudo-time-method/coupled-pbns/dt-method"
            : "pseudo-time-method/segregated-pbns/dt-method";
        var code = RegexHelper.Group(general, $"\\({Regex.Escape(key)}\\s+([\\d.]+)\\)");

        return code == "0"
            ? "Off"
            : flowScheme == "Coupled"
                ? "Global Time Step"
                : "Local Time Step";
    }

    /// <summary>The label for the flow discretisation scheme code.</summary>
    private static string GetFlowScheme(string general)
    {
        var index = RegexHelper.Group(general, @"\(flow/scheme\s+(\d+)\)");
        return DiscretizationScheme.TryGetValue(index, out var label) ? label : string.Empty;
    }
}
