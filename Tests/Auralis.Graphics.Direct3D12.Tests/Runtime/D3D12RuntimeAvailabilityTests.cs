using Auralis.Graphics.Direct3D12.Runtime;

namespace Auralis.Graphics.Direct3D12.Tests.Runtime;

public sealed class D3D12RuntimeAvailabilityTests
{
    [Fact]
    public void Evaluate_RejectsNonWindowsBeforeInspectingLibraries()
    {
        D3D12RuntimeAvailability result = D3D12RuntimeAvailability.Evaluate(
            isWindows: false, dxgiAvailable: true, direct3D12Available: true);

        Assert.Equal(D3D12RuntimeStatus.UnsupportedPlatform, result.Status);
        Assert.False(result.IsAvailable);
        Assert.NotNull(result.UnavailableReason!);
        Assert.Contains("Windows", result.UnavailableReason);
    }

    [Fact]
    public void Evaluate_ReportsMissingDxgi()
    {
        D3D12RuntimeAvailability result = D3D12RuntimeAvailability.Evaluate(
            isWindows: true, dxgiAvailable: false, direct3D12Available: false);

        Assert.Equal(D3D12RuntimeStatus.MissingDxgi, result.Status);
        Assert.NotNull(result.UnavailableReason);
        Assert.Contains("dxgi.dll", result.UnavailableReason);
    }

    [Fact]
    public void Evaluate_ReportsMissingDirect3D12()
    {
        D3D12RuntimeAvailability result = D3D12RuntimeAvailability.Evaluate(
            isWindows: true, dxgiAvailable: true, direct3D12Available: false);

        Assert.Equal(D3D12RuntimeStatus.MissingDirect3D12, result.Status);
        Assert.NotNull(result.UnavailableReason);
        Assert.Contains("d3d12.dll", result.UnavailableReason);
    }

    [Fact]
    public void Evaluate_ReportsLoaderAvailabilityWithoutClaimingDeviceSupport()
    {
        D3D12RuntimeAvailability result = D3D12RuntimeAvailability.Evaluate(
            isWindows: true, dxgiAvailable: true, direct3D12Available: true);

        Assert.Equal(D3D12RuntimeStatus.Available, result.Status);
        Assert.True(result.IsAvailable);
        Assert.Null(result.UnavailableReason);
    }

    [Fact]
    public void Probe_ReturnsAConsistentResultForTheCurrentPlatform()
    {
        D3D12RuntimeAvailability result = D3D12RuntimeAvailability.Probe();

        if (!OperatingSystem.IsWindows())
            Assert.Equal(D3D12RuntimeStatus.UnsupportedPlatform, result.Status);
        else
            Assert.NotEqual(D3D12RuntimeStatus.UnsupportedPlatform, result.Status);
    }
}
