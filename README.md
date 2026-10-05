# WindowsDeviceControl

Wi-Fi, Bluetooth, audio, display brightness, display layouts, power and storage control for .NET on Windows. The parts
that are awkward or undocumented, in one library, callable from an ordinary unpackaged process.

```
dotnet add package WindowsDeviceControl
```

Targets `net8.0-windows10.0.19041.0` and `net10.0-windows10.0.19041.0`. No COM registration, no packaged identity, no
native component, and no admin rights except where Windows itself demands them. It was extracted from a shipping Windows
shell application where all of it runs on real hardware every session.

## Why this exists

Each of these is solvable on its own, and collectively they are a bad week.

**Wi-Fi from an unpackaged process.** WinRT's `WiFiAdapter` needs the `wiFiControl` capability, which an unpackaged
desktop, service or kiosk application cannot declare. That leaves the native WLAN API and hand-written profile XML,
including the part nobody enjoys: a saved profile has to exist before you can join a protected network, and the SSID may
not be valid UTF-8.

**Default audio endpoint switching.** There is no public API. It goes through `IPolicyConfig`, a COM interface Microsoft
never documented and whose vtable ordering differs across Windows versions.

**Spatial sound and the endpoint format.** Windows Sonic, Dolby Atmos and DTS have a public WinRT API, but it only
answers when handed the WinRT device id built from the endpoint id, and it reports an unknown device as "no spatial
sound" rather than failing. The default format behind the Advanced tab and the speaker-setup wizard, which is where
channel count and 5.1 or 7.1 layout live, has no public API at all and goes through the same `IPolicyConfig`.

**Panel brightness.** The documented route is WMI `WmiMonitorBrightnessMethods`, which requires elevation and silently
does nothing on many laptop and handheld panels. The ACPI backlight device answers the same request unelevated.

**Bluetooth pairing that shows a PIN.** Enumerating and connecting is easy. Running the pairing ceremony, accepting a
deferral, surfacing the PIN to your own UI and answering it, is where the examples run out.

## Usage

```csharp
using WindowsDeviceControl;

// Wi-Fi
var status   = WindowsRadio.GetWifiStatus();
var networks = WindowsRadio.ListWifiNetworks();
WindowsRadio.RequestWifiScan();
var network  = networks[0].Key;                                       // SSID bytes + security
var joined   = WindowsRadio.ConnectWifi(network, "passphrase");      // Joined, Failed, Pending, Refused
if (joined.Outcome == WindowsRadio.WifiConnectOutcome.Failed)
{
    Console.WriteLine(WindowsRadio.ReasonText(joined.ReasonCode));    // Windows' own wording
    // ...and only re-prompt when the key was actually the problem.
    if (WindowsRadio.GetReasonVerdict(joined.ReasonCode) == WindowsRadio.WifiFailureKind.KeyRejected)
        AskForPassphraseAgain();
}
WindowsRadio.ForgetWifi(network);                                    // every matching profile

// Change feeds: one disposable registration per call; disposing it is the stop
using var wifiWatch = WindowsRadio.StartWifiWatch(change => Post(change));

// Bluetooth, including the pairing ceremony
foreach (var d in WindowsRadio.ListBluetoothDevices(pairedOnly: false))
    Console.WriteLine($"{d.Name} paired={d.Paired} connected={d.Connected}");

var paired = await WindowsRadio.PairBluetoothAsync(deviceId, onRequest: request =>
{
    // Show request.Pin in your own UI, then answer before the attempt's deadline.
    WindowsRadio.RespondToPairing(request.Token, accept: true, pin: null);
}, cancellationToken);                                                // TimeoutException after 90 s

// Radio power (airplane-mode aware), with each adapter's answer
WindowsRadio.GetPower(WindowsRadio.RadioKind.Bluetooth);
var power = WindowsRadio.SetPower(WindowsRadio.RadioKind.WiFi, on: true); // power.Access, power.Adapters

// Audio
CoreAudio.ListEndpoints(CoreAudio.AudioDirection.Render, out var outputs);
CoreAudio.SetDefaultEndpoint(outputs[0].Id, out var roleResults); // transactional per-role results
CoreAudio.SetVolume(35, out _);
CoreAudio.SetMuted(true);

// Spatial sound and the endpoint format, both applied to the running engine at once
CoreAudio.GetSpatialAudio(outputs[0].Id, out var spatial);            // supported formats, default, active
CoreAudio.SetSpatialAudio(outputs[0].Id, CoreAudio.SpatialAudioFormats.WindowsSonic, out var spatialStatus);
CoreAudio.ListSupportedDeviceFormats(outputs[0].Id, out var formats); // what the Advanced tab would offer
CoreAudio.SetDeviceFormat(outputs[0].Id, CoreAudio.AudioDeviceFormat.Pcm(channels: 6, sampleRate: 48000, bitsPerSample: 24));

// Brightness, no elevation
if (Backlight.TryReadBrightness(out int percent))
    Backlight.TrySetBrightness(Math.Min(100, percent + 10));

// Active CCD topology. Persist DevicePath and EDID IDs, then discard saved
// adapter/target coordinates after every topology change.
DisplayTopologySnapshot topology = DisplayTopology.CaptureActive();
DisplayTargetIdentity television = topology.Paths[0].Target;
DisplayScaleResult scaled = DisplayScaling.Set(television, 150); // Written, AlreadySet, Refused, ...
```

