using System.Runtime.InteropServices;
using Auralis.Graphics.Direct3D12.Interop;
using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

public sealed class D3D12HResultTests
{
    [Theory]
    [InlineData(0)] // S_OK
    [InlineData(1)] // S_FALSE
    [InlineData(2)] // Other non-negative success status
    public void ThrowIfFailedAcceptsEveryNonNegativeStatus(int hresult)
    {
        D3D12HResult.ThrowIfFailed(hresult, "ID3D12Device::CreateCommandQueue");
    }

    [Theory]
    [InlineData(unchecked((int)0x80004005))]
    [InlineData(unchecked((int)0x80070057))]
    [InlineData(unchecked((int)0x887A0005))]
    public void ThrowIfFailedPreservesOperationAndOriginalHResult(int hresult)
    {
        const string operation = "ID3D12Device::CreateCommandQueue";

        D3D12NativeException exception = Assert.Throws<D3D12NativeException>(
            () => D3D12HResult.ThrowIfFailed(hresult, operation));

        string expectedHex = $"0x{unchecked((uint)hresult):X8}";
        Assert.Equal(operation, exception.Operation);
        Assert.Equal(hresult, exception.HResultCode);
        Assert.Equal(unchecked((uint)hresult), exception.UnsignedHResultCode);
        Assert.Equal(hresult, exception.HResult);
        Assert.Contains(operation, exception.Message);
        Assert.Contains(expectedHex, exception.Message);

        COMException innerException = Assert.IsType<COMException>(exception.InnerException);
        Assert.Equal(hresult, innerException.HResult);
    }

    [Fact]
    public void NativeExceptionFactoryRejectsSuccessStatuses()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => D3D12NativeException.Create("ID3D12Device::CreateCommandQueue", 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void NativeExceptionFactoryRejectsMissingOperation(string operation)
    {
        Assert.Throws<ArgumentException>(
            () => D3D12NativeException.Create(operation, unchecked((int)0x80004005)));
    }
}
