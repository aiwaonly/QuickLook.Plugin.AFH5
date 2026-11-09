using System;
using System.Collections.Generic;

namespace QuickLook.Plugin.AFH5;

/// <summary>
/// Marks a boundary setting as readable from <c>Thread Variables</c> and groups
/// it into one of the output categories (momentum / thermal / radiation / ...).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BoundaryFieldAttribute(string? group, string? key = null) : Attribute
{
    /// <summary>Output category; <c>null</c> means the "general" bucket.</summary>
    public string? Group { get; } = group;

    /// <summary>
    /// Explicit setting key, used when the property name cannot be mapped back to
    /// the Scheme name mechanically (e.g. <c>p_profile_multiplier</c>).
    /// </summary>
    public string? Key { get; } = key;
}

/// <summary>One boundary (cell zone or face zone) of a case file.</summary>
public interface IBoundary
{
    /// <summary>Fluent zone type, e.g. <c>velocity-inlet</c>.</summary>
    string Type { get; }

    /// <summary>Zone name as written in the case file.</summary>
    string Name { get; }

    /// <summary>Numeric zone id, kept as text so it round-trips unchanged.</summary>
    string Id { get; }

    /// <summary>
    /// Fluent setting name (already normalised by <see cref="BoundaryNaming.Normalize"/>)
    /// mapped to the raw value read from the case file.
    /// </summary>
    Dictionary<string, object> Values { get; }
}

/// <summary>Shared implementation for every boundary type.</summary>
public abstract class BoundaryBase(string name, string id) : IBoundary
{
    public abstract string Type { get; }

    public string Name { get; } = name;

    public string Id { get; } = id;

    public Dictionary<string, object> Values { get; } = [];

    public override string ToString() => $"{Type}: {Name} ({Id})";
}

/// <summary>
/// Name normalisation shared by the reader and the boundary model. Fluent writes
/// settings as Scheme symbols, so <c>-</c> and <c>/</c> become underscores and a
/// trailing <c>?</c> is dropped.
/// </summary>
public static class BoundaryNaming
{
    /// <summary>Normalise a Scheme setting name, e.g. <c>sources?</c> to <c>sources</c>.</summary>
    public static string Normalize(string schemeName)
    {
        return schemeName.Replace("-", "_").Replace("?", string.Empty).Replace("/", "_");
    }

