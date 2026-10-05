using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.Devices.Radios;
using Microsoft.Win32;

namespace WindowsDeviceControl;

public static partial class WindowsRadio
{
    private static readonly TimeSpan RadioCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly object RadioCacheLock = new();
    private static (long Taken, Radio[] Radios)? _radioCache;

    /// <summary>Reads the combined power state of every adapter of one kind.</summary>
    /// <param name="kind">Which radio family to read.</param>
    /// <returns>
    ///     The aggregate state; <see cref="Power.Absent" /> when the machine has no such
    ///     adapter.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="kind" /> is not a defined
    ///     <see cref="RadioKind" /> value.
    /// </exception>
    public static Power GetPower(RadioKind kind)
    {
        ValidateRadioKind(kind);
        var radios = GetRadios(kind, out var states);
        var power = Power.Absent;
        for (var index = 0; index < radios.Count; index++)
        {
            power = Prefer(power, MapPower(states?[index] ?? radios[index].State));
        }

        return power;
    }

    /// <summary>Asks Windows whether this process may change radio power.</summary>
    /// <returns>Whether radio control is permitted, and if not, why.</returns>
    /// <remarks>
    ///     Called for you by <see cref="SetPower" />. Call it directly to decide whether to
    ///     show a radio toggle at all — a denied toggle that silently does nothing is worse than an
    ///     absent one.
    /// </remarks>
    public static Access RequestAccess()
    {
        return MapAccess(
            Radio.RequestAccessAsync().WaitWinRt());
    }

    /// <summary>Turns every adapter of one kind on or off.</summary>
    /// <param name="kind">Which radio family to change.</param>
    /// <param name="on">True to turn the radios on, false to turn them off.</param>
    /// <returns>
    ///     Each adapter's answer and the combined <see cref="RadioPowerResult.Access" />. Every
    ///     adapter is written once; a refusal or a failed write on one adapter is reported in its
    ///     entry rather than thrown, so a caller can tell a partial change from none. Nothing is
    ///     read back: each write's own status is its result.
    /// </returns>
    /// <exception cref="InvalidOperationException">The machine has no adapter of this kind.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="kind" /> is not a defined
    ///     <see cref="RadioKind" /> value.
    /// </exception>
    public static RadioPowerResult SetPower(RadioKind kind, bool on)
    {
        ValidateRadioKind(kind);
        var access = RequestAccess();
        if (access != Access.Allowed)
        {
            return new RadioPowerResult(access, []);
        }

        var radios = GetRadios(kind, out _);
        if (radios.Count == 0)
        {
            throw new InvalidOperationException("Windows reported no radio of the requested kind.");
        }

        var adapters = new RadioAdapterResult[radios.Count];
        for (var index = 0; index < radios.Count; index++)
        {
            var name = string.Empty;
            try
            {
                name = radios[index].Name ?? string.Empty;
                var result = MapAccess(radios[index].SetStateAsync(on ? RadioState.On : RadioState.Off)
                    .WaitWinRt());
                adapters[index] = new RadioAdapterResult(name, result, 0);
            }
            catch (Exception ex)
            {
                adapters[index] = new RadioAdapterResult(name, null, ex.HResult);
            }
        }

        return new RadioPowerResult(CombineAdapterAccess(adapters), adapters);
    }

    /// <summary>
    ///     Folds adapter answers into one: allowed only when every adapter allowed, otherwise the
    ///     first refusal, or <see cref="Access.Unspecified" /> when an adapter failed without one.
    /// </summary>
    internal static Access CombineAdapterAccess(IReadOnlyList<RadioAdapterResult> adapters)
    {
        var failed = false;
        foreach (var adapter in adapters)
        {
            if (adapter.Access is not { } answer)
            {
                failed = true;
            }
            else if (answer != Access.Allowed)
            {
                return answer;
            }
        }

        return failed ? Access.Unspecified : Access.Allowed;
    }

    /// <summary>Reads the privacy consent recorded for a capability.</summary>
    /// <param name="capability">
    ///     The capability name, as the privacy store spells it — for example
    ///     <c>location</c> or <c>radios</c>.
    /// </param>
    /// <returns>The user-scope and machine-scope consent values.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="capability" /> is null, empty or white space, which would read the
    ///     consent store's root instead of a capability.
    /// </exception>
    /// <remarks>
    ///     Diagnostic only: the owning API remains the authority on what is permitted, and this can
    ///     disagree with it. It exists to answer "why did enumeration return nothing" — on a
    ///     provisioned kiosk or signage machine, location consent is commonly off, and Wi-Fi
    ///     enumeration then returns an empty list rather than an error.
    /// </remarks>
    public static (Consent User, Consent Machine) GetConsent(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        return (
            ReadConsent(Registry.CurrentUser, capability),
            ReadConsent(Registry.LocalMachine, capability));
    }

