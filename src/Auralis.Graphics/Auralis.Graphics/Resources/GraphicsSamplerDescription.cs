namespace Auralis.Graphics.Resources;

public enum GraphicsFilter
{
    Nearest,
    Linear
}

public enum GraphicsAddressMode
{
    Repeat,
    MirroredRepeat,
    ClampToEdge,
    ClampToBorder,
    MirrorClampToEdge
}

public enum GraphicsCompareOperation
{
    Never,
    Less,
    Equal,
    LessEqual,
    Greater,
    NotEqual,
    GreaterEqual,
    Always
}

public enum GraphicsBorderColor
{
    FloatTransparentBlack,
    IntTransparentBlack,
    FloatOpaqueBlack,
    IntOpaqueBlack,
    FloatOpaqueWhite,
    IntOpaqueWhite
}

/// <summary>Backend-independent sampler behavior.</summary>
public readonly record struct GraphicsSamplerDescription(
    GraphicsFilter MinFilter,
    GraphicsFilter MagFilter,
    GraphicsFilter MipFilter,
    GraphicsAddressMode AddressU,
    GraphicsAddressMode AddressV,
    GraphicsAddressMode AddressW,
    float MinLod,
    float MaxLod,
    float LodBias = 0,
    float MaxAnisotropy = 1,
    bool ComparisonEnabled = false,
    GraphicsCompareOperation CompareOperation = GraphicsCompareOperation.Always,
    GraphicsBorderColor BorderColor = GraphicsBorderColor.FloatTransparentBlack)
{
    public void Validate()
    {
        if (!Enum.IsDefined(MinFilter) || !Enum.IsDefined(MagFilter) || !Enum.IsDefined(MipFilter))
            throw new ArgumentOutOfRangeException(nameof(MinFilter));
        if (!Enum.IsDefined(AddressU) || !Enum.IsDefined(AddressV) || !Enum.IsDefined(AddressW))
            throw new ArgumentOutOfRangeException(nameof(AddressU));
        if (!Enum.IsDefined(CompareOperation))
            throw new ArgumentOutOfRangeException(nameof(CompareOperation));
        if (!Enum.IsDefined(BorderColor))
            throw new ArgumentOutOfRangeException(nameof(BorderColor));

        if (!float.IsFinite(MinLod) || !float.IsFinite(MaxLod) || !float.IsFinite(LodBias) ||
            !float.IsFinite(MaxAnisotropy))
            throw new ArgumentException("Sampler numeric values must be finite.");

        if (MinLod > MaxLod)
            throw new ArgumentException("Minimum LOD cannot exceed maximum LOD.");

        if (MaxAnisotropy < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAnisotropy));
    }
}
