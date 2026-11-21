using System.Text;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.OpenGL.TestSupport;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.OpenGLES.IntegrationTests;

internal static class Program
{
    private const string RunVariable = "AURALIS_RUN_OPENGLES_HARDWARE_TESTS";

    private static async Task<int> Main()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RunVariable), "1", StringComparison.Ordinal))
        {
            Console.WriteLine($"SKIP: set {RunVariable}=1 to execute tests against an actual OpenGL ES 3.1+ context.");
            return 77;
        }

        GlfwOpenGLESContextProvider context;
        try
        {
            context = GlfwOpenGLESContextProvider.Create();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"SKIP: GLFW could not create a host OpenGL ES context: {exception.Message}");
            return 77;
        }

        IGraphicsDevice? device = null;
        try
        {
            GraphicsBackendProviderRegistry registry = new();
            registry.Register(new OpenGLESGraphicsBackendProvider(context));

            device = await GraphicsDeviceFactory.CreateAsync(
                registry,
                new GraphicsDeviceOptions
                {
                    Backend = GraphicsBackend.OpenGLES,
                    RequiredQueues = GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute
                });

            Console.WriteLine($"OpenGL ES driver: {device.Info.Name}; API: {device.Info.ApiVersion}; driver: {device.Info.DriverVersion}");

            RunBufferCopyAndFence(device);
            RunComputeDispatch(device);
            RunComputeDescriptorTypesAndSnapshots(device, context);
            RunComputePipelineLayoutSwitch(device);
            RunTextureAndSamplerResources(device);
            RunResourceRetentionAfterRecording(device);
            RunReusableCommandListAfterOwnerDisposal(device);
            RunOffscreenGraphicsPipeline(device, context);

            Console.WriteLine("PASS: all opted-in OpenGL ES hardware integration tests completed.");
            return 0;
        }
        catch (GraphicsBackendUnavailableException exception)
        {
            Console.WriteLine($"SKIP: the created context does not meet the OpenGL ES backend requirements: {exception.Message}");
            return 77;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: OpenGL ES hardware integration failed: {exception}");
            return 1;
        }
        finally
        {
            if (device is not null)
                device.Dispose();
            else
                context.Dispose();
        }
    }

    private static void RunBufferCopyAndFence(IGraphicsDevice device)
    {
        const int size = 1024;
        using IGraphicsBuffer source = device.CreateBuffer(new GraphicsBufferDescription(
            size,
            GraphicsBufferUsage.CopySource | GraphicsBufferUsage.CopyDestination,
            GraphicsMemoryAccess.CpuReadWrite));
        using IGraphicsBuffer destination = device.CreateBuffer(new GraphicsBufferDescription(
            size,
            GraphicsBufferUsage.CopySource | GraphicsBufferUsage.CopyDestination,
            GraphicsMemoryAccess.CpuReadWrite));

        Memory<byte> writeMapping = source.Map(GraphicsMapMode.Write);
        Span<byte> bytes = writeMapping.Span;
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)((i * 37 + 11) & 0xFF);
        source.Unmap();

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Copy);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();
        commandList.CopyBuffer(source, 0, destination, 0, size);
        commandList.End();
        queue.Submit([commandList], fence, 1);

        if (!fence.Wait(1, TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The GLES buffer-copy fence did not complete within 10 seconds.");

        Memory<byte> readback = destination.Map(GraphicsMapMode.Read);
        for (int i = 0; i < size; i++)
        {
            byte expected = (byte)((i * 37 + 11) & 0xFF);
            if (readback.Span[i] != expected)
                throw new InvalidOperationException($"GLES buffer-copy mismatch at byte {i}.");
        }
        destination.Unmap();
        Console.WriteLine("PASS: GLES buffer allocation, map/unmap, copy, and GPU fence.");
    }


    private static void RunTextureAndSamplerResources(IGraphicsDevice device)
    {
        using IGraphicsTexture texture2D = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            64, 64, 1, 1, 1, 1, GraphicsTextureUsage.Sampled));
        using IGraphicsTexture textureArray = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            64, 64, 1, 1, 4, 1, GraphicsTextureUsage.Sampled));
        using IGraphicsTexture texture3D = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture3D, GraphicsFormat.RGBA8UNorm,
            32, 32, 8, 1, 1, 1, GraphicsTextureUsage.Sampled));
        using IGraphicsTexture cube = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Cube, GraphicsFormat.RGBA8UNorm,
            32, 32, 1, 1, 6, 1, GraphicsTextureUsage.Sampled));
        using IGraphicsSampler sampler = device.CreateSampler(new GraphicsSamplerDescription(
            GraphicsFilter.Linear, GraphicsFilter.Linear, GraphicsFilter.Linear,
            GraphicsAddressMode.ClampToEdge, GraphicsAddressMode.ClampToEdge, GraphicsAddressMode.ClampToEdge,
            0, 16));

        Console.WriteLine("PASS: GLES 2D, 2D-array, 3D and cube texture allocation plus sampler creation/disposal.");
    }

    private static void RunResourceRetentionAfterRecording(IGraphicsDevice device)
    {
        IGraphicsBuffer source = device.CreateBuffer(new GraphicsBufferDescription(
            256,
            GraphicsBufferUsage.CopySource | GraphicsBufferUsage.CopyDestination,
            GraphicsMemoryAccess.GpuOnly));
        IGraphicsBuffer destination = device.CreateBuffer(new GraphicsBufferDescription(
            256,
            GraphicsBufferUsage.CopySource | GraphicsBufferUsage.CopyDestination,
            GraphicsMemoryAccess.GpuOnly));

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Copy);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();
        commandList.CopyBuffer(source, 0, destination, 0, 256);
        commandList.End();

        // Public disposal must not delete either native buffer while the executable command list retains it.
        source.Dispose();
        destination.Dispose();

        queue.Submit([commandList], fence, 1);
        if (!fence.Wait(1, TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The GLES resource-retention fence did not complete within 10 seconds.");

        Console.WriteLine("PASS: GLES native buffers remain alive after public disposal while recorded work retains them.");
    }


    private static void RunReusableCommandListAfterOwnerDisposal(IGraphicsDevice device)
    {
        const int size = 4096;
        using IGraphicsBuffer source = device.CreateBuffer(new GraphicsBufferDescription(
            size,
            GraphicsBufferUsage.CopySource | GraphicsBufferUsage.CopyDestination,
            GraphicsMemoryAccess.CpuReadWrite));
        using IGraphicsBuffer destination = device.CreateBuffer(new GraphicsBufferDescription(
            size,
            GraphicsBufferUsage.CopySource | GraphicsBufferUsage.CopyDestination,
            GraphicsMemoryAccess.CpuReadWrite));

        Memory<byte> input = source.Map(GraphicsMapMode.Write, 0, size);
        for (int i = 0; i < input.Length; i++)
            input.Span[i] = (byte)((i * 29 + 3) & 0xFF);
        source.Unmap();

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Copy);
        using IGraphicsCommandList commandList = queue.CreateCommandList(GraphicsCommandListUsage.Reusable);
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();
        commandList.CopyBuffer(source, 0, destination, 0, size);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        if (!fence.Wait(1, TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The first GLES reusable-command-list submission did not complete.");

        // Creating a list polls the queue's completion sync so the reusable list becomes Executable.
        using (IGraphicsCommandList completionPoll = queue.CreateCommandList())
        {
        }

        source.Dispose();
        queue.Submit([commandList], fence, 2);
        if (!fence.Wait(2, TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The second GLES reusable-command-list submission did not complete.");

        using (IGraphicsCommandList completionPoll = queue.CreateCommandList())
        {
        }

        Memory<byte> actual = destination.Map(GraphicsMapMode.Read, 0, size);
        for (int i = 0; i < actual.Length; i++)
        {
            byte expected = (byte)((i * 29 + 3) & 0xFF);
            if (actual.Span[i] != expected)
                throw new InvalidOperationException(
                    $"GLES reusable-command-list copy mismatch at byte {i}: expected 0x{expected:X2}, received 0x{actual.Span[i]:X2}.");
        }
        destination.Unmap();

        Console.WriteLine("PASS: GLES reusable command list retains a disposed source buffer across repeated submissions and fence completion.");
    }

    private static void RunOffscreenGraphicsPipeline(IGraphicsDevice device, GlfwOpenGLESContextProvider context)
    {
        using IGraphicsTexture color = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            64, 64, 1, 1, 1, 1,
            GraphicsTextureUsage.ColorAttachment | GraphicsTextureUsage.Sampled |
            GraphicsTextureUsage.CopySource));
        using IGraphicsTexture copyDestination = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            64, 64, 1, 1, 1, 1,
            GraphicsTextureUsage.ColorAttachment | GraphicsTextureUsage.CopyDestination));
        using IGraphicsTexture depthStencil = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.D24UNormS8UInt,
            64, 64, 1, 1, 1, 1, GraphicsTextureUsage.DepthStencilAttachment));
        using IGraphicsTexture sampledTexture = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            32, 32, 1, 1, 1, 1, GraphicsTextureUsage.Sampled));
        using IGraphicsSampler sampler = device.CreateSampler(new GraphicsSamplerDescription(
            GraphicsFilter.Linear, GraphicsFilter.Linear, GraphicsFilter.Linear,
            GraphicsAddressMode.ClampToEdge, GraphicsAddressMode.ClampToEdge, GraphicsAddressMode.ClampToEdge,
            0, 8));

        using IGraphicsTextureView colorView = color.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        using IGraphicsTextureView depthView = depthStencil.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        using IGraphicsTextureView sampledView = sampledTexture.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        using IGraphicsBindingLayout sampledLayout = device.CreateBindingLayout(new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(0, GraphicsDescriptorType.CombinedTextureSampler, 1, GraphicsShaderStages.Fragment)
        ]));
        using IGraphicsBindingSet sampledSet = device.CreateBindingSet(sampledLayout);
        sampledSet.SetTexture(0, 0, sampledView);
        sampledSet.SetSampler(0, 0, sampler);
        IGraphicsBindingLayout[] bindingLayouts = [sampledLayout];
        using IGraphicsPipelineLayout pipelineLayout = device.CreatePipelineLayout(bindingLayouts);

        using IGraphicsShader vertexShader = device.CreateShader(new GraphicsShaderDescription
        {
            Stage = GraphicsShaderStage.Vertex,
            Format = GraphicsShaderFormat.GlslSource,
            Code = Encoding.UTF8.GetBytes(
                """
                #version 310 es
                const vec2 positions[3] = vec2[3](
                    vec2(-0.75, -0.75),
                    vec2( 0.75, -0.75),
                    vec2( 0.00,  0.75));
                void main()
                {
                    gl_Position = vec4(positions[gl_VertexID], 0.0, 1.0);
                }
                """)
        });
        using IGraphicsShader fragmentShader = device.CreateShader(new GraphicsShaderDescription
        {
            Stage = GraphicsShaderStage.Fragment,
            Format = GraphicsShaderFormat.GlslSource,
            Code = Encoding.UTF8.GetBytes(
                """
                #version 310 es
                precision highp float;
                layout(binding = 0) uniform highp sampler2D sourceTexture;
                layout(location = 0) out vec4 outputColor;
                void main()
                {
                    outputColor = texture(sourceTexture, vec2(0.5, 0.5));
                }
                """)
        });

        IGraphicsShader[] shaders = [vertexShader, fragmentShader];
        GraphicsFormat[] colorFormats = [GraphicsFormat.RGBA8UNorm];
        using IGraphicsRenderPipeline pipeline = device.CreateRenderPipeline(
            new GraphicsRenderPipelineDescription(
                pipelineLayout, shaders, GraphicsPrimitiveTopology.TriangleList,
                colorFormats, GraphicsFormat.D24UNormS8UInt));

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Graphics);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();
        IGraphicsTextureView[] colors = [colorView];

        commandList.Begin();
        commandList.SetRenderTargets(colors, depthView);
        commandList.SetViewport(new GraphicsViewport(0, 0, 64, 64));
        commandList.SetScissor(new GraphicsScissorRect(0, 0, 64, 64));
        commandList.ClearColor(0, new GraphicsClearColor(0, 0, 0, 1));
        commandList.ClearDepthStencil(1, 0xFF, clearDepth: true, clearStencil: true);
        commandList.SetRenderPipeline(pipeline);
        commandList.SetBindingSet(0, sampledSet);
        commandList.Draw(3);
        commandList.CopyTexture(color, copyDestination);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        if (!fence.Wait(1, TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The GLES offscreen rendering fence did not complete within 10 seconds.");

        OpenGLESGraphicsDevice glesDevice = (OpenGLESGraphicsDevice)device;
        context.MakeCurrent();
        try
        {
            GLEnum error = glesDevice.Api.GetError();
            if (error != GLEnum.NoError)
                throw new GraphicsException($"The GLES driver reported {error} after offscreen clear and draw execution.");
        }
        finally
        {
            context.ReleaseCurrent();
        }

        Console.WriteLine("PASS: GLES vertex/fragment compilation, graphics pipeline link, FBO attachments, viewport/scissor, color/depth/stencil clear, combined sampled-texture/sampler binding, draw, and validated color-texture blit copy.");
    }


    private static void RunComputeDescriptorTypesAndSnapshots(
        IGraphicsDevice device,
        GlfwOpenGLESContextProvider context)
    {
        // Two explicitly-cleared texels make the linear sampler's interpolation observable through SSBO readback.
        using IGraphicsTexture sampledTexture = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            2, 1, 1, 1, 1, 1,
            GraphicsTextureUsage.Sampled | GraphicsTextureUsage.ColorAttachment));
        using IGraphicsTextureView sampledView = sampledTexture.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        ClearTwoTexelTexture(device, sampledView);

        using IGraphicsTexture alternativeSampledTexture = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            2, 1, 1, 1, 1, 1,
            GraphicsTextureUsage.Sampled | GraphicsTextureUsage.ColorAttachment));
        using IGraphicsTextureView alternativeSampledView = alternativeSampledTexture.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        ClearTexture(device, alternativeSampledView, 2, 1, new GraphicsClearColor(0, 1, 0, 1));

        using IGraphicsTexture storageTexture = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            1, 1, 1, 1, 1, 1,
            GraphicsTextureUsage.Storage));
        uint originalTextureHandle = ((OpenGLESGraphicsTexture)sampledTexture).Handle;
        uint originalSamplerHandle;
        uint originalStorageImageHandle;
        using IGraphicsTextureView storageView = storageTexture.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        using IGraphicsTexture alternativeStorageTexture = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            1, 1, 1, 1, 1, 1,
            GraphicsTextureUsage.Storage));
        using IGraphicsTextureView alternativeStorageView = alternativeStorageTexture.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));

        using IGraphicsBuffer parameters = device.CreateBuffer(new GraphicsBufferDescription(
            4 * sizeof(uint), GraphicsBufferUsage.Constant, GraphicsMemoryAccess.CpuReadWrite));
        using IGraphicsBuffer alternativeParameters = device.CreateBuffer(new GraphicsBufferDescription(
            4 * sizeof(uint), GraphicsBufferUsage.Constant, GraphicsMemoryAccess.CpuReadWrite));
        using IGraphicsBuffer output = device.CreateBuffer(new GraphicsBufferDescription(
            sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.CpuReadWrite));
        using IGraphicsBuffer alternativeOutput = device.CreateBuffer(new GraphicsBufferDescription(
            sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.CpuReadWrite));
        using IGraphicsBuffer partialOutput = device.CreateBuffer(new GraphicsBufferDescription(
            sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.CpuReadWrite));
        using IGraphicsBuffer alternativePartialOutput = device.CreateBuffer(new GraphicsBufferDescription(
            sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.CpuReadWrite));

        WriteUInts(parameters, 7u, 0x00A11CEu, 0, 0);
        WriteUInts(alternativeParameters, 0x22u, 0xBADu, 0, 0);
        WriteUInts(output, 0xDEADBEEFu);
        WriteUInts(alternativeOutput, 0xDEADBEEFu);
        WriteUInts(partialOutput, 0xDEADBEEFu);
        WriteUInts(alternativePartialOutput, 0xDEADBEEFu);

        using IGraphicsSampler linearSampler = device.CreateSampler(new GraphicsSamplerDescription(
            GraphicsFilter.Linear, GraphicsFilter.Linear, GraphicsFilter.Linear,
            GraphicsAddressMode.ClampToEdge, GraphicsAddressMode.ClampToEdge, GraphicsAddressMode.ClampToEdge,
            0, 0));
        using IGraphicsSampler nearestSampler = device.CreateSampler(new GraphicsSamplerDescription(
            GraphicsFilter.Nearest, GraphicsFilter.Nearest, GraphicsFilter.Nearest,
            GraphicsAddressMode.ClampToEdge, GraphicsAddressMode.ClampToEdge, GraphicsAddressMode.ClampToEdge,
            0, 0));
        originalSamplerHandle = ((OpenGLESGraphicsSampler)linearSampler).Handle;
        originalStorageImageHandle = ((OpenGLESGraphicsTexture)storageTexture).Handle;

        using IGraphicsBindingLayout bindingLayout = device.CreateBindingLayout(new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(0, GraphicsDescriptorType.UniformBuffer, 1, GraphicsShaderStages.Compute),
            new GraphicsBindingLayoutEntry(1, GraphicsDescriptorType.StorageBuffer, 1, GraphicsShaderStages.Compute),
            new GraphicsBindingLayoutEntry(2, GraphicsDescriptorType.SampledTexture, 1, GraphicsShaderStages.Compute),
            new GraphicsBindingLayoutEntry(3, GraphicsDescriptorType.Sampler, 1, GraphicsShaderStages.Compute),
            new GraphicsBindingLayoutEntry(4, GraphicsDescriptorType.CombinedTextureSampler, 1, GraphicsShaderStages.Compute),
            new GraphicsBindingLayoutEntry(5, GraphicsDescriptorType.StorageTexture, 1, GraphicsShaderStages.Compute),
            new GraphicsBindingLayoutEntry(
                6, GraphicsDescriptorType.StorageBuffer, 2, GraphicsShaderStages.Compute,
                GraphicsBindingFlags.PartiallyBound)
        ]));
        using IGraphicsBindingSet bindingSet = device.CreateBindingSet(bindingLayout);
        bindingSet.SetBuffer(0, 0, parameters, 0, 4 * sizeof(uint));
        bindingSet.SetBuffer(1, 0, output, 0, sizeof(uint));
        bindingSet.SetTexture(2, 0, sampledView);
        bindingSet.SetSampler(3, 0, linearSampler);
        bindingSet.SetTexture(4, 0, sampledView);
        bindingSet.SetSampler(4, 0, linearSampler);
        bindingSet.SetTexture(5, 0, storageView);
        // Array element zero remains unbound; element one is the only populated element.
        bindingSet.SetBuffer(6, 1, partialOutput, 0, sizeof(uint));

        IGraphicsBindingLayout[] bindingLayouts = [bindingLayout];
        using IGraphicsPipelineLayout pipelineLayout = device.CreatePipelineLayout(bindingLayouts);
        using IGraphicsShader shader = device.CreateShader(new GraphicsShaderDescription
        {
            Stage = GraphicsShaderStage.Compute,
            Format = GraphicsShaderFormat.GlslSource,
            Code = Encoding.UTF8.GetBytes(
                """
                #version 310 es
                layout(local_size_x = 1) in;
                layout(std140, binding = 0) uniform Parameters { uvec4 values; } parameters;
                layout(std430, binding = 0) buffer Result { uint value; } resultBuffer;
                layout(binding = 0) uniform highp sampler2D sampledOnly;
                layout(binding = 2) uniform highp sampler2D sampledWithSampler;
                layout(rgba8, binding = 0) coherent uniform highp image2D storageOutput;
                layout(std430, binding = 1) buffer PartialResult { uint marker; } partialResults[2];

                void main()
                {
                    vec4 exactTexel = texelFetch(sampledOnly, ivec2(0, 0), 0);
                    vec4 filteredTexel = texture(sampledWithSampler, vec2(0.375, 0.5));
                    imageStore(storageOutput, ivec2(0, 0), filteredTexel);
                    memoryBarrierImage();
                    vec4 imageReadback = imageLoad(storageOutput, ivec2(0, 0));

                    resultBuffer.value =
                        (uint(round(exactTexel.r * 255.0)) << 24u) |
                        (uint(round(imageReadback.r * 255.0)) << 16u) |
                        (uint(round(imageReadback.b * 255.0)) << 8u) |
                        parameters.values.x;
                    partialResults[1].marker = parameters.values.y;
                }
                """)
        });
        using IGraphicsComputePipeline pipeline = device.CreateComputePipeline(
            new GraphicsComputePipelineDescription(pipelineLayout, shader));

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Compute);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();
        commandList.SetComputePipeline(pipeline);
        commandList.SetBindingSet(0, bindingSet);
        commandList.Dispatch(1);
        commandList.End();

        // Mutate every descriptor category after recording. The executable command list must keep
        // the original UBO, SSBOs, sampled view, sampler, storage image, and partially-bound slot snapshot.
        bindingSet.SetBuffer(0, 0, alternativeParameters, 0, 4 * sizeof(uint));
        bindingSet.SetBuffer(1, 0, alternativeOutput, 0, sizeof(uint));
        bindingSet.SetTexture(2, 0, alternativeSampledView);
        bindingSet.SetSampler(3, 0, nearestSampler);
        bindingSet.SetTexture(4, 0, alternativeSampledView);
        bindingSet.SetSampler(4, 0, nearestSampler);
        bindingSet.SetTexture(5, 0, alternativeStorageView);
        bindingSet.SetBuffer(6, 1, alternativePartialOutput, 0, sizeof(uint));
        bindingSet.Dispose();

        // Dispose the original texture, sampler, UBO and image owners before submission. The
        // recorded descriptor snapshot must still keep the native handles alive through execution.
        sampledTexture.Dispose();
        linearSampler.Dispose();
        parameters.Dispose();
        storageTexture.Dispose();

        OpenGLESGraphicsDevice glesDevice = (OpenGLESGraphicsDevice)device;
        using IGraphicsBuffer sentinelBufferResource = device.CreateBuffer(new GraphicsBufferDescription(
            sizeof(uint),
            GraphicsBufferUsage.Storage,
            GraphicsMemoryAccess.GpuOnly));
        uint sentinelBuffer = ((OpenGLESGraphicsBuffer)sentinelBufferResource).Handle;
        context.MakeCurrent();
        try
        {
            glesDevice.Api.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, sentinelBuffer);
            glesDevice.Api.GetInteger(GLEnum.ShaderStorageBufferBinding, 1, out int seededBinding);
            if (seededBinding != (int)sentinelBuffer)
                throw new GraphicsException("Could not seed the GLES partially-bound SSBO slot with the sentinel buffer.");
        }
        finally
        {
            context.ReleaseCurrent();
        }

        queue.Submit([commandList], fence, 1);
        if (!fence.Wait(1, TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The GLES full-descriptor integration fence did not complete within 10 seconds.");

        context.MakeCurrent();
        try
        {
            glesDevice.Api.GetInteger(GLEnum.ShaderStorageBufferBinding, 1, out int missingArrayElementBinding);
            if (missingArrayElementBinding != 0)
                throw new InvalidOperationException(
                    $"Partially-bound GLES SSBO array element zero was not explicitly unbound (binding={missingArrayElementBinding}).");
            glesDevice.Api.GetInteger(GLEnum.ImageBindingName, 0, out int imageBinding);
            if (imageBinding != (int)originalStorageImageHandle)
                throw new InvalidOperationException(
                    $"The storage-image descriptor snapshot bound {imageBinding}, expected {originalStorageImageHandle}.");
            glesDevice.Api.GetInteger(GLEnum.SamplerBinding, 1, out int separateSamplerBinding);
            if (separateSamplerBinding != (int)originalSamplerHandle)
                throw new InvalidOperationException(
                    $"The standalone sampler descriptor bound {separateSamplerBinding}, expected {originalSamplerHandle}.");
            glesDevice.Api.GetInteger(GLEnum.SamplerBinding, 2, out int samplerBinding);
            if (samplerBinding != (int)originalSamplerHandle)
                throw new InvalidOperationException(
                    $"The combined-sampler descriptor snapshot bound {samplerBinding}, expected {originalSamplerHandle}.");
            glesDevice.Api.ActiveTexture((TextureUnit)0x84C2);
            glesDevice.Api.GetInteger(GLEnum.TextureBinding2D, out int textureBinding);
            if (textureBinding != (int)originalTextureHandle)
                throw new InvalidOperationException(
                    $"The combined-texture descriptor snapshot bound {textureBinding}, expected {originalTextureHandle}.");
        }
        finally
        {
            context.ReleaseCurrent();
        }

        uint expectedResult = (0xFFu << 24) | (191u << 16) | (64u << 8) | 7u;
        uint actualResult = ReadUInt(output);
        if (actualResult != expectedResult)
            throw new InvalidOperationException(
                $"GLES descriptor snapshot result was 0x{actualResult:X8}, expected 0x{expectedResult:X8}.");
        AssertUInt(ReadUInt(partialOutput), 0x00A11CEu);
        AssertUInt(ReadUInt(alternativeOutput), 0xDEADBEEFu);
        AssertUInt(ReadUInt(alternativePartialOutput), 0xDEADBEEFu);

        Console.WriteLine(
            "PASS: GLES UBO, SSBO, sampled texture, sampler, combined texture/sampler, storage image, partially-bound SSBO array unbinding, descriptor-set mutation snapshots, texture/sampler disposal-after-recording, and GPU readback.");
    }

    private static void ClearTwoTexelTexture(IGraphicsDevice device, IGraphicsTextureView textureView) =>
        ClearTexture(
            device,
            textureView,
            2,
            1,
            new GraphicsClearColor(1, 0, 0, 1),
            new GraphicsClearColor(0, 0, 1, 1));

    private static void ClearTexture(
        IGraphicsDevice device,
        IGraphicsTextureView textureView,
        uint width,
        uint height,
        GraphicsClearColor firstColor,
        GraphicsClearColor? secondColor = null)
    {
        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Graphics);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();
        IGraphicsTextureView[] targets = [textureView];

        commandList.Begin();
        commandList.SetRenderTargets(targets);
        commandList.SetViewport(new GraphicsViewport(0, 0, width, height));
        if (secondColor is { } nextColor)
        {
            commandList.SetScissor(new GraphicsScissorRect(0, 0, 1, height));
            commandList.ClearColor(0, firstColor);
            commandList.SetScissor(new GraphicsScissorRect(1, 0, width - 1, height));
            commandList.ClearColor(0, nextColor);
        }
        else
        {
            commandList.SetScissor(new GraphicsScissorRect(0, 0, width, height));
            commandList.ClearColor(0, firstColor);
        }
        commandList.End();

        queue.Submit([commandList], fence, 1);
        if (!fence.Wait(1, TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The GLES texture-initialization fence did not complete within 10 seconds.");
    }

    private static void WriteUInts(IGraphicsBuffer buffer, params uint[] values)
    {
        Memory<byte> memory = buffer.Map(GraphicsMapMode.Write, 0, checked((ulong)(values.Length * sizeof(uint))));
        for (int i = 0; i < values.Length; i++)
            BitConverter.TryWriteBytes(memory.Span.Slice(i * sizeof(uint), sizeof(uint)), values[i]);
        buffer.Unmap();
    }


    private static void AssertUInt(uint actual, uint expected)
    {
        if (actual != expected)
            throw new InvalidOperationException(
                $"GPU readback was 0x{actual:X8}; expected 0x{expected:X8}.");
    }

    private static uint ReadUInt(IGraphicsBuffer buffer)
    {
        Memory<byte> memory = buffer.Map(GraphicsMapMode.Read, 0, sizeof(uint));
        uint value = BitConverter.ToUInt32(memory.Span);
        buffer.Unmap();
        return value;
    }


    private static void RunComputePipelineLayoutSwitch(IGraphicsDevice device)
    {
        using IGraphicsBuffer firstOutput = device.CreateBuffer(new GraphicsBufferDescription(
            sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.CpuReadWrite));
        using IGraphicsBuffer secondOutput = device.CreateBuffer(new GraphicsBufferDescription(
            sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.CpuReadWrite));

        using IGraphicsBindingLayout firstBindingLayout = device.CreateBindingLayout(
            new GraphicsBindingLayoutDescription(
            [
                new GraphicsBindingLayoutEntry(0, GraphicsDescriptorType.StorageBuffer, 1, GraphicsShaderStages.Compute)
            ]));
        using IGraphicsBindingLayout secondBindingLayout = device.CreateBindingLayout(
            new GraphicsBindingLayoutDescription(
            [
                new GraphicsBindingLayoutEntry(7, GraphicsDescriptorType.StorageBuffer, 1, GraphicsShaderStages.Compute)
            ]));
        using IGraphicsBindingSet firstSet = device.CreateBindingSet(firstBindingLayout);
        using IGraphicsBindingSet secondSet = device.CreateBindingSet(secondBindingLayout);
        firstSet.SetBuffer(0, 0, firstOutput, 0, sizeof(uint));
        secondSet.SetBuffer(7, 0, secondOutput, 0, sizeof(uint));

        IGraphicsBindingLayout[] firstLayouts = [firstBindingLayout];
        IGraphicsBindingLayout[] secondLayouts = [secondBindingLayout];
        using IGraphicsPipelineLayout firstPipelineLayout = device.CreatePipelineLayout(firstLayouts);
        using IGraphicsPipelineLayout secondPipelineLayout = device.CreatePipelineLayout(secondLayouts);

        using IGraphicsShader firstShader = device.CreateShader(new GraphicsShaderDescription
        {
            Stage = GraphicsShaderStage.Compute,
            Format = GraphicsShaderFormat.GlslSource,
            Code = Encoding.UTF8.GetBytes(
                """
                #version 310 es
                layout(local_size_x = 1) in;
                layout(std430, binding = 0) buffer FirstResult { uint value; } resultBuffer;
                void main() { resultBuffer.value = 0x13572468u; }
                """)
        });
        using IGraphicsShader secondShader = device.CreateShader(new GraphicsShaderDescription
        {
            Stage = GraphicsShaderStage.Compute,
            Format = GraphicsShaderFormat.GlslSource,
            Code = Encoding.UTF8.GetBytes(
                """
                #version 310 es
                layout(local_size_x = 1) in;
                layout(std430, binding = 0) buffer SecondResult { uint value; } resultBuffer;
                void main() { resultBuffer.value = 0x24681357u; }
                """)
        });
        using IGraphicsComputePipeline firstPipeline = device.CreateComputePipeline(
            new GraphicsComputePipelineDescription(firstPipelineLayout, firstShader));
        using IGraphicsComputePipeline secondPipeline = device.CreateComputePipeline(
            new GraphicsComputePipelineDescription(secondPipelineLayout, secondShader));

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Compute);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();
        commandList.SetComputePipeline(firstPipeline);
        commandList.SetBindingSet(0, firstSet);
        commandList.Dispatch(1);
        commandList.SetComputePipeline(secondPipeline);
        commandList.SetBindingSet(0, secondSet);
        commandList.Dispatch(1);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        if (!fence.Wait(1, TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The GLES pipeline-layout-switch fence did not complete within 10 seconds.");

        uint firstValue = ReadUInt(firstOutput);
        uint secondValue = ReadUInt(secondOutput);
        if (firstValue != 0x13572468u || secondValue != 0x24681357u)
            throw new InvalidOperationException(
                $"GLES pipeline-layout switch returned 0x{firstValue:X8}/0x{secondValue:X8} instead of 0x13572468/0x24681357.");

        Console.WriteLine("PASS: GLES compute pipeline switching between distinct descriptor layouts and GPU-visible SSBO results.");
    }

    private static void RunComputeDispatch(IGraphicsDevice device)
    {
        if (!device.Capabilities.Features.Supports(GraphicsFeature.ComputeShaders))
            throw new NotSupportedException("The actual GLES context did not expose compute shaders.");

        using IGraphicsBuffer output = device.CreateBuffer(new GraphicsBufferDescription(
            sizeof(uint),
            GraphicsBufferUsage.Storage,
            GraphicsMemoryAccess.GpuToCpu));

        using IGraphicsShader shader = device.CreateShader(new GraphicsShaderDescription
        {
            Stage = GraphicsShaderStage.Compute,
            Format = GraphicsShaderFormat.GlslSource,
            Code = Encoding.UTF8.GetBytes(
                """
                #version 310 es
                layout(local_size_x = 1) in;
                layout(std430, binding = 0) buffer Result { uint value; } resultBuffer;
                void main() { resultBuffer.value = 0xA17E51u; }
                """)
        });

        using IGraphicsBindingLayout bindingLayout = device.CreateBindingLayout(
            new GraphicsBindingLayoutDescription(
            [
                new GraphicsBindingLayoutEntry(
                    0,
                    GraphicsDescriptorType.StorageBuffer,
                    1,
                    GraphicsShaderStages.Compute)
            ]));
        using IGraphicsBindingSet bindingSet = device.CreateBindingSet(bindingLayout);
        bindingSet.SetBuffer(0, 0, output, 0, sizeof(uint));

        IGraphicsBindingLayout[] layouts = [bindingLayout];
        using IGraphicsPipelineLayout pipelineLayout = device.CreatePipelineLayout(layouts);
        using IGraphicsComputePipeline pipeline = device.CreateComputePipeline(
            new GraphicsComputePipelineDescription(pipelineLayout, shader));

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Compute);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();
        commandList.SetComputePipeline(pipeline);
        commandList.SetBindingSet(0, bindingSet);
        commandList.Dispatch(1);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        if (!fence.Wait(1, TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The GLES compute fence did not complete within 10 seconds.");

        Memory<byte> readback = output.Map(GraphicsMapMode.Read);
        uint actual = BitConverter.ToUInt32(readback.Span);
        output.Unmap();
        if (actual != 0xA17E51u)
            throw new InvalidOperationException($"GLES compute result was 0x{actual:X8}, expected 0x00A17E51.");

        Console.WriteLine("PASS: GLES GLSL ES compute compilation, pipeline, SSBO binding, dispatch, and readback.");
    }
}
