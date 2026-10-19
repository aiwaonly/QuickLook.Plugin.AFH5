using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuickLook.Plugin.AFH5;

/// <summary>
/// Readers for report definitions, residuals, monitors, convergence, cell
/// registers, iteration, surfaces, graphics and parameters.
/// </summary>
public static partial class CaseReader
{
    /// <summary>Report keys that carry an explicit ordering, most specific first.</summary>
    private static readonly string[] ReportOrderKeys = ["zone-list", "surface-ids", "zone-ids"];
    // ------------------------------------------------------ report definitions

    private static Dictionary<string, object> ReadReportDefinitions(CaseTexts texts)
    {
        var general = texts.General;
        var data = new Dictionary<string, object>(StringComparer.Ordinal);

        var root = Sexp.ParseDocumentFirst(Block(general, @"(\(monitor/report-definitions.*)"));
        foreach (var entry in Sexp.Elements(Sexp.Element(root, 1)))
        {
            // ((name . "report-def-0") (report-definition <type> ...))
            var fields = Sexp.Elements(entry);
            var name = Sexp.Text(Sexp.ValueOf(fields[0]));
            var reportDefinition = Sexp.Elements(fields[1]);
            var type = Sexp.Text(Sexp.Element(reportDefinition, 1));

            // The dotted form (report-definition . flux-massflow) expands to
            // [report-definition, ., flux-massflow, (setting) ...], so the kind is
            // the third element; the undotted form keeps it at index 1.
            var kind = Sexp.Text(Sexp.Element(Sexp.Second(fields[1]), 0));
            var start = 2;

            var report = new Dictionary<string, object>(StringComparer.Ordinal) { ["type"] = kind };
            foreach (var property in reportDefinition.Skip(start))
            {
                var parts = Sexp.Elements(property);
                if (parts.Count == 0)
                {
                    continue;
                }

                var propertyName = Sexp.Text(parts[0]);
                if (propertyName is "old-props" or "locations")
                {
                    continue;
                }

                // 'report-type' normalises to 'type' and would clobber the report
                // definition kind (flux-massflow), so it is emitted under its own key.
                if (propertyName == "report-type")
                {
                    report["report-type"] = Sexp.IsBinding(property)
                        ? Sexp.Text(Sexp.ValueOf(property))
                        : string.Empty;
                    continue;
                }

                if (Sexp.IsBinding(property))
                {
                    report[propertyName] = Sexp.ValueOf(property) ?? string.Empty;
                }
                else
                {
                    // A proper list value, e.g. (zone-ids 12 14). Elements() expands
                    // any dotted tail, and the '.' markers are dropped afterwards.
                    report[propertyName] = parts.Skip(1)
                        .Where(item => !Sexp.IsDot(item))
                        .ToList();
                }
            }
            data[name] = Sexp.Convert(report) ?? string.Empty;
        }

        // (monitor/average-over-state ...) fills in the monitored value per report.
        var averageRoot = Sexp.ParseDocumentFirst(Block(general, @"(\(monitor/average-over-state.*)"));
        foreach (var entry in Sexp.Elements(Sexp.Element(averageRoot, 1)))
        {
            var fields = Sexp.Elements(entry);
            var name = Sexp.Text(fields[0]);
            if (!data.TryGetValue(name, out var bucket) || bucket is not Dictionary<string, object> report)
            {
                continue;
            }

            // (avg-over (id ...) ((start . end) value ...)). ValueAt skips the '.'
            // markers so the bounds and values are addressed by value position.
            var rangeValue = fields[2];
            report["iter-range"] =
                $"{Sexp.Text(Sexp.ValueAt(rangeValue, 0))} -> {Sexp.Text(Sexp.ValueAt(rangeValue, 1))}";

            // A report is single-valued when any one of three independent checks
            // holds: per-zone? is '#f', per-surface? is '#f', or the type is
            // 'single-val-expression'. A missing flag is not '#f', so it does not
            // by itself select the single-value path. The flags are evaluated
            // separately rather than short-circuited together, which matters for a
            // report that is per-zone but has no per-surface? at all.
            var singleValue = false;
            var perZoneKey = "per-zone?";
            var perSurfaceKey = "per-surface?";
            if (report.TryGetValue(perZoneKey, out var pz) && Sexp.Text(pz) == "#f")
            {
                singleValue = true;
            }
            if (report.TryGetValue(perSurfaceKey, out var ps) && Sexp.Text(ps) == "#f")
            {
                singleValue = true;
            }
            if (Sexp.Text(report.GetValueOrDefault("type")) == "single-val-expression")
            {
                singleValue = true;
            }

            if (singleValue)
            {
                // The monitored value is a one-element dotted list.
                var first = Sexp.Element(Sexp.ValueAt(rangeValue, 2), 0);
                report["average-over-state"] = new List<object> { first is null ? string.Empty : Sexp.Text(first) };
                continue;
            }

            var ids = Sexp.Elements(fields[1]).Where(id => !Sexp.IsDot(id)).Select(IndexText).ToList();

            // Range and values share one flat sequence: [start, end, value, value...].
            var values = new List<string>();
            foreach (var item in Sexp.Elements(rangeValue).Skip(2))
            {
                values.AddRange(Sexp.Flatten(item).ConvertAll(Sexp.Text));
            }

            var orderKey = ReportOrderKeys.FirstOrDefault(key => report.ContainsKey(key));
            // The key holds a nested dotted list such as ((34 . (259 . ()))), so all
            // elements are flattened out of it.
            var order = orderKey is null
                ? []
                : Sexp.Flatten(report[orderKey]).Select(IndexText).ToList();

            // Each value belongs to the entry whose id sits at the same index, but
            // the output follows the order of zone-ids/surface-ids.
            var sorted = new object?[values.Count];
            for (var i = 0; i < Math.Min(ids.Count, values.Count); i++)
            {
                var position = order.IndexOf(ids[i]);
                if (position >= 0 && position < sorted.Length)
                {
                    sorted[position] = values[i];
                }
            }
            report["average-over-state"] = sorted.ToList();
        }

        // Convert whatever raw S-expression values are still present (report
        // properties and the per-report values collected above).
        foreach (var entry in data.Keys.ToList())
        {
            data[entry] = Sexp.Convert(data[entry]) ?? string.Empty;
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["report-definitions"] = data };
    }