| Type                            | Role                                                                                                                                         |
|---------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------|
| `WindowsRadio`                  | Radio power, Wi-Fi scan, list, connect and forget, Bluetooth discovery and pairing, change watches (`StartWifiWatch`, `StartBluetoothWatch`) |
| `WifiProfile`                   | Builds profile XML: `CreateOpen`, `CreatePsk` in WPA3-transition, WPA2-AES and WPA-TKIP shapes; survives a non-UTF-8 SSID                    |
| `CoreAudio`                     | Endpoints, default-endpoint switching, volume and mute per direction, change watches (`StartVolumeWatch`, `StartEndpointWatch`), spatial sound, default format and channel layout, Bluetooth audio connect and disconnect |
| `AudioFilePreview`              | Plays one local audio file on the default route and reports a failure as `AudioPreviewFailure`                                              |
| `WaveOutFeedback`               | The short click Windows itself plays for volume feedback                                                                                     |
| `Backlight`                     | Internal panel brightness over the ACPI backlight device                                                                                     |
| `DisplayTopology`               | Active CCD paths and rematchable monitor identities (`DisplayTargetIdentity`, `ActiveDisplayPath`, `DisplayTopologySnapshot`)                |
| `DisplayLayouts`                | Complete desktop arrangements by value: which monitors are on, which is primary, position, mode, scaling and HDR                             |
| `DisplayModes`                  | Driver-validated modes of one active display, applied once without persisting, plus the primary display's transient mode                     |
| `DisplayEdid`                   | Progressive timings read from one monitor's EDID, even while its source is disabled                                                          |
| `DisplayScaling`                | One display's Windows scaling percentage, read and written through the relative-step packets                                                 |
| `DisplayColor`                  | One display's advanced colour (HDR) state, with support read before every write                                                              |
| `WindowsPower`                  | Schemes, AC and DC values, effective mode overlays, status, suspend and shutdown, notifications, hybrid core placement                       |
| `PowerNotificationRegistration` | A power-setting or suspend and resume notification registration; disposing it unregisters                                                   |
| `WindowsPowerRequest`           | A display or system wake request and its reason string                                                                                       |
| `PowerRequestList`              | The system-wide power request list that `powercfg /requests` shows, decoded with bounds checks                                               |
| `WindowsWakeSecurity`           | Wake-policy recovery values, capture and restore                                                                                             |
| `ModernStandby`                 | S0 idle support, wake devices, resume attribution, standby timing                                                                            |
| `WindowsStorage`                | Mounted volumes and the physical disk number behind each one, read-only                                                                      |

