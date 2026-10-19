using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace QuickLook.Plugin.AFH5;

/// <summary>
/// Boundary reading: cell zones and face zones, the settings they carry, and the
/// grouping of those settings into output categories.
/// </summary>
public static partial class CaseReader
{
    /// <summary>
    /// Boundary type name to the class modelling its settings. Registration order
    /// is irrelevant because unknown types fall back to
    /// <see cref="NotImplementedBoundary"/>.
    /// </summary>
    private static readonly Dictionary<string, Type> BoundaryTypes = new(StringComparer.Ordinal)
    {
        ["fluid"] = typeof(Fluid),
        ["solid"] = typeof(Solid),
        ["velocity-inlet"] = typeof(VelocityInlet),
        ["mass-flow-inlet"] = typeof(MassFlowInlet),
        ["pressure-inlet"] = typeof(PressureInlet),
        ["pressure-far-field"] = typeof(PressureFarField),
        ["intake-fan"] = typeof(IntakeFan),
        ["inlet-vent"] = typeof(InletVent),
        ["pressure-outlet"] = typeof(PressureOutlet),
        ["mass-flow-outlet"] = typeof(MassFlowOutlet),
        ["outflow"] = typeof(Outflow),
        ["exhaust-fan"] = typeof(ExhaustFan),
        ["outlet-vent"] = typeof(OutletVent),
        ["wall"] = typeof(Wall),
        ["interior"] = typeof(Interior),
        ["porous-jump"] = typeof(PorousJump),
        ["fan"] = typeof(Fan),
        ["radiator"] = typeof(Radiator),
        ["interface"] = typeof(Interface),
        ["overset"] = typeof(Overset),
        ["symmetry"] = typeof(Symmetry),
        ["axis"] = typeof(Axis),
        ["periodic"] = typeof(Periodic),
    };

    private static IBoundary CreateBoundary(string type, string name, string id)
    {
        if (BoundaryTypes.TryGetValue(type, out var boundaryType))
        {
            return (IBoundary)Activator.CreateInstance(boundaryType, name, id)!;
        }

        return new NotImplementedBoundary(type, name, id);
    }

    /// <summary>Read every zone of the <c>Thread Variables</c> document.</summary>
    private static Dictionary<string, object> ReadBoundary(CaseTexts texts)
    {
        var turbModel = GetTurbModel(ParseCaseConfig(texts.General));
        var radModel = GetRadiationModel(ParseCaseConfig(texts.General));

        var data = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var zone in Sexp.ParseDocument(texts.Boundary))
        {
            // (index (id type name ...) (settings...))
            var meta = Sexp.Elements(Sexp.Element(zone, 1));
            if (meta.Count < 3)
            {
                continue;
            }

            var id = Sexp.Text(meta[0]);
            var type = Sexp.Text(meta[1]);
            var name = Sexp.Text(meta[2]);

            var boundary = CreateBoundary(type, name, id);
            var declared = BoundaryFields(boundary.GetType());

            foreach (var property in Sexp.Elements(Sexp.Element(zone, 2)))
            {
                var parts = Sexp.Elements(property);
                if (parts.Count < 2)
                {
                    continue;
                }

                var key = BoundaryNaming.Normalize(Sexp.Text(parts[0]));
                if (!declared.ContainsKey(key))
                {
                    continue;
                }

                boundary.Values[key] = ReadBoundaryValue(key, parts);
            }

            if (!data.TryGetValue(type, out var bucket))
            {
                bucket = new Dictionary<string, object>(StringComparer.Ordinal);
                data[type] = bucket;
            }
            ((Dictionary<string, object>)bucket)[name] = ToBoundaryDict(boundary, turbModel, radModel);
        }

