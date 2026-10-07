using System;
using System.Collections.Generic;
using System.IO;
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
public sealed record WakeSecuritySnapshot(
    bool PolicyExisted,
    int PolicyAc,
    int PolicyDc,
    int NoLockScreen,
    IReadOnlyList<WakeSecurityScheme> Schemes);

/// <summary>The wake sign-in setting one restore step writes.</summary>
public enum WakeSecuritySetting
{
    /// <summary>The console-lock policy values, or the removal of a policy key that did not exist before.</summary>
    ConsoleLockPolicy,

    /// <summary>One scheme's console-lock values, or the scheme list itself when it could not be read.</summary>
    SchemeConsoleLock,

    /// <summary>The re-activation of the active scheme that makes the scheme values take effect.</summary>
    ActiveSchemeRefresh,

    /// <summary>The personalization NoLockScreen value.</summary>
    NoLockScreen
}

/// <summary>One restore step that failed to write.</summary>
/// <param name="Setting">The setting the step writes.</param>
/// <param name="Scheme">The scheme for a <see cref="WakeSecuritySetting.SchemeConsoleLock" /> step, otherwise null.</param>
/// <param name="Error">The exception the write threw, with any native code preserved.</param>
public sealed record WakeSecurityRestoreFailure(WakeSecuritySetting Setting, Guid? Scheme, Exception Error);

/// <summary>The outcome of <see cref="WindowsWakeSecurity.Restore" />: every step was attempted.</summary>
/// <param name="Failures">The steps that failed to write; empty when everything restorable was restored.</param>
public sealed record WakeSecurityRestoreResult(IReadOnlyList<WakeSecurityRestoreFailure> Failures)
{
    /// <summary>Whether every applicable step was written.</summary>
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>One step of a wake sign-in restore.</summary>
/// <param name="Setting">What the step writes.</param>
/// <param name="Scheme">The scheme of a scheme step, otherwise <see cref="Guid.Empty" />.</param>
/// <param name="Ac">The AC value; for <see cref="WakeSecuritySetting.NoLockScreen" /> the value itself. -1 deletes it.</param>
/// <param name="Dc">The battery value; -1 deletes it.</param>
/// <param name="PolicyExisted">For the policy step, whether the policy key existed when the snapshot was taken.</param>
internal sealed record WakeSecurityRestoreItem(
    WakeSecuritySetting Setting,
    Guid Scheme,
    int Ac,
    int Dc,
    bool PolicyExisted);

/// <summary>
///     Windows wake sign-in primitives. Mutations require elevation; failures propagate to the caller.
/// </summary>
/// <remarks>
///     Owns no persistence, retry or default policy. A caller composes the primitives, persists a
///     <see cref="Capture" /> snapshot before the first write and retains it until <see cref="Restore" />
///     succeeds. Calls are synchronous and mutations are not transactional: earlier values may have
///     changed when a later write throws. Callers must serialize competing changes. Every negative
///     value deletes a stored value (-1 is the snapshot's absence sentinel); nonnegative values are
///     written as DWORDs. Deletion from an absent key does nothing.
/// </remarks>
public static class WindowsWakeSecurity
{
    private const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string PersonalizationKey = @"SOFTWARE\Policies\Microsoft\Windows\Personalization";
    private const string AcValue = "ACSettingIndex";
    private const string DcValue = "DCSettingIndex";
    private const string NoLockScreenValue = "NoLockScreen";
    private static readonly Guid ConsoleLock = new("0e796bdb-100d-47d6-a2d5-f7d2daa51f51");
    private static readonly string PolicyKey = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\" + ConsoleLock;

    /// <summary>Captures stored DWORD values and absence; read failures prevent a partial snapshot.</summary>
    /// <remarks>
    ///     Schemes come from <see cref="WindowsPower.EnumerateSchemes" />, or the active scheme alone when the
    ///     enumeration is empty. Values are read sequentially without locking out external policy changes.
    ///     Restore supports nonnegative policy values and the -1 absence sentinel; other negative DWORD
    ///     representations are also treated as deletion by the mutation methods.
    /// </remarks>
    /// <returns>A snapshot to persist before a mutation.</returns>
    /// <exception cref="InvalidDataException">
    ///     A captured value is not a DWORD, so it could not be restored exactly; nothing should be changed.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">A required registry key could not be read.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Scheme enumeration or active-scheme lookup failed.</exception>
    public static WakeSecuritySnapshot Capture()
    {
        using var policy = Registry.LocalMachine.OpenSubKey(PolicyKey);
        using var personalization = Registry.LocalMachine.OpenSubKey(PersonalizationKey);
        List<WakeSecurityScheme> schemes = [];
        foreach (var scheme in SchemesOrActive())
        {
            using var setting = Registry.LocalMachine.OpenSubKey(SchemePath(scheme));
            schemes.Add(new WakeSecurityScheme(scheme, Read(setting, AcValue), Read(setting, DcValue)));
        }

        return new WakeSecuritySnapshot(policy is not null, Read(policy, AcValue), Read(policy, DcValue),
            Read(personalization, NoLockScreenValue), schemes);
    }

    /// <summary>Interprets captured policy precedence without touching Windows.</summary>
    /// <remarks>
    ///     Each power line is evaluated on its own: a policy value set for it wins, otherwise every
    ///     observed scheme's value for that line decides.
    /// </remarks>
    /// <param name="snapshot">Observed policy and scheme values.</param>
    /// <returns>True only when both the AC and the battery line disable wake sign-in.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot" /> is null.</exception>
    public static bool IsSignInDisabled(WakeSecuritySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ac = snapshot.PolicyAc >= 0
            ? snapshot.PolicyAc == 0
            : snapshot.Schemes.Count > 0 && snapshot.Schemes.All(scheme => scheme.Ac == 0);
        var dc = snapshot.PolicyDc >= 0
            ? snapshot.PolicyDc == 0
            : snapshot.Schemes.Count > 0 && snapshot.Schemes.All(scheme => scheme.Dc == 0);
        return ac && dc;
    }

    /// <summary>Writes the console-lock policy that applies to every scheme, existing and future.</summary>
    /// <param name="ac">AC value (0 no sign-in, 1 sign-in), or -1 to delete it.</param>
    /// <param name="dc">Battery value, or -1 to delete it.</param>
    /// <exception cref="UnauthorizedAccessException">The caller cannot write the policy key.</exception>
    /// <exception cref="IOException">A registry write or deletion failed; earlier changes may remain.</exception>
    public static void SetConsoleLockPolicy(int ac, int dc)
    {
        WriteValues(PolicyKey, (AcValue, ac), (DcValue, dc));
    }

    /// <summary>Writes one scheme's stored console-lock values.</summary>
    /// <remarks>Takes effect for the active scheme after <see cref="WindowsPower.RefreshActiveScheme" />.</remarks>
    /// <param name="scheme">Installed scheme identity.</param>
    /// <param name="ac">AC value (0 no sign-in, 1 sign-in), or -1 to delete it.</param>
    /// <param name="dc">Battery value, or -1 to delete it.</param>
    /// <exception cref="System.ComponentModel.Win32Exception">A policy write failed; earlier changes may remain.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller cannot open the scheme key for deletion.</exception>
    /// <exception cref="IOException">A registry deletion failed.</exception>
    public static void SetSchemeConsoleLock(Guid scheme, int ac, int dc)
    {
        if (ac >= 0)
        {
            WindowsPower.WriteSetting(scheme, ModernStandby.SubgroupNone, ConsoleLock, false, (uint)ac);
        }

        if (dc >= 0)
        {
            WindowsPower.WriteSetting(scheme, ModernStandby.SubgroupNone, ConsoleLock, true, (uint)dc);
        }

        if (ac < 0 || dc < 0)
        {
            using var key = Registry.LocalMachine.OpenSubKey(SchemePath(scheme), true);
            if (ac < 0)
            {
                key?.DeleteValue(AcValue, false);
            }

            if (dc < 0)
            {
                key?.DeleteValue(DcValue, false);
            }
        }
    }

