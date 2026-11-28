using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Direct3D12.Interop;
using Auralis.Graphics.Resources;
using TerraFX.Interop.DirectX;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>Native D3D12 feature and format capability queries.</summary>
[SupportedOSPlatform("windows10.0.10240")]
internal static unsafe class D3D12DeviceCapabilityQuery
{
    private const int EInvalidArg = unchecked((int)0x80070057);
    private static readonly GraphicsFormat[] MappableFormats =
    [
        GraphicsFormat.R8UNorm, GraphicsFormat.R8SNorm, GraphicsFormat.R8UInt, GraphicsFormat.R8SInt,
        GraphicsFormat.RG8UNorm, GraphicsFormat.RG8SNorm, GraphicsFormat.RG8UInt, GraphicsFormat.RG8SInt,
        GraphicsFormat.RGBA8UNorm, GraphicsFormat.RGBA8UNormSrgb, GraphicsFormat.RGBA8SNorm,
        GraphicsFormat.RGBA8UInt, GraphicsFormat.RGBA8SInt,
        GraphicsFormat.R10G10B10A2UNorm, GraphicsFormat.R10G10B10A2UInt, GraphicsFormat.R11G11B10Float,
        GraphicsFormat.BGRA8UNorm, GraphicsFormat.BGRA8UNormSrgb,
        GraphicsFormat.R16Float, GraphicsFormat.R16UNorm, GraphicsFormat.R16SNorm,
        GraphicsFormat.R16UInt, GraphicsFormat.R16SInt,
        GraphicsFormat.RG16Float, GraphicsFormat.RG16UNorm, GraphicsFormat.RG16SNorm,
        GraphicsFormat.RG16UInt, GraphicsFormat.RG16SInt,
        GraphicsFormat.RGBA16Float, GraphicsFormat.RGBA16UNorm, GraphicsFormat.RGBA16SNorm,
        GraphicsFormat.RGBA16UInt, GraphicsFormat.RGBA16SInt,
        GraphicsFormat.R32Float, GraphicsFormat.R32UInt, GraphicsFormat.R32SInt,
        GraphicsFormat.RG32Float, GraphicsFormat.RG32UInt, GraphicsFormat.RG32SInt,
        GraphicsFormat.RGB32Float, GraphicsFormat.RGBA32Float, GraphicsFormat.RGBA32UInt,
        GraphicsFormat.RGBA32SInt,
        GraphicsFormat.D16UNorm, GraphicsFormat.D24UNormS8UInt, GraphicsFormat.D32Float,
        GraphicsFormat.D32FloatS8UInt,
        GraphicsFormat.BC1UNorm, GraphicsFormat.BC1UNormSrgb,
        GraphicsFormat.BC2UNorm, GraphicsFormat.BC2UNormSrgb,
        GraphicsFormat.BC3UNorm, GraphicsFormat.BC3UNormSrgb,
        GraphicsFormat.BC4UNorm, GraphicsFormat.BC4SNorm,
        GraphicsFormat.BC5UNorm, GraphicsFormat.BC5SNorm,
        GraphicsFormat.BC6HUFloat, GraphicsFormat.BC6HSFloat,
        GraphicsFormat.BC7UNorm, GraphicsFormat.BC7UNormSrgb
    ];

    public static D3D12FeatureSnapshot QueryFeatures(ID3D12Device* device)
    {
        if (device == null)
            throw new ArgumentNullException(nameof(device));

        D3D12_FEATURE_DATA_D3D12_OPTIONS options = default;
        QueryRequired(device, D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS, ref options,
            "ID3D12Device::CheckFeatureSupport(D3D12_OPTIONS)");

        D3D12_FEATURE_DATA_D3D12_OPTIONS2 options2 = default;
        bool hasOptions2 = TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS2, ref options2,
            "ID3D12Device::CheckFeatureSupport(D3D12_OPTIONS2)");

