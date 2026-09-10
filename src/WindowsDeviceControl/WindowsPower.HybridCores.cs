using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsDeviceControl;

/// <summary>Windows heterogeneous thread scheduling policy values.</summary>
/// <remarks>
///     These are the indexes Windows publishes for the processor subgroup settings SCHEDPOLICY and
///     SHORTSCHEDPOLICY. A machine may publish a value this enumeration does not name; such a value is
///     preserved as its raw number rather than replaced.
/// </remarks>
public enum HybridSchedulingPolicy : uint
{
    /// <summary>All processors are eligible.</summary>
    AllProcessors = 0,

    /// <summary>Only processors of the most performant efficiency class are eligible.</summary>
    PerformantProcessors = 1,

    /// <summary>Performant processors are preferred; other classes remain eligible.</summary>
    PreferPerformantProcessors = 2,

    /// <summary>Only processors of the most efficient class are eligible.</summary>
    EfficientProcessors = 3,

    /// <summary>Efficient processors are preferred; other classes remain eligible.</summary>
    PreferEfficientProcessors = 4,

    /// <summary>Windows chooses the placement.</summary>
    Automatic = 5,
}

/// <summary>One processor efficiency class reported by Windows CPU set information.</summary>
/// <param name="EfficiencyClass">
///     Windows efficiency class. Higher values are more performant; the highest observed class is the
///     P-core class on Intel hybrid parts.
/// </param>
/// <param name="Cores">Distinct physical cores observed in this class.</param>
/// <param name="LogicalProcessors">Logical processors observed in this class.</param>
public sealed record HybridCoreClass(byte EfficiencyClass, int Cores, int LogicalProcessors);

/// <summary>What a machine and one power scheme actually support for hybrid core placement.</summary>
/// <param name="Classes">Observed efficiency classes, ordered from most efficient to most performant.</param>
/// <param name="Configurable">
///     Whether all three hybrid policy settings could be read from the queried scheme. False means the
///     controls are not present and must not be offered.
/// </param>
/// <param name="HeterogeneousPolicies">
///     Values Windows publishes for HETEROPOLICY. Windows exposes no semantic names for these; empty
///     when the machine publishes no enumeration.
/// </param>
/// <param name="SchedulingPolicies">Values Windows publishes for SCHEDPOLICY.</param>
/// <param name="ShortSchedulingPolicies">Values Windows publishes for SHORTSCHEDPOLICY.</param>
public sealed record HybridCoreSupport(
    IReadOnlyList<HybridCoreClass> Classes,
    bool Configurable,
    IReadOnlyList<uint> HeterogeneousPolicies,
    IReadOnlyList<HybridSchedulingPolicy> SchedulingPolicies,
    IReadOnlyList<HybridSchedulingPolicy> ShortSchedulingPolicies)
{
    /// <summary>Whether the machine reports more than one processor efficiency class.</summary>
    public bool Hybrid => Classes.Count > 1;
}

/// <summary>Stored hybrid core placement values for one scheme and one power source.</summary>
/// <param name="HeterogeneousPolicy">Raw HETEROPOLICY value; Windows publishes no semantic names for it.</param>
/// <param name="Threads">SCHEDPOLICY value applied to ordinary threads.</param>
/// <param name="ShortThreads">SHORTSCHEDPOLICY value applied to short-running threads.</param>
public sealed record HybridCoreState(
    uint HeterogeneousPolicy, HybridSchedulingPolicy Threads, HybridSchedulingPolicy ShortThreads);

public static partial class WindowsPower
{
    /// <summary>Processor power settings subgroup (SUB_PROCESSOR).</summary>
    public static readonly Guid SubgroupProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

    /// <summary>Effective heterogeneous policy setting (HETEROPOLICY).</summary>
    public static readonly Guid SettingHeterogeneousPolicy = new("7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5");

    /// <summary>Heterogeneous thread scheduling policy setting (SCHEDPOLICY).</summary>
    public static readonly Guid SettingThreadSchedulingPolicy = new("93b8b6dc-0698-4d1c-9ee4-0644e900c85d");

    /// <summary>Heterogeneous short-running thread scheduling policy setting (SHORTSCHEDPOLICY).</summary>
    public static readonly Guid SettingShortThreadSchedulingPolicy = new("bae08b81-2d5e-4688-ad6a-13243356654b");