    /// <summary>The adapters of one kind, from a briefly cached enumeration.</summary>
    /// <param name="kind">Which radio family to return.</param>
    /// <param name="states">
    ///     When the cached list was reused, the state each returned adapter
    ///     reported while the cache was checked; null after a fresh enumeration.
    /// </param>
    private static IReadOnlyList<Radio> GetRadios(RadioKind kind, out RadioState[]? states)
    {
        Radio[]? all = null;
        RadioState[]? observed = null;
        lock (RadioCacheLock)
        {
            if (_radioCache is { } cached
                && Stopwatch.GetElapsedTime(cached.Taken) < RadioCacheTtl
                && TryReadStates(cached.Radios, out observed))
            {
                all = cached.Radios;
            }
        }

        if (all is null)
        {
            // Enumeration can stall in the radio stack, so it runs outside the lock: one stalled
            // enumeration must not hold up a caller the cache can still serve. Two concurrent misses
            // may both enumerate; the last one published wins, and both lists are fresh.
            all = Radio.GetRadiosAsync().WaitWinRt().ToArray();
            observed = null;
            lock (RadioCacheLock)
            {
                _radioCache = (Stopwatch.GetTimestamp(), all);
            }
        }

        // Fully qualified: this type declares its own RadioKind, so the WinRT one needs naming.
        var expected = kind == RadioKind.WiFi
            ? Windows.Devices.Radios.RadioKind.WiFi
            : Windows.Devices.Radios.RadioKind.Bluetooth;
        List<Radio> radios = [];
        List<RadioState>? kindStates = observed is null ? null : [];
        for (var index = 0; index < all.Length; index++)
        {
            if (all[index].Kind == expected)
            {
                radios.Add(all[index]);
                kindStates?.Add(observed![index]);
            }
        }

        states = kindStates?.ToArray();
        return radios;
    }

    /// <summary>Refuses an undefined radio kind. Every public member checks it before enumerating.</summary>
    internal static void ValidateRadioKind(RadioKind kind)
    {
        if (kind is not RadioKind.WiFi and not RadioKind.Bluetooth)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown radio kind.");
        }
    }

    /// <summary>Reads every cached adapter's state. One unreadable adapter means the cache is stale.</summary>
    private static bool TryReadStates(Radio[] radios, out RadioState[]? states)
    {
        var read = new RadioState[radios.Length];
        for (var index = 0; index < radios.Length; index++)
        {
            try
            {
                read[index] = radios[index].State;
            }
            catch
            {
                states = null;
                return false;
            }
        }

        states = read;
        return true;
    }

    /// <summary>Reduces several adapters' power states to the one a caller should act on.</summary>
    /// <param name="states">The individual adapter states.</param>
    /// <returns>
    ///     The state that represents the group: any adapter on means on, and a machine-wide block is
    ///     reported ahead of a merely-off adapter, so a caller does not offer to enable a radio that
    ///     airplane mode or a hardware switch will refuse.
    /// </returns>
    public static Power AggregatePower(IEnumerable<Power> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var power = Power.Absent;
        foreach (var state in states)
        {
            power = Prefer(power, state);
        }

        return power;
    }

    /// <summary>
    ///     The state that represents both: On, then Disabled, Off and Unknown. Any other value
    ///     counts as no adapter.
    /// </summary>
    private static Power Prefer(Power current, Power candidate)
    {
        return Rank(candidate) < Rank(current) ? candidate : current;
    }

    private static int Rank(Power state)
    {
        return state switch
        {
            Power.On => 0,
            Power.Disabled => 1,
            Power.Off => 2,
            Power.Unknown => 3,
            _ => 4
        };
    }

    private static Power MapPower(RadioState state)
    {
        return state switch
        {
            RadioState.On => Power.On,
            RadioState.Off => Power.Off,
            RadioState.Disabled => Power.Disabled,
            _ => Power.Unknown
        };
    }

    private static Access MapAccess(RadioAccessStatus status)
    {
        return status switch
        {
            RadioAccessStatus.Allowed => Access.Allowed,
            RadioAccessStatus.DeniedByUser => Access.DeniedByUser,
            RadioAccessStatus.DeniedBySystem => Access.DeniedBySystem,
            _ => Access.Unspecified
        };
    }

    private static Consent ReadConsent(RegistryKey root, string capability)
    {
        try
        {
            using var key = root.OpenSubKey(
                $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\{capability}",
                false);
            if (key is null)
            {
                return Consent.Unset;
            }

            return key.GetValue("Value") switch
            {
                string value when value.Trim().Equals("Allow", StringComparison.OrdinalIgnoreCase)
                    => Consent.Allow,
                string value when value.Trim().Equals("Deny", StringComparison.OrdinalIgnoreCase)
                    => Consent.Deny,
                string value when string.IsNullOrWhiteSpace(value) => Consent.Unset,
                null => Consent.Unset,
                _ => Consent.Unknown
            };
        }
        catch
        {
            return Consent.Unknown;
        }
    }
}
