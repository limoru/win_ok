using System.Text;
using Automation.MemoryProbe;
using Xunit;

namespace Automation.Core.Tests;

public sealed class MemoryPatternSearchTests
{
    [Fact]
    public void FindsAllOverlappingUtf8Matches()
    {
        var haystack = Encoding.UTF8.GetBytes("xabcabcx");

        var offsets = MemoryPatternSearch.FindOffsets(haystack, Encoding.UTF8.GetBytes("abc"));

        Assert.Equal([1, 4], offsets);
    }

    [Fact]
    public void FindsUtf16LittleEndianPattern()
    {
        var haystack = Encoding.Unicode.GetBytes("page:献上赞美;next");
        var needle = Encoding.Unicode.GetBytes("献上赞美");

        var offsets = MemoryPatternSearch.FindOffsets(haystack, needle);

        Assert.Single(offsets);
        Assert.Equal(10, offsets[0]);
    }

    [Theory]
    [InlineData(0x02u, true)]
    [InlineData(0x04u, true)]
    [InlineData(0x20u, true)]
    [InlineData(0x40u, true)]
    [InlineData(0x01u, false)]
    [InlineData(0x104u, false)]
    [InlineData(0x10u, false)]
    public void ReadableProtectionClassificationIsFailClosed(uint protection, bool expected)
    {
        Assert.Equal(expected, ProcessMemorySession.IsReadableProtection(protection));
    }

    [Theory]
    [InlineData(5, ProcessMemoryProbeStatus.ReadDenied)]
    [InlineData(299, ProcessMemoryProbeStatus.ReadDenied)]
    [InlineData(487, ProcessMemoryProbeStatus.ReadFailed)]
    public void ReadFailureClassificationIsExplicit(int win32Error, ProcessMemoryProbeStatus expected)
    {
        Assert.Equal(expected, ProcessMemorySession.ClassifyReadFailure(win32Error));
    }

    [Theory]
    [InlineData(87, ProcessMemoryProbeStatus.ProcessNotFound)]
    [InlineData(1168, ProcessMemoryProbeStatus.ProcessNotFound)]
    [InlineData(5, ProcessMemoryProbeStatus.AccessDenied)]
    public void OpenFailureClassificationIsExplicit(int win32Error, ProcessMemoryProbeStatus expected)
    {
        Assert.Equal(expected, ProcessMemorySession.ClassifyOpenFailure(win32Error));
    }
}