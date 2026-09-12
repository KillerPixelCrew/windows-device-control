# WindowsDeviceControl

Wi-Fi, Bluetooth, audio, display-brightness and power control for .NET on Windows. The parts that are
awkward or undocumented, in one library, callable from an ordinary unpackaged process.

```
dotnet add package WindowsDeviceControl
```

Targets `net8.0-windows10.0.19041.0` and `net10.0-windows10.0.19041.0`. No COM registration, no
packaged identity, no native component, and no admin rights except where Windows itself demands
them. The code was extracted from a shipping Windows shell application, where all of it runs on real
hardware every session.

## Why this exists

Each of these is individually solvable and collectively a bad week.

- Wi-Fi from an unpackaged process. WinRT's `WiFiAdapter` needs the `wiFiControl` capability,
  which an unpackaged desktop, service or kiosk application cannot declare. That leaves the native
  WLAN API and hand-written profile XML, including the part nobody enjoys: a saved profile has to
  exist before you can join a protected network, and the SSID may not be valid UTF-8.
- Default audio endpoint switching. There is no public API. It is done through `IPolicyConfig`, a
  COM interface Microsoft never documented and whose vtable ordering differs across Windows
  versions.
- Panel brightness. The documented route is WMI `WmiMonitorBrightnessMethods`, which requires
  elevation and silently does nothing on many laptop and handheld panels. The ACPI backlight device
  answers the same request unelevated.
- Bluetooth pairing that shows a PIN. Enumerating and connecting is easy. Running the pairing
  ceremony — accepting a deferral, surfacing the PIN to your own UI, answering it — is where the
  examples run out.

## Usage

```csharp
using WindowsDeviceControl;

// Wi-Fi
var status   = WindowsRadio.GetWifiStatus();
var networks = WindowsRadio.ListWifiNetworks();
WindowsRadio.RequestWifiScan();
uint reason  = WindowsRadio.ConnectWifi("MyNetwork", "passphrase");   // 0 = joined
if (reason != 0)
{
    Console.WriteLine(WindowsRadio.ReasonText(reason));               // Windows' own wording
    // ...and only re-prompt when the key was actually the problem.
    if (WindowsRadio.GetReasonVerdict(reason) == WindowsRadio.WifiFailureKind.KeyRejected)
        AskForPassphraseAgain();
}
WindowsRadio.ForgetWifi("MyNetwork");

// Bluetooth, including the pairing ceremony
foreach (var d in WindowsRadio.ListBluetoothDevices(pairedOnly: false))
    Console.WriteLine($"{d.Name} paired={d.Paired} connected={d.Connected}");

WindowsRadio.PairBluetooth(deviceId, onRequest: request =>
{
    // Show request.Pin in your own UI, then answer before the deferral expires.
    WindowsRadio.RespondToPairing(request.Token, accept: true, pin: null);
});

// Radio power (airplane-mode aware)
WindowsRadio.GetPower(WindowsRadio.RadioKind.Bluetooth);
WindowsRadio.SetPower(WindowsRadio.RadioKind.WiFi, on: true);

// Audio
CoreAudio.ListEndpoints(CoreAudio.AudioDirection.Render, out var outputs);
CoreAudio.SetDefaultEndpoint(outputs[0].Id, out var roleResults); // transactional per-role results
CoreAudio.SetVolume(35, out _);
CoreAudio.SetMuted(true);

// Brightness, no elevation
if (Backlight.TryReadBrightness(out int percent))
    Backlight.TrySetBrightness(Math.Min(100, percent + 10));

// Active CCD topology and a hotplug-safe wait. Persist DevicePath and EDID IDs,
// then discard saved adapter/target coordinates after every topology change.
DisplayTopologySnapshot topology = DisplayTopology.CaptureActive();
DisplayTargetIdentity television = topology.Paths[0].Target;
var wait = await DisplayTopology.WaitForPresentAsync(television, TimeSpan.FromSeconds(15), cancellationToken);
```