The other public types are the records, results and enums these members take and return, documented beside them.

Every public member is documented and the build fails on one that is not, so IntelliSense is the reference for
callback threads, blocking, consent, error meanings and ownership.

### Threading

Every call that reaches Windows blocks until Windows answers, including the WinRT and WLAN waits behind radio power,
Bluetooth listing and unpairing, spatial sound and the Wi-Fi connect wait. Call them from a worker thread, not a UI
thread. They are safe to call from any thread and take no cancellation token. `PairBluetoothAsync` is the exception: it
returns at once, completes on a Windows thread, and takes a token that ends that attempt only.

Watch callbacks, pairing questions and `AudioFilePreview.Failed` arrive on a Windows thread, never the caller's, so
marshal them to your UI yourself. A watch delivers one callback at a time per registration and contains a callback
exception. A pairing callback that throws declines its question and ends the attempt with that exception.

Native codes are kept on purpose, because renaming them would hide what they are. A failed `ConnectWifi` carries
Windows' raw WLAN reason code, which you pass to `ReasonText` or `GetReasonVerdict`; a failed WLAN list or scan is a
`Win32Exception` whose `NativeErrorCode` is the WLAN status (5 is the Windows 11 24H2 location gate); and the
`CoreAudio` methods returning `int` return HRESULTs. Everything else is a named enum: radio kind, audio direction,
network security, connection state and outcome, pairing kind and outcome, watch events, volume-key commands and Wi-Fi
failure classification.

A Wi-Fi network is identified by its `WifiNetworkKey`, the SSID's exact bytes plus its security class, never by display
text: two names can decode to the same text, and the same name can be advertised with different security. Every watch
returns a registration of its own; several can run side by side, and disposing one is that registration's stop.

## What Windows will still refuse you

Written down here rather than discovered at deployment time.

**Radio power needs consent.** `RequestAccess()` returns `DeniedByUser` when the user has denied radio control to your
application, and `DeniedBySystem` on a policy-managed or kiosk-provisioned machine. Neither is retryable, so check
before offering a toggle.

**Wi-Fi enumeration can require location consent.** `GetConsent(capability)` reports what the privacy store records, as
a diagnostic only, since the owning API remains the authority on what is permitted. This ambushes kiosk deployments in
particular, because the machine is often provisioned with location off.

**Not every panel has an ACPI backlight.** `TryReadBrightness` returns `false` rather than throwing. Treat that as "this
machine has no controllable internal panel", which is the normal answer on a desktop.

`docs/radios.md` records the platform constraints behind all of this, including the approaches that were tried and did
not work. Read it before changing how a Windows API is called.

## Audio file preview

`AudioFilePreview` owns local audio preview through Windows Media Foundation and the default audio route. `Play` takes
an existing absolute path; `Stop` releases playback and `Dispose` ends the owner. It changes neither endpoint selection
nor system volume. Unsupported or corrupt media raises `Failed` on the media callback thread with an
`AudioPreviewFailure`: Windows' error class, the extended HRESULT and its message. Consumers serialize owner calls and
marshal failures to their UI. A stopped preview's queued failure cannot replace a newer preview's status.

## Displays

Display identity uses the monitor device-interface path as its primary rematching key. EDID manufacturer and product IDs
are a fallback for when neither observation has a device path. Friendly names and `DISPLAY1` numbering are presentation
data. Adapter LUID and target ID describe the current route and have to be refreshed after hotplug. Enumeration retries
the documented sizing race, sizes its buffers from what Windows reports and stays read-only. Possible source and target
combinations can be far larger than the active display count: a desktop query on 2026-09-13 returned 284 possible routes
and three active ones.

The display records are plain positional data a caller can persist. Members are only ever added, each with a default,
so an older stored value still reads.

One display whose name cannot be read (a monitor dropping out, an indirect or virtual display) does not hide the others:
`CaptureActive` leaves it out, and a lookup by identity reports that display as unreadable rather than inactive only
when the unreadable path sits on its last known route.

