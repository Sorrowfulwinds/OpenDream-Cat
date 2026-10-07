using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.Maths;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace OpenDreamShared.Dream;

/// <summary>
///     An object describing type and vars so the client doesn't have to make a ShaderInstance for shaders with the same
///     params
/// </summary>
[Serializable]
[NetSerializable]
[ImplicitDataDefinitionForInheritors]
public partial record DreamFilter {
    private static readonly Dictionary<string, Type> FilterTypes = new() {
        ["alpha"] = typeof(DreamFilterAlpha),
        ["angular_blur"] = typeof(DreamFilterAngularBlur),
        ["bloom"] = typeof(DreamFilterBloom),
        ["blur"] = typeof(DreamFilterBlur),
        ["color"] = typeof(DreamFilterColor),
        ["displace"] = typeof(DreamFilterDisplace),
        ["drop_shadow"] = typeof(DreamFilterDropShadow),
        ["layer"] = typeof(DreamFilterLayer),
        ["motion_blur"] = typeof(DreamFilterMotionBlur),
        ["outline"] = typeof(DreamFilterOutline),
        ["radial_blur"] = typeof(DreamFilterRadialBlur),
        ["rays"] = typeof(DreamFilterRays),
        ["ripple"] = typeof(DreamFilterRipple),
        ["wave"] = typeof(DreamFilterWave),
        ["greyscale"] = typeof(DreamFilterGreyscale)
    };

    [ViewVariables(VVAccess.ReadOnly)] [DataField("name")]
    public string? FilterName;

    [ViewVariables(VVAccess.ReadOnly)] [DataField("type")]
    public string FilterType;

    /// <summary>
    ///     Indicates this filter was used in the last render cycle, for shader caching purposes
    /// </summary>
    public bool Used;

    public static IEnumerable<Type> AllTypes => FilterTypes.Values;

    public static Type? GetType(string filterType) {
        return FilterTypes.GetValueOrDefault(filterType);
    }

