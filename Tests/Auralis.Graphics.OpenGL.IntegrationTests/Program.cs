using System.Buffers.Binary;
using System.Text;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGL;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.OpenGL.TestSupport;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGL;
using OpenGLExtMeshShader = Silk.NET.OpenGL.Extensions.EXT.ExtMeshShader;
using NvMeshShader = Silk.NET.OpenGL.Extensions.NV.NVMeshShader;
using Xunit;
using Xunit.Sdk;

namespace Auralis.Graphics.OpenGL.IntegrationTests;

public sealed partial class OpenGLIntegrationTests : IClassFixture<OpenGLHardwareFixture>
{

    private readonly OpenGLHardwareFixture _fixture;
    public OpenGLIntegrationTests(OpenGLHardwareFixture fixture) => _fixture = fixture;
    private void RunHardwareTest(Action<GlfwOpenGLHardwareSession> test)
    {
        GlfwOpenGLHardwareSession session = _fixture.GetSessionOrSkip();
        OpenGLGraphicsDevice nativeDevice = (OpenGLGraphicsDevice)session.Device;
        MeshShaderContextDiagnostics meshShaderDiagnostics =
            QueryMeshShaderContextDiagnostics(nativeDevice);
        Console.WriteLine(
            $"Desktop OpenGL context: vendor='{nativeDevice.Info.DriverName}', " +
            $"renderer='{nativeDevice.Info.Name}', driver='{nativeDevice.Info.DriverVersion}', " +
            $"API='{nativeDevice.Info.ApiVersion}', process='{Environment.ProcessPath}', " +
            $"GL_NUM_EXTENSIONS={meshShaderDiagnostics.ExtensionCount}, " +
            $"indexed mesh extensions=[{meshShaderDiagnostics.AdvertisedExtensions}], " +
            $"Silk.NET IsExtensionPresent: EXT={meshShaderDiagnostics.SilkExtAdvertised}, " +
            $"NV={meshShaderDiagnostics.SilkNvAdvertised}, " +
            $"device advertisement: EXT={nativeDevice.HasExtMeshShaderExtension}, " +
            $"NV={nativeDevice.HasNvMeshShaderExtension}, " +
            $"wrappers: EXT={nativeDevice.MeshShaderApi is not null}, NV={nativeDevice.NvMeshShaderApi is not null}, " +
            $"entry points: EXT direct={meshShaderDiagnostics.ExtDirectEntryPoint}, " +
            $"EXT indirect={meshShaderDiagnostics.ExtIndirectEntryPoint}, " +
            $"EXT multi-indirect={meshShaderDiagnostics.ExtMultiDrawEntryPoint}, " +
            $"NV direct={meshShaderDiagnostics.NvDirectEntryPoint}.");
        try
        {
            test(session);
            Assert.Empty(session.ContextProvider.DrainErrors());
        }
        catch (HardwareTestSkippedException exception)
        {
            throw SkipException.ForSkip(exception.Message);
        }
    }

    private static MeshShaderContextDiagnostics QueryMeshShaderContextDiagnostics(
        OpenGLGraphicsDevice device)
    {
        // The device's GL instance and extension cache are queried only while the device's own
        // context gate is held. Enumerate GL_NUM_EXTENSIONS/glGetStringi independently, then
        // compare that exact list against Silk.NET and the immutable device-initialization result.
        using OpenGLContextScope context = device.AcquireContext();
        GL gl = context.Gl;
        int extensionCount = gl.GetInteger(GLEnum.NumExtensions);
        if (extensionCount < 0)
            throw new GraphicsException($"The current OpenGL context returned an invalid GL_NUM_EXTENSIONS value: {extensionCount}.");

        bool indexedExtAdvertised = false;
        bool indexedNvAdvertised = false;
        for (int i = 0; i < extensionCount; i++)
        {
            string extension = gl.GetStringS(StringName.Extensions, (uint)i);
            if (string.Equals(extension, "GL_" + OpenGLExtMeshShader.ExtensionName, StringComparison.Ordinal))
                indexedExtAdvertised = true;
            else if (string.Equals(extension, "GL_" + NvMeshShader.ExtensionName, StringComparison.Ordinal))
                indexedNvAdvertised = true;
        }

        bool silkExtAdvertised = gl.IsExtensionPresent(OpenGLExtMeshShader.ExtensionName);
        bool silkNvAdvertised = gl.IsExtensionPresent(NvMeshShader.ExtensionName);
        if (indexedExtAdvertised != silkExtAdvertised ||
            indexedNvAdvertised != silkNvAdvertised ||
            indexedExtAdvertised != device.HasExtMeshShaderExtension ||
            indexedNvAdvertised != device.HasNvMeshShaderExtension)
        {
            throw new GraphicsException(
                "Desktop OpenGL mesh-shader extension discovery disagrees for the active context: " +
                $"indexed EXT={indexedExtAdvertised}, Silk.NET EXT={silkExtAdvertised}, device EXT={device.HasExtMeshShaderExtension}; " +
                $"indexed NV={indexedNvAdvertised}, Silk.NET NV={silkNvAdvertised}, device NV={device.HasNvMeshShaderExtension}.");
        }

        // These are deliberately diagnostic-only here. An advertised extension whose required
        // function cannot be resolved is reported as a native-entry-point problem, never as an
        // unadvertised extension or a capability skip.
        bool extDirect = device.ContextProvider.GetProcAddress("glDrawMeshTasksEXT") != 0;
        bool extIndirect = device.ContextProvider.GetProcAddress("glDrawMeshTasksIndirectEXT") != 0;
        bool extMultiDraw = device.ContextProvider.GetProcAddress("glMultiDrawMeshTasksIndirectEXT") != 0;
        bool nvDirect = device.ContextProvider.GetProcAddress("glDrawMeshTasksNV") != 0;

        string advertisedExtensions = string.Join(
            ", ",
            indexedExtAdvertised ? "GL_EXT_mesh_shader" : null,
            indexedNvAdvertised ? "GL_NV_mesh_shader" : null);
        return new MeshShaderContextDiagnostics(
            extensionCount,
            advertisedExtensions,
            silkExtAdvertised,
            silkNvAdvertised,
            extDirect,
            extIndirect,
            extMultiDraw,
            nvDirect);
    }