The library has no wait of its own. A caller waiting for a monitor to arrive repeats `DisplayLayouts.Observe()` and acts
once two fingerprints a moment apart agree, on whatever timing and cancellation it needs.

Every display write in the process (layout apply, mode apply, the transient primary mode, scaling and advanced colour)
takes one shared gate, so two writers never interleave; reads take no lock. A write Windows accepts is the result and is
not read back. Results carry outcome codes and native statuses, not text: the caller words them.

### Editable display layouts

`DisplayLayouts` holds a desktop arrangement as editable values.

`Observe()` reports every monitor the adapter can see, active or not, with a fingerprint that only changes when the
observation does; two equal fingerprints a moment apart are what a caller waits for before acting on an arrival.
`Capture()` returns the current desktop as values: identity, position, resolution, refresh, rotation, scaling and HDR.
`Validate(layout)` asks Windows without changing anything, and `Apply(layout)` applies once. A layout output's rotation
of zero keeps whatever rotation the display runs at; any other value is written and compared. An apply and its rollback
are saved to the Windows display database, so the arrangement survives a reboot; a caller that needs to undo it applies
the layout it captured before.

`Describe(layout)` is the rule set on its own, returning the `DisplayLayoutProblem` the layout breaks or null. It is
pure and touches no display, so an editor can refuse a layout as it is typed and a stored layout can be checked while the
monitors it names are unplugged.

A layout is checked before Windows sees it: at least one display, exactly one at 0,0 as the primary, no duplicates, no
overlaps, and every display touching the arrangement, because Windows snaps a detached desktop to something other than
what was asked for. A requested monitor that is not connected returns `TargetsAbsent` rather than throwing, so a caller
can wait for a television that only appears once an HDMI switch selects this machine. An arrangement that already
matches returns `AlreadyActive` without writing, which makes compensation idempotent.

The planner supplies a complete configuration: the path already driving a monitor keeps its source, a second display
never shares one, monitors left out of the layout are supplied inactive, and the target mode index is left invalid so
the driver picks a signal for the requested resolution and rate. Scaling and HDR are applied per display afterwards, and
a refusal there is a typed warning rather than a reason to undo an arrangement that is already on screen. An
application Windows refuses gets exactly one rollback to the arrangement captured before it, scaling and colour
included, and nothing is ever retried automatically. `SDC_TOPOLOGY_SUPPLIED` is deliberately not used, because it
takes modes from the Windows database and so cannot express position or resolution.

Planned source modes use `DISPLAYCONFIG_PIXELFORMAT_32BPP` (4), and requested refresh rates use progressive scan
ordering. On 2026-09-13, a connected but inactive HISENSE on Windows build 26200 rejected a 3840x2160 at 60 Hz layout
with native error 87 while scan ordering was unspecified. Setting progressive ordering made native validation succeed
without changing the requested rate. That is validation evidence and does not by itself prove a completed display
switch.

