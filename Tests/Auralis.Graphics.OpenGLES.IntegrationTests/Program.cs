using System.Text;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.OpenGL.TestSupport;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;

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