    private static void RequireNativeEntryPoint(
        OpenGLGraphicsDevice device,
        string extensionName,
        string entryPointName)
    {
        using OpenGLContextScope context = device.AcquireContext();
        if (device.ContextProvider.GetProcAddress(entryPointName) == 0)
        {
            throw new GraphicsException(
                $"{extensionName} is advertised by the active OpenGL context, but its required native entry point " +
                $"'{entryPointName}' could not be resolved from that same current context.");
        }
    }

    private readonly record struct MeshShaderContextDiagnostics(
        int ExtensionCount,
        string AdvertisedExtensions,
        bool SilkExtAdvertised,
        bool SilkNvAdvertised,
        bool ExtDirectEntryPoint,
        bool ExtIndirectEntryPoint,
        bool ExtMultiDrawEntryPoint,
        bool NvDirectEntryPoint);

    [Fact] public void NestedContextProviderActivationIsBalanced() => RunHardwareTest(NestedContextProviderActivation);
    [Fact] public void FenceWaitTimeoutIsBoundedByContextGateContention() => RunHardwareTest(FenceWaitTimeoutUnderContextContention);
    [Fact] public void DisposedSignalFenceSubmissionRegression() => RunHardwareTest(DisposedSignalFenceIsRejectedBeforeSubmission);
    [Fact] public void BufferCopyAfterDisposeBeforeSubmit() => RunHardwareTest(CopyBufferAfterRecordDispose);
    [Fact] public void TextureCopyAfterDisposeBeforeSubmit() => RunHardwareTest(CopyTextureAfterRecordDispose);
    [Fact] public void CompletionPollingWorksWithoutUserProvidedFence() => RunHardwareTest(CompletionPollingWithoutUserFence);
    [Fact] public void BufferCanBeDisposedWhileGpuWorkIsPending() => RunHardwareTest(CopyBufferAfterSubmitDispose);
    [Fact] public void ReusableCommandListsRetainResourcesAfterOwnerDisposal() => RunHardwareTest(ReusableCommandListAfterOwnerDisposal);
    [Fact] public void PartiallyBoundDescriptorSwitchingAndIndirectComputeWork() => RunHardwareTest(PartiallyBoundIndirectCompute);
    [Fact] public void ComputeDescriptorCategoriesSnapshotsAndResourceRetentionWork() => RunHardwareTest(ComputeDescriptorTypesAndSnapshots);
    [Fact] public void OffscreenGraphicsDrawAndTextureCopyReadbackWork() => RunHardwareTest(OffscreenGraphicsDrawAndTextureCopyReadback);
    [Fact] public void ConventionalDirectAndIndirectGraphicsDrawsWork() => RunHardwareTest(ConventionalDirectAndIndirectDraw);
    [Fact] public void ExtMeshShaderDirectAndIndirectDrawsWork() => RunHardwareTest(ExtMeshShaderDirectAndIndirectDraw);
    [Fact] public void ExtTaskAndMeshShaderPipelineWorks() => RunHardwareTest(ExtTaskAndMeshShaderDraw);
    [Fact] public void NvMeshShaderDirectDrawWorks() => RunHardwareTest(NvMeshShaderDirectDraw);
    [Fact] public void NvTaskAndMeshShaderPipelineWorks() => RunHardwareTest(NvTaskAndMeshShaderDraw);


    private static readonly TimeSpan s_gpuTimeout = TimeSpan.FromSeconds(15);

    

    private static void NestedContextProviderActivation(GlfwOpenGLHardwareSession session)
    {
        GlfwOpenGLContextProvider context = session.ContextProvider;
        OpenGLGraphicsDevice device = (OpenGLGraphicsDevice)session.Device;
        using (OpenGLContextScope outer = device.AcquireContext())
        {
            string? version = outer.Gl.GetStringS(Silk.NET.OpenGL.StringName.Version);
            using (OpenGLContextScope inner = device.AcquireContext())
                _ = inner.Gl.GetError();
            if (string.IsNullOrWhiteSpace(version))
                throw new GraphicsException("Nested desktop context scopes lost the current context.");
        }

        context.MakeCurrent();
        try
        {
            context.MakeCurrent();
            context.ReleaseCurrent();
            IReadOnlyList<Silk.NET.OpenGL.GLEnum> errors = context.DrainErrors();
            if (errors.Count != 0)
                throw new GraphicsException(
                    $"Nested desktop context activation left GL errors: {string.Join(", ", errors)}.");
        }
        finally
        {
            context.ReleaseCurrent();
        }
        Console.WriteLine("Validated nested desktop GL provider MakeCurrent/ReleaseCurrent reference counting.");
    }