Reference comparisons used
[DisplayMagician's CCD implementation](https://github.com/terrymacdonald/DisplayMagician/blob/main/DisplayMagicianShared/Windows/CCD.cs)
and [ColorControl's CCD implementation](https://github.com/Maassoft/ColorControl/blob/master/Shared/Native/CCD.cs).

### Supported display modes

`DisplayModes.Read(target)` returns fresh current and driver-validated modes for an active CCD target.
`DisplayModes.Apply(snapshot, mode)` rechecks the exact route and advertised mode and applies once without persisting
registry settings. A write Windows accepts is `Applied`; a refused one gets one write-back of the captured mode while the
route is unchanged. Run these blocking driver operations on a worker thread. Clone sources are refused, because changing
one source affects several targets. Driver acceptance does not prove that a picture is visible.

Native API reference:
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsexw

`DisplayEdid.ReadModes(target)` reads recognized progressive timings from the exact monitor's EDID, including when
Windows has disabled its source, with a status that separates a missing monitor, a timed-out lookup and a missing
descriptor. Call it on a worker. It uses
[DisplayMonitor.FromInterfaceIdAsync and GetDescriptor](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.displaymonitor)
with a three-second interface lookup budget, and no display is activated or tested against the driver. These are
candidates for a saved layout; the apply path still has to validate the complete arrangement.

The reader checks block checksums and bounds and handles base established, standard and detailed timings, common CTA
progressive video codes and detailed timings, and DisplayID type I and VII detailed timings. Unknown timing formats and
interlaced entries are skipped. Integer refresh rates follow `DisplayMode`, and EDID does not supply the driver's
complete set of scaled or custom modes.

Timing format references:
[DRM EDID timing definitions](https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/drm_edid.c)
and
[libdisplay-info DisplayID decoding](https://chromium.googlesource.com/external/gitlab.freedesktop.org/emersion/libdisplay-info/+/refs/heads/upstream/main/displayid.c).

On 2026-09-13, read-only checks on Windows build 26200 returned EDID modes for three connected but disabled monitors:
Odyssey G7 (3840x2160 at 165 Hz), HP X32 (2560x1440 at 165 Hz) and Odyssey G93SC (5120x1440 at 240 Hz). That did not
apply those modes or prove they are visible.

## Windows power policy

`WindowsPower` exposes scheme enumeration and localized names, active scheme read and write, AC and DC policy values,
and effective power-mode overlays. These synchronous calls belong off UI threads. Native failures throw `Win32Exception`
with the Windows error code. Each request is issued once, and a read reports what Windows stores. The order of several
writes is the caller's.

```csharp
Guid scheme = WindowsPower.GetActiveScheme();
string name = WindowsPower.ReadSchemeName(scheme);
Guid mode = WindowsPower.GetEffectiveMode();
// Explicit user action:
WindowsPower.SetActiveScheme(scheme);
```

`WriteSetting` updates a scheme's stored AC or DC value without activating it. Call `SetActiveScheme`
when your policy requires immediate application. A successful request is no guarantee that another Windows policy writer
will leave the value alone.

`SuspendAsync` requests standby or hibernation off-thread and usually completes after resume.
`RequestActionAsync` supports shutdown, restart and sign-out using the absolute system tool path, and completion means
the tool accepted the request. Cancellation cannot undo a dispatched operation.
`TryGetStatus` reports source and battery values with Windows' unknown sentinels intact. Window owners can register
power-setting notifications and suspend and resume notifications. Each returns a `PowerNotificationRegistration` that
unregisters when disposed, and a refused registration throws `Win32Exception` with the native error. The suspend and
resume registration is what delivers PBT_APMSUSPEND and the resume codes to a message-only window, which the broadcast
alone never reaches.

`WindowsPowerRequest` owns a display or system wake request and its reason string. Acquire and Release are idempotent,
failures throw with native error codes, and a failed release stays held until an explicit retry or disposal. Disposal
closes the kernel handle without repeating a failed clear. `PowerRequestList.Query` returns the entries with a
`PowerRequestListStatus`: anything other than `Read` comes with null entries, `AccessDenied` when the read needs
elevation, `QueryFailed` with the NTSTATUS, `UnrecognizedLayout` when the undocumented layout cannot be decoded safely,
and `Unsupported` in a 32-bit process. The buffer grows until the whole list fits; nothing is truncated.

`WindowsWakeSecurity.Capture` returns wake-policy recovery values, absent values included, and refuses a value that is
not a DWORD because it could not be restored exactly. Persist that snapshot before writing with `SetConsoleLockPolicy`,
`SetSchemeConsoleLock` (each scheme from `WindowsPower.EnumerateSchemes`, then `WindowsPower.RefreshActiveScheme`) and
`SetNoLockScreen`, where -1 deletes a value. `Restore` writes only what the snapshot recorded, skips a scheme removed
since, attempts every step once and returns the ones that failed; keep the snapshot until it reports none. Registry
writes need elevation, and this library never elevates and never stores application configuration. Some Windows
editions ignore personalization policy even after accepting its registry value.

### Modern Standby wake sources

`ModernStandby.Query` reports whether the machine supports S0 low-power idle and in what connected form, and which
mandatory wake paths exist: power button, sleep button, lid, wake alarm. Those are reported and never written. Nothing
in this class can take away a recovery wake path.

`EnumerateWakeDevices` returns the wake sources a caller can act on: the devices Windows reports as programmable, plus
any that are armed without being programmable. The second group comes back as
`WakeDeviceControl.Fixed`, visible and reportable but refused for writes. Devices that merely support waking from S0 are
deliberately excluded, since on a handheld that is most of the HID and Bluetooth endpoints, none of which Windows offers
for change.

```csharp
WakeDeviceSnapshot snapshot = ModernStandby.CaptureWakeDevices();
foreach (WakeDevice device in ModernStandby.EnumerateWakeDevices())
{
    if (device is { Control: WakeDeviceControl.Programmable, Armed: true })
    {
        ModernStandby.TrySetWakeArmed(device.Name, armed: false);
    }
}
IReadOnlyList<WakeDeviceRestoreFailure> failures = ModernStandby.RestoreWakeDevices(snapshot);
```

`TrySetWakeArmed` re-reads programmability rather than trusting the caller's record, returns false for a device Windows
no longer offers, and is idempotent. Writes need elevation: unelevated, Windows fails the set with
`ERROR_WMI_SET_FAILURE` (4214), which surfaces as a `Win32Exception`.
`RestoreWakeDevices` touches only devices the snapshot observed, because a device that appeared since has no prior state
to restore. It enumerates once, attempts every write even after one fails, and returns the devices whose write failed
with their native error; keep the snapshot until that list is empty.

`WasLastResumeUnattended` reports whether Windows attributes the last resume to something other than the user, meaning a
wake timer, a device or background work rather than a button, key or lid. It is the one call that separates a wake worth
staying awake for from one worth suspending again on, so read it on the resume notification rather than caching it.

`ReadStandbyTiming` returns the last sleep and wake interrupt-time marks along with the current one, from a single read,
giving `Slept` and `SinceWake` for a grace period or a standby diagnostic.

Neither of those reports *what* woke the machine. Windows exposes no documented call for that.

Software wake sources are ordinary power settings. `ModernStandby` exposes their identities,
`SettingAllowWakeTimers`, `SettingAllowAwayMode`, `SettingUnattendedSleepTimeout`,
`SettingConnectivityInStandby`, `SettingDisconnectedStandby` and the two subgroups, and they are read and written with
`WindowsPower.ReadSetting` and `WriteSetting` plus `RefreshActiveScheme`. The library stores no policy of its own for
them.

### Hybrid processor core placement

`QueryHybridCores` reports the machine's processor efficiency classes from Windows CPU set information, and whether a
scheme exposes the three hidden processor settings that steer thread placement on hybrid parts: HETEROPOLICY,
SCHEDPOLICY and SHORTSCHEDPOLICY. Offer these controls only when the machine is hybrid and the scheme is configurable.
The published value lists come from Windows, so a build that publishes none returns empty lists rather than an invented
range.

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

AC and battery values are separate, so read and write each source explicitly. `ReadHybridCores` is the snapshot to
persist before a write and to restore from afterwards, because `WriteHybridCores`
issues its three writes in order and does not roll back. Windows applies processor policy when a scheme is activated, so
a write to the active scheme needs `RefreshActiveScheme` to take effect. A policy value this library does not name is
preserved as its raw number rather than replaced.

## Status

Pre-1.0. The surface can still move before it is frozen, so pin an exact version if that matters to you.

Issues and pull requests are welcome, especially hardware reports. The behaviour of Wi-Fi drivers, Bluetooth stacks and
backlight interfaces varies more across machines than any of the underlying documentation admits.

## Licence

MIT, see `LICENSE`.
