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
        Assert.NotNull(status.UnavailableReason);
        Assert.Contains("only on Windows", status.UnavailableReason);
    }

    [Fact]
    public void MissingDxgiIsReportedBeforeD3D12()
    {
        D3D12RuntimeStatus status = D3D12RuntimeAvailability.Evaluate(
            isWindows: true,
            hasDxgi: false,
            hasD3D12: false);

        Assert.False(status.IsAvailable);
        Assert.Contains("dxgi.dll", status.UnavailableReason);
    }

    [Fact]
    public void MissingD3D12RuntimeIsReported()
    {
        D3D12RuntimeStatus status = D3D12RuntimeAvailability.Evaluate(
            isWindows: true,
            hasDxgi: true,
            hasD3D12: false);

        Assert.False(status.IsAvailable);
        Assert.Contains("d3d12.dll", status.UnavailableReason);
    }

    [Fact]
    public void BothNativeLibrariesProduceAvailableLoaderStatus()
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
            Assert.False(status.IsAvailable);
    }
}