    private static void FenceWaitTimeoutUnderContextContention(GlfwOpenGLHardwareSession session)
    {
        OpenGLGraphicsDevice device = (OpenGLGraphicsDevice)session.Device;
        using IGraphicsFence fence = device.CreateFence();
        using ManualResetEventSlim gateEntered = new();
        using ManualResetEventSlim releaseGate = new();
        using ManualResetEventSlim waitCompleted = new();

        Exception? holderFailure = null;
        Thread holder = new(() =>
        {
            try
            {
                using OpenGLContextScope context = device.AcquireContext();
                _ = context.Gl.GetError();
                gateEntered.Set();
                releaseGate.Wait();
            }
            catch (Exception exception)
            {
                holderFailure = exception;
                gateEntered.Set();
            }
        });

        holder.Start();
        if (!gateEntered.Wait(TimeSpan.FromSeconds(2)))
        {
            releaseGate.Set();
            holder.Join();
            throw new TimeoutException("The desktop context holder did not acquire the device gate.");
        }
        if (holderFailure is not null)
        {
            releaseGate.Set();
            holder.Join();
            throw new GraphicsException("The desktop context holder failed.", holderFailure);
        }

        bool waitResult = true;
        Exception? waitFailure = null;
        Thread waiter = new(() =>
        {
            try
            {
                waitResult = fence.Wait(1, TimeSpan.FromMilliseconds(50));
            }
            catch (Exception exception)
            {
                waitFailure = exception;
            }
            finally
            {
                waitCompleted.Set();
            }
        });

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        waiter.Start();
        bool returnedBeforeGateRelease = waitCompleted.Wait(TimeSpan.FromMilliseconds(750));
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);

        releaseGate.Set();
        if (!holder.Join(TimeSpan.FromSeconds(2)) || !waiter.Join(TimeSpan.FromSeconds(2)))
            throw new TimeoutException("A desktop context/fence contention test thread did not exit.");

        if (!returnedBeforeGateRelease)
            throw new InvalidOperationException("Desktop fence.Wait remained blocked behind the context gate past its timeout.");
        if (waitFailure is not null)
            throw new GraphicsException("Desktop fence.Wait failed during context-gate contention.", waitFailure);
        if (waitResult)
            throw new InvalidOperationException("An unsignaled desktop fence unexpectedly reported completion.");
        if (elapsed > TimeSpan.FromMilliseconds(500))
            throw new InvalidOperationException($"Desktop fence.Wait exceeded its 50 ms timeout ({elapsed.TotalMilliseconds:F1} ms).");