    private const uint ErrorFileNotFound = 2;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint RegDword = 4;
    private const uint CpuSetInformationType = 0;
    private const int CpuSetHeaderBytes = 8;
    private const int CpuSetEntryBytes = 32;
    private const uint MaximumCpuSetBytes = 1 << 20;
    private const uint MaximumPossibleValues = 64;

    /// <summary>Reports the machine's efficiency classes and whether one scheme exposes hybrid policy.</summary>
    /// <remarks>
    ///     Reads Windows CPU set information and the scheme's stored policy values. Offer hybrid core
    ///     controls only when <see cref="HybridCoreSupport.Hybrid"/> and
    ///     <see cref="HybridCoreSupport.Configurable"/> are both true. The published value lists are what
    ///     this Windows build accepts; a build that publishes none returns empty lists rather than an
    ///     invented range.
    /// </remarks>
    /// <param name="scheme">Power scheme identity to probe.</param>
    /// <returns>Observed hardware classes and per-scheme configurability.</returns>
    public static HybridCoreSupport QueryHybridCores(Guid scheme)
    {
        IReadOnlyList<HybridCoreClass> classes = ParseCpuSets(ReadCpuSetInformation());
        bool configurable = CanRead(scheme, SettingHeterogeneousPolicy)
            && CanRead(scheme, SettingThreadSchedulingPolicy)
            && CanRead(scheme, SettingShortThreadSchedulingPolicy);
        return new(classes, configurable,
            PossibleValues(SettingHeterogeneousPolicy),
            PossiblePolicies(SettingThreadSchedulingPolicy),
            PossiblePolicies(SettingShortThreadSchedulingPolicy));
    }

    /// <summary>Reads the stored hybrid core placement values. Native failures throw Win32Exception.</summary>
    /// <remarks>Capture this before a write and retain it until a restore succeeds.</remarks>
    /// <param name="scheme">Power scheme identity.</param>
    /// <param name="onBattery">True selects the DC values; false selects AC.</param>
    /// <returns>The stored values, with unnamed policy numbers preserved.</returns>
    public static HybridCoreState ReadHybridCores(Guid scheme, bool onBattery) => new(
        ReadSetting(scheme, SubgroupProcessor, SettingHeterogeneousPolicy, onBattery),
        (HybridSchedulingPolicy)ReadSetting(scheme, SubgroupProcessor, SettingThreadSchedulingPolicy, onBattery),
        (HybridSchedulingPolicy)ReadSetting(scheme, SubgroupProcessor, SettingShortThreadSchedulingPolicy, onBattery));