| Type | Role |
| --- | --- |
| `WindowsRadio` | Radio power, Wi-Fi scan/list/connect/forget, Bluetooth discovery and pairing, change watches (`StartWifiWatch`, `StartBluetoothWatch`) |
| `WifiProfile` | Builds profile XML: `CreateOpen`, `CreatePsk` in WPA3-transition / WPA2-AES / WPA-TKIP shapes; survives a non-UTF-8 SSID |
| `CoreAudio` | Endpoints, default-endpoint switching, volume and mute per direction, Bluetooth audio connect/disconnect |
| `Backlight` | Internal panel brightness over the ACPI backlight device |
| `WaveOutFeedback` | The short click Windows itself plays for volume feedback |
| `DisplayTopology` | Active CCD paths, rematchable monitor identities, and cancellable display-appearance waits |
| `DisplayLayouts` | Complete desktop arrangements by value: which monitors are on, which is primary, position, mode, scaling and HDR |
| `DisplayScaling` | One display's Windows scaling percentage, read and written through the relative-step packets |
| `DisplayColor` | One display's advanced colour (HDR) state, with support re-read before every write |

Every public member is documented and the build fails on one that is not, so IntelliSense is the
reference — including which callbacks arrive on a Windows service thread and which calls return
before the work they started has finished.

Two integer contracts are kept on purpose, because renaming them would hide what they are:
`ConnectWifi` returns Windows' raw WLAN reason code (pass it to `ReasonText` or
`GetReasonVerdict`), and the `CoreAudio` methods returning `int` return HRESULTs. Everything else — radio kind,
audio direction, network security, connection state, pairing kind and outcome, watch events,
volume-key commands, Wi-Fi failure classification — is a named enum.

Display identity uses the monitor device-interface path as its primary rematching key. EDID
manufacturer/product IDs are a fallback only when neither observation has a device path. Friendly
names and `DISPLAY1` numbering are presentation data. Adapter LUID and target ID describe the current
route and must be refreshed after hotplug. Enumeration retries the documented sizing race and remains
read-only. `WaitForPresentAsync` polls fresh complete CCD snapshots; timeout and cancellation never
change display state. `WaitForAvailableAsync` instead queries all CCD paths and requires target availability, so callers can wait for a connected but disabled display before applying a profile.

`CaptureProfile` serializes the active CCD paths and modes alongside those identities. Before
application, `ValidateProfile` rematches every saved target against the current topology and asks
Windows to validate the supplied configuration without applying it. `ApplyProfile` repeats that
validation, captures the current active topology for rollback, applies once, and verifies the saved
targets are active. A failed or unconfirmed application attempts one rollback and reports both the
original Windows status and rollback result. A successful native return is not reported as a
confirmed profile until the readback succeeds.

## What Windows will still refuse you

Documented here rather than discovered at deployment time.

- Radio power needs consent. `RequestAccess()` returns `DeniedByUser` when the user has denied
  radio control to your application, and `DeniedBySystem` on a policy-managed or kiosk-provisioned
  machine. Neither is retryable; check before offering a toggle.
- Wi-Fi enumeration can require location consent. `GetConsent(capability)` reports what the privacy
  store records, as a diagnostic only: the owning API remains the authority on what is permitted.
  This ambushes kiosk deployments in particular, because the machine is often provisioned with
  location off.
- Not every panel has an ACPI backlight. `TryReadBrightness` returns `false` rather than throwing.
  Treat it as "this machine has no controllable internal panel", the normal answer on a desktop.

## Platform findings

`docs/radios.md` records the platform constraints behind all of this, including the approaches that
were tried and disproven. Read it before changing how a Windows API is called.

## Status

Pre-1.0. The surface can still move before it is frozen; pin an exact version if that matters to
you.

Issues and pull requests are welcome, especially hardware reports: the behaviour of Wi-Fi drivers,
Bluetooth stacks and backlight interfaces varies more across machines than any of the underlying
documentation admits.

