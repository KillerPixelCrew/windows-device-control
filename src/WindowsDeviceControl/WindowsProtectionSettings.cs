using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace WindowsDeviceControl;

/// <summary>An explicit system Control Flow Guard override, or the Windows default.</summary>
public enum ControlFlowGuardSetting
{
    /// <summary>No override; Windows supplies the system default.</summary>
    Default,
    /// <summary>Explicitly enabled.</summary>
    Enabled,
    /// <summary>Explicitly disabled.</summary>
    Disabled
}

/// <summary>Configured Windows driver protections, preserving absent registry overrides.</summary>
/// <param name="MemoryIntegrity">Configured HVCI override, or null for Windows default.</param>
/// <param name="VulnerableDriverBlocklist">Configured driver-blocklist override, or null for Windows default.</param>
/// <param name="ControlFlowGuard">System CFG override, or null when it was not requested or Windows could not report it.</param>
/// <param name="MemoryIntegrityLocked">Whether HVCI is locked through the firmware policy.</param>
public sealed record WindowsProtectionState(bool? MemoryIntegrity, bool? VulnerableDriverBlocklist,
    ControlFlowGuardSetting? ControlFlowGuard, bool MemoryIntegrityLocked);

/// <summary>Current Microsoft Defender path and process exclusions, without inferred ownership.</summary>
/// <param name="Paths">Configured exclusion paths.</param>
/// <param name="Processes">Configured exclusion process names or paths.</param>
public sealed record DefenderExclusionState(IReadOnlyList<string> Paths, IReadOnlyList<string> Processes);

/// <summary>Windows protection settings and scoped Defender exclusion primitives.</summary>
/// <remarks>
///     All calls are synchronous and belong on a worker. Writes require administrator permission;
///     device policy, Tamper Protection and firmware locks can still refuse them. Callers own consent,
///     recovery snapshots and restart policy. No operation disables Defender or bypasses driver signing.
/// </remarks>
public static class WindowsProtectionSettings
{
    private const string MemoryKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string BlocklistKey = @"SYSTEM\CurrentControlSet\Control\CI\Config";
    private const string MemoryPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";
    private const string MemoryPolicyValue = "HypervisorEnforcedCodeIntegrity";

    /// <summary>Reads configured protection overrides; this does not claim they took effect in the current boot.</summary>
    /// <param name="controlFlowGuard">
    ///     Whether to query CFG too. Only CFG needs PowerShell; the HVCI and blocklist overrides are plain
    ///     registry reads, so a caller that needs only those skips the process and its failure modes.
    /// </param>
    /// <returns>
    ///     Exact configured states, including absence and the HVCI lock. CFG is null when not requested or
    ///     when its query failed, so a PowerShell failure never hides the registry-backed protections.
    /// </returns>
    public static WindowsProtectionState Read(bool controlFlowGuard = true)
    {
        return new WindowsProtectionState(ReadDword(MemoryKey, "Enabled"),
            ReadDword(BlocklistKey, "VulnerableDriverBlocklistEnable"),
            controlFlowGuard ? TryReadControlFlowGuard() : null,
            ReadDword(MemoryKey, "Locked") == true);
    }

