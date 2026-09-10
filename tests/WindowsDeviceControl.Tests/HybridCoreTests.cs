using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class HybridCoreTests
{
    private const int EntryBytes = 32;

    [Fact]
    public void GroupsLogicalProcessorsAndDistinctCoresByEfficiencyClass()
    {
        // Two SMT threads on one performant core, then two single-threaded efficient cores.
        byte[] buffer = Buffer(
            (group: 0, core: 0, efficiency: 1),
            (group: 0, core: 0, efficiency: 1),
            (group: 0, core: 1, efficiency: 0),
            (group: 0, core: 2, efficiency: 0));

        IReadOnlyList<HybridCoreClass> classes = WindowsPower.ParseCpuSets(buffer);

        Assert.Equal([new(0, 2, 2), new(1, 1, 2)], classes);
    }

    [Fact]
    public void SeparatesEqualCoreIndexesInDifferentProcessorGroups()
    {
        byte[] buffer = Buffer((group: 0, core: 3, efficiency: 0), (group: 1, core: 3, efficiency: 0));

        Assert.Equal([new HybridCoreClass(0, 2, 2)], WindowsPower.ParseCpuSets(buffer));
    }

    [Fact]
    public void SkipsEntriesOfOtherInformationTypesUsingTheirOwnSize()
    {
        byte[] buffer = Buffer((group: 0, core: 0, efficiency: 2));
        byte[] mixed = new byte[EntryBytes + buffer.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(mixed, EntryBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(mixed.AsSpan(4), 7);
        buffer.CopyTo(mixed, EntryBytes);

        Assert.Equal([new HybridCoreClass(2, 1, 1)], WindowsPower.ParseCpuSets(mixed));
    }

    [Fact]
    public void EmptyInformationReportsNoClasses() => Assert.Empty(WindowsPower.ParseCpuSets([]));

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    [InlineData(64u)]
    public void RejectsEntrySizesOutsideTheBuffer(uint size)
    {
        byte[] buffer = Buffer((group: 0, core: 0, efficiency: 0));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, size);

        var error = Assert.Throws<Win32Exception>(() => WindowsPower.ParseCpuSets(buffer));
        Assert.Equal(13, error.NativeErrorCode);
    }

    [Fact]
    public void RejectsACpuSetEntryTruncatedBeforeItsEfficiencyClass()
    {
        byte[] buffer = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 16);

        var error = Assert.Throws<Win32Exception>(() => WindowsPower.ParseCpuSets(buffer));
        Assert.Equal(13, error.NativeErrorCode);
    }

    [Fact]
    public void StopsAtATrailingFragmentTooShortForAHeader()
    {
        byte[] buffer = Buffer((group: 0, core: 0, efficiency: 0));
        Array.Resize(ref buffer, buffer.Length + 4);

        Assert.Equal([new HybridCoreClass(0, 1, 1)], WindowsPower.ParseCpuSets(buffer));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void HybridRequiresMoreThanOneEfficiencyClass(int classes, bool hybrid)
    {
        List<HybridCoreClass> observed = [];
        for (byte index = 0; index < classes; index++) { observed.Add(new(index, 1, 1)); }

        Assert.Equal(hybrid, new HybridCoreSupport(observed, true, [], [], []).Hybrid);
    }

    private static byte[] Buffer(params (int group, byte core, byte efficiency)[] processors)
    {
        byte[] buffer = new byte[processors.Length * EntryBytes];
        for (int index = 0; index < processors.Length; index++)
        {
            Span<byte> entry = buffer.AsSpan(index * EntryBytes, EntryBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, EntryBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)index);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[12..], (ushort)processors[index].group);
            entry[14] = (byte)index;
            entry[15] = processors[index].core;
            entry[18] = processors[index].efficiency;
        }
        return buffer;
    }
}