    /// <summary>Writes the three hybrid core placement values once, in order.</summary>
    /// <remarks>
    ///     Does not activate the scheme, retry, or roll back: a failure throws with the earlier writes
    ///     already stored, and the caller restores from its own snapshot. Processor policy written to the
    ///     active scheme takes effect after <see cref="RefreshActiveScheme"/>. Confirm with
    ///     <see cref="ReadHybridCores"/>.
    /// </remarks>
    /// <param name="scheme">Power scheme identity.</param>
    /// <param name="onBattery">True selects DC; false selects AC.</param>
    /// <param name="state">Values to store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    public static void WriteHybridCores(Guid scheme, bool onBattery, HybridCoreState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        WriteSetting(scheme, SubgroupProcessor, SettingHeterogeneousPolicy, onBattery, state.HeterogeneousPolicy);
        WriteSetting(scheme, SubgroupProcessor, SettingThreadSchedulingPolicy, onBattery, (uint)state.Threads);
        WriteSetting(scheme, SubgroupProcessor, SettingShortThreadSchedulingPolicy, onBattery, (uint)state.ShortThreads);
    }

    /// <summary>Re-activates the current scheme so stored policy values take effect.</summary>
    /// <remarks>Windows applies processor policy on scheme activation, not on the write itself.</remarks>
    public static void RefreshActiveScheme() => SetActiveScheme(GetActiveScheme());

    /// <summary>Reads one published value for a setting, or null past the end of the enumeration.</summary>
    /// <param name="subgroup">Policy subgroup identity.</param>
    /// <param name="setting">Policy setting identity.</param>
    /// <param name="index">Zero-based enumeration index.</param>
    /// <returns>The published raw value, or null when the setting publishes no value at that index.</returns>
    public static uint? ReadPossibleValue(Guid subgroup, Guid setting, uint index)
    {
        uint size = sizeof(uint);
        uint status = PowerReadPossibleValue(0, in subgroup, in setting, out uint type, index, out uint value, ref size);
        // Past the last published value, and for a setting that publishes none, Windows reports the
        // value as missing. A value too large for a DWORD is not an index enumeration either.
        if (status is ErrorFileNotFound or ErrorNoMoreItems or ErrorMoreData)
        {
            return null;
        }
        Check(status, "PowerReadPossibleValue");
        return type == RegDword && size == sizeof(uint) ? value : null;
    }

    internal static IReadOnlyList<HybridCoreClass> ParseCpuSets(ReadOnlySpan<byte> buffer)
    {
        Dictionary<byte, (int Logical, HashSet<int> Cores)> classes = [];
        int offset = 0;
        while (offset + CpuSetHeaderBytes <= buffer.Length)
        {
            ReadOnlySpan<byte> entry = buffer[offset..];
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            if (size < CpuSetHeaderBytes || size > (uint)entry.Length)
            {
                throw Failure(ErrorInvalidData, "GetSystemCpuSetInformation");
            }
            if (type == CpuSetInformationType)
            {
                if (size < CpuSetEntryBytes)
                {
                    throw Failure(ErrorInvalidData, "GetSystemCpuSetInformation");
                }
                int core = (BinaryPrimitives.ReadUInt16LittleEndian(entry[12..]) << 8) | entry[15];
                byte efficiency = entry[18];
                if (!classes.TryGetValue(efficiency, out var observed))
                {
                    observed = (0, []);
                }
                observed.Cores.Add(core);
                classes[efficiency] = (observed.Logical + 1, observed.Cores);
            }
            offset += (int)size;
        }
        List<HybridCoreClass> result = [];
        foreach (byte efficiency in SortedKeys(classes))
        {
            (int logical, HashSet<int> cores) = classes[efficiency];
            result.Add(new(efficiency, cores.Count, logical));
        }
        return result;
    }

    private static List<byte> SortedKeys(Dictionary<byte, (int Logical, HashSet<int> Cores)> classes)
    {
        List<byte> keys = new(classes.Keys);
        keys.Sort();
        return keys;
    }

    private static bool CanRead(Guid scheme, Guid setting)
    {
        try
        {
            ReadSetting(scheme, SubgroupProcessor, setting, false);
            ReadSetting(scheme, SubgroupProcessor, setting, true);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static List<uint> PossibleValues(Guid setting)
    {
        List<uint> values = [];
        for (uint index = 0; index < MaximumPossibleValues; index++)
        {
            if (ReadPossibleValue(SubgroupProcessor, setting, index) is not { } value)
            {
                break;
            }
            values.Add(value);
        }
        return values;
    }

    private static List<HybridSchedulingPolicy> PossiblePolicies(Guid setting)
    {
        List<HybridSchedulingPolicy> policies = [];
        foreach (uint value in PossibleValues(setting))
        {
            policies.Add((HybridSchedulingPolicy)value);
        }
        return policies;
    }

    private static unsafe byte[] ReadCpuSetInformation()
    {
        if (!GetSystemCpuSetInformation(null, 0, out uint required, GetCurrentProcess(), 0))
        {
            uint error = (uint)Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer)
            {
                throw Failure(error, "GetSystemCpuSetInformation");
            }
        }
        if (required is 0 or > MaximumCpuSetBytes)
        {
            throw Failure(ErrorInvalidData, "GetSystemCpuSetInformation");
        }
        byte[] buffer = new byte[required];
        uint written;
        fixed (byte* pointer = buffer)
        {
            if (!GetSystemCpuSetInformation(pointer, required, out written, GetCurrentProcess(), 0))
            {
                throw Failure((uint)Marshal.GetLastWin32Error(), "GetSystemCpuSetInformation");
            }
        }
        if (written is 0 || written > required)
        {
            throw Failure(ErrorInvalidData, "GetSystemCpuSetInformation");
        }
        Array.Resize(ref buffer, (int)written);
        return buffer;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadPossibleValue(
        nint rootPowerKey, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid,
        out uint type, uint possibleSettingIndex, out uint buffer, ref uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetSystemCpuSetInformation(
        byte* information, uint bufferLength, out uint returnedLength, nint process, uint flags);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();
}