## Licence

MIT. See `LICENSE`.

## Windows power policy

`WindowsPower` exposes scheme enumeration and localized names, active scheme read/write, AC/DC
policy values and effective power-mode overlays. These synchronous calls belong off UI threads.
Native failures throw `Win32Exception` with the Windows error code. Writes are issued once;
consumers own transaction ordering and confirmation through readback.

```csharp
Guid scheme = WindowsPower.GetActiveScheme();
string name = WindowsPower.ReadSchemeName(scheme);
Guid mode = WindowsPower.GetEffectiveMode();
// Explicit user action:
WindowsPower.SetActiveScheme(scheme);
```

`WriteSetting` updates a scheme's stored AC/DC value without activating it. Call `SetActiveScheme`
when the caller's policy requires immediate application. A successful request is not a guarantee
that another Windows policy writer will leave the value unchanged.

`SuspendAsync` requests standby or hibernation off-thread and usually completes after resume.
`RequestActionAsync` supports shutdown, restart and sign-out using the absolute system tool path;
completion means the tool accepted the request. Cancellation cannot undo a dispatched operation.
`TryGetStatus` reports source and battery values with Windows' unknown sentinels intact.
Window owners can register power-setting notifications and must unregister their returned handles.

`WindowsPowerRequest` owns a display/system wake request and its reason string. Acquire/Release
are idempotent; failures throw with native error codes, and a failed release remains held until
explicit retry or disposal. Disposal closes the kernel handle without repeating a failed clear.
`PowerRequestList.Query` returns null entries and a diagnostic when the undocumented Windows layout
cannot be read safely. Request-list reads may need elevation.

`WindowsWakeSecurity.Capture` returns wake-policy recovery values, including absent values. Persist
that snapshot before `DisableSignIn`, and retain it until `Restore` succeeds. Registry writes need
elevation; this library never elevates or stores application configuration. Windows editions may
ignore personalization policy even after accepting its registry value.

### Modern Standby wake sources

`ModernStandby.Query` reports whether the machine supports S0 low-power idle and its connected
form, and which mandatory wake paths exist — power button, sleep button, lid, wake alarm. Those are
reported, never written: nothing in this class can take away a recovery wake path.

`EnumerateWakeDevices` returns the wake sources a caller can act on: the devices Windows reports as
programmable, plus any that are armed without being programmable. The second group comes back as
`WakeDeviceControl.Fixed` — visible and reportable, refused for writes. Devices that merely support
waking from S0 are deliberately excluded; on a handheld that is most of the HID and Bluetooth
endpoints, none of which Windows offers for change.

```csharp
WakeDeviceSnapshot snapshot = ModernStandby.CaptureWakeDevices();
foreach (WakeDevice device in ModernStandby.EnumerateWakeDevices())
{
    if (device is { Control: WakeDeviceControl.Programmable, Armed: true })
    {
        ModernStandby.TrySetWakeArmed(device.Name, armed: false);
    }
}
ModernStandby.RestoreWakeDevices(snapshot);
```

`TrySetWakeArmed` re-reads programmability rather than trusting the caller's record, returns false
for a device Windows no longer offers, and is idempotent. Writes need elevation: unelevated,
Windows fails the set with `ERROR_WMI_SET_FAILURE` (4214), which surfaces as a `Win32Exception`.
`RestoreWakeDevices` touches only devices the snapshot observed, because a device that appeared
since has no prior state to restore.

`WasLastResumeUnattended` reports whether Windows attributes the last resume to something other
than the user — a wake timer, a device, background work — rather than a button, key or lid. It is
the one call that separates a wake worth staying awake for from one worth suspending again on, so
read it on the resume notification rather than caching it. `ReadStandbyTiming` returns the last
sleep and wake interrupt-time marks with the current one, from a single read, giving `Slept` and
`SinceWake` for a grace period or a standby diagnostic. Neither reports *what* woke the machine:
Windows exposes no documented call for that.