        return new Dictionary<string, object>(StringComparer.Ordinal) { ["boundary"] = data };
    }

    /// <summary>Setting name to its output group, restricted to fields the type declares.</summary>
    private static Dictionary<string, string?> BoundaryFields(Type type)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = property.GetCustomAttribute<BoundaryFieldAttribute>();
            if (attribute is null)
            {
                continue;
            }

            result[attribute.Key ?? BoundaryNaming.ToSettingName(property.Name)] = Categorize(attribute.Group);
        }
        return result;
    }

    /// <summary>
    /// The output category of a field. Ungrouped fields are tagged either with no
    /// group at all or with "general", which is also the bucket name, so the two
    /// spellings are folded together here.
    /// </summary>
    private static string? Categorize(string? group)
    {
        return string.IsNullOrEmpty(group) || group == "general" ? null : group;
    }

    /// <summary>Convert one boundary setting to its output value shape.</summary>
    private static object ReadBoundaryValue(string key, List<object> parts)
    {
        // (key . expr): a single scalar binding. Checked before the "all scalars"
        // case because the '.' marker is itself a scalar.
        if (parts.Count == 3 && Sexp.IsDot(parts[1]))
        {
            return Sexp.Text(parts[2]);
        }

        // (key v1 v2 ...): several scalars.
        if (parts.Count >= 2 && parts.GetRange(1, parts.Count - 1).All(Sexp.IsScalar))
        {
            return string.Join(" ", parts.GetRange(1, parts.Count - 1).Select(Sexp.Text));
        }

        switch (key)
        {
            case "source_terms":
                return ReadSourceTerms(parts);
            case "shell_conduction":
                return ReadShellConduction(parts);
            default:
                {
                    // (key (constant . 0) (profile "" "")) and similar selections.
                    var selection = Sexp.Elements(parts[1]);
                    if (selection.Count >= 3 && Sexp.IsDot(selection[1]))
                    {
                        return $"{Sexp.Text(selection[0])}/{Sexp.Text(selection[2])}";
                    }

                    var values = parts.GetRange(1, parts.Count - 1)
                        .Select(item => SelectionExpression(item))
                        .ToList();
                    return values.Count == 1 ? values[0] : string.Join(" ", values);
                }
        }
    }

    private static object ReadSourceTerms(List<object> parts)
    {
        var value = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var sourceTerm in parts.Skip(1))
        {
            var fields = Sexp.Elements(sourceTerm);
            if (fields.Count != 2)
            {
                continue;
            }

            var equation = Sexp.Text(fields[0]);
            var sourceProperty = Sexp.Elements(fields[1]);
            if (sourceProperty.Count == 0)
            {
                continue;
            }

            // ((profile "udf" "name") ...) or ((constant . 0) ...)
            var selection = Sexp.Elements(sourceProperty[0]);
            if (selection.Count == 0)
            {
                continue;
            }

            var kind = Sexp.Text(selection[0]);
            if (kind == "profile" && selection.Count >= 3)
            {
                value[equation] = $"profile/{Sexp.Text(selection[1])}/{Sexp.Text(selection[2])}";
            }
            else if (selection.Count >= 2 && kind == "constant")
            {
                value[equation] = $"constant/{Sexp.Text(selection[1])}";
            }
        }
        return value;
    }

    private static object ReadShellConduction(List<object> parts)
    {
        var value = new Dictionary<string, object>(StringComparer.Ordinal);
        var index = 0;
        foreach (var shell in parts.Skip(1))
        {
            value[$"shell-{index}"] = Sexp.Elements(shell).ConvertAll(item => SelectionExpression(item));
            index++;
        }
        return value;
    }

    // ------------------------------------------------------- output conversion

    /// <summary>Category order used when grouping fields, matching declaration order.</summary>
    private static readonly string[] GroupOrder =
    [
        "general", "momentum", "thermal", "radiation", "reference-frame", "porous-zone",
    ];

    /// <summary>Map numeric codes to labels, apply the model-specific filters, then group.</summary>
    private static Dictionary<string, object> ToBoundaryDict(IBoundary boundary, string turbModel, string radModel)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var entry in boundary.Values)
        {
            data[entry.Key] = entry.Value;
        }

        MapConstants(data);
        data["id_"] = boundary.Id;

        FilterSources(data);
        FilterPorous(data, boundary);
        FilterVelocitySpec(data);
        FilterTurbulence(data, turbModel);
        FilterFlowSpec(data);
        FilterDirectionSpec(data, boundary);
        FilterReverseFlow(data);
        FilterRadiation(data, boundary, radModel);
        FilterThermalBC(data, boundary);

        // _group_by_category
        var fields = BoundaryFields(boundary.GetType());
        var result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["id_"] = boundary.Id,
        };

        foreach (var group in GroupOrder)
        {
            foreach (var field in fields)
            {
                var key = field.Key;
                var groupName = field.Value;
                if (groupName != (group == "general" ? null : group))
                {
                    continue;
                }

                if (key is "name" or "id_" || !data.TryGetValue(key, out var value))
                {
                    continue;
                }

                var bucket = group;
                if (!result.TryGetValue(bucket, out var existing))
                {
                    existing = new Dictionary<string, object>(StringComparer.Ordinal);
                    result[bucket] = existing;
                }
                ((Dictionary<string, object>)existing)[key] = value;
            }
        }

        return result;
    }

    private static void MapConstants(Dictionary<string, object> data)
    {
        foreach (var key in data.Keys.ToList())
        {
            if (BoundaryEnums.All.TryGetValue(key.ToUpperInvariant(), out var table)
                && data[key] is string code)
            {
                data[key] = BoundaryEnums.FromCode(table, code) ?? "unknown";
            }
        }
    }

    private static void FilterSources(Dictionary<string, object> data)
    {
        if (AsText(data, "sources") == "#f")
        {
            data.Remove("source_terms");
        }
    }

    private static void FilterPorous(Dictionary<string, object> data, IBoundary boundary)
    {
        if (AsText(data, "porous") == "#f")
        {
            RemoveGroup(data, boundary, "porous-zone");
        }
    }

    private static void FilterVelocitySpec(Dictionary<string, object> data)
    {
        switch (AsText(data, "velocity_spec"))
        {
            case "Magnitude and Direction":
                data.Remove("u");
                data.Remove("v");
                data.Remove("w");
                break;
            case "Components":
                data.Remove("ni");
                data.Remove("nj");
                data.Remove("nk");
                break;
            case "Magnitude, Normal to Boundary":
                foreach (var key in new[] { "coordinate_system", "ni", "nj", "nk", "u", "v", "w" })
                {
                    data.Remove(key);
                }
                break;
        }
    }

    private static void FilterTurbulence(Dictionary<string, object> data, string turbModel)
    {
        if (turbModel is "inviscid" or "lam")
        {
            foreach (var key in new[]
                     {
                         "ke_spec", "turb_intensity", "turb_length_scale",
                         "turb_hydraulic_diam", "turb_viscosity_ratio",
                         "rough_bc", "roughness_height", "roughness_const",
                     })
            {
                data.Remove(key);
            }
            return;
        }

        switch (AsText(data, "ke_spec"))
        {
            case "Intensity and Length Scale":
                data.Remove("turb_viscosity_ratio");
                data.Remove("turb_hydraulic_diam");
                break;
            case "Intensity and Viscosity Ratio":
                data.Remove("turb_length_scale");
                data.Remove("turb_hydraulic_diam");
                break;
            case "Intensity and Hydraulic Diameter":
                data.Remove("turb_length_scale");
                data.Remove("turb_viscosity_ratio");
                break;
        }
    }

    private static void FilterFlowSpec(Dictionary<string, object> data)
    {
        switch (AsText(data, "flow_spec"))
        {
            case "Mass Flow Rate":
                data.Remove("mass_flux");
                data.Remove("mass_flux_ave");
                break;
            case "Mass Flux":
                data.Remove("mass_flow");
                data.Remove("mass_flux_ave");
                break;
            case "Mass Flux with Average Mass Flux":
                data.Remove("mass_flow");
                break;
        }
    }

    private static void FilterDirectionSpec(Dictionary<string, object> data, IBoundary boundary)
    {
        // These zone types always keep the block, even when direction_spec is absent.
        var keep = AsText(data, "direction_spec") == "Direction Vector"
                   || boundary is PressureFarField or Fluid or Solid;

        if (!keep)
        {
            foreach (var key in new[] { "ni", "nj", "nk", "ai", "aj", "ak", "u", "v", "w", "coordinate_system" })
            {
                data.Remove(key);
            }
            return;
        }

        switch (AsText(data, "coordinate_system"))
        {
            case "Cartesian(X, Y, Z)":
                foreach (var key in new[] { "ai", "aj", "ak", "axis_direction_component" })
                {
                    data.Remove(key);
                }
                break;
            case "Cylindrical(Radial, Tangential, Axial)":
                foreach (var key in new[] { "u", "v", "w" })
                {
                    data.Remove(key);
                }
                break;
        }
    }

    private static void FilterReverseFlow(Dictionary<string, object> data)
    {
        if (AsText(data, "prevent_reverse_flow") != "#t")
        {
            return;
        }

        foreach (var key in new[]
                 {
                     "frame_of_reference", "direction_spec", "p_backflow_spec_gen",
                     "ke_spec", "turb_intensity", "turb_length_scale", "turb_hydraulic_diam",
                     "turb_viscosity_ratio", "t0",
                 })
        {
            data.Remove(key);
        }
    }

    private static void FilterRadiation(Dictionary<string, object> data, IBoundary boundary, string radModel)
    {
        if (radModel != "off")
        {
            if (AsText(data, "t_b_b_spec") == "Boundary Temperature")
            {
                data.Remove("t_b_b");
            }

            if (AsText(data, "radiation_bc") == "(Semi-)Transparent")
            {
                data.Remove("in_emiss");
            }
            return;
        }

        data.Remove("radiating");
        RemoveGroup(data, boundary, "radiation");
    }

    private static void FilterThermalBC(Dictionary<string, object> data, IBoundary boundary)
    {
        switch (AsText(data, "thermal_bc"))
        {
            case "Temperature":
                Remove(data, "q", "h", "tinf", "ex_emiss", "trad");
                break;
            case "Heat Flux":
                Remove(data, "t", "h", "tinf", "ex_emiss", "trad");
                break;
            case "Convection":
                Remove(data, "q", "t", "ex_emiss", "trad");
                break;
            case "Coupled":
            case "via System Coupling":
                Remove(data, "q", "t", "h", "tinf", "ex_emiss", "trad");
                break;
            case "Radiation":
                Remove(data, "q", "t", "h", "tinf");
                break;
            case "Mixed":
                Remove(data, "q", "t");
                break;
        }

        if (AsText(data, "planar_conduction") == "#f")
        {
            data.Remove("shell_conduction");
        }
        else
        {
            data.Remove("d");
            data.Remove("q_dot");
            if (boundary is Wall)
            {
                data.Remove("material");
            }
        }
    }

    /// <summary>Drops every setting that belongs to one output category.</summary>
    private static void RemoveGroup(Dictionary<string, object> data, IBoundary boundary, string group)
    {
        foreach (var key in BoundaryFields(boundary.GetType())
                     .Where(pair => pair.Value == group)
                     .Select(pair => pair.Key))
        {
            data.Remove(key);
        }
    }

    private static void Remove(Dictionary<string, object> data, params string[] keys)
    {
        foreach (var key in keys)
        {
            data.Remove(key);
        }
    }

    private static string AsText(Dictionary<string, object> data, string key)
    {
        return data.TryGetValue(key, out var value) ? value as string ?? Sexp.Text(value) : string.Empty;
    }
}