    /// <summary>
    /// Convert a C# property name to the setting key the reader produces, so
    /// <c>AxisDirectionComponent</c> maps to <c>axis_direction_component</c> and
    /// <c>Direction1X</c> to <c>direction_1_x</c>.
    /// </summary>
    public static string ToSettingName(string propertyName)
    {
        var builder = new System.Text.StringBuilder(propertyName.Length + 4);
        for (var i = 0; i < propertyName.Length; i++)
        {
            var c = propertyName[i];
            if (char.IsUpper(c))
            {
                var previousIsLowerOrDigit = i > 0 && (char.IsLower(propertyName[i - 1]) || char.IsDigit(propertyName[i - 1]));
                var nextIsLower = i + 1 < propertyName.Length && char.IsLower(propertyName[i + 1]);
                if (i > 0 && (previousIsLowerOrDigit || nextIsLower))
                {
                    builder.Append('_');
                }
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }
}

/// <summary>
/// Fluent numeric code tables for boundary settings. Each table maps a raw code
/// from the case file to the label shown in the output.
/// </summary>
public static class BoundaryEnums
{
    // ----------------------------------------------------------------- momentum

    public static readonly Dictionary<string, string> VELOCITY_SPEC = new()
    {
        ["0"] = "Magnitude and Direction",
        ["1"] = "Components",
        ["2"] = "Magnitude, Normal to Boundary",
    };

    public static readonly Dictionary<string, string> FRAME_OF_REFERENCE = new()
    {
        ["0"] = "Absolute",
        ["1"] = "Ralative to Adjacent Cell Zone",
    };

    public static readonly Dictionary<string, string> DIRECTION_SPEC = new()
    {
        ["0"] = "Direction Vector",
        ["1"] = "Normal to Boundary",
        ["2"] = "From Neighboring Cell",
    };

    public static readonly Dictionary<string, string> COORDINATE_SYSTEM = new()
    {
        ["0"] = "Cartesian(X, Y, Z)",
        ["1"] = "Cylindrical(Radial, Tangential, Axial)",
        ["2"] = "Local Cylindrical(Radial, Tangential, Axial)",
        ["3"] = "Local Cylindrical Swirl",
    };

    public static readonly Dictionary<string, string> KE_SPEC = new()
    {
        ["1"] = "Intensity and Length Scale",
        ["2"] = "Intensity and Viscosity Ratio",
        ["3"] = "Intensity and Hydraulic Diameter",
    };

    public static readonly Dictionary<string, string> FLOW_SPEC = new()
    {
        ["0"] = "Mass Flow Rate",
        ["1"] = "Mass Flux",
        ["2"] = "Mass Flux with Average Mass Flux",
    };

    public static readonly Dictionary<string, string> P_BACKFLOW_SPEC_GEN = new()
    {
        ["0"] = "Total Pressure",
        ["1"] = "Static Pressure",
    };

    // ------------------------------------------------------------------ thermal

    public static readonly Dictionary<string, string> THERMAL_BC = new()
    {
        ["0"] = "Temperature",
        ["1"] = "Heat Flux",
        ["2"] = "Convection",
        ["3"] = "Coupled",
        ["4"] = "Radiation",
        ["5"] = "Mixed",
        ["6"] = "Network",
        ["7"] = "Skip",
        ["8"] = "via System Coupling",
    };

    public static readonly Dictionary<string, string> MOTION_BC = new()
    {
        ["0"] = "Stationary Wall",
        ["1"] = "Moving Wall",
    };

    public static readonly Dictionary<string, string> SHEAR_BC = new()
    {
        ["0"] = "No Slip",
        ["1"] = "Specified Shear",
        ["2"] = "Marangoni Stress",
        ["3"] = "Specularity Coefficient",
        ["4"] = "Finite Slip",
        ["5"] = "Partial Slip",
    };

    public static readonly Dictionary<string, string> ROUGH_BC = new()
    {
        ["0"] = "Standard",
        ["1"] = "High Roughness (Icing)",
    };

    // ---------------------------------------------------------------- radiation

    public static readonly Dictionary<string, string> RADIATION_BC = new()
    {
        ["0"] = "Gray",
        ["1"] = "Specular",
        ["2"] = "(Semi-)Transparent",
        ["3"] = "Opaque",
    };

    public static readonly Dictionary<string, string> T_B_B_SPEC = new()
    {
        ["0"] = "Boundary Temperature",
        ["1"] = "Specified External Temperature",
    };

    // --------------------------------------------------------------- porous zone

    public static readonly Dictionary<string, string> DIR_SPEC_COND = new()
    {
        ["0"] = "Cartesian",
        ["1"] = "Conical",
        ["2"] = "Curvilinear Coordinate System",
    };

    /// <summary>
    /// Every table keyed by the upper-cased setting name, so a setting such as
    /// <c>velocity_spec</c> can find its code table.
    /// </summary>
    public static readonly Dictionary<string, Dictionary<string, string>> All = new()
    {
        ["VELOCITY_SPEC"] = VELOCITY_SPEC,
        ["FRAME_OF_REFERENCE"] = FRAME_OF_REFERENCE,
        ["DIRECTION_SPEC"] = DIRECTION_SPEC,
        ["COORDINATE_SYSTEM"] = COORDINATE_SYSTEM,
        ["KE_SPEC"] = KE_SPEC,
        ["FLOW_SPEC"] = FLOW_SPEC,
        ["P_BACKFLOW_SPEC_GEN"] = P_BACKFLOW_SPEC_GEN,
        ["THERMAL_BC"] = THERMAL_BC,
        ["MOTION_BC"] = MOTION_BC,
        ["SHEAR_BC"] = SHEAR_BC,
        ["ROUGH_BC"] = ROUGH_BC,
        ["RADIATION_BC"] = RADIATION_BC,
        ["T_B_B_SPEC"] = T_B_B_SPEC,
        ["DIR_SPEC_COND"] = DIR_SPEC_COND,
    };

    /// <summary>The label for a code in <paramref name="table"/>, or null if unknown.</summary>
    public static string? FromCode(Dictionary<string, string> table, string code)
    {
        return table.TryGetValue(code, out var label) ? label : null;
    }
}


// ============================================================== cell zones

public sealed class Fluid(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "fluid";

    [BoundaryField("general")] public string? Material { get; set; }
    [BoundaryField("general")] public string? Sources { get; set; }
    [BoundaryField("general")] public string? SourceTerms { get; set; }
    [BoundaryField("general")] public string? Fixed { get; set; }
    [BoundaryField("general")] public string? MrfMotion { get; set; }
    [BoundaryField("general")] public string? MgridMotion { get; set; }
    [BoundaryField("general")] public string? SolidMotion { get; set; }
    [BoundaryField("general")] public string? Laminar { get; set; }
    [BoundaryField("general")] public string? Porous { get; set; }
    [BoundaryField("general")] public string? Fanzone { get; set; }
    [BoundaryField("general")] public string? Radiating { get; set; }

    [BoundaryField("reference-frame")] public string? XOrigin { get; set; }
    [BoundaryField("reference-frame")] public string? YOrigin { get; set; }
    [BoundaryField("reference-frame")] public string? ZOrigin { get; set; }
    [BoundaryField("reference-frame")] public string? AxisOriginComponent { get; set; }
    [BoundaryField("reference-frame")] public string? Ai { get; set; }
    [BoundaryField("reference-frame")] public string? Aj { get; set; }
    [BoundaryField("reference-frame")] public string? Ak { get; set; }
    [BoundaryField("reference-frame")] public string? AxisDirectionComponent { get; set; }

    [BoundaryField("porous-zone")] public string? DirSpecCond { get; set; }
    [BoundaryField("porous-zone")] public string? Direction1X { get; set; }
    [BoundaryField("porous-zone")] public string? Direction1Y { get; set; }
    [BoundaryField("porous-zone")] public string? Direction1Z { get; set; }
    [BoundaryField("porous-zone")] public string? Direction1Components { get; set; }
    [BoundaryField("porous-zone")] public string? Direction2X { get; set; }
    [BoundaryField("porous-zone")] public string? Direction2Y { get; set; }
    [BoundaryField("porous-zone")] public string? Direction2Z { get; set; }
    [BoundaryField("porous-zone")] public string? Direction2Components { get; set; }
    [BoundaryField("porous-zone")] public string? RelVelResistance { get; set; }
    [BoundaryField("porous-zone", "porous_r_1")] public string? PorousR1 { get; set; }
    [BoundaryField("porous-zone", "porous_r_2")] public string? PorousR2 { get; set; }
    [BoundaryField("porous-zone", "porous_r_3")] public string? PorousR3 { get; set; }
    [BoundaryField("porous-zone")] public string? ViscousResistanceComponents { get; set; }
    [BoundaryField("porous-zone")] public string? AltInertialForm { get; set; }
    [BoundaryField("porous-zone", "porous_c_1")] public string? PorousC1 { get; set; }
    [BoundaryField("porous-zone", "porous_c_2")] public string? PorousC2 { get; set; }
    [BoundaryField("porous-zone", "porous_c_3")] public string? PorousC3 { get; set; }
    [BoundaryField("porous-zone")] public string? InertialResistanceComponents { get; set; }
    [BoundaryField("porous-zone")] public string? C0 { get; set; }
    [BoundaryField("porous-zone")] public string? C1 { get; set; }
    [BoundaryField("porous-zone")] public string? PowerLawModelConstants { get; set; }
    [BoundaryField("porous-zone")] public string? Porosity { get; set; }
    [BoundaryField("porous-zone")] public string? ViscosityRatio { get; set; }
}

public sealed class Solid(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "solid";

    [BoundaryField("general")] public string? Material { get; set; }
    [BoundaryField("general")] public string? Sources { get; set; }
    [BoundaryField("general")] public string? SourceTerms { get; set; }
    [BoundaryField("general")] public string? Fixed { get; set; }
    [BoundaryField("general")] public string? SolidMotion { get; set; }
    [BoundaryField("general")] public string? Radiating { get; set; }

    [BoundaryField("reference-frame")] public string? XOrigin { get; set; }
    [BoundaryField("reference-frame")] public string? YOrigin { get; set; }
    [BoundaryField("reference-frame")] public string? ZOrigin { get; set; }
    [BoundaryField("reference-frame")] public string? AxisOriginComponent { get; set; }
    [BoundaryField("reference-frame")] public string? Ai { get; set; }
    [BoundaryField("reference-frame")] public string? Aj { get; set; }
    [BoundaryField("reference-frame")] public string? Ak { get; set; }
    [BoundaryField("reference-frame")] public string? AxisDirectionComponent { get; set; }
}

// ================================================================== inlets

public sealed class VelocityInlet(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "velocity-inlet";

    [BoundaryField("momentum")] public string? VelocitySpec { get; set; }
    [BoundaryField("momentum")] public string? FrameOfReference { get; set; }
    [BoundaryField("momentum")] public string? Vmag { get; set; }

    [BoundaryField("momentum")] public string? KeSpec { get; set; }
    [BoundaryField("momentum")] public string? TurbIntensity { get; set; }
    [BoundaryField("momentum")] public string? TurbLengthScale { get; set; }
    [BoundaryField("momentum")] public string? TurbHydraulicDiam { get; set; }
    [BoundaryField("momentum")] public string? TurbViscosityRatio { get; set; }

    [BoundaryField("momentum")] public string? CoordinateSystem { get; set; }
    [BoundaryField("momentum")] public string? Ni { get; set; }
    [BoundaryField("momentum")] public string? Nj { get; set; }
    [BoundaryField("momentum")] public string? Nk { get; set; }
    [BoundaryField("momentum")] public string? U { get; set; }
    [BoundaryField("momentum")] public string? V { get; set; }
    [BoundaryField("momentum")] public string? W { get; set; }

    [BoundaryField("thermal")] public string? T { get; set; }

    [BoundaryField("radiation")] public string? RadiationBc { get; set; }
    [BoundaryField("radiation")] public string? InEmiss { get; set; }
    [BoundaryField("radiation", "t_b_b_spec")] public string? TBBSpec { get; set; }
    [BoundaryField("radiation", "t_b_b")] public string? TBB { get; set; }
}

public sealed class MassFlowInlet(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "mass-flow-inlet";

    [BoundaryField("momentum")] public string? FrameOfReference { get; set; }
    [BoundaryField("momentum")] public string? FlowSpec { get; set; }
    [BoundaryField("momentum")] public string? MassFlow { get; set; }
    [BoundaryField("momentum")] public string? MassFlux { get; set; }
    [BoundaryField("momentum")] public string? MassFluxAve { get; set; }
    [BoundaryField("momentum")] public string? P { get; set; }

    [BoundaryField("momentum")] public string? KeSpec { get; set; }
    [BoundaryField("momentum")] public string? TurbIntensity { get; set; }
    [BoundaryField("momentum")] public string? TurbLengthScale { get; set; }
    [BoundaryField("momentum")] public string? TurbHydraulicDiam { get; set; }
    [BoundaryField("momentum")] public string? TurbViscosityRatio { get; set; }

    [BoundaryField("momentum")] public string? DirectionSpec { get; set; }
    [BoundaryField("momentum")] public string? CoordinateSystem { get; set; }
    [BoundaryField("momentum")] public string? Ni { get; set; }
    [BoundaryField("momentum")] public string? Nj { get; set; }
    [BoundaryField("momentum")] public string? Nk { get; set; }
    [BoundaryField("momentum")] public string? U { get; set; }
    [BoundaryField("momentum")] public string? V { get; set; }
    [BoundaryField("momentum")] public string? W { get; set; }

    [BoundaryField("thermal")] public string? T0 { get; set; }

    [BoundaryField("radiation")] public string? RadiationBc { get; set; }
    [BoundaryField("radiation")] public string? InEmiss { get; set; }
    [BoundaryField("radiation", "t_b_b_spec")] public string? TBBSpec { get; set; }
    [BoundaryField("radiation", "t_b_b")] public string? TBB { get; set; }
}

public sealed class PressureInlet(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "pressure-inlet";

    [BoundaryField("momentum")] public string? FrameOfReference { get; set; }
    [BoundaryField("momentum")] public string? P0 { get; set; }
    [BoundaryField("momentum")] public string? P { get; set; }

    [BoundaryField("momentum")] public string? DirectionSpec { get; set; }
    [BoundaryField("momentum")] public string? CoordinateSystem { get; set; }
    [BoundaryField("momentum")] public string? Ni { get; set; }
    [BoundaryField("momentum")] public string? Nj { get; set; }
    [BoundaryField("momentum")] public string? Nk { get; set; }
    [BoundaryField("momentum")] public string? U { get; set; }
    [BoundaryField("momentum")] public string? V { get; set; }
    [BoundaryField("momentum")] public string? W { get; set; }

    [BoundaryField("momentum")] public string? PreventReverseFlow { get; set; }
    [BoundaryField("momentum")] public string? KeSpec { get; set; }
    [BoundaryField("momentum")] public string? TurbIntensity { get; set; }
    [BoundaryField("momentum")] public string? TurbLengthScale { get; set; }
    [BoundaryField("momentum")] public string? TurbHydraulicDiam { get; set; }
    [BoundaryField("momentum")] public string? TurbViscosityRatio { get; set; }

    [BoundaryField("thermal")] public string? T0 { get; set; }

    [BoundaryField("radiation")] public string? RadiationBc { get; set; }
    [BoundaryField("radiation")] public string? InEmiss { get; set; }
    [BoundaryField("radiation", "t_b_b_spec")] public string? TBBSpec { get; set; }
    [BoundaryField("radiation", "t_b_b")] public string? TBB { get; set; }
}

public sealed class PressureFarField(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "pressure-far-field";

    [BoundaryField("momentum")] public string? P { get; set; }
    [BoundaryField("momentum")] public string? M { get; set; }
    [BoundaryField("momentum")] public string? CoordinateSystem { get; set; }
    [BoundaryField("momentum")] public string? Ni { get; set; }
    [BoundaryField("momentum")] public string? Nj { get; set; }
    [BoundaryField("momentum")] public string? Nk { get; set; }
    [BoundaryField("momentum")] public string? FlowDirectionComponent { get; set; }
    [BoundaryField("momentum")] public string? Ai { get; set; }
    [BoundaryField("momentum")] public string? Aj { get; set; }
    [BoundaryField("momentum")] public string? Ak { get; set; }
    [BoundaryField("momentum")] public string? AxisDirectionComponent { get; set; }

    [BoundaryField("momentum")] public string? KeSpec { get; set; }
    [BoundaryField("momentum")] public string? TurbIntensity { get; set; }
    [BoundaryField("momentum")] public string? TurbLengthScale { get; set; }
    [BoundaryField("momentum")] public string? TurbHydraulicDiam { get; set; }
    [BoundaryField("momentum")] public string? TurbViscosityRatio { get; set; }

    [BoundaryField("thermal")] public string? T { get; set; }

    [BoundaryField("radiation")] public string? RadiationBc { get; set; }
    [BoundaryField("radiation")] public string? InEmiss { get; set; }
    [BoundaryField("radiation", "t_b_b_spec")] public string? TBBSpec { get; set; }
    [BoundaryField("radiation", "t_b_b")] public string? TBB { get; set; }
}

public sealed class IntakeFan(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "intake-fan";
}

public sealed class InletVent(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "inlet-vent";
}

// ================================================================= outlets

public sealed class PressureOutlet(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "pressure-outlet";

    [BoundaryField("momentum")] public string? FrameOfReference { get; set; }
    [BoundaryField("momentum")] public string? P { get; set; }
    [BoundaryField("momentum", "p_profile_multiplier")] public string? PProfileMultiplier { get; set; }

    [BoundaryField("momentum")] public string? DirectionSpec { get; set; }
    [BoundaryField("momentum")] public string? CoordinateSystem { get; set; }
    [BoundaryField("momentum")] public string? Ni { get; set; }
    [BoundaryField("momentum")] public string? Nj { get; set; }
    [BoundaryField("momentum")] public string? Nk { get; set; }
    [BoundaryField("momentum")] public string? U { get; set; }
    [BoundaryField("momentum")] public string? V { get; set; }
    [BoundaryField("momentum")] public string? W { get; set; }

    [BoundaryField("momentum")] public string? PBackflowSpecGen { get; set; }

    [BoundaryField("momentum")] public string? PreventReverseFlow { get; set; }
    [BoundaryField("momentum")] public string? Radial { get; set; }
    [BoundaryField("momentum")] public string? AvgPressSpec { get; set; }
    [BoundaryField("momentum")] public string? TargetedMfBoundary { get; set; }

    [BoundaryField("momentum")] public string? KeSpec { get; set; }
    [BoundaryField("momentum")] public string? TurbIntensity { get; set; }
    [BoundaryField("momentum")] public string? TurbLengthScale { get; set; }
    [BoundaryField("momentum")] public string? TurbHydraulicDiam { get; set; }
    [BoundaryField("momentum")] public string? TurbViscosityRatio { get; set; }

    [BoundaryField("thermal")] public string? T0 { get; set; }

    [BoundaryField("radiation")] public string? RadiationBc { get; set; }
    [BoundaryField("radiation")] public string? InEmiss { get; set; }
    [BoundaryField("radiation", "t_b_b_spec")] public string? TBBSpec { get; set; }
    [BoundaryField("radiation", "t_b_b")] public string? TBB { get; set; }
}

public sealed class MassFlowOutlet(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "mass-flow-outlet";

    [BoundaryField("momentum")] public string? FlowSpec { get; set; }
    [BoundaryField("momentum")] public string? MassFlow { get; set; }
    [BoundaryField("momentum")] public string? MassFlux { get; set; }
    [BoundaryField("momentum")] public string? MassFluxAve { get; set; }
    [BoundaryField("momentum")] public string? FrameOfReference { get; set; }

    [BoundaryField("momentum")] public string? KeSpec { get; set; }
    [BoundaryField("momentum")] public string? TurbIntensity { get; set; }
    [BoundaryField("momentum")] public string? TurbLengthScale { get; set; }
    [BoundaryField("momentum")] public string? TurbHydraulicDiam { get; set; }
    [BoundaryField("momentum")] public string? TurbViscosityRatio { get; set; }

    [BoundaryField("radiation")] public string? RadiationBc { get; set; }
    [BoundaryField("radiation")] public string? InEmiss { get; set; }
    [BoundaryField("radiation", "t_b_b_spec")] public string? TBBSpec { get; set; }
    [BoundaryField("radiation", "t_b_b")] public string? TBB { get; set; }
}

public sealed class Outflow(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "outflow";

    [BoundaryField("momentum")] public string? FlowrateFrac { get; set; }

    [BoundaryField("radiation")] public string? RadiationBc { get; set; }
    [BoundaryField("radiation")] public string? InEmiss { get; set; }
    [BoundaryField("radiation", "t_b_b_spec")] public string? TBBSpec { get; set; }
    [BoundaryField("radiation", "t_b_b")] public string? TBB { get; set; }
}

public sealed class ExhaustFan(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "exhaust-fan";
}

public sealed class OutletVent(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "outlet-vent";
}

// =================================================================== walls

public sealed class Wall(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "wall";

    [BoundaryField("momentum")] public string? MotionBc { get; set; }
    [BoundaryField("momentum")] public string? ShearBc { get; set; }
    [BoundaryField("momentum")] public string? RoughBc { get; set; }
    [BoundaryField("momentum")] public string? Moving { get; set; }
    [BoundaryField("momentum")] public string? Relative { get; set; }
    [BoundaryField("momentum")] public string? RoughnessHeight { get; set; }
    [BoundaryField("momentum")] public string? RoughnessConst { get; set; }

    [BoundaryField("thermal")] public string? D { get; set; }
    [BoundaryField("thermal")] public string? QDot { get; set; }
    [BoundaryField("thermal")] public string? Material { get; set; }

    [BoundaryField("thermal")] public string? ThermalBc { get; set; }
    [BoundaryField("thermal")] public string? Q { get; set; }
    [BoundaryField("thermal")] public string? T { get; set; }
    [BoundaryField("thermal")] public string? H { get; set; }
    [BoundaryField("thermal")] public string? Tinf { get; set; }
    [BoundaryField("thermal")] public string? ExEmiss { get; set; }
    [BoundaryField("thermal")] public string? Trad { get; set; }

    [BoundaryField("thermal")] public string? PlanarConduction { get; set; }
    [BoundaryField("thermal")] public string? ShellConduction { get; set; }

    [BoundaryField("radiation")] public string? RadiationBc { get; set; }
    [BoundaryField("radiation")] public string? InEmiss { get; set; }
    [BoundaryField("radiation")] public string? BandDiffuseFrac { get; set; }
}

// ================================================================ internal

public sealed class Interior(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "interior";

    [BoundaryField(null)] public string? IsNotARansLesInterface { get; set; }
}

public sealed class PorousJump(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "porous-jump";

    [BoundaryField(null)] public string? Alpha { get; set; }
    [BoundaryField(null)] public string? Dm { get; set; }
    [BoundaryField(null)] public string? C2 { get; set; }
}

public sealed class Fan(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "fan";
}

public sealed class Radiator(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "radiator";
}

// =============================================================== interfaces

public sealed class Interface(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "interface";
}

public sealed class Overset(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "overset";
}

public sealed class Symmetry(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "symmetry";
}

public sealed class Axis(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "axis";
}

public sealed class Periodic(string name, string id) : BoundaryBase(name, id)
{
    public override string Type => "periodic";

    [BoundaryField(null)] public string? PJump { get; set; }
    [BoundaryField(null)] public string? XOrigin { get; set; }
    [BoundaryField(null)] public string? YOrigin { get; set; }
    [BoundaryField(null)] public string? ZOrigin { get; set; }
    [BoundaryField(null)] public string? ShiftX { get; set; }
    [BoundaryField(null)] public string? ShiftY { get; set; }
    [BoundaryField(null)] public string? ShiftZ { get; set; }
}

/// <summary>Fallback for zone types that are not modelled explicitly.</summary>
public sealed class NotImplementedBoundary(string type, string name, string id) : BoundaryBase(name, id)
{
    public override string Type { get; } = type;
}