Software wake sources are ordinary power settings. `ModernStandby` exposes their identities —
`SettingAllowWakeTimers`, `SettingAllowAwayMode`, `SettingUnattendedSleepTimeout`,
`SettingConnectivityInStandby`, `SettingDisconnectedStandby` and the two subgroups — and they are
read and written with `WindowsPower.ReadSetting` / `WriteSetting` plus `RefreshActiveScheme`. The
library stores no policy of its own for them.

### Hybrid processor core placement

`QueryHybridCores` reports the machine's processor efficiency classes from Windows CPU set
information and whether one scheme exposes the three hidden processor settings that steer thread
placement on hybrid parts: HETEROPOLICY, SCHEDPOLICY and SHORTSCHEDPOLICY. Offer these controls only
when the machine is hybrid and the scheme is configurable; the published value lists come from
Windows, so a build that publishes none returns empty lists instead of an invented range.

```csharp
Guid scheme = WindowsPower.GetActiveScheme();
HybridCoreSupport support = WindowsPower.QueryHybridCores(scheme);
if (support is { Hybrid: true, Configurable: true })
{
    HybridCoreState previous = WindowsPower.ReadHybridCores(scheme, onBattery: false);
    WindowsPower.WriteHybridCores(scheme, onBattery: false,
        previous with { Threads = HybridSchedulingPolicy.PreferPerformantProcessors });
    WindowsPower.RefreshActiveScheme();
}
```

AC and battery values are separate; read and write each source explicitly. `ReadHybridCores` is the
snapshot to persist before a write and to restore from afterwards, because `WriteHybridCores` issues
its three writes in order and does not roll back. Windows applies processor policy when a scheme is
activated, so a write to the active scheme needs `RefreshActiveScheme` to take effect. A policy value
this library does not name is preserved as its raw number rather than replaced.

### Editable display layouts

`DisplayTopology` replays a captured native configuration, which restores what was there and nothing
else. `DisplayLayouts` is the editable form. `Observe()` reports every monitor the adapter can see,
active or not, with a fingerprint that only changes when the observation does; two equal
fingerprints a moment apart are what a caller waits for before acting on an arrival. `Capture()`
returns the current desktop as values: identity, position, resolution, refresh, rotation, scaling
and HDR. `Validate(layout)` asks Windows without changing anything, and `Apply(layout)` applies and
confirms by readback.

A layout is checked before Windows sees it: at least one display, exactly one at 0,0 as the primary,
no duplicates, no overlaps, and every display touching the arrangement, because Windows snaps a
detached desktop and the readback would then never match. A requested monitor that is not connected
returns `TargetsAbsent` rather than throwing, so a caller can wait for a television that only
appears when an HDMI switch selects this machine. An arrangement that already matches returns
`AlreadyActive` without writing, which makes compensation idempotent.

The planner supplies a complete configuration: the path already driving a monitor keeps its source,
a second display never shares one, monitors left out of the layout are supplied inactive, and the
target mode index is left invalid so the driver picks a signal for the requested resolution and
rate. Scaling and HDR are applied per display afterwards; a refusal there is a warning rather than a
reason to undo an arrangement that is already on screen. An unconfirmed application gets exactly one
rollback, and nothing is ever retried automatically. `SDC_TOPOLOGY_SUPPLIED` is deliberately not
used: it takes modes from the Windows database, so it cannot express position or resolution.

### Supported display modes

DisplayModes.Read(target) returns fresh current and driver-validated modes for an active CCD target.
DisplayModes.Apply(snapshot, mode) rechecks the exact route and advertised mode, applies without
persisting registry settings, confirms readback, and attempts one rollback after an unconfirmed write.
Run these blocking driver operations on a worker thread. Clone sources are refused because changing
one source affects multiple targets. Physical visibility still needs application-level confirmation.

Native API reference: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsexw
