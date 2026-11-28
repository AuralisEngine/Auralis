using Auralis.Graphics.Direct3D12.Runtime;
using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

public sealed class D3D12RuntimeAvailabilityTests
{
    [Fact]
    public void NonWindowsHostIsReportedUnavailable()
    {
        D3D12RuntimeStatus status = D3D12RuntimeAvailability.Evaluate(
            isWindows: false,
            hasDxgi: true,
            hasD3D12: true);

        Assert.False(status.IsAvailable);
        Assert.Equal(
            "The Direct3D 12 backend is available only on Windows.",
            status.UnavailableReason);
    }

    [Fact]
    public void MissingDxgiIsReportedBeforeD3D12()
    {
        D3D12RuntimeStatus status = D3D12RuntimeAvailability.Evaluate(
            isWindows: true,
            hasDxgi: false,
            hasD3D12: false);

        Assert.False(status.IsAvailable);
        Assert.Equal(
            "The DXGI system library (dxgi.dll) could not be loaded from System32.",
            status.UnavailableReason);
    }

    [Fact]
    public void MissingD3D12RuntimeIsReported()
    {
        D3D12RuntimeStatus status = D3D12RuntimeAvailability.Evaluate(
            isWindows: true,
            hasDxgi: true,
            hasD3D12: false);

        Assert.False(status.IsAvailable);
        Assert.Equal(
            "The Direct3D 12 system library (d3d12.dll) could not be loaded from System32.",
            status.UnavailableReason);
    }

    [Fact]
    public void BothNativeLibrariesProduceLoaderAvailableStatus()
    {
        D3D12RuntimeStatus status = D3D12RuntimeAvailability.Evaluate(
            isWindows: true,
            hasDxgi: true,
            hasD3D12: true);

        Assert.True(status.IsAvailable);
        Assert.Null(status.UnavailableReason);
    }

    [Fact]
    public void CurrentHostProbeAlwaysReturnsAConsistentStatus()
    {
        D3D12RuntimeStatus status = D3D12RuntimeAvailability.Probe();

        Assert.Equal(status.IsAvailable, status.UnavailableReason is null);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(status.IsAvailable);
        }
    }
}
