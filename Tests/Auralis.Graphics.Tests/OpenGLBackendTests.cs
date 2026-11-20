using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGL;
using Auralis.Graphics.Devices;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class OpenGLBackendTests
{
    [Fact]
    public void OpenGLBackendIsAppendedWithoutChangingExistingBackendValues()
    {
        Assert.Equal(0, (int)GraphicsBackend.Direct3D12);
        Assert.Equal(1, (int)GraphicsBackend.Vulkan);
        Assert.Equal(2, (int)GraphicsBackend.Metal);
        Assert.Equal(3, (int)GraphicsBackend.OpenGL);
    }

    [Fact]
    public void ProviderRequiresAnAvailableInjectedContext()
    {
        var context = new TestContextProvider(isAvailable: false);
        var provider = new OpenGLGraphicsBackendProvider(context);

        Assert.Equal(GraphicsBackend.OpenGL, provider.Backend);
        Assert.False(provider.IsAvailable);
        Assert.Equal("No current desktop GL context.", provider.UnavailableReason);
        Assert.Throws<GraphicsBackendUnavailableException>(
            () => provider.EnumerateAdaptersAsync());
    }

    [Fact]
    public async Task ProviderDoesNotInventSystemWideAdapterInformation()
    {
        var provider = new OpenGLGraphicsBackendProvider(new TestContextProvider(true));

        ReadOnlyMemory<GraphicsAdapterInfo> adapters = await provider.EnumerateAdaptersAsync();

        Assert.Empty(adapters.ToArray());
    }

    [Fact]
    public void ProviderCanBeRegisteredExplicitlyWithoutCreatingAContext()
    {
        var context = new TestContextProvider(isAvailable: false);
        var provider = new OpenGLGraphicsBackendProvider(context);
        var registry = new GraphicsBackendProviderRegistry();

        registry.Register(provider);

        Assert.True(registry.TryGetProvider(GraphicsBackend.OpenGL, out var registered));
        Assert.Same(provider, registered);
        Assert.Throws<InvalidOperationException>(
            () => registry.Register(new OpenGLGraphicsBackendProvider(new TestContextProvider(false))));
    }

    [Fact]
    public void ContextProviderOwnershipIsExplicit()
    {
        var borrowed = new TestContextProvider(true, OpenGLContextOwnership.Borrowed);
        var owned = new TestContextProvider(true, OpenGLContextOwnership.Owned);

        Assert.Equal(OpenGLContextOwnership.Borrowed, borrowed.Ownership);
        Assert.Equal(OpenGLContextOwnership.Owned, owned.Ownership);
        Assert.Same(borrowed.ContextIdentity, borrowed.ContextIdentity);
    }

    private sealed class TestContextProvider(
        bool isAvailable,
        OpenGLContextOwnership ownership = OpenGLContextOwnership.Borrowed) : IOpenGLContextProvider
    {
        public object ContextIdentity { get; } = new object();
        public OpenGLContextOwnership Ownership { get; } = ownership;
        public bool IsAvailable { get; } = isAvailable;
        public string? UnavailableReason => IsAvailable ? null : "No current desktop GL context.";
        public bool SupportsPresentation => false;

        public void MakeCurrent() => throw new NotSupportedException();
        public void ReleaseCurrent() => throw new NotSupportedException();
        public nint GetProcAddress(string name) => 0;
        public void SwapBuffers() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
