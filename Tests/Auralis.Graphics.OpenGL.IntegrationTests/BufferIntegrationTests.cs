using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Xunit;

namespace Auralis.Graphics.OpenGL.IntegrationTests;

[Collection(OpenGLIntegrationCollection.Name)]
public sealed class BufferIntegrationTests(OpenGLIntegrationFixture fixture)
{
    [Fact]
    public void CpuWritableBufferCanBeMappedAndReadBack()
    {
        const int length = 4096;
        using IGraphicsBuffer buffer = fixture.Device.CreateBuffer(
            new GraphicsBufferDescription(
                length,
                GraphicsBufferUsage.CopySource,
                GraphicsMemoryAccess.CpuToGpu));

        Memory<byte> mapped = buffer.Map(GraphicsMapMode.Write);
        try
        {
            for (int i = 0; i < length; i++)
                mapped.Span[i] = Pattern(i);
        }
        finally
        {
            buffer.Unmap();
        }

        mapped = buffer.Map(GraphicsMapMode.Read);
        try
        {
            for (int i = 0; i < length; i++)
                Assert.Equal(Pattern(i), mapped.Span[i]);
        }
        finally
        {
            buffer.Unmap();
        }
    }

    [Fact]
    public void SubmittedBufferCopyIsVisibleAfterFenceCompletion()
    {
        const int length = 16 * 1024;

        using IGraphicsBuffer source = fixture.Device.CreateBuffer(
            new GraphicsBufferDescription(
                length,
                GraphicsBufferUsage.CopySource,
                GraphicsMemoryAccess.CpuToGpu));

        using IGraphicsBuffer destination = fixture.Device.CreateBuffer(
            new GraphicsBufferDescription(
                length,
                GraphicsBufferUsage.CopyDestination,
                GraphicsMemoryAccess.GpuToCpu));

        Memory<byte> sourceMemory = source.Map(GraphicsMapMode.Write);
        try
        {
            for (int i = 0; i < length; i++)
                sourceMemory.Span[i] = Pattern(i);
        }
        finally
        {
            source.Unmap();
        }

        IGraphicsCommandQueue queue = fixture.Device.GetQueue(GraphicsQueueType.Copy);
        using IGraphicsCommandList commands = queue.CreateCommandList();
        using IGraphicsFence fence = fixture.Device.CreateFence();

        commands.Begin();
        commands.CopyBuffer(source, 0, destination, 0, length);
        commands.End();

        IGraphicsCommandList[] submission = [commands];
        queue.Submit(submission, fence, 1);

        Assert.True(
            fence.Wait(1, TimeSpan.FromSeconds(15)),
            "The GPU did not complete the buffer-copy submission within 15 seconds.");

        Memory<byte> destinationMemory = destination.Map(GraphicsMapMode.Read);
        try
        {
            for (int i = 0; i < length; i++)
                Assert.Equal(Pattern(i), destinationMemory.Span[i]);
        }
        finally
        {
            destination.Unmap();
        }
    }

    private static byte Pattern(int index) =>
        unchecked((byte)((index * 31) ^ (index >> 3) ^ 0xA7));
}