    /// <summary>Writes the personalization NoLockScreen policy value.</summary>
    /// <param name="value">1 hides the lock screen, 0 shows it, -1 deletes the value.</param>
    /// <exception cref="UnauthorizedAccessException">The caller cannot write the personalization key.</exception>
    /// <exception cref="IOException">The registry write or deletion failed.</exception>
    public static void SetNoLockScreen(int value)
    {
        WriteValues(PersonalizationKey, (NoLockScreenValue, value));
    }

    /// <summary>Restores captured values, attempting every step even when an earlier one fails.</summary>
    /// <remarks>
    ///     Touches only what the snapshot recorded: the policy values (removing a policy key the snapshot did not
    ///     see, when nothing else is left in it), each captured scheme that is still installed, a refresh of the
    ///     active scheme, and NoLockScreen. A scheme removed since the capture is no longer applicable and is
    ///     skipped. Each step is written once; a failed step is reported, not retried.
    /// </remarks>
    /// <param name="snapshot">A snapshot from <see cref="Capture" />.</param>
    /// <returns>The steps that failed. Retain the snapshot while any remain.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot" /> is null.</exception>
    public static WakeSecurityRestoreResult Restore(WakeSecuritySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<WakeSecurityRestoreFailure> failures = [];
        IReadOnlyCollection<Guid> installed;
        try
        {
            installed = SchemesOrActive();
        }
        catch (Exception ex)
        {
            // Without the scheme list no scheme step can be told apart from a vanished one; the
            // other steps still run.
            failures.Add(new WakeSecurityRestoreFailure(WakeSecuritySetting.SchemeConsoleLock, null, ex));
            installed = [];
        }

        failures.AddRange(ExecuteRestore(RestorePlan(snapshot, installed), ApplyRestoreItem));
        return new WakeSecurityRestoreResult(failures);
    }

    /// <summary>Builds the restore steps in write order, leaving out schemes that are no longer installed.</summary>
    /// <param name="snapshot">Captured values and scheme identities.</param>
    /// <param name="installed">Currently installed scheme identities, or an empty collection when enumeration failed.</param>
    /// <returns>Policy, surviving scheme, active-refresh and personalization steps in execution order.</returns>
    internal static IReadOnlyList<WakeSecurityRestoreItem> RestorePlan(
        WakeSecuritySnapshot snapshot, IReadOnlyCollection<Guid> installed)
    {
        List<WakeSecurityRestoreItem> plan =
        [
            new(WakeSecuritySetting.ConsoleLockPolicy, Guid.Empty, snapshot.PolicyAc, snapshot.PolicyDc,
                snapshot.PolicyExisted)
        ];
        foreach (var scheme in snapshot.Schemes)
        {
            if (installed.Contains(scheme.Scheme))
            {
                plan.Add(new WakeSecurityRestoreItem(WakeSecuritySetting.SchemeConsoleLock, scheme.Scheme, scheme.Ac,
                    scheme.Dc, false));
            }
        }

        plan.Add(new WakeSecurityRestoreItem(WakeSecuritySetting.ActiveSchemeRefresh, Guid.Empty, -1, -1, false));
        plan.Add(new WakeSecurityRestoreItem(WakeSecuritySetting.NoLockScreen, Guid.Empty, snapshot.NoLockScreen, -1,
            false));
        return plan;
    }

    /// <summary>Applies every step once, collecting the ones that throw instead of stopping at the first.</summary>
    /// <param name="plan">Restore steps in desired write order.</param>
    /// <param name="apply">Synchronous step writer; each thrown exception is retained in the result.</param>
    /// <returns>Failures in plan order after every step has been attempted once.</returns>
    internal static List<WakeSecurityRestoreFailure> ExecuteRestore(
        IReadOnlyList<WakeSecurityRestoreItem> plan, Action<WakeSecurityRestoreItem> apply)
    {
        List<WakeSecurityRestoreFailure> failures = [];
        foreach (var item in plan)
        {
            try
            {
                apply(item);
            }
            catch (Exception ex)
            {
                failures.Add(new WakeSecurityRestoreFailure(item.Setting,
                    item.Setting == WakeSecuritySetting.SchemeConsoleLock ? item.Scheme : null, ex));
            }
        }

        return failures;
    }