        Console.WriteLine("Validated bounded desktop fence timeout while another thread owns the device context gate.");
    }

    private static void DisposedSignalFenceIsRejectedBeforeSubmission(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Graphics);
        using IGraphicsCommandList command = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        command.Begin();
        command.End();
        fence.Dispose();

        bool rejected = false;
        try
        {
            queue.Submit([command], fence, 1);
        }
        catch (GraphicsValidationException)
        {
            rejected = true;
        }

        if (!rejected)
            throw new InvalidOperationException("Submission accepted a disposed desktop OpenGL signal fence.");
        if (command.State != GraphicsCommandListState.Executable)
            throw new InvalidOperationException(
                $"Disposed-fence validation changed the command list to {command.State}; expected Executable.");
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

    private static void CompletionPollingWithoutUserFence(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        using IGraphicsBuffer source = CreateBuffer(
            device, 256, GraphicsBufferUsage.CopySource, GraphicsMemoryAccess.CpuReadWrite);
        using IGraphicsBuffer destination = CreateBuffer(
            device, 256, GraphicsBufferUsage.CopyDestination, GraphicsMemoryAccess.GpuToCpu);
        WritePattern(source, 256, seed: 53);

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Copy);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        commandList.Begin();
        commandList.CopyBuffer(source, 0, destination, 0, 256);
        commandList.End();

        // Intentionally omit a user fence. Reading State polls the queue's internal completion
        // sync and must make progress solely because Submit flushed the GL command stream.
        queue.Submit([commandList]);
        if (!SpinWait.SpinUntil(
                () => commandList.State == GraphicsCommandListState.Executable,
                s_gpuTimeout))
            throw new TimeoutException("The command list did not complete through state polling without a user fence.");

        AssertPattern(ReadBuffer(destination, 256), seed: 53);
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


    private static void ReusableCommandListAfterOwnerDisposal(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        const int size = 4096;
        using IGraphicsBuffer source = CreateBuffer(
            device, size, GraphicsBufferUsage.CopySource, GraphicsMemoryAccess.CpuReadWrite);
        using IGraphicsBuffer destination = CreateBuffer(
            device, size, GraphicsBufferUsage.CopyDestination, GraphicsMemoryAccess.CpuReadWrite);

        WritePattern(source, size, seed: 53);
        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Copy);
        using IGraphicsCommandList commandList = queue.CreateCommandList(GraphicsCommandListUsage.Reusable);
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();
        commandList.CopyBuffer(source, 0, destination, 0, size);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        WaitForGpu(fence, commandList, 1);

        // An executable reusable list retains the source's native resource between submissions.
        source.Dispose();
        queue.Submit([commandList], fence, 2);
        WaitForGpu(fence, commandList, 2);

        AssertPattern(ReadBuffer(destination, size), seed: 53);
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


    private static void ComputeDescriptorTypesAndSnapshots(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        if (!device.Capabilities.Features.Supports(GraphicsFeature.ComputeShaders))
            throw new HardwareTestSkippedException("The selected context does not expose compute shaders.");
        RequireFormat(device, GraphicsFormat.RGBA8UNorm,
            GraphicsFormatSupport.Sampled | GraphicsFormatSupport.ColorAttachment | GraphicsFormatSupport.Storage);

        using IGraphicsTexture sampledTexture = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            2, 1, 1, 1, 1, 1,
            GraphicsTextureUsage.Sampled | GraphicsTextureUsage.ColorAttachment));
        using IGraphicsTextureView sampledView = sampledTexture.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        ClearTwoTexelTexture(device, sampledView);

        using IGraphicsTexture alternativeSampledTexture = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            2, 1, 1, 1, 1, 1,
            GraphicsTextureUsage.Sampled | GraphicsTextureUsage.ColorAttachment));
        using IGraphicsTextureView alternativeSampledView = alternativeSampledTexture.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        ClearTexture(device, alternativeSampledView, 2, 1, new GraphicsClearColor(0, 1, 0, 1));

        using IGraphicsTexture storageTexture = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            1, 1, 1, 1, 1, 1, GraphicsTextureUsage.Storage));
        using IGraphicsTextureView storageView = storageTexture.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        using IGraphicsTexture alternativeStorageTexture = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            1, 1, 1, 1, 1, 1, GraphicsTextureUsage.Storage));
        using IGraphicsTextureView alternativeStorageView = alternativeStorageTexture.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));

        using IGraphicsBuffer parameters = CreateBuffer(
            device, 4 * sizeof(uint), GraphicsBufferUsage.Constant, GraphicsMemoryAccess.CpuReadWrite);
        using IGraphicsBuffer alternativeParameters = CreateBuffer(
            device, 4 * sizeof(uint), GraphicsBufferUsage.Constant, GraphicsMemoryAccess.CpuReadWrite);
        using IGraphicsBuffer output = CreateBuffer(
            device, sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.CpuReadWrite);
        using IGraphicsBuffer alternativeOutput = CreateBuffer(
            device, sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.CpuReadWrite);
        using IGraphicsBuffer partialOutput = CreateBuffer(
            device, sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.CpuReadWrite);
        using IGraphicsBuffer alternativePartialOutput = CreateBuffer(
            device, sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.CpuReadWrite);
        using IGraphicsBuffer sentinelBuffer = CreateBuffer(
            device, sizeof(uint), GraphicsBufferUsage.Storage, GraphicsMemoryAccess.GpuOnly);

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

        uint originalTextureHandle = ((OpenGLGraphicsTextureView)sampledView).Handle;
        uint originalSamplerHandle = ((OpenGLGraphicsSampler)linearSampler).Handle;
        uint originalStorageImageHandle = ((OpenGLGraphicsTextureView)storageView).Handle;
        uint sentinelBufferHandle = ((OpenGLGraphicsBuffer)sentinelBuffer).Handle;

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
        // Partially-bound SSBO element zero is intentionally left empty. Element one is populated.
        bindingSet.SetBuffer(6, 1, partialOutput, 0, sizeof(uint));

        IGraphicsBindingLayout[] layouts = [bindingLayout];
        using IGraphicsPipelineLayout pipelineLayout = device.CreatePipelineLayout(layouts);
        using IGraphicsShader shader = CreateShader(
            device,
            GraphicsShaderStage.Compute,
            """
            #version 450 core
            layout(local_size_x = 1) in;
            layout(std140, binding = 0) uniform Parameters { uvec4 values; } parameters;
            layout(std430, binding = 0) buffer Result { uint value; } resultBuffer;
            layout(binding = 0) uniform sampler2D sampledOnly;
            layout(binding = 2) uniform sampler2D sampledWithSampler;
            layout(rgba8, binding = 0) coherent uniform image2D storageOutput;
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
            """);
        using IGraphicsComputePipeline pipeline = device.CreateComputePipeline(
            new GraphicsComputePipelineDescription(pipelineLayout, shader));

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Compute);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        commandList.Begin();
        commandList.SetComputePipeline(pipeline);
        commandList.SetBindingSet(0, bindingSet);
        commandList.Dispatch(1);
        GraphicsResourceBarrier[] readbackBarriers =
        [
            new GraphicsResourceBarrier(
                output,
                GraphicsPipelineStage.ComputeShader,
                GraphicsPipelineStage.Host,
                GraphicsResourceAccess.ShaderWrite,
                GraphicsResourceAccess.HostRead),
            new GraphicsResourceBarrier(
                partialOutput,
                GraphicsPipelineStage.ComputeShader,
                GraphicsPipelineStage.Host,
                GraphicsResourceAccess.ShaderWrite,
                GraphicsResourceAccess.HostRead)
        ];
        commandList.MemoryBarrier(readbackBarriers);
        commandList.End();

        // Change every descriptor after recording and dispose the set. The command list must
        // continue using the original copied values rather than the mutated descriptor-set storage.
        bindingSet.SetBuffer(0, 0, alternativeParameters, 0, 4 * sizeof(uint));
        bindingSet.SetBuffer(1, 0, alternativeOutput, 0, sizeof(uint));
        bindingSet.SetTexture(2, 0, alternativeSampledView);
        bindingSet.SetSampler(3, 0, nearestSampler);
        bindingSet.SetTexture(4, 0, alternativeSampledView);
        bindingSet.SetSampler(4, 0, nearestSampler);
        bindingSet.SetTexture(5, 0, alternativeStorageView);
        bindingSet.SetBuffer(6, 1, alternativePartialOutput, 0, sizeof(uint));
        bindingSet.Dispose();

        // Close original owners before submission; the executable command list retains their native objects.
        sampledView.Dispose();
        sampledTexture.Dispose();
        linearSampler.Dispose();
        parameters.Dispose();
        storageView.Dispose();
        storageTexture.Dispose();
        pipeline.Dispose();

        session.ContextProvider.MakeCurrent();
        try
        {
            using GL gl = GL.GetApi(session.ContextProvider.GetProcAddress);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, sentinelBufferHandle);
            gl.GetInteger(GLEnum.ShaderStorageBufferBinding, 1, out int seededBinding);
            AssertUInt(unchecked((uint)seededBinding), sentinelBufferHandle);
        }
        finally
        {
            session.ContextProvider.ReleaseCurrent();
        }

        queue.Submit([commandList], fence, 1);
        WaitForGpu(fence, commandList, 1);

        session.ContextProvider.MakeCurrent();
        try
        {
            using GL gl = GL.GetApi(session.ContextProvider.GetProcAddress);
            gl.GetInteger(GLEnum.ShaderStorageBufferBinding, 1, out int unboundSlot);
            AssertUInt(unchecked((uint)unboundSlot), 0u);
            gl.GetInteger(GLEnum.ImageBindingName, 0, out int imageBinding);
            AssertUInt(unchecked((uint)imageBinding), originalStorageImageHandle);
            gl.GetInteger(GLEnum.SamplerBinding, 1, out int separateSamplerBinding);
            AssertUInt(unchecked((uint)separateSamplerBinding), originalSamplerHandle);
            gl.GetInteger(GLEnum.SamplerBinding, 2, out int combinedSamplerBinding);
            AssertUInt(unchecked((uint)combinedSamplerBinding), originalSamplerHandle);
            gl.ActiveTexture((TextureUnit)0x84C2);
            gl.GetInteger(GLEnum.TextureBinding2D, out int textureBinding);
            AssertUInt(unchecked((uint)textureBinding), originalTextureHandle);
        }
        finally
        {
            session.ContextProvider.ReleaseCurrent();
        }

        AssertUInt(ReadUInt(output), (0xFFu << 24) | (191u << 16) | (64u << 8) | 7u);
        AssertUInt(ReadUInt(partialOutput), 0x00A11CEu);
        AssertUInt(ReadUInt(alternativeOutput), 0xDEADBEEFu);
        AssertUInt(ReadUInt(alternativePartialOutput), 0xDEADBEEFu);
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
        WaitForGpu(fence, commandList, 1);
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

        using IGraphicsBuffer indexBuffer = CreateBuffer(
            device,
            3 * sizeof(uint),
            GraphicsBufferUsage.Index,
            GraphicsMemoryAccess.CpuToGpu);
        WriteUInts(indexBuffer, 0, 1, 2);

        using IGraphicsBuffer indexedIndirectArguments = CreateBuffer(
            device,
            5 * sizeof(uint),
            GraphicsBufferUsage.IndirectArguments,
            GraphicsMemoryAccess.CpuToGpu);
        WriteUInts(indexedIndirectArguments, 3, 2, 0, 0, 0);

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Graphics);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();

        IGraphicsTextureView[] targets = [colorView];
        commandList.Begin();
        commandList.SetRenderTargets(targets);
        commandList.SetViewport(new GraphicsViewport(0, 0, 32, 32));
        commandList.SetRenderPipeline(pipeline);
        commandList.Draw(3);
        commandList.Draw(3, instanceCount: 2);
        commandList.SetIndexBuffer(indexBuffer, GraphicsIndexFormat.UInt32);
        commandList.DrawIndexed(3);
        commandList.DrawIndexed(3, instanceCount: 2);
        commandList.DrawIndexedIndirect(indexedIndirectArguments, 0);
        commandList.DrawIndirect(indirectArguments, 0);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        WaitForGpu(fence, commandList, 1);
    }

    private static void OffscreenGraphicsDrawAndTextureCopyReadback(GlfwOpenGLHardwareSession session)
    {
        const uint width = 32;
        const uint height = 32;
        IGraphicsDevice device = session.Device;
        RequireFormat(
            device,
            GraphicsFormat.RGBA8UNorm,
            GraphicsFormatSupport.ColorAttachment |
            GraphicsFormatSupport.CopySource |
            GraphicsFormatSupport.CopyDestination);

        using IGraphicsTexture renderedTexture = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            width, height, 1, 1, 1, 1,
            GraphicsTextureUsage.ColorAttachment | GraphicsTextureUsage.CopySource));
        using IGraphicsTexture copiedTexture = device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            width, height, 1, 1, 1, 1,
            GraphicsTextureUsage.CopyDestination | GraphicsTextureUsage.ColorAttachment));
        using IGraphicsTextureView renderTarget = renderedTexture.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));

        using IGraphicsShader vertexShader = CreateShader(
            device,
            GraphicsShaderStage.Vertex,
            """
            #version 450 core
            const vec2 positions[3] = vec2[3](
                vec2(-0.6, -0.5),
                vec2( 0.6, -0.5),
                vec2( 0.0,  0.6));
            void main() { gl_Position = vec4(positions[gl_VertexID], 0.0, 1.0); }
            """);
        using IGraphicsShader fragmentShader = CreateShader(
            device,
            GraphicsShaderStage.Fragment,
            """
            #version 450 core
            layout(location = 0) out vec4 outputColor;
            void main() { outputColor = vec4(0.1, 0.7, 0.3, 1.0); }
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

        IGraphicsCommandQueue graphicsQueue = device.GetQueue(GraphicsQueueType.Graphics);
        using (IGraphicsCommandList graphicsCommands = graphicsQueue.CreateCommandList())
        using (IGraphicsFence graphicsFence = device.CreateFence())
        {
            IGraphicsTextureView[] targets = [renderTarget];
            graphicsCommands.Begin();
            graphicsCommands.SetRenderTargets(targets);
            graphicsCommands.SetViewport(new GraphicsViewport(0, 0, width, height));
            graphicsCommands.SetScissor(new GraphicsScissorRect(0, 0, width, height));
            graphicsCommands.ClearColor(0, new GraphicsClearColor(0, 0, 0, 1));
            graphicsCommands.SetRenderPipeline(pipeline);
            graphicsCommands.Draw(3);
            graphicsCommands.End();

            graphicsQueue.Submit([graphicsCommands], graphicsFence, 1);
            WaitForGpu(graphicsFence, graphicsCommands, 1);
        }

        IGraphicsCommandQueue copyQueue = device.GetQueue(GraphicsQueueType.Copy);
        using (IGraphicsCommandList copyCommands = copyQueue.CreateCommandList())
        using (IGraphicsFence copyFence = device.CreateFence())
        {
            copyCommands.Begin();
            copyCommands.CopyTexture(renderedTexture, copiedTexture);
            copyCommands.End();

            copyQueue.Submit([copyCommands], copyFence, 1);
            WaitForGpu(copyFence, copyCommands, 1);
        }

        byte[] renderedPixels = ReadTexturePixels(session, renderedTexture, width, height);
        byte[] copiedPixels = ReadTexturePixels(session, copiedTexture, width, height);
        if (!renderedPixels.AsSpan().SequenceEqual(copiedPixels))
            throw new InvalidOperationException("The GPU texture copy did not preserve the rendered image byte-for-byte.");

        AssertPixelNear(renderedPixels, width, x: 16, y: 16, red: 26, green: 179, blue: 77, alpha: 255);
        AssertPixelNear(renderedPixels, width, x: 0, y: 0, red: 0, green: 0, blue: 0, alpha: 255);
    }

    private static unsafe byte[] ReadTexturePixels(
        GlfwOpenGLHardwareSession session,
        IGraphicsTexture texture,
        uint width,
        uint height)
    {
        OpenGLGraphicsTexture nativeTexture = (OpenGLGraphicsTexture)texture;
        byte[] pixels = new byte[checked((int)(width * height * 4))];
        session.ContextProvider.MakeCurrent();
        try
        {
            using GL gl = GL.GetApi(session.ContextProvider.GetProcAddress);
            fixed (byte* destination = pixels)
            {
                gl.GetTextureImage(
                    nativeTexture.Handle,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    checked((uint)pixels.Length),
                    destination);
            }
        }
        finally
        {
            session.ContextProvider.ReleaseCurrent();
        }

        return pixels;
    }

    private static void AssertPixelNear(
        byte[] pixels,
        uint width,
        uint x,
        uint y,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        int offset = checked((int)((y * width + x) * 4));
        AssertChannelNear(pixels[offset], red, x, y, "red");
        AssertChannelNear(pixels[offset + 1], green, x, y, "green");
        AssertChannelNear(pixels[offset + 2], blue, x, y, "blue");
        AssertChannelNear(pixels[offset + 3], alpha, x, y, "alpha");
    }

    private static void AssertChannelNear(byte actual, byte expected, uint x, uint y, string channel)
    {
        if (Math.Abs(actual - expected) > 1)
            throw new InvalidOperationException(
                $"Unexpected {channel} value at pixel ({x}, {y}): expected {expected} ± 1, received {actual}.");
    }

    private static void ExtMeshShaderDirectAndIndirectDraw(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        OpenGLGraphicsDevice nativeDevice = (OpenGLGraphicsDevice)device;
        if (!nativeDevice.HasExtMeshShaderExtension)
            throw new HardwareTestSkippedException("The context does not advertise GL_EXT_mesh_shader.");
        if (nativeDevice.MeshShaderApi is null)
            throw new GraphicsException("GL_EXT_mesh_shader is advertised, but its Silk.NET API wrapper was not initialized.");
        RequireNativeEntryPoint(nativeDevice, "GL_EXT_mesh_shader", "glDrawMeshTasksEXT");
        RequireNativeEntryPoint(nativeDevice, "GL_EXT_mesh_shader", "glDrawMeshTasksIndirectEXT");
        if (!nativeDevice.MeshShaderCapabilities.SupportsMeshShader(OpenGLMeshShaderDialect.Ext))
            throw new HardwareTestSkippedException(
                $"GL_EXT_mesh_shader is advertised, but its mesh work-group limits are unusable: " +
                $"count=({nativeDevice.MaxMeshWorkGroupCountX}, {nativeDevice.MaxMeshWorkGroupCountY}, {nativeDevice.MaxMeshWorkGroupCountZ}), " +
                $"total={nativeDevice.MaxMeshWorkGroupTotalCount}.");

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

        // GL_EXT_mesh_shader uses a three-dimensional indirect group-count record.
        uint[] indirectValues = [1u, 1u, 1u];
        using IGraphicsBuffer indirectArguments = CreateBuffer(
            device,
            (ulong)(indirectValues.Length * sizeof(uint)),
            GraphicsBufferUsage.IndirectArguments,
            GraphicsMemoryAccess.CpuToGpu);
        WriteUInts(indirectArguments, indirectValues);

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Graphics);
        using IGraphicsCommandList commandList = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();
        IGraphicsTextureView[] targets = [colorView];

        commandList.Begin();
        commandList.SetRenderTargets(targets);
        commandList.SetViewport(new GraphicsViewport(0, 0, 32, 32));
        commandList.SetScissor(new GraphicsScissorRect(0, 0, 32, 32));
        commandList.ClearColor(0, new GraphicsClearColor(0, 0, 0, 1));
        commandList.SetRenderPipeline(pipeline);
        commandList.DrawMeshTasks(1, 1, 1);
        commandList.DrawMeshTasksIndirect(indirectArguments, 0);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        WaitForGpu(fence, commandList, 1);
        byte[] pixels = ReadTexturePixels(session, colorTarget, 32, 32);
        AssertPixelNear(pixels, 32, 16, 16, 204, 51, 26, 255);
    }

    private static void ExtTaskAndMeshShaderDraw(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        OpenGLGraphicsDevice nativeDevice = (OpenGLGraphicsDevice)device;
        if (!nativeDevice.HasExtMeshShaderExtension)
            throw new HardwareTestSkippedException("The context does not advertise GL_EXT_mesh_shader.");
        if (nativeDevice.MeshShaderApi is null)
            throw new GraphicsException("GL_EXT_mesh_shader is advertised, but its Silk.NET API wrapper was not initialized.");
        RequireNativeEntryPoint(nativeDevice, "GL_EXT_mesh_shader", "glDrawMeshTasksEXT");
        if (!nativeDevice.MeshShaderCapabilities.SupportsTaskShader(OpenGLMeshShaderDialect.Ext))
            throw new HardwareTestSkippedException(
                $"GL_EXT_mesh_shader is advertised, but its EXT task/mesh work-group limits are unusable: " +
                $"mesh=({nativeDevice.MaxMeshWorkGroupCountX}, {nativeDevice.MaxMeshWorkGroupCountY}, {nativeDevice.MaxMeshWorkGroupCountZ}; total={nativeDevice.MaxMeshWorkGroupTotalCount}), " +
                $"task=({nativeDevice.MaxTaskWorkGroupCountX}, {nativeDevice.MaxTaskWorkGroupCountY}, {nativeDevice.MaxTaskWorkGroupCountZ}; total={nativeDevice.MaxTaskWorkGroupTotalCount}).");

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
        commandList.SetScissor(new GraphicsScissorRect(0, 0, 32, 32));
        commandList.ClearColor(0, new GraphicsClearColor(0, 0, 0, 1));
        commandList.SetRenderPipeline(pipeline);
        commandList.DrawMeshTasks(1, 1, 1);
        commandList.End();

        queue.Submit([commandList], fence, 1);
        WaitForGpu(fence, commandList, 1);
        byte[] pixels = ReadTexturePixels(session, colorTarget, 32, 32);
        AssertPixelNear(pixels, 32, 16, 16, 26, 77, 230, 255);
    }

    private static void NvMeshShaderDirectDraw(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        OpenGLGraphicsDevice nativeDevice = (OpenGLGraphicsDevice)device;
        if (!nativeDevice.HasNvMeshShaderExtension)
            throw new HardwareTestSkippedException("The context does not advertise GL_NV_mesh_shader.");
        if (nativeDevice.NvMeshShaderApi is null)
            throw new GraphicsException("GL_NV_mesh_shader is advertised, but its Silk.NET API wrapper was not initialized.");
        RequireNativeEntryPoint(nativeDevice, "GL_NV_mesh_shader", "glDrawMeshTasksNV");
        if (nativeDevice.MaxNvDrawMeshTasksCount == 0)
            throw new HardwareTestSkippedException($"GL_NV_mesh_shader is advertised, but GL_MAX_DRAW_MESH_TASKS_COUNT_NV is zero. Active adapter: {nativeDevice.Info.DriverName} / {nativeDevice.Info.Name} (GL {nativeDevice.Info.DriverVersion}).");

        using IGraphicsTexture colorTarget = CreateColorTarget(device);
        using IGraphicsTextureView colorView = colorTarget.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        using IGraphicsShader meshShader = CreateShader(
            device,
            GraphicsShaderStage.Mesh,
            """
            #version 450 core
            #extension GL_NV_mesh_shader : require
            layout(local_size_x = 1) in;
            layout(triangles, max_vertices = 3, max_primitives = 1) out;
            void main()
            {
                gl_PrimitiveCountNV = 1;
                gl_MeshVerticesNV[0].gl_Position = vec4(-0.6, -0.5, 0.0, 1.0);
                gl_MeshVerticesNV[1].gl_Position = vec4( 0.6, -0.5, 0.0, 1.0);
                gl_MeshVerticesNV[2].gl_Position = vec4( 0.0,  0.6, 0.0, 1.0);
                gl_PrimitiveIndicesNV[0] = 0;
                gl_PrimitiveIndicesNV[1] = 1;
                gl_PrimitiveIndicesNV[2] = 2;
            }
            """);
        using IGraphicsShader fragmentShader = CreateShader(
            device,
            GraphicsShaderStage.Fragment,
            """
            #version 450 core
            layout(location = 0) out vec4 color;
            void main() { color = vec4(0.7, 0.3, 0.1, 1.0); }
            """);
        using IGraphicsPipelineLayout pipelineLayout = device.CreatePipelineLayout([]);
        IGraphicsShader[] shaders = [meshShader, fragmentShader];
        GraphicsFormat[] formats = [GraphicsFormat.RGBA8UNorm];
        using IGraphicsRenderPipeline pipeline = device.CreateRenderPipeline(
            new GraphicsRenderPipelineDescription(
                pipelineLayout, shaders, GraphicsPrimitiveTopology.TriangleList, formats));

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Graphics);
        using IGraphicsCommandList commands = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();
        IGraphicsTextureView[] targets = [colorView];
        commands.Begin();
        commands.SetRenderTargets(targets);
        commands.SetViewport(new GraphicsViewport(0, 0, 32, 32));
        commands.SetScissor(new GraphicsScissorRect(0, 0, 32, 32));
        commands.ClearColor(0, new GraphicsClearColor(0, 0, 0, 1));
        commands.SetRenderPipeline(pipeline);
        commands.DrawMeshTasks(1, 1, 1);
        commands.End();
        queue.Submit([commands], fence, 1);
        WaitForGpu(fence, commands, 1);

        byte[] pixels = ReadTexturePixels(session, colorTarget, 32, 32);
        AssertPixelNear(pixels, 32, 16, 16, 179, 77, 26, 255);
    }

    private static void NvTaskAndMeshShaderDraw(GlfwOpenGLHardwareSession session)
    {
        IGraphicsDevice device = session.Device;
        OpenGLGraphicsDevice nativeDevice = (OpenGLGraphicsDevice)device;
        if (!nativeDevice.HasNvMeshShaderExtension)
            throw new HardwareTestSkippedException("The context does not advertise GL_NV_mesh_shader.");
        if (nativeDevice.NvMeshShaderApi is null)
            throw new GraphicsException("GL_NV_mesh_shader is advertised, but its Silk.NET API wrapper was not initialized.");
        RequireNativeEntryPoint(nativeDevice, "GL_NV_mesh_shader", "glDrawMeshTasksNV");
        if (!nativeDevice.MeshShaderCapabilities.SupportsTaskShader(OpenGLMeshShaderDialect.Nv))
            throw new HardwareTestSkippedException(
                $"GL_NV_mesh_shader is advertised, but its NV task/mesh dispatch limit is unusable: {nativeDevice.MaxNvDrawMeshTasksCount}. Active adapter: {nativeDevice.Info.DriverName} / {nativeDevice.Info.Name} (GL {nativeDevice.Info.DriverVersion}); process: {Environment.ProcessPath}.");

        using IGraphicsTexture colorTarget = CreateColorTarget(device);
        using IGraphicsTextureView colorView = colorTarget.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        using IGraphicsShader taskShader = CreateShader(
            device,
            GraphicsShaderStage.Task,
            """
            #version 450 core
            #extension GL_NV_mesh_shader : require
            layout(local_size_x = 1) in;
            void main() { gl_TaskCountNV = 1; }
            """);
        using IGraphicsShader meshShader = CreateShader(
            device,
            GraphicsShaderStage.Mesh,
            """
            #version 450 core
            #extension GL_NV_mesh_shader : require
            layout(local_size_x = 1) in;
            layout(triangles, max_vertices = 3, max_primitives = 1) out;
            void main()
            {
                gl_PrimitiveCountNV = 1;
                gl_MeshVerticesNV[0].gl_Position = vec4(-0.6, -0.5, 0.0, 1.0);
                gl_MeshVerticesNV[1].gl_Position = vec4( 0.6, -0.5, 0.0, 1.0);
                gl_MeshVerticesNV[2].gl_Position = vec4( 0.0,  0.6, 0.0, 1.0);
                gl_PrimitiveIndicesNV[0] = 0;
                gl_PrimitiveIndicesNV[1] = 1;
                gl_PrimitiveIndicesNV[2] = 2;
            }
            """);
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
        GraphicsFormat[] formats = [GraphicsFormat.RGBA8UNorm];
        using IGraphicsRenderPipeline pipeline = device.CreateRenderPipeline(
            new GraphicsRenderPipelineDescription(
                pipelineLayout, shaders, GraphicsPrimitiveTopology.TriangleList, formats));

        IGraphicsCommandQueue queue = device.GetQueue(GraphicsQueueType.Graphics);
        using IGraphicsCommandList commands = queue.CreateCommandList();
        using IGraphicsFence fence = device.CreateFence();
        IGraphicsTextureView[] targets = [colorView];
        commands.Begin();
        commands.SetRenderTargets(targets);
        commands.SetViewport(new GraphicsViewport(0, 0, 32, 32));
        commands.SetScissor(new GraphicsScissorRect(0, 0, 32, 32));
        commands.ClearColor(0, new GraphicsClearColor(0, 0, 0, 1));
        commands.SetRenderPipeline(pipeline);
        commands.DrawMeshTasks(1, 1, 1);
        commands.End();
        queue.Submit([commands], fence, 1);
        WaitForGpu(fence, commands, 1);

        byte[] pixels = ReadTexturePixels(session, colorTarget, 32, 32);
        AssertPixelNear(pixels, 32, 16, 16, 26, 77, 230, 255);
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

    private static IGraphicsShader CreateNvMeshShader(IGraphicsDevice device) =>
        CreateShader(
            device,
            GraphicsShaderStage.Mesh,
            """
            #version 450 core
            #extension GL_NV_mesh_shader : require
            layout(local_size_x = 1) in;
            layout(triangles, max_vertices = 3, max_primitives = 1) out;
            void main()
            {
                gl_PrimitiveCountNV = 1;
                gl_MeshVerticesNV[0].gl_Position = vec4(-0.6, -0.5, 0.0, 1.0);
                gl_MeshVerticesNV[1].gl_Position = vec4( 0.6, -0.5, 0.0, 1.0);
                gl_MeshVerticesNV[2].gl_Position = vec4( 0.0,  0.6, 0.0, 1.0);
                gl_PrimitiveIndicesNV[0] = 0;
                gl_PrimitiveIndicesNV[1] = 1;
                gl_PrimitiveIndicesNV[2] = 2;
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
        AssertUInt(actual, expected);
    }

    private static void AssertUInt(uint actual, uint expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"GPU output mismatch: expected 0x{expected:X8}, received 0x{actual:X8}.");
    }

    private static uint ReadUInt(IGraphicsBuffer buffer) =>
        BinaryPrimitives.ReadUInt32LittleEndian(ReadBuffer(buffer, sizeof(uint)));

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
