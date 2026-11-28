using System.Runtime.InteropServices;
using Auralis.Graphics.Direct3D12.Interop;
using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

public sealed class D3D12HResultTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ThrowIfFailedAcceptsNonNegativeSuccessStatuses(int hresult)
    {
        D3D12HResult.ThrowIfFailed(hresult, "ID3D12Device::CreateCommandQueue");
    }

    [Fact]
    public void ThrowIfFailedPreservesOperationAndOriginalHResult()
    {
        const int deviceRemoved = unchecked((int)0x887A0005);

        D3D12NativeException exception = Assert.Throws<D3D12NativeException>(
            () => D3D12HResult.ThrowIfFailed(
                deviceRemoved,
                "ID3D12Device::CreateCommandQueue"));

        Assert.Equal("ID3D12Device::CreateCommandQueue", exception.Operation);
        Assert.Equal(deviceRemoved, exception.HResultCode);
        Assert.Contains("0x887A0005", exception.Message);

        COMException innerException = Assert.IsType<COMException>(exception.InnerException);
        Assert.Equal(deviceRemoved, innerException.HResult);
    }

    [Fact]
    public void NativeExceptionFormatsUnsignedHexForHighBitHresults()
    {
        const int hresult = unchecked((int)0x80070057);

        D3D12NativeException exception = D3D12NativeException.Create(
            "ID3D12Device::CreateCommittedResource",
            hresult);

        Assert.Contains("0x80070057", exception.Message);
        Assert.Equal(unchecked((uint)hresult), unchecked((uint)exception.HResultCode));
    }

    [Fact]
    public void NativeExceptionFactoryRejectsSuccessStatuses()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => D3D12NativeException.Create("ID3D12Device::CreateCommandQueue", 1));
    }
}
