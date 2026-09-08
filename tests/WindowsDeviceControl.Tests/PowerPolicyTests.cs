using System;
using System.ComponentModel;
using System.Text;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class PowerPolicyTests
{
    private static readonly Guid Custom = new("d0905d52-3ba4-4fe9-a463-e6d96ac80e93");
    [Theory]
    [InlineData("Höchstleistung")]
    [InlineData("省電力")]
    public void DecodesLocalizedUtf16NamesUsingTheReturnedByteCount(string name)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(name + "\0ignored padding");
        Assert.Equal(name, WindowsPower.DecodeName(bytes, (uint)(name.Length + 1) * 2, Custom));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    public void RejectsMalformedNameLengthsOrMissingTerminators(uint size)
    {
        byte[] bytes = Encoding.Unicode.GetBytes("abc\0");
        var error = Assert.Throws<Win32Exception>(() => WindowsPower.DecodeName(bytes, size, Custom));
        Assert.Equal(13, error.NativeErrorCode);
    }

    [Fact]
    public void EmptyNameFallsBackToStableGuid()
        => Assert.Equal(Custom.ToString("D"), WindowsPower.DecodeName([0, 0], 2, Custom));

}