    /// <summary>
    ///     Calculate the size of the texture necessary to render this filter
    /// </summary>
    /// <param name="baseSize">The size of the object the filter is being applied to</param>
    /// <param name="textureSizeCallback">A callback that returns the size of a given render source</param>
    public Vector2i CalculateRequiredRenderSpace(Vector2i baseSize, Func<string, Vector2i> textureSizeCallback) {
        Vector2 requiredSpace = baseSize;

        // All the "* 2" in here is because everything is rendered in the center,
        // So every increase in size needs applied to both sides
        switch (this) {
            case DreamFilterAlpha alpha:
                requiredSpace += Vector2.Abs(new Vector2(alpha.X, alpha.Y)) * 2;

                if (!string.IsNullOrEmpty(alpha.RenderSource)) {
                    Vector2i textureSize = textureSizeCallback(alpha.RenderSource);
                    requiredSpace = Vector2.Max(requiredSpace, textureSize);
                } else if (alpha.Icon != 0) {
                    // TODO
                }

                break;
            case DreamFilterBlur blur:
                requiredSpace += new Vector2(blur.Size) * 2;
                break;
            case DreamFilterDropShadow dropShadow:
                if (dropShadow.Size - dropShadow.X > 0)
                    requiredSpace.X += (dropShadow.Size + dropShadow.X) * 2;

                if (dropShadow.Size - dropShadow.Y > 0)
                    requiredSpace.Y += (dropShadow.Size + dropShadow.Y) * 2;
                break;
            case DreamFilterOutline outline:
                requiredSpace += new Vector2(outline.Size) * 2;
                break;
        }

        return (Vector2i)requiredSpace;
    }
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterAlpha : DreamFilter {
    [ViewVariables] [DataField("flags")] public short Flags;
    [ViewVariables] [DataField("icon")] public int Icon; // Icon resource ID

    [ViewVariables] [DataField("render_source")]
    public string RenderSource = ""; // String that gets special processing in the render loop

    [ViewVariables] [DataField("x")] public float X;
    [ViewVariables] [DataField("y")] public float Y;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterAngularBlur : DreamFilter {
    [ViewVariables] [DataField("size")] public float Size = 1f;
    [ViewVariables] [DataField("x")] public float X;
    [ViewVariables] [DataField("y")] public float Y;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterBloom : DreamFilter {
    [ViewVariables] [DataField("alpha")] public float Alpha = 255f;
    [ViewVariables] [DataField("offset")] public float Offset = 1f;
    [ViewVariables] [DataField("size")] public float Size = 1f;

    [ViewVariables] [DataField("threshold")]
    public Color Threshold = Color.Black;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterBlur : DreamFilter {
    [ViewVariables] [DataField("size")] public float Size = 1f;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterColor : DreamFilter {
    [ViewVariables] [DataField("color", required: true)]
    public ColorMatrix Color;

    [ViewVariables] [DataField("space")] public float Space; // Default is FILTER_COLOR_RGB = 0
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterDisplace : DreamFilter {
    [ViewVariables] [DataField("icon")] public int Icon; // Icon resource ID

    [ViewVariables] [DataField("render_source")]
    public string RenderSource = ""; // String that will require special processing

    [ViewVariables] [DataField("size")] public float Size = 1f;
    [ViewVariables] [DataField("x")] public float X;
    [ViewVariables] [DataField("y")] public float Y;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterDropShadow : DreamFilter {
    [ViewVariables] [DataField("color")] public Color Color = Color.Black.WithAlpha(128);
    [ViewVariables] [DataField("offset")] public float Offset;
    [ViewVariables] [DataField("size")] public float Size = 1f;
    [ViewVariables] [DataField("x")] public float X = 1f;
    [ViewVariables] [DataField("y")] public float Y = -1f;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterLayer : DreamFilter {
    [ViewVariables] [DataField("blend_mode")]
    public float BlendMode;

    [ViewVariables] [DataField("color")]
    public Color
        Color = Color.Black
            .WithAlpha(128); // Shit needs to be string or color matrix, because of course one has to be special

    [ViewVariables] [DataField("flags")] public float Flags; // Default is FILTER_OVERLAY = 0
    [ViewVariables] [DataField("icon")] public int Icon; // Icon resource ID

    [ViewVariables] [DataField("render_source")]
    public string RenderSource = ""; // String that will require special processing

    [ViewVariables] [DataField("transform")]
    public Matrix3x2 Transform = Matrix3x2.Identity;

    [ViewVariables] [DataField("x")] public float X;
    [ViewVariables] [DataField("y")] public float Y;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterMotionBlur : DreamFilter {
    [ViewVariables] [DataField("x")] public float X;
    [ViewVariables] [DataField("y")] public float Y;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterOutline : DreamFilter {
    [ViewVariables] [DataField("color")] public Color Color = Color.Black;
    [ViewVariables] [DataField("flags")] public float Flags;
    [ViewVariables] [DataField("size")] public float Size = 1f;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterRadialBlur : DreamFilter {
    [ViewVariables] [DataField("size")] public float Size = 0.01f;
    [ViewVariables] [DataField("x")] public float X;
    [ViewVariables] [DataField("y")] public float Y;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterRays : DreamFilter {
    [ViewVariables] [DataField("color")] public Color Color = Color.White;
    [ViewVariables] [DataField("density")] public float Density = 10f;
    [ViewVariables] [DataField("factor")] public float Factor;
    [ViewVariables] [DataField("flags")] public float Flags = 3f; // Defaults to FILTER_OVERLAY | FILTER_UNDERLAY
    [ViewVariables] [DataField("offset")] public float Offset;
    [ViewVariables] [DataField("size")] public float Size = 16f; // Defaults to half tile width

    [ViewVariables] [DataField("threshold")]
    public float Threshold = 0.5f;

    [ViewVariables] [DataField("x")] public float X;
    [ViewVariables] [DataField("y")] public float Y;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterRipple : DreamFilter {
    [ViewVariables] [DataField("falloff")] public float Falloff = 1f;
    [ViewVariables] [DataField("flags")] public float Flags;
    [ViewVariables] [DataField("radius")] public float Radius;
    [ViewVariables] [DataField("repeat")] public float Repeat = 2f;
    [ViewVariables] [DataField("size")] public float Size = 1f;
    [ViewVariables] [DataField("x")] public float X;
    [ViewVariables] [DataField("y")] public float Y;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterWave : DreamFilter {
    [ViewVariables] [DataField("flags")] public float Flags;
    [ViewVariables] [DataField("offset")] public float Offset;
    [ViewVariables] [DataField("size")] public float Size = 1f;
    [ViewVariables] [DataField("x")] public float X;
    [ViewVariables] [DataField("y")] public float Y;
}

[Serializable]
[NetSerializable]
public sealed partial record DreamFilterGreyscale : DreamFilter;
