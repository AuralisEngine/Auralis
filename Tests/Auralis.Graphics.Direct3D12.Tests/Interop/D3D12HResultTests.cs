using System.Runtime.InteropServices;
using Auralis.Graphics.Direct3D12.Interop;

namespace Auralis.Graphics.Direct3D12.Tests.Interop;

public sealed class D3D12HResultTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0x00000001)]
    public void ThrowIfFailed_AcceptsNonNegativeStatusCodes(int hresult)
    {
        D3D12HResult.ThrowIfFailed(hresult, "Test operation");
    }

    [Fact]
    public void ThrowIfFailed_PreservesFailureCodeAndOperation()
    {
        const int hresult = unchecked((int)0x887A0005);

        D3D12NativeException exception = Assert.Throws<D3D12NativeException>(
            () => D3D12HResult.ThrowIfFailed(hresult, "Create device"));

        Assert.Equal(hresult, exception.NativeHResult);
        Assert.Equal("Create device", exception.Operation);
        Assert.Contains("0x887A0005", exception.Message);
        Assert.IsType<COMException>(exception.InnerException);
        Assert.Equal(hresult, exception.InnerException.HResult);
    }

    [Fact]
    public void NativeException_RejectsSuccessfulHResult()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12NativeException(0, "Create device"));
    }

    [Fact]
    public void ThrowIfFailed_RejectsBlankOperation()
    {
        Assert.Throws<ArgumentException>(() => D3D12HResult.ThrowIfFailed(unchecked((int)0x80004005), " "));
    }
}
