using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace WindowsDeviceControl;

/// <summary>Stored sign-in-on-wake values for one scheme; -1 means absent.</summary>
/// <param name="Scheme">Scheme identity.</param>
/// <param name="Ac">Stored AC value.</param>
/// <param name="Dc">Stored battery value.</param>
public sealed record WakeSecurityScheme(Guid Scheme, int Ac, int Dc);

/// <summary>Recovery snapshot for Windows wake sign-in policy. Persist before changing it.</summary>
/// <param name="PolicyExisted">Whether the console-lock policy key existed.</param>
/// <param name="PolicyAc">Prior AC policy value, or -1.</param>
/// <param name="PolicyDc">Prior battery policy value, or -1.</param>
/// <param name="NoLockScreen">Prior personalization value, or -1.</param>
/// <param name="Schemes">Per-scheme values.</param>
public sealed record WakeSecuritySnapshot(bool PolicyExisted, int PolicyAc, int PolicyDc,
    int NoLockScreen, IReadOnlyList<WakeSecurityScheme> Schemes);

/// <summary>Windows wake sign-in primitives. Mutations require elevation; failures propagate to the caller.</summary>
/// <remarks>Owns no persistence or retry policy. Callers must retain recovery state until restoration succeeds.</remarks>
public static class WindowsWakeSecurity
{
    private static readonly Guid ConsoleLock = new("0e796bdb-100d-47d6-a2d5-f7d2daa51f51");
    private static readonly Guid SubNone = new("fea3413e-7e05-4911-9a71-700331f1c294");
    private static readonly string PolicyKey = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\" + ConsoleLock;
    private const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string PersonalizationKey = @"SOFTWARE\Policies\Microsoft\Windows\Personalization";

    /// <summary>Captures exact stored values. A read failure throws instead of returning a partial snapshot.</summary>
    /// <returns>A snapshot to persist before a mutation.</returns>
    public static WakeSecuritySnapshot Capture()
    {
        using var policy = Registry.LocalMachine.OpenSubKey(PolicyKey);
        using var personalization = Registry.LocalMachine.OpenSubKey(PersonalizationKey);
        List<WakeSecurityScheme> schemes = [];
        foreach (Guid scheme in EnumerateSchemes())
        {
            using var setting = Registry.LocalMachine.OpenSubKey(SchemePath(scheme));
            schemes.Add(new(scheme, Read(setting, "ACSettingIndex"), Read(setting, "DCSettingIndex")));
        }
        return new(policy is not null, Read(policy, "ACSettingIndex"), Read(policy, "DCSettingIndex"),
            Read(personalization, "NoLockScreen"), schemes);
    }

    /// <summary>Interprets captured policy precedence without touching Windows.</summary>
    /// <param name="snapshot">Observed policy and scheme values.</param>
    /// <returns>True only when policy, or every observed scheme, disables wake sign-in.</returns>
    public static bool IsSignInDisabled(WakeSecuritySnapshot snapshot) => snapshot.PolicyAc >= 0
        ? snapshot.PolicyAc == 0 && (snapshot.PolicyDc < 0 ? snapshot.PolicyAc : snapshot.PolicyDc) == 0
        : snapshot.Schemes.Count > 0 && snapshot.Schemes.All(scheme => scheme.Ac == 0 && scheme.Dc == 0);

    /// <summary>Disables wake sign-in for existing schemes and future schemes through policy.</summary>
    public static void DisableSignIn()
    {
        using (var policy = Registry.LocalMachine.CreateSubKey(PolicyKey))
        {
            policy.SetValue("ACSettingIndex", 0, RegistryValueKind.DWord);
            policy.SetValue("DCSettingIndex", 0, RegistryValueKind.DWord);
        }
        foreach (Guid scheme in EnumerateSchemes()) { WriteScheme(scheme, 0, 0); }
        WindowsPower.SetActiveScheme(WindowsPower.GetActiveScheme());
        using var personalization = Registry.LocalMachine.CreateSubKey(PersonalizationKey);
        personalization.SetValue("NoLockScreen", 1, RegistryValueKind.DWord);
    }

    /// <summary>Restores captured values. A failure throws and requires the caller to retain its snapshot.</summary>
    /// <param name="snapshot">Previously captured values; null restores default sign-in requirements.</param>
    public static void Restore(WakeSecuritySnapshot? snapshot)
    {
        if (snapshot is { PolicyExisted: false })
        { Registry.LocalMachine.DeleteSubKey(PolicyKey, throwOnMissingSubKey: false); }
        else
        {
            using var policy = snapshot is { PolicyExisted: true }
                ? Registry.LocalMachine.CreateSubKey(PolicyKey)
                : Registry.LocalMachine.OpenSubKey(PolicyKey, writable: true);
            if (policy is not null)
            {
                RestoreValue(policy, "ACSettingIndex", snapshot?.PolicyAc ?? -1);
                RestoreValue(policy, "DCSettingIndex", snapshot?.PolicyDc ?? -1);
            }
        }
        if (snapshot is { Schemes.Count: > 0 })
        {
            foreach (var scheme in snapshot.Schemes) { WriteScheme(scheme.Scheme, scheme.Ac, scheme.Dc); }
        }
        else { foreach (Guid scheme in EnumerateSchemes()) { WriteScheme(scheme, 1, 1); } }
        WindowsPower.SetActiveScheme(WindowsPower.GetActiveScheme());
        using var personalization = snapshot is { NoLockScreen: >= 0 }
            ? Registry.LocalMachine.CreateSubKey(PersonalizationKey)
            : Registry.LocalMachine.OpenSubKey(PersonalizationKey, writable: true);
        if (personalization is not null) { RestoreValue(personalization, "NoLockScreen", snapshot?.NoLockScreen ?? -1); }
    }

    private static int Read(RegistryKey? key, string value) => key?.GetValue(value) as int? ?? -1;
    private static string SchemePath(Guid scheme) => $@"{SchemesKey}\{scheme}\{SubNone}\{ConsoleLock}";
    private static void RestoreValue(RegistryKey key, string name, int value)
    {
        if (value < 0) { key.DeleteValue(name, throwOnMissingValue: false); }
        else { key.SetValue(name, value, RegistryValueKind.DWord); }
    }
    private static List<Guid> EnumerateSchemes()
    {
        List<Guid> result = [];
        for (uint index = 0; WindowsPower.EnumerateScheme(index) is { } scheme; index++) { result.Add(scheme); }
        if (result.Count == 0) { result.Add(WindowsPower.GetActiveScheme()); }
        return result;
    }
    private static void WriteScheme(Guid scheme, int ac, int dc)
    {
        if (ac >= 0) { WindowsPower.WriteSetting(scheme, SubNone, ConsoleLock, false, (uint)ac); }
        if (dc >= 0) { WindowsPower.WriteSetting(scheme, SubNone, ConsoleLock, true, (uint)dc); }
        if (ac < 0 || dc < 0)
        {
            using var key = Registry.LocalMachine.OpenSubKey(SchemePath(scheme), writable: true);
            if (ac < 0) { key?.DeleteValue("ACSettingIndex", false); }
            if (dc < 0) { key?.DeleteValue("DCSettingIndex", false); }
        }
    }
}
