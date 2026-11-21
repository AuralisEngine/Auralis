using System.Buffers.Binary;
using System.Text;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.OpenGL.TestSupport;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;

namespace Auralis.Graphics.OpenGL.IntegrationTests;

internal static class Program
{
    private const string RunEnvironmentVariable = "AURALIS_RUN_OPENGL_HARDWARE_TESTS";
    private static readonly TimeSpan s_gpuTimeout = TimeSpan.FromSeconds(15);

    private static async Task<int> Main()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RunEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            Console.WriteLine($"SKIP: set {RunEnvironmentVariable}=1 to run tests against a real desktop OpenGL 4.5 driver.");
            return 77;
        }

        GlfwOpenGLHardwareSession session;
        try
        {
            session = await GlfwOpenGLHardwareSession.CreateAsync();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"SKIP: a usable GLFW/OpenGL 4.5 Core hardware context could not be created: {exception.Message}");
            return 77;
        }

        using (session)
        {
            Console.WriteLine($"OpenGL hardware test session: {session.Device.Info}");
            IReadOnlyList<Silk.NET.OpenGL.GLEnum> startupErrors = session.ContextProvider.DrainErrors();
            if (startupErrors.Count != 0)
            {
                Console.Error.WriteLine($"FAIL: driver context initialization left GL errors: {string.Join(", ", startupErrors)}");
                return 1;
            }

            int passed = 0;
            int skipped = 0;
            int failed = 0;

            Run("buffer copy after dispose-before-submit", session, CopyBufferAfterRecordDispose,
                ref passed, ref skipped, ref failed);
            Run("texture copy after dispose-before-submit", session, CopyTextureAfterRecordDispose,
                ref passed, ref skipped, ref failed);
            Run("buffer dispose after submission while GPU work is pending", session, CopyBufferAfterSubmitDispose,
                ref passed, ref skipped, ref failed);
            Run("partially-bound descriptor switching and indirect compute", session, PartiallyBoundIndirectCompute,
                ref passed, ref skipped, ref failed);
            Run("conventional direct and indirect graphics draws", session, ConventionalDirectAndIndirectDraw,
                ref passed, ref skipped, ref failed);
            Run("EXT mesh-shader direct and indirect draws", session, MeshShaderDirectAndIndirectDraw,
                ref passed, ref skipped, ref failed);
            Run("EXT task+mesh shader pipeline", session, TaskAndMeshShaderDraw,
                ref passed, ref skipped, ref failed);

            Console.WriteLine();
            Console.WriteLine($"Hardware integration result: {passed} passed, {skipped} skipped, {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
    }

    private static void Run(
        string name,
        GlfwOpenGLHardwareSession session,
        Action<GlfwOpenGLHardwareSession> test,
        ref int passed,
        ref int skipped,
        ref int failed)
    {
        try
        {
            test(session);
            IReadOnlyList<Silk.NET.OpenGL.GLEnum> errors = session.ContextProvider.DrainErrors();
            if (errors.Count != 0)
                throw new InvalidOperationException($"OpenGL reported errors after the test: {string.Join(", ", errors)}.");

            passed++;
            Console.WriteLine($"PASS: {name}");
        }
        catch (HardwareTestSkippedException exception)
        {
            skipped++;
            Console.WriteLine($"SKIP: {name} — {exception.Message}");
        }
        catch (Exception exception)
        {
            failed++;
            Console.Error.WriteLine($"FAIL: {name} — {exception}");
        }

    }

    private static void CopyBufferAfterRecordDispose(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        using IGraphicsBuffer source = CreateBuffer(
            device, 256, GraphicsBufferUsage.CopySource, GraphicsMemoryAccess.CpuReadWrite);
        using IGraphicsBuffer destination = CreateBuffer(
            device, 256, GraphicsBufferUsage.CopyDestination, GraphicsMemoryAccess.GpuToCpu);

        WritePattern(source, 256, seed: 17);

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Copy);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();
        commandList.CopyBuffer(source, 0, destination, 0, 256);
        commandList.End();

        // The source's public owner reference is closed before Submit. The recorded list must
        // retain its native GL buffer until its commands and reusable recording have released it.
        source.Dispose();

        queue.Submit([commandList], fence, 1);
        WaitForGpu(fence, commandList, 1);
        AssertPattern(ReadBuffer(destination, 256), seed: 17);
    }

    private static void CopyTextureAfterRecordDispose(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        RequireFormat(device, GraphicsFormat.RGBA8UNorm,
            GraphicsFormatSupport.CopySource | GraphicsFormatSupport.CopyDestination);

        using IGraphicsTexture source = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            32, 32, 1, 1, 1, 1,
            GraphicsTextureUsage.CopySource));
        using IGraphicsTexture destination = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            32, 32, 1, 1, 1, 1,
            GraphicsTextureUsage.CopyDestination));

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Copy);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();
        commandList.CopyTexture(source, destination);
        commandList.End();
        source.Dispose();

        queue.Submit([commandList], fence, 1);
        WaitForGpu(fence, commandList, 1);
    }

    private static void CopyBufferAfterSubmitDispose(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        const int size = 8 * 1024 * 1024;
        using IGraphicsBuffer source = CreateBuffer(
            device, size, GraphicsBufferUsage.CopySource, GraphicsMemoryAccess.CpuReadWrite);
        using IGraphicsBuffer destination = CreateBuffer(
            device, size, GraphicsBufferUsage.CopyDestination, GraphicsMemoryAccess.GpuToCpu);

        WritePattern(source, size, seed: 91);
        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Copy);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();
        commandList.CopyBuffer(source, 0, destination, 0, size);
        commandList.End();
        queue.Submit([commandList], fence, 1);

        // Submit has inserted the GL completion sync. Disposing now must enqueue deletion rather
        // than deleting the source while the GPU submission may still reference it.
        source.Dispose();
        WaitForGpu(fence, commandList, 1);
        AssertPattern(ReadBuffer(destination, size), seed: 91);
    }

    private static void PartiallyBoundIndirectCompute(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        if (!device.Capabilities.Features.Supports(GraphicsFeature.ComputeShaders))
            throw new HardwareTestSkippedException("The selected context does not expose compute shaders.");

        using IGraphicsBuffer outputAtSlotOne = CreateBuffer(
            device, sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.GpuToCpu);
        using IGraphicsBuffer outputAtSlotZero = CreateBuffer(
            device, sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.GpuToCpu);
        using IGraphicsBuffer indirectArguments = CreateBuffer(
            device, 3 * sizeof(uint), GraphicsBufferUsage.IndirectArguments, GraphicsMemoryAccess.CpuToGpu);

        WriteUInts(indirectArguments, 1, 1, 1);

        using IGraphicsBindingLayout bindingLayout = device.CreateBindingLayout(
            new GraphicsBindingLayoutDescription(
            [
                new GraphicsBindingLayoutEntry(
                    0,
                    GraphicsDescriptorType.StorageBuffer,
                    2,
                    GraphicsShaderStages.Compute,
                    GraphicsBindingFlags.PartiallyBound)
            ]));
        using IGraphicsBindingSet setAtSlotOne = device.CreateBindingSet(bindingLayout);
        using IGraphicsBindingSet setAtSlotZero = device.CreateBindingSet(bindingLayout);
        setAtSlotOne.SetBuffer(0, 1, outputAtSlotOne, 0, sizeof(uint));
        setAtSlotZero.SetBuffer(0, 0, outputAtSlotZero, 0, sizeof(uint));

        IGraphicsBindingLayout[] layouts = [bindingLayout];
        using IGraphicsPipelineLayout firstPipelineLayout = device.CreatePipelineLayout(layouts);
        using IGraphicsPipelineLayout secondPipelineLayout = device.CreatePipelineLayout(layouts);

        using IGraphicsShader firstShader = CreateShader(
            device,
            GraphicsShaderStage.Compute,
            """
            #version 450 core
            layout(local_size_x = 1) in;
            layout(std430, binding = 1) buffer ResultAtOne { uint value; } resultBuffer;
            void main() { resultBuffer.value = 0x00A11CEu; }
            """);
        using IGraphicsShader secondShader = CreateShader(
            device,
            GraphicsShaderStage.Compute,
            """
            #version 450 core
            layout(local_size_x = 1) in;
            layout(std430, binding = 0) buffer ResultAtZero { uint value; } resultBuffer;
            void main() { resultBuffer.value = 0x000B005u; }
            """);
        using IGraphicsComputePipeline firstPipeline = device.CreateComputePipeline(
            new GraphicsComputePipelineDescription(firstPipelineLayout, firstShader));
        using IGraphicsComputePipeline secondPipeline = device.CreateComputePipeline(
            new GraphicsComputePipelineDescription(secondPipelineLayout, secondShader));

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Compute);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();

        // The first set leaves array element zero empty and writes through element one using
        // indirect dispatch. The second pipeline has a different layout object and binds only
        // element zero, exercising missing-slot unbinding across both descriptor and pipeline switches.
        commandList.SetComputePipeline(firstPipeline);
        commandList.SetBindingSet(0, setAtSlotOne);
        commandList.DispatchIndirect(indirectArguments, 0);

        commandList.SetComputePipeline(secondPipeline);
        commandList.SetBindingSet(0, setAtSlotZero);
        commandList.Dispatch(1);

        GraphicsResourceBarrier[] hostReadBarriers =
        [
            new GraphicsResourceBarrier(
                outputAtSlotOne,
                GraphicsPipelineStage.ComputeShader,
                GraphicsPipelineStage.Host,
                GraphicsResourceAccess.ShaderWrite,
                GraphicsResourceAccess.HostRead),
            new GraphicsResourceBarrier(
                outputAtSlotZero,
                GraphicsPipelineStage.ComputeShader,
                GraphicsPipelineStage.Host,
                GraphicsResourceAccess.ShaderWrite,
                GraphicsResourceAccess.HostRead)
        ];
        commandList.MemoryBarrier(hostReadBarriers);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        WaitForGpu(fence, commandList, 1);

        AssertUInt(ReadBuffer(outputAtSlotOne, sizeof(uint)), 0x00A11CEu);
        AssertUInt(ReadBuffer(outputAtSlotZero, sizeof(uint)), 0x000B005u);
    }

    private static void ConventionalDirectAndIndirectDraw(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        RequireFormat(device, GraphicsFormat.RGBA8UNorm, GraphicsFormatSupport.ColorAttachment);

        using IGraphicsTexture colorTarget = CreateColorTarget(device);
        using IGraphicsTextureView colorView = colorTarget.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        using IGraphicsShader vertexShader = CreateShader(
            device,
            GraphicsShaderStage.Vertex,
            """
            #version 450 core
            const vec2 vertices[3] = vec2[3](
                vec2(-0.6, -0.5),
                vec2( 0.6, -0.5),
                vec2( 0.0,  0.6));
            void main() { gl_Position = vec4(vertices[gl_VertexID], 0.0, 1.0); }
            """);
        using IGraphicsShader fragmentShader = CreateShader(
            device,
            GraphicsShaderStage.Fragment,
            """
            #version 450 core
            layout(location = 0) out vec4 color;
            void main() { color = vec4(0.1, 0.7, 0.3, 1.0); }
            """);
        using IGraphicsPipelineLayout pipelineLayout = device.CreatePipelineLayout([]);
        IGraphicsShader[] shaders = [vertexShader, fragmentShader];
        GraphicsFormat[] colorFormats = [GraphicsFormat.RGBA8UNorm];
        using IGraphicsRenderPipeline pipeline = device.CreateRenderPipeline(
            new GraphicsRenderPipelineDescription(
                pipelineLayout,
                shaders,
                GraphicsPrimitiveTopology.TriangleList,
                colorFormats));

        using IGraphicsBuffer indirectArguments = CreateBuffer(
            device,
            4 * sizeof(uint),
            GraphicsBufferUsage.IndirectArguments,
            GraphicsMemoryAccess.CpuToGpu);
        WriteUInts(indirectArguments, 3, 1, 0, 0);

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Graphics);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        IGraphicsTextureView[] targets = [colorView];
        commandList.Begin();
        commandList.SetRenderTargets(targets);
        commandList.SetViewport(new GraphicsViewport(0, 0, 32, 32));
        commandList.SetRenderPipeline(pipeline);
        commandList.Draw(3);
        commandList.DrawIndirect(indirectArguments, 0);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        WaitForGpu(fence, commandList, 1);
    }

    private static void MeshShaderDirectAndIndirectDraw(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        if (!device.Capabilities.Features.Supports(GraphicsFeature.MeshShaders))
            throw new HardwareTestSkippedException("GL_EXT_mesh_shader or valid mesh work-group limits are unavailable.");

        RequireFormat(device, GraphicsFormat.RGBA8UNorm, GraphicsFormatSupport.ColorAttachment);
        using IGraphicsTexture colorTarget = CreateColorTarget(device);
        using IGraphicsTextureView colorView = colorTarget.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        using IGraphicsShader meshShader = CreateMeshShader(device);
        using IGraphicsShader fragmentShader = CreateShader(
            device,
            GraphicsShaderStage.Fragment,
            """
            #version 450 core
            layout(location = 0) out vec4 color;
            void main() { color = vec4(0.8, 0.2, 0.1, 1.0); }
            """);
        using IGraphicsPipelineLayout pipelineLayout = device.CreatePipelineLayout([]);
        IGraphicsShader[] shaders = [meshShader, fragmentShader];
        GraphicsFormat[] colorFormats = [GraphicsFormat.RGBA8UNorm];
        using IGraphicsRenderPipeline pipeline = device.CreateRenderPipeline(
            new GraphicsRenderPipelineDescription(
                pipelineLayout,
                shaders,
                GraphicsPrimitiveTopology.TriangleList,
                colorFormats));
        using IGraphicsBuffer indirectArguments = CreateBuffer(
            device, 3 * sizeof(uint), GraphicsBufferUsage.IndirectArguments, GraphicsMemoryAccess.CpuToGpu);
        WriteUInts(indirectArguments, 1, 1, 1);

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Graphics);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();
        IGraphicsTextureView[] targets = [colorView];

        commandList.Begin();
        commandList.SetRenderTargets(targets);
        commandList.SetViewport(new GraphicsViewport(0, 0, 32, 32));
        commandList.SetRenderPipeline(pipeline);
        commandList.DrawMeshTasks(1, 1, 1);
        commandList.DrawMeshTasksIndirect(indirectArguments, 0);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        WaitForGpu(fence, commandList, 1);
    }

    private static void TaskAndMeshShaderDraw(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        if (!device.Capabilities.Features.Supports(GraphicsFeature.TaskShaders))
            throw new HardwareTestSkippedException("GL_EXT_mesh_shader task limits are unavailable or task shaders are unsupported.");

        RequireFormat(device, GraphicsFormat.RGBA8UNorm, GraphicsFormatSupport.ColorAttachment);
        using IGraphicsTexture colorTarget = CreateColorTarget(device);
        using IGraphicsTextureView colorView = colorTarget.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        using IGraphicsShader taskShader = CreateShader(
            device,
            GraphicsShaderStage.Task,
            """
            #version 450 core
            #extension GL_EXT_mesh_shader : require
            layout(local_size_x = 1) in;
            void main() { EmitMeshTasksEXT(1, 1, 1); }
            """);
        using IGraphicsShader meshShader = CreateMeshShader(device);
        using IGraphicsShader fragmentShader = CreateShader(
            device,
            GraphicsShaderStage.Fragment,
            """
            #version 450 core
            layout(location = 0) out vec4 color;
            void main() { color = vec4(0.1, 0.3, 0.9, 1.0); }
            """);
        using IGraphicsPipelineLayout pipelineLayout = device.CreatePipelineLayout([]);
        IGraphicsShader[] shaders = [taskShader, meshShader, fragmentShader];
        GraphicsFormat[] colorFormats = [GraphicsFormat.RGBA8UNorm];
        using IGraphicsRenderPipeline pipeline = device.CreateRenderPipeline(
            new GraphicsRenderPipelineDescription(
                pipelineLayout,
                shaders,
                GraphicsPrimitiveTopology.TriangleList,
                colorFormats));

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Graphics);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();
        IGraphicsTextureView[] targets = [colorView];

        commandList.Begin();
        commandList.SetRenderTargets(targets);
        commandList.SetViewport(new GraphicsViewport(0, 0, 32, 32));
        commandList.SetRenderPipeline(pipeline);
        commandList.DrawMeshTasks(1, 1, 1);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        WaitForGpu(fence, commandList, 1);
    }

    private static IGraphicsBuffer CreateBuffer(
        IGraphicsDevice device,
        ulong size,
        GraphicsBufferUsage usage,
        GraphicsMemoryAccess access) =>
        device.CreateBuffer(new GraphicsBufferDescription(size, usage, access));

    private static IGraphicsTexture CreateColorTarget(IGraphicsDevice device) =>
        device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            32, 32, 1, 1, 1, 1,
            GraphicsTextureUsage.ColorAttachment));

    private static IGraphicsShader CreateShader(
        IGraphicsDevice device,
        GraphicsShaderStage stage,
        string source)
    {
        byte[] code = Encoding.UTF8.GetBytes(source);
        return device.CreateShader(new GraphicsShaderDescription
        {
            Stage = stage,
            Format = GraphicsShaderFormat.GlslSource,
            Code = code,
            EntryPoint = "main"
        });
    }

    private static IGraphicsShader CreateMeshShader(IGraphicsDevice device) =>
        CreateShader(
            device,
            GraphicsShaderStage.Mesh,
            """
            #version 450 core
            #extension GL_EXT_mesh_shader : require
            layout(local_size_x = 1) in;
            layout(triangles, max_vertices = 3, max_primitives = 1) out;
            void main()
            {
                SetMeshOutputsEXT(3, 1);
                gl_MeshVerticesEXT[0].gl_Position = vec4(-0.6, -0.5, 0.0, 1.0);
                gl_MeshVerticesEXT[1].gl_Position = vec4( 0.6, -0.5, 0.0, 1.0);
                gl_MeshVerticesEXT[2].gl_Position = vec4( 0.0,  0.6, 0.0, 1.0);
                gl_PrimitiveTriangleIndicesEXT[0] = uvec3(0, 1, 2);
            }
            """);

    private static void RequireFormat(
        IGraphicsDevice device,
        GraphicsFormat format,
        GraphicsFormatSupport support)
    {
        if (!device.Capabilities.SupportsFormat(format, support))
            throw new HardwareTestSkippedException($"Format {format} does not support {support} on this driver.");
    }

    private static void WritePattern(IGraphicsBuffer buffer, int size, byte seed)
    {
        Memory<byte> memory = buffer.Map(GraphicsMapMode.Write, 0, (ulong)size);
        Span<byte> bytes = memory.Span;
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = unchecked((byte)(seed + i * 31));
        buffer.Unmap();
    }

    private static byte[] ReadBuffer(IGraphicsBuffer buffer, int size)
    {
        Memory<byte> mapped = buffer.Map(GraphicsMapMode.Read, 0, (ulong)size);
        byte[] bytes = mapped.ToArray();
        buffer.Unmap();
        return bytes;
    }

    private static void WriteUInts(IGraphicsBuffer buffer, params uint[] values)
    {
        Memory<byte> memory = buffer.Map(GraphicsMapMode.Write, 0, checked((ulong)(values.Length * sizeof(uint))));
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(memory.Span.Slice(i * sizeof(uint), sizeof(uint)), values[i]);
        buffer.Unmap();
    }

    private static void AssertUInt(byte[] bytes, uint expected)
    {
        uint actual = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (actual != expected)
            throw new InvalidOperationException($"GPU output mismatch: expected 0x{expected:X8}, received 0x{actual:X8}.");
    }

    private static void AssertPattern(byte[] actual, byte seed)
    {
        for (int i = 0; i < actual.Length; i++)
        {
            byte expected = unchecked((byte)(seed + i * 31));
            if (actual[i] != expected)
                throw new InvalidOperationException(
                    $"GPU copy mismatch at byte {i}: expected 0x{expected:X2}, received 0x{actual[i]:X2}.");
        }
    }

    private static void WaitForGpu(IGraphicsFence fence, IGraphicsCommandList commandList, ulong value)
    {
        if (!fence.Wait(value, s_gpuTimeout))
            throw new TimeoutException($"The GPU did not complete fence value {value} within {s_gpuTimeout}.");

        // State polling processes the queue's completion sync and transitions the command list
        // from Pending back to Executable before resources are disposed.
        if (commandList.State == GraphicsCommandListState.Pending)
            throw new InvalidOperationException("The command list remained Pending after its completion fence signaled.");
    }

    private sealed class HardwareTestSkippedException(string message) : Exception(message) { }
}