    /// <summary>
    /// Render a zone or surface id. Fluent sometimes stores these as doubles, so
    /// whole numbers are printed without a fractional part to match the ids that
    /// appear elsewhere in the document.
    /// </summary>
    private static string IndexText(object? value)
    {
        return value switch
        {
            double d when d == Math.Floor(d) && !double.IsInfinity(d) => ((long)d).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            _ => Sexp.Text(value),
        };
    }

    // --------------------------------------------------------------- residuals

    private static Dictionary<string, object> ReadResiduals(CaseTexts texts)
    {
        var general = texts.General;
        var data = new Dictionary<string, object>(StringComparer.Ordinal);

        // These are keyed by the setting name without the trailing '?', since not
        // every setting carries one.
        foreach (var setting in new[]
                 {
                     "advanced-options?", "normalize?", "compute-local?", "scale?",
                     "convergence-criterion-type", "n-display", "n-save", "plot?", "print?",
                     "n-maximize-norms",
                 })
        {
            data[setting.TrimEnd('?')] = RegexHelper.Group(
                general,
                $"\\(residuals/{Regex.Escape(setting)}\\s+(#[tf]|[\\d.]+)\\)");
        }

        data["convergence-criterion-type"] = data["convergence-criterion-type"] switch
        {
            "0" => "absolute",
            "1" => "relative",
            "2" => "absolute-relative",
            _ => "none",
        };

        var section = GetSolverTime(ParseCaseConfig(general)) == "transient"
            ? "residuals/settings-transient"
            : "residuals/settings";
        var root = Sexp.ParseDocumentFirst(Block(general, $@"(\({section}\s+.*)"));
        var residualList = Sexp.Element(root, 1);
        if (Sexp.Text(residualList) != "#f")
        {
            foreach (var equation in Sexp.Elements(residualList))
            {
                var fields = Sexp.Elements(equation);
                if (fields.Count < 5)
                {
                    continue;
                }

                data[Sexp.Text(fields[0])] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["monitor"] = Sexp.Text(fields[1]),
                    ["check-convergence"] = Sexp.Text(fields[3]),
                    ["absolute-criteria"] = Sexp.Text(fields[4]),
                    ["relative-criteria"] = Sexp.Text(fields[fields.Count - 1]),
                };
            }
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["residuals"] = data };
    }

    // --------------------------------------------------- monitorsets / plotsets

    private static Dictionary<string, object> ReadMonitorsets(CaseTexts texts)
    {
        return ReadNamedPropertySets(texts.General, "monitor/monitorsets", "monitorsets");
    }

    private static Dictionary<string, object> ReadPlotsets(CaseTexts texts)
    {
        return ReadNamedPropertySets(texts.General, "monitor/plotsets", "plotsets");
    }

    private static Dictionary<string, object> ReadNamedPropertySets(string general, string section, string key)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var root = Sexp.ParseDocumentFirst(Block(general, $@"(\({Regex.Escape(section)}.*)"));

        foreach (var entry in Sexp.Elements(Sexp.Element(root, 1)))
        {
            var fields = Sexp.Elements(entry);
            if (fields.Count == 0)
            {
                continue;
            }

            var name = Sexp.Text(Sexp.ValueOf(fields[0]));
            var properties = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in fields.Skip(1))
            {
                var parts = Sexp.Elements(property);
                if (parts.Count == 0)
                {
                    continue;
                }

                var propertyName = Sexp.Text(parts[0]);
                if (propertyName == "old-props")
                {
                    continue;
                }

                if (Sexp.IsBinding(property))
                {
                    properties[propertyName] = Sexp.Text(Sexp.ValueOf(property));
                }
                else
                {
                    properties[propertyName] = parts.Skip(1).Select(Sexp.Text).ToList();
                }
            }
            data[name] = properties;
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { [key] = data };
    }

    // ----------------------------------------------------------- convergencesets

    private static Dictionary<string, object> ReadConvergencesets(CaseTexts texts)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var root = Sexp.ParseDocumentFirst(Block(texts.General, @"(\(monitor/convergencesets.*)"));

        foreach (var property in Sexp.Elements(Sexp.Element(root, 1)))
        {
            var parts = Sexp.Elements(property);
            if (parts.Count == 0)
            {
                continue;
            }

            var propertyName = Sexp.Text(parts[0]);
            if (propertyName == "conv-reports")
            {
                var reports = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var report in parts.Skip(1))
                {
                    var reportFields = Sexp.Elements(report);
                    if (reportFields.Count == 0)
                    {
                        continue;
                    }

                    var reportName = Sexp.Text(Sexp.ValueOf(reportFields[0]));
                    var reportProperties = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var entry in reportFields.Skip(1))
                    {
                        var entryParts = Sexp.Elements(entry);
                        if (entryParts.Count == 0 || Sexp.Text(entryParts[0]) == "old-props" || !Sexp.IsBinding(entry))
                        {
                            continue;
                        }

                        reportProperties[Sexp.Text(entryParts[0])] = Sexp.Text(Sexp.ValueOf(entry));
                    }
                    reports[reportName] = reportProperties;
                }
                data["reports"] = reports;
            }
            else if (Sexp.IsBinding(property))
            {
                data[propertyName] = Sexp.Text(Sexp.ValueOf(property));
            }
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["convergencesets"] = data };
    }

    // ------------------------------------------------------------ cell registers

    private static Dictionary<string, object> ReadCellRegisters(CaseTexts texts)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var root = Sexp.ParseDocumentFirst(Block(texts.General, @"(\(cell-registers.*)"));

        foreach (var entry in Sexp.Elements(Sexp.Element(root, 1)))
        {
            var fields = Sexp.Elements(entry);
            if (fields.Count == 0)
            {
                continue;
            }

            var name = Sexp.Text(Sexp.ValueOf(fields[0]));
            var register = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var property in fields.Skip(1))
            {
                var parts = Sexp.Elements(property);
                if (parts.Count == 0)
                {
                    continue;
                }

                var propertyName = Sexp.Text(parts[0]);
                switch (propertyName)
                {
                    case "type":
                        {
                            var options = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["type"] = Sexp.Text(Sexp.Element(parts, 1)),
                            };
                            foreach (var item in parts.Skip(2))
                            {
                                var itemParts = Sexp.Elements(item);
                                if (itemParts.Count == 0)
                                {
                                    continue;
                                }

                                options[Sexp.Text(itemParts[0])] = itemParts.Count >= 3 && Sexp.IsDot(itemParts[1])
                                    ? Sexp.Text(itemParts[2])
                                    : $"{Sexp.Text(Sexp.Element(itemParts, 1))}/{Sexp.Text(Sexp.Element(itemParts, 3))}";
                            }
                            register["options"] = options;
                            break;
                        }
                    case "display-options":
                        {
                            var options = new Dictionary<string, object>(StringComparer.Ordinal);
                            foreach (var item in parts.Skip(1))
                            {
                                var itemParts = Sexp.Elements(item);
                                if (itemParts.Count >= 3 && Sexp.IsDot(itemParts[1]))
                                {
                                    options[Sexp.Text(itemParts[0])] = Sexp.Text(itemParts[2]);
                                }
                            }
                            register["display-options"] = options;
                            break;
                        }
                    default:
                        if (Sexp.IsBinding(property))
                        {
                            register[propertyName] = Sexp.Text(Sexp.ValueOf(property));
                        }
                        break;
                }
            }

            data[name] = register;
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["cell-registers"] = data };
    }

    // ---------------------------------------------------------------- iteration

    private static Dictionary<string, object> ReadIter(CaseTexts texts)
    {
        var general = texts.General;
        var data = new Dictionary<string, object>(StringComparer.Ordinal);

        if (GetSolverTime(ParseCaseConfig(general)) == "steady")
        {
            var pseudoTimeMethod = GetPseudoTimeMethodFromText(general);
            data["pseudo-time-method"] = pseudoTimeMethod;

            if (pseudoTimeMethod != "Off")
            {
                var automatic = RegexHelper.Group(general, @"\(pseudo-auto-time-step\?\s+(#[tf])\)") == "#t";
                data["time-step-method"] = automatic ? "Automatic" : "User-Specified";

                if (automatic)
                {
                    data["time-scale-factor"] = SelectorExpression(general, "pseudo-auto-time-step-scale-factor");
                    var lengthScale = RegexHelper.Group(general, @"\(pseudo/autotime-lscale-option\s+(\d+)\)");
                    data["length-scale-method"] = lengthScale switch
                    {
                        "0" => "Aggressive",
                        "1" => "Conservative",
                        _ => "User-Specified",
                    };

                    if ((string)data["length-scale-method"] == "User-Specified")
                    {
                        data["length-scale"] = RegexHelper.Group(general, @"\(pseudo/autotime-lscale-userspec\s+([\d.]+)\)");
                    }
                }
                else
                {
                    data["pseudo-time-step"] = SelectorExpression(general, "pseudo-time-step");
                }

                data["verbosity"] = RegexHelper.Group(general, @"\(pseudo-auto-time-verbosity\s+(\d+)\)");
            }

            data["iterations"] = RegexHelper.Group(general, @"\(number-of-iterations\s+(\d+)\)");
            data["reporting-interval"] = RegexHelper.Group(general, @"\(iteration-chunk\s+(\d+)\)");
            data["update-interval"] = RegexHelper.Group(general, @"\(profile/update-interval\s+(\d+)\)");
            data["save-steady-statistics"] = RegexHelper.Group(general, @"\(save-steady-statistics\?\s+(#[tf])\)");
        }
        else
        {
            data["physical-time-step"] = SelectorExpression(general, "physical-time-step");
            foreach (var key in new[] { "time-step", "max-iterations-per-step", "flow-time", "time/total-timesteps" })
            {
                data[key] = RegexHelper.Group(general, $"\\({Regex.Escape(key)}\\s+([\\d.]+)\\)");
            }
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["iter"] = data };
    }

    private static string GetPseudoTimeMethodFromText(string general)
    {
        var flowScheme = GetFlowScheme(general);
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

    // ----------------------------------------------------------------- surfaces

    private static Dictionary<string, object> ReadSurfaces(CaseTexts texts)
    {
        var cortex = texts.Cortex;
        var data = new Dictionary<string, object>(StringComparer.Ordinal);

        var nameToId = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupsRoot = Sexp.ParseDocumentFirst(Block(cortex, @"(\(surfaces/groups.*)"));
        foreach (var surface in Sexp.Elements(Sexp.Element(groupsRoot, 1)))
        {
            var fields = Sexp.Elements(surface);
            if (fields.Count < 2)
            {
                continue;
            }

            nameToId[Sexp.Text(fields[0])] = Sexp.Text(Sexp.Element(Sexp.Element(fields[1], 0), 0));
        }

        var idToVirtualId = new Dictionary<string, string>(StringComparer.Ordinal);
        var mapRoot = Sexp.ParseDocumentFirst(Block(cortex, @"(\(cx-surface-id-map.*)"));
        foreach (var idGroup in Sexp.Elements(Sexp.Element(mapRoot, 1)))
        {
            var fields = Sexp.Elements(idGroup);
            if (fields.Count >= 2)
            {
                idToVirtualId[Sexp.Text(fields[0])] = Sexp.Text(fields[1]);
            }
        }

        var virtualIdToName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in nameToId)
        {
            var name = entry.Key;
            var id = entry.Value;
            if (idToVirtualId.TryGetValue(id, out var virtualId))
            {
                virtualIdToName[virtualId] = name;
            }
        }

        var definitionRoot = Sexp.ParseDocumentFirst(Block(cortex, @"(\(cx-surface-def-list.*)"));
        foreach (var definition in Sexp.Elements(Sexp.Element(definitionRoot, 1)))
        {
            var fields = Sexp.Elements(definition);
            if (fields.Count < 3)
            {
                continue;
            }

            var virtualId = Sexp.Text(fields[0]);
            var surface = fields[2];
            var kind = Sexp.Text(Sexp.Element(surface, 0));
            if (!virtualIdToName.TryGetValue(virtualId, out var surfaceName))
            {
                continue;
            }

            var parts = Sexp.Elements(surface);
            switch (kind)
            {
                case "line-surface" when parts.Count >= 4:
                    data[surfaceName] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["type"] = "line-surface",
                        ["start"] = Coordinate(parts[2]),
                        ["end"] = Coordinate(parts[3]),
                    };
                    break;
                case "plane-surface" when parts.Count >= 7:
                    {
                        // The method value is quoted: '((reference-plane . x)
                        // (direction . v) (point . p)), so unwrap it first.
                        var method = Sexp.Unquote(parts[6]);
                        data[surfaceName] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["type"] = "plane-surface",
                            ["point 1"] = Coordinate(parts[2]),
                            ["point 2"] = Coordinate(parts[3]),
                            ["point 3"] = Coordinate(parts[4]),
                            ["bounded"] = Sexp.Text(parts[5]),
                            ["method"] = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["reference plane"] = Sexp.Text(Sexp.ValueAt(method, 0)),
                                ["direction vector"] = PointList(Sexp.ValueAt(method, 1)),
                                ["reference point"] = PointList(Sexp.ValueAt(method, 2)),
                            },
                        };
                    }
                    break;
                case "iso-surface" when parts.Count >= 4:
                    {
                        var from = Sexp.Elements(parts[2])
                            .Select(id => virtualIdToName.TryGetValue(Sexp.Text(id), out var n) ? n : Sexp.Text(id))
                            .ToList();
                        data[surfaceName] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["type"] = "iso-surface",
                            ["from"] = from,
                            ["reference"] = Sexp.Text(parts[3]),
                            ["iso-values"] = Sexp.Text(Sexp.Element(parts, 4)),
                        };
                    }
                    break;
                case "iso-clip-new" when parts.Count >= 5:
                    {
                        var clips = Sexp.Elements(parts[2])
                            .Select(id => virtualIdToName.TryGetValue(Sexp.Text(id), out var n) ? n : Sexp.Text(id))
                            .ToList();
                        data[surfaceName] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["type"] = "iso-clip-new",
                            ["clip surfaces"] = clips,
                            ["reference"] = Sexp.Text(parts[3]),
                            ["min"] = Sexp.Text(parts[4]),
                            ["max"] = Sexp.Text(Sexp.Element(parts, 5)),
                        };
                    }
                    break;
                case "point-surface" when parts.Count >= 6:
                    {
                        data[surfaceName] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["type"] = "point-surface",
                            ["reference frame"] = Sexp.Text(parts[1]),
                            ["point"] = Coordinate(Sexp.Element(parts, 2)),
                            ["snap type"] = Sexp.Text(Sexp.Element(parts, 4)),
                        };
                    }
                    break;
            }
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["surfaces"] = data };
    }

    /// <summary>
    /// A surface point or direction vector. Fluent writes these as
    /// <c>'((x . (y . (z . ()))))</c>, so the quote form is unwrapped and the
    /// nested dotted list flattened.
    /// </summary>
    private static List<object> Coordinate(object? value)
    {
        return Sexp.Flatten(Sexp.Unquote(value)).Select(item => (object)Sexp.Text(item)).ToList();
    }

    /// <summary>A flat list of scalars, e.g. a direction vector.</summary>
    private static List<object> PointList(object? value)
    {
        return Sexp.Flatten(Sexp.Unquote(value)).Select(item => (object)Sexp.Text(item)).ToList();
    }

    // ---------------------------------------------------------- graphics items

    private static Dictionary<string, object> ReadGraphics(CaseTexts texts, string graphicsType)
    {
        var cortex = texts.Cortex;
        var idToName = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupsRoot = Sexp.ParseDocumentFirst(Block(cortex, @"(\(surfaces/groups.*)"));
        foreach (var surface in Sexp.Elements(Sexp.Element(groupsRoot, 1)))
        {
            var fields = Sexp.Elements(surface);
            if (fields.Count < 2)
            {
                continue;
            }

            idToName[Sexp.Text(Sexp.Element(Sexp.Element(fields[1], 0), 0))] = Sexp.Text(fields[0]);
        }

        var key = graphicsType == "xy_plot" ? "xy-plot" : graphicsType;
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var root = Sexp.ParseDocumentFirst(Block(texts.General, $@"(\(graphics/{Regex.Escape(key)}\s.*)"));

        foreach (var item in Sexp.Elements(Sexp.Element(root, 1)))
        {
            var fields = Sexp.Elements(item);
            if (fields.Count == 0)
            {
                continue;
            }

            var name = Sexp.Text(Sexp.ValueOf(fields[0]));
            var properties = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var property in fields.Skip(1))
            {
                var parts = Sexp.Elements(property);
                if (parts.Count == 0)
                {
                    continue;
                }

                var propertyName = Sexp.Text(parts[0]);
                switch (propertyName)
                {
                    case "surfaces-list":
                        properties[propertyName] = parts.Skip(1)
                            .Select(id => idToName.TryGetValue(Sexp.Text(id), out var n) ? n : Sexp.Text(id))
                            .ToList();
                        break;
                    case "edge-type" when parts.Count >= 2:
                        properties[propertyName] = Sexp.Text(parts[1]);
                        break;
                    case "coloring" when parts.Count >= 3:
                        properties[propertyName] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["type"] = Sexp.Text(parts[1]),
                            ["option"] = Sexp.Text(parts[2]),
                        };
                        break;
                    case "graphics-objects":
                        properties[propertyName] = parts.Skip(1).Select(item2 =>
                        {
                            var objectProperties = new Dictionary<string, object>(StringComparer.Ordinal);
                            foreach (var entry in Sexp.Elements(item2))
                            {
                                var entryParts = Sexp.Elements(entry);
                                if (entryParts.Count >= 3 && Sexp.IsDot(entryParts[1]))
                                {
                                    objectProperties[Sexp.Text(entryParts[0])] = Sexp.Text(entryParts[2]);
                                }
                            }
                            return (object)objectProperties;
                        }).ToList();
                        break;
                    case "range-option":
                        {
                            var options = new Dictionary<string, object>(StringComparer.Ordinal);
                            foreach (var option in parts.Skip(2))
                            {
                                var optionParts = Sexp.Elements(option);
                                if (optionParts.Count >= 3 && Sexp.IsDot(optionParts[1]))
                                {
                                    options[Sexp.Text(optionParts[0])] = Sexp.Text(optionParts[2]);
                                }
                            }
                            properties[propertyName] = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                // (range-option . (auto-range-on ...)): the value is
                                // the element after the '.' marker.
                                ["auto-range"] = LastSegment(Sexp.Text(Sexp.ValueAt(property, 1))),
                                ["options"] = options,
                            };
                        }
                        break;
                    case "scale":
                    case "vector-opt":
                    case "color-map":
                    case "log-scale":
                    case "auto-scale":
                    case "labels":
                        {
                            var values = new Dictionary<string, object>(StringComparer.Ordinal);
                            foreach (var value in parts.Skip(1))
                            {
                                var valueParts = Sexp.Elements(value);
                                if (valueParts.Count >= 3 && Sexp.IsDot(valueParts[1]))
                                {
                                    values[Sexp.Text(valueParts[0])] = Sexp.Text(valueParts[2]);
                                }
                            }
                            properties[propertyName] = values;
                        }
                        break;
                    default:
                        if (Sexp.IsBinding(property))
                        {
                            properties[propertyName] = Sexp.Text(Sexp.ValueOf(property));
                        }
                        break;
                }
            }

            data[name] = properties;
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { [key] = data };
    }

    /// <summary>The text after the last '-' of a hyphenated Scheme token.</summary>
    private static string LastSegment(string value)
    {
        var index = value.LastIndexOf('-');
        return index >= 0 ? value.Substring(index + 1) : value;
    }

    // --------------------------------------------------------------- parameters

    private static Dictionary<string, object> ReadParameters(CaseTexts texts)
    {
        var input = new Dictionary<string, object>(StringComparer.Ordinal);
        var namedExpressions = ReadNamedExpressions(texts)["named-expressions"] as Dictionary<string, object>
                               ?? new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var entry in namedExpressions)
        {
            var name = entry.Key;
            var value = entry.Value;
            if (value is Dictionary<string, object> properties
                && Sexp.Text(properties.GetValueOrDefault("input-parameter")) == "#t")
            {
                input[name] = value;
            }
        }

        var output = new Dictionary<string, object>(StringComparer.Ordinal);
        var root = Sexp.ParseDocumentFirst(Block(texts.General, @"(\(parameters/output-parameters.*)"));
        foreach (var entry in Sexp.Elements(Sexp.Element(root, 1)))
        {
            var fields = Sexp.Elements(entry);
            if (fields.Count < 2)
            {
                continue;
            }

            var name = Sexp.Text(fields[0]);
            var properties = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var value in Sexp.Elements(fields[1]))
            {
                var parts = Sexp.Elements(value);
                if (parts.Count == 0)
                {
                    continue;
                }

                var propertyName = Sexp.Text(parts[0]);
                switch (propertyName)
                {
                    case "name":
                        properties["name"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["value"] = Sexp.Text(Sexp.Element(Sexp.Element(parts, 1), 1)),
                            ["type"] = Sexp.Text(Sexp.Element(Sexp.Element(parts, 2), 1)),
                        };
                        break;
                    case "settings":
                        {
                            var settings = Sexp.Elements(Sexp.Element(parts, 1));
                            properties["settings"] = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                [Sexp.Text(Sexp.Element(settings, 0))] = Sexp.Text(Sexp.Element(settings, 1)),
                            };
                        }
                        break;
                    case "units-quantity":
                        properties["units-quantity"] = parts.Skip(1).Select(Sexp.Text).ToList();
                        break;
                    default:
                        if (Sexp.IsBinding(value))
                        {
                            properties[propertyName] = Sexp.Text(Sexp.ValueOf(value));
                        }
                        break;
                }
            }
            output[name] = properties;
        }

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["parameters"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["input"] = input,
                ["output"] = output,
            },
        };
    }
}