    /// <summary>Converts one raw registry value to the snapshot form: -1 when absent.</summary>
    /// <exception cref="InvalidDataException">The value exists but is not a DWORD.</exception>
    /// <param name="raw">Raw RegistryKey.GetValue result; null means absent.</param>
    /// <param name="key">Registry path used in a type-error message.</param>
    /// <param name="value">Registry value name used in a type-error message.</param>
    /// <returns>The stored signed DWORD representation, or -1 when absent.</returns>
    internal static int ToDword(object? raw, string key, string value)
    {
        return raw switch
        {
            null => -1,
            int dword => dword,
            _ => throw new InvalidDataException($@"{key}\{value} is not a DWORD value.")
        };
    }

    private static void ApplyRestoreItem(WakeSecurityRestoreItem item)
    {
        switch (item.Setting)
        {
            case WakeSecuritySetting.ConsoleLockPolicy when item.PolicyExisted:
                SetConsoleLockPolicy(item.Ac, item.Dc);
                break;
            case WakeSecuritySetting.ConsoleLockPolicy:
                RemoveConsoleLockPolicy();
                break;
            case WakeSecuritySetting.SchemeConsoleLock:
                SetSchemeConsoleLock(item.Scheme, item.Ac, item.Dc);
                break;
            case WakeSecuritySetting.ActiveSchemeRefresh:
                WindowsPower.RefreshActiveScheme();
                break;
            case WakeSecuritySetting.NoLockScreen:
                SetNoLockScreen(item.Ac);
                break;
        }
    }

    /// <summary>
    ///     Deletes the policy values, then the policy key only when nothing else was added to it since,
    ///     so a key that gained values or subkeys is never removed with them.
    /// </summary>
    private static void RemoveConsoleLockPolicy()
    {
        using (var policy = Registry.LocalMachine.OpenSubKey(PolicyKey, true))
        {
            if (policy is null)
            {
                return;
            }

            policy.DeleteValue(AcValue, false);
            policy.DeleteValue(DcValue, false);
            if (policy.ValueCount > 0 || policy.SubKeyCount > 0)
            {
                return;
            }
        }

        Registry.LocalMachine.DeleteSubKey(PolicyKey, false);
    }

    private static int Read(RegistryKey? key, string value)
    {
        return key is null
            ? -1
            : ToDword(key.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.Name, value);
    }

    private static string SchemePath(Guid scheme)
    {
        return $@"{SchemesKey}\{scheme}\{ModernStandby.SubgroupNone}\{ConsoleLock}";
    }

    private static void WriteValues(string path, params (string Name, int Value)[] values)
    {
        if (values.Any(entry => entry.Value >= 0))
        {
            using var created = Registry.LocalMachine.CreateSubKey(path);
            foreach (var (name, value) in values)
            {
                SetOrDelete(created, name, value);
            }

            return;
        }

        using var existing = Registry.LocalMachine.OpenSubKey(path, true);
        if (existing is null)
        {
            return;
        }

        foreach (var (name, value) in values)
        {
            SetOrDelete(existing, name, value);
        }
    }

    private static void SetOrDelete(RegistryKey key, string name, int value)
    {
        if (value < 0)
        {
            key.DeleteValue(name, false);
        }
        else
        {
            key.SetValue(name, value, RegistryValueKind.DWord);
        }
    }

    private static List<Guid> SchemesOrActive()
    {
        List<Guid> result = [.. WindowsPower.EnumerateSchemes()];
        if (result.Count == 0)
        {
            result.Add(WindowsPower.GetActiveScheme());
        }

        return result;
    }
}