    private static ControlFlowGuardSetting? TryReadControlFlowGuard()
    {
        try
        {
            return RunPowerShell("[Console]::Write([string](Get-ProcessMitigation -System).Cfg.Enable)").Trim() switch
            {
                "ON" => ControlFlowGuardSetting.Enabled,
                "OFF" => ControlFlowGuardSetting.Disabled,
                "NOTSET" => ControlFlowGuardSetting.Default,
                _ => null
            };
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException
                                              or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Sets or removes the HVCI configured override. A restart is required; firmware locks are not bypassed.</summary>
    /// <remarks>
    ///     Windows 11 re-enabled HVCI on its own after only the scenario value was cleared (an Atlas,
    ///     2026-10-31, <c>WasEnabledBy</c> 2). Off therefore also sets the Device Guard policy value Windows
    ///     respects; any other value removes a disabling policy value again.
    /// </remarks>
    /// <param name="enabled">True/false for an explicit override, null to restore absence.</param>
    public static void SetMemoryIntegrity(bool? enabled)
    {
        WriteDword(MemoryKey, "Enabled", enabled);
        using var policy = Registry.LocalMachine.CreateSubKey(MemoryPolicyKey, writable: true);
        if (enabled == false)
        {
            policy.SetValue(MemoryPolicyValue, 0, RegistryValueKind.DWord);
        }
        else if (policy.GetValue(MemoryPolicyValue) is 0)
        {
            policy.DeleteValue(MemoryPolicyValue, throwOnMissingValue: false);
        }
    }

    /// <summary>Sets or removes the vulnerable-driver-blocklist override; this does not bypass certificate validation.</summary>
    /// <param name="enabled">True/false for an explicit override, null to restore absence.</param>
    public static void SetVulnerableDriverBlocklist(bool? enabled) =>
        WriteDword(BlocklistKey, "VulnerableDriverBlocklistEnable", enabled);

    /// <summary>Sets only the system CFG override, preserving unrelated exploit protections.</summary>
    /// <param name="setting">Explicit enabled/disabled state or removal of the override.</param>
    /// <exception cref="IOException">Windows refused the command.</exception>
    public static void SetControlFlowGuard(ControlFlowGuardSetting setting)
    {
        var command = setting switch
        {
            ControlFlowGuardSetting.Enabled => "Set-ProcessMitigation -System -Enable CFG",
            ControlFlowGuardSetting.Disabled => "Set-ProcessMitigation -System -Disable CFG",
            ControlFlowGuardSetting.Default => "Set-ProcessMitigation -System -Remove -Disable CFG",
            _ => throw new ArgumentOutOfRangeException(nameof(setting))
        };
        RunPowerShell(command);
    }

    /// <summary>Reads Defender exclusions; inaccessible preferences are reported as an error, never an empty list.</summary>
    /// <returns>Configured paths and processes.</returns>
    /// <exception cref="IOException">Defender refused the query or returned malformed output.</exception>
    public static DefenderExclusionState ReadDefenderExclusions()
    {
        var output = RunPowerShell("$p=Get-MpPreference; "
            + "foreach($v in $p.ExclusionPath){if($v){[Console]::WriteLine('P:'+"
            + "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($v)))}}; "
            + "foreach($v in $p.ExclusionProcess){if($v){[Console]::WriteLine('X:'+"
            + "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($v)))}}");
        var paths = new List<string>();
        var processes = new List<string>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!(line.StartsWith("P:", StringComparison.Ordinal) || line.StartsWith("X:", StringComparison.Ordinal)))
            {
                throw new IOException("Defender returned an unexpected exclusion record.");
            }
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(line[2..]));
            (line[0] == 'P' ? paths : processes).Add(value);
        }
        return new DefenderExclusionState(paths, processes);
    }

    /// <summary>Adds one exact Defender path exclusion; does not replace the existing list.</summary>
    /// <param name="path">Absolute file or folder path, without wildcard expansion.</param>
    public static void AddDefenderPath(string path) => Exclusion("Add", "Path", path);

    /// <summary>Removes one exact Defender path exclusion; callers must establish their ownership first.</summary>
    /// <param name="path">Previously owned absolute path.</param>
    public static void RemoveDefenderPath(string path) => Exclusion("Remove", "Path", path);

    /// <summary>Adds one exact Defender process exclusion; does not replace the existing list.</summary>
    /// <param name="path">Absolute executable path.</param>
    public static void AddDefenderProcess(string path) => Exclusion("Add", "Process", path);

    /// <summary>Removes one exact Defender process exclusion; callers must establish their ownership first.</summary>
    /// <param name="path">Previously owned absolute executable path.</param>
    public static void RemoveDefenderProcess(string path) => Exclusion("Remove", "Process", path);

    private static void Exclusion(string operation, string kind, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['*', '?', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("An exact absolute exclusion path is required.", nameof(path));
        }
        RunPowerShell($"{operation}-MpPreference -Exclusion{kind} '{path.Replace("'", "''")}'");
    }

    private static bool? ReadDword(string path, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key?.GetValue(name) switch
        {
            null => null,
            0 => false,
            1 => true,
            _ => throw new IOException("Windows returned an invalid protection override: " + name)
        };
    }

    private static void WriteDword(string path, string name, bool? value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value.Value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    private static string RunPowerShell(string command)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " + command)));
        using var process = Process.Start(start) ?? throw new IOException("Windows PowerShell could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("The Windows protection command did not finish; its outcome is unknown.");
        }
        var detail = error.GetAwaiter().GetResult().Trim();
        var text = output.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new IOException($"Windows protection command exited {process.ExitCode}: {detail}");
        }
        return text;
    }
}