        D3D12_FEATURE_DATA_D3D12_OPTIONS3 options3 = default;
        bool hasOptions3 = TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS3, ref options3,
            "ID3D12Device::CheckFeatureSupport(D3D12_OPTIONS3)");

        D3D12_FEATURE_DATA_D3D12_OPTIONS5 options5 = default;
        bool hasOptions5 = TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS5, ref options5,
            "ID3D12Device::CheckFeatureSupport(D3D12_OPTIONS5)");

        D3D12_FEATURE_DATA_D3D12_OPTIONS6 options6 = default;
        bool hasOptions6 = TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS6, ref options6,
            "ID3D12Device::CheckFeatureSupport(D3D12_OPTIONS6)");

        D3D12_FEATURE_DATA_D3D12_OPTIONS7 options7 = default;
        bool hasOptions7 = TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS7, ref options7,
            "ID3D12Device::CheckFeatureSupport(D3D12_OPTIONS7)");

        D3D12_FEATURE_DATA_GPU_VIRTUAL_ADDRESS_SUPPORT virtualAddress = default;
        bool hasVirtualAddress = TryQuery(device,
            D3D12_FEATURE.D3D12_FEATURE_GPU_VIRTUAL_ADDRESS_SUPPORT,
            ref virtualAddress,
            "ID3D12Device::CheckFeatureSupport(GPU_VIRTUAL_ADDRESS_SUPPORT)");

        D3D12_FEATURE_DATA_FEATURE_LEVELS featureLevels = default;
        D3D_FEATURE_LEVEL* requestedLevels = stackalloc D3D_FEATURE_LEVEL[]
        {
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_12_0,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_12_1,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_12_2
        };
        featureLevels.NumFeatureLevels = 5;
        featureLevels.pFeatureLevelsRequested = requestedLevels;

        uint highestFeatureLevel = (uint)D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0;
        if (TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_FEATURE_LEVELS, ref featureLevels,
                "ID3D12Device::CheckFeatureSupport(FEATURE_LEVELS)"))
        {
            highestFeatureLevel = (uint)featureLevels.MaxSupportedFeatureLevel;
        }

        D3D12_FEATURE_DATA_SHADER_MODEL shaderModel = new()
        {
            HighestShaderModel = D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_0
        };
        if (!TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_SHADER_MODEL, ref shaderModel,
                "ID3D12Device::CheckFeatureSupport(SHADER_MODEL)"))
        {
            shaderModel = new D3D12_FEATURE_DATA_SHADER_MODEL
            {
                HighestShaderModel = D3D_SHADER_MODEL.D3D_SHADER_MODEL_5_1
            };
            TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_SHADER_MODEL, ref shaderModel,
                "ID3D12Device::CheckFeatureSupport(SHADER_MODEL 5_1)");
        }

        D3D12_FEATURE_DATA_ROOT_SIGNATURE rootSignature = new()
        {
            HighestVersion = D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1_2
        };
        if (!TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_ROOT_SIGNATURE, ref rootSignature,
                "ID3D12Device::CheckFeatureSupport(ROOT_SIGNATURE)"))
        {
            rootSignature = new D3D12_FEATURE_DATA_ROOT_SIGNATURE
            {
                HighestVersion = D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1_0
            };
            TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_ROOT_SIGNATURE, ref rootSignature,
                "ID3D12Device::CheckFeatureSupport(ROOT_SIGNATURE 1_0)");
        }

        return new D3D12FeatureSnapshot(
            highestFeatureLevel,
            (uint)options.ResourceBindingTier,
            (uint)options.TiledResourcesTier,
            (uint)options.ConservativeRasterizationTier,
            hasOptions5 ? (uint)options5.RaytracingTier : 0,
            hasOptions6 ? (uint)options6.VariableShadingRateTier : 0,
            hasOptions7 ? (uint)options7.MeshShaderTier : 0,
            options.OutputMergerLogicOp,
            hasOptions2 && options2.DepthBoundsTestSupported,
            hasOptions3 && options3.CopyQueueTimestampQueriesSupported,
            hasVirtualAddress ? virtualAddress.MaxGPUVirtualAddressBitsPerResource : 0,
            (uint)shaderModel.HighestShaderModel,
            (uint)rootSignature.HighestVersion);
    }

    public static GraphicsDeviceCapabilities QueryCapabilities(
        ID3D12Device* device,
        in D3D12FeatureSnapshot features)
    {
        if (device == null)
            throw new ArgumentNullException(nameof(device));

        List<GraphicsFormatCapabilities> formats = new(MappableFormats.Length);
        foreach (GraphicsFormat format in MappableFormats)
        {
            if (!D3D12FormatMapper.TryGetDxgiFormat(format, out DXGI_FORMAT dxgiFormat))
                continue;

            D3D12_FEATURE_DATA_FORMAT_SUPPORT formatSupport = new()
            {
                Format = dxgiFormat
            };

            if (!TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_FORMAT_SUPPORT, ref formatSupport,
                    $"ID3D12Device::CheckFeatureSupport(FORMAT_SUPPORT, {format})"))
            {
                continue;
            }

            if (formatSupport.Support1 == 0 && formatSupport.Support2 == 0)
                continue;

            bool isAccelerationStructureVertex =
                features.RayTracingTier != 0 &&
                format == GraphicsFormat.RGB32Float;

            GraphicsFormatSupport support = D3D12FormatMapper.MapSupport(
                format,
                formatSupport.Support1,
                formatSupport.Support2,
                isAccelerationStructureVertex);

            GraphicsSampleCountFlags sampleCounts = QuerySampleCounts(device, dxgiFormat);
            if (sampleCounts == GraphicsSampleCountFlags.None || support == GraphicsFormatSupport.None)
                continue;

            formats.Add(new GraphicsFormatCapabilities(
                format,
                support,
                sampleCounts));
        }

        return new GraphicsDeviceCapabilities(
            features.SupportedFeatures,
            D3D12CapabilityMapper.QueueCapabilities,
            D3D12CapabilityMapper.CreateLimits(features),
            CollectionsMarshal.AsSpan(formats),
            features.SupportedDescriptorIndexing);
    }

    private static GraphicsSampleCountFlags QuerySampleCounts(
        ID3D12Device* device,
        DXGI_FORMAT format)
    {
        uint counts = 1;
        ReadOnlySpan<uint> candidates = [2, 4, 8, 16, 32, 64];

        foreach (uint sampleCount in candidates)
        {
            D3D12_FEATURE_DATA_MULTISAMPLE_QUALITY_LEVELS data = new()
            {
                Format = format,
                SampleCount = sampleCount,
                Flags = D3D12_MULTISAMPLE_QUALITY_LEVEL_FLAGS.D3D12_MULTISAMPLE_QUALITY_LEVELS_FLAG_NONE
            };

            if (TryQuery(device, D3D12_FEATURE.D3D12_FEATURE_MULTISAMPLE_QUALITY_LEVELS,
                    ref data, $"CheckFeatureSupport(MULTISAMPLE_QUALITY_LEVELS, {format}, {sampleCount})") &&
                data.NumQualityLevels != 0)
            {
                counts |= sampleCount;
            }
        }

        return D3D12FormatMapper.MapSampleCounts(counts);
    }

    private static void QueryRequired<T>(
        ID3D12Device* device,
        D3D12_FEATURE feature,
        ref T data,
        string operation)
        where T : unmanaged
    {
        if (!TryQuery(device, feature, ref data, operation))
            throw new GraphicsException($"The D3D12 device did not answer required capability query '{operation}'.");
    }

    private static bool TryQuery<T>(
        ID3D12Device* device,
        D3D12_FEATURE feature,
        ref T data,
        string operation)
        where T : unmanaged
    {
        T local = data;
        int result = device->CheckFeatureSupport(feature, &local, (uint)sizeof(T));
        if (result >= 0)
        {
            data = local;
            return true;
        }

        if (result == EInvalidArg)
            return false;

        D3D12HResult.ThrowIfFailed(result, operation);
        return false;
    }
}
