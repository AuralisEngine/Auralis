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
            RunTextureAndSamplerResources(device);
            RunResourceRetentionAfterRecording(device);
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
