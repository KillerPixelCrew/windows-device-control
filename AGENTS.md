# WindowsDeviceControl contributor guide

## Scope and sources of truth

WindowsDeviceControl is a public, pre-1.0 .NET library for Windows radio, Wi-Fi, Bluetooth, audio,
internal-panel brightness, display topology, layouts and modes, power policy, Modern Standby, wake
security and storage control from an ordinary unpackaged process. It describes Windows, not any one
host application.

Read `README.md` for the public behavior, `docs/how-it-works.md` for request and ownership paths,
and `docs/api-reference.md` for the complete source/API/test map. Read `docs/radios.md` before
changing a radio or audio Windows API path: it records dated platform findings and rejected
alternatives. Do not replace a proven route with a superficially simpler API without new evidence
that addresses the documented failure.

Every public member must have complete XML documentation. IntelliSense is part of the contract,
including callback threading, completion timing, consent, error meanings, and ownership.

## Repository map

- `WindowsRadio*.cs`: radio access and power, native WLAN, Wi-Fi watches, Bluetooth discovery,
  pairing, and unpairing. `WindowsRadio.cs` holds the public types, and the partials follow the
  areas: `Power`, `Bluetooth`, `Wifi`, `WifiRules` (the pure Wi-Fi decisions the tests cover),
  `WifiWatch` (WLAN notifications) and `WlanNative`. Only the three WLAN partials are `unsafe`.
- `WifiProfile.cs`: exact WLAN profile XML, SSID bytes, security shapes, and passphrase validation.
- `CoreAudio.cs`: endpoint enumeration, default-role transactions, and volume/mute.
  `CoreAudio.Bluetooth.cs` holds Bluetooth audio connection, `CoreAudio.Spatial.cs` the spatial
  sound state through the public WinRT configuration, `CoreAudio.Formats.cs` the endpoint default
  format (channel layout, rate, depth) through `IPolicyConfig`, `CoreAudio.Watch.cs` the volume and
  endpoint change notifications with the two callback interfaces they implement
  (`IAudioEndpointVolumeCallback`, `IMMNotificationClient`), and `CoreAudio.Native.cs` the other COM
  declarations, native wave-format layouts, `PROPVARIANT` cleanup and the shared device enumerator.
- `AudioFilePreview.cs`: one owned Media Foundation preview of a local file on the default route;
  failures carry Windows' error class and HRESULT.
- `Backlight.cs`: ACPI internal-panel brightness through `\\.\LCD`.
- `DisplayTopology*.cs`: supported CCD enumeration, stable monitor matching and the one display
  write gate in `DisplayTopology.cs`, and the public records and result types in
  `DisplayTopology.Types.cs`. `DisplayTopology.Native.cs` owns the native CCD shapes, which are
  internal so the layout, scaling and colour code share one set of offsets and one query rather than
  keeping second copies.
- `DisplayLayouts.cs` and `DisplayLayoutPlanner.cs`: complete desktop arrangements as values, with
  the planning rules kept pure and testable on synthetic path arrays. An absent monitor is a waiting
  state, matching topology is not rewritten (requested HDR/scaling still apply), an accepted apply
  is not read back, and a refused apply attempts one rollback. RollbackSucceeded describes topology
  only; rollback extras are best effort and their failures are not returned. The observation
  fingerprint excludes rotation, HDR and scaling. `Describe` exposes those rules without touching a
  display, so a caller's editor and its stored configuration check a layout the same way this
  library will. Keep it that way: a second copy of the rules in a consumer is how the two drift
  apart. Apply and its rollback always pass `SDC_SAVE_TO_DATABASE`, so an applied arrangement
  survives a reboot; that is documented, not a parameter. The display records are positional data
  consumers persist: never rename, retype or remove a public member, and give every added positional
  member a default.
- `DisplayModes.cs`: driver-mode enumeration, exact active-route validation and transient
  application with one write-back after a refusal and no readback. UI and mode-selection policy
  remain with callers.
- `DisplayEdid.cs`: read-only timing candidates for an exact monitor interface, even while its
  source is disabled. Keep checksums and block bounds strict; EDID candidates are not
  driver-validated mode snapshots and must not bypass the normal layout validation and apply path.
  `DisplayEdid.Identity.cs` reads checksum-valid cached EDID serial identity for the exact
  interface. Persist it with the target and prefer it over volatile device paths when both
  observations have it. Manufacturer/product alone cannot distinguish identical models; ambiguous
  routes are refused.
- `DisplayScaling.cs` and `DisplayColor.cs`: per-display scaling percentage and advanced colour,
  addressed by monitor identity. Support and current value are read before every write as its input,
  nothing is read after it, and a refusal is reported with its native status rather than retried.
  Display results are outcome codes and statuses; wording belongs to the caller.
- `WaveOutFeedback.cs`: reusable low-latency volume cue.
- `WindowsPower*.cs`: power-scheme enumeration, policy values, power-mode operations and hybrid
  processor core placement. Each request is issued once and a read reports what Windows stores;
  callers own policy ordering and UI, and the library preserves native error codes.
  `WindowsPower.Status.cs` preserves battery/AC unknown sentinels, `WindowsPower.Actions.cs` owns
  asynchronous suspend/session actions, and `WindowsPower.Notifications.cs` owns caller-window
  registrations and their SafeHandle lifetime. Cancellation cannot undo a dispatched session action.
- `WindowsPowerRequest.cs`: thread-safe native power-request ownership and reason-buffer lifetime.
- `PowerRequestList.cs`: bounds-checked system-wide wake-request decoding; an unreadable layout is
  unknown.
- `WindowsWakeSecurity.cs`: wake sign-in capture, write and restore primitives. Callers compose the
  writes, persist recovery snapshots before mutation and retain them until a restore reports no
  failure.
- `ModernStandby.cs`: S0 low-power-idle capability, wake-capable device enumeration and per-device
  arming with snapshot/restore, unattended-resume detection and standby timing, plus the identities
  of the software wake-source power settings.
- `WindowsStorage.cs`: read-only volume-to-disk-number mapping; nothing here mounts, ejects or
  writes. Paths are drive-letter paths only; failed disk lookups retain a metadata row with -1,
  which must never be used to group unknown disks.
- `Interop.cs`: internal helpers shared by the native callers: Win32 error codes and the exceptions
  that preserve them, the kernel32 device calls, fixed-width string reads, and blocking WinRT waits.
- `docs/radios.md`: platform rationale, failure modes, and rejected approaches.
- `docs/README.md`, `docs/how-it-works.md` and `docs/api-reference.md`: reading order, complete
  request paths and source/public API/test navigation. Update these alongside XML when a contract
  changes.
- `tests/WindowsDeviceControl.Tests`: deterministic, hardware-independent tests, one file per
  subject (`WindowsRadioTests`, `CoreAudioTests`, `DisplayTopologyTests`, `DisplayLayoutTests`,
  `WindowsPowerTests` and the rest), with shared builders and assertions in `TestFixtures.cs`.

Source paths without a leading directory in the map above are relative to
`src/WindowsDeviceControl`. There is no native companion, helper process, packaged identity, or COM
registration. Preserve ordinary unpackaged use unless an intentional public design change says
otherwise.

## Public contracts

The library targets both `net8.0-windows10.0.19041.0` and `net10.0-windows10.0.19041.0`. Keep the
Windows platform floor and both target frameworks aligned with the APIs used.

Native detail is kept, never flattened:

- `WindowsRadio.ConnectWifi` returns a `WifiConnectResult` whose failed outcome carries the Windows
  WLAN reason code; WLAN list and scan failures are `Win32Exception`s whose `NativeErrorCode` is the
  WLAN status.
- `CoreAudio` methods that return `int` return HRESULTs.

Do not replace those with invented success/failure enums that discard platform detail. Preserve the
named enums used for all other semantic state. Results carry codes and statuses; wording belongs to
the caller.

A Wi-Fi network is identified by `WifiNetworkKey`: its exact SSID bytes and security class. Display
text is never an identity.

Watches (`StartWifiWatch`, `StartBluetoothWatch`, the `CoreAudio` watches) return one disposable
registration per call. Registrations are independent, disposal is idempotent and is the stop, and
there is no process-wide feed slot. Callbacks arrive on a Windows thread, one at a time per
registration, and a callback exception is contained. A Wi-Fi registration must not be disposed from
inside its own callback.

Public API changes require implementation, XML documentation, README usage, and focused tests. The
package version lives in `src/WindowsDeviceControl/WindowsDeviceControl.csproj`.

## Radio and Wi-Fi invariants

Radio power uses `Windows.Devices.Radios.Radio`. Request access before mutation and apply the
requested state to every adapter of the requested kind. Preserve deterministic aggregate priority:
On, Disabled, Off, Unknown, then Absent for an empty set.

Radio enumeration and reads are distinct from permission to change state. The privacy consent store
is diagnostic only; the owning API remains authoritative.

A consumer process must match Windows architecture. Do not "fix" an x86-on-x64 empty enumeration by
reporting fabricated hardware.

Wi-Fi uses native WLANAPI, not WinRT `WiFiAdapter`. Preserve these rules:

- Scan and observe every WLAN interface.
- Merge networks by exact raw SSID bytes; use the strongest observation only for display signal.
- Match saved profiles by SSID inside their XML, not by profile name.
- XML-escape display text and retain `<hex>` for exact SSID bytes.
- Validate PSKs before connection.
- Keep WPA3 transition, WPA2-AES, legacy WPA-TKIP, OWE, and unsupported enterprise/WEP distinctions.
- Use collision-free names for generated all-user profiles.
- Snapshot and restore an existing profile's exact XML on failure.
- Never infer identity from unreadable XML, and never overwrite or delete such a profile.
- Register the WLAN callback before connecting and wait for the scoped completion/failure event.
- Re-prompt for credentials only for classified authentication or key failure.
- Serialize watch start, stop, and callback delivery; connection failures are observable changes.

## Bluetooth invariants

Snapshot discovery queries classic and LE Association Endpoints through the combined selector and
groups duplicate endpoints by container identity, never by friendly name. The live watcher uses the
same combined selector but keys records by endpoint ID and emits Added, Updated, and Removed changes
immediately. Preserve that deliberate distinction rather than making the watcher pretend it has a
snapshot-wide container view.

Stopping a watcher must revoke every handler before returning. `DeviceWatcher.Stop()` alone is
asynchronous and is not sufficient protection against callbacks into discarded state.

Pairing supports every documented ceremony. Keep the request deferral alive until answered and
complete each token at most once, including timeout/late-answer races. Pairing remains bounded, and
cancellation affects only its own attempt.

Unpairing is a destructive identity operation. It remains separate from the soft Bluetooth-audio
connect/disconnect action.

Bluetooth audio identity comes from Core Audio endpoint container GUIDs. Offer the soft action only
for audio-backed devices, send the one-shot `IKsControl` request, release the endpoint, and confirm
state from a later snapshot rather than treating the call return as final state.

## Audio, brightness, and feedback invariants

Keep COM declarations and `PROPVARIANT` cleanup private to `CoreAudio`, in `CoreAudio.Native.cs`.

Default endpoint changes are transactions across Console, Multimedia, and Communications roles.
Snapshot all previous defaults before writing. On failure, roll back every changed role in reverse
order, attempt every rollback even if one fails, and return per-role apply/rollback HRESULTs.

Volume commands have a different contract: ApplyCommand reads volume/mute after its write, and
SetVolume writes volume, unmutes positive values, then reads mute. A later failure can follow an
accepted mutation. Document that partial completion and do not automatically repeat a command.

Spatial sound goes through `Windows.Media.Audio.SpatialAudioDeviceConfiguration`, addressed by the
WinRT device id built from the endpoint id, after Core Audio has confirmed the endpoint exists; the
WinRT class answers an unknown id with "unsupported" rather than an error. Do not write the
endpoint's registry blobs instead: they are the audio service's own serialisation and are not picked
up live. A licence refusal is Windows' answer, returned as the named status, never retried.

The endpoint default format is one `IPolicyConfig.SetDeviceFormat` write with the integer PCM format
and its float mix form. Windows validates against the driver and refuses an unsupported layout with
`AUDCLNT_E_UNSUPPORTED_FORMAT` without changing anything, so there is no rollback and no retry.
`IPolicyConfig` is the Windows 7 and later layout: `GetPropertyValue` and `SetPropertyValue` carry
the FxProperties store flag, and dropping it shifts every argument.

Internal-panel brightness uses the ACPI backlight device. Do not substitute WMI or DDC/CI for this
contract. Set AC and DC policy together. Absence of a controllable internal panel is normal:
`TryReadBrightness` and `TrySetBrightness` report it without inventing success.

`WaveOutFeedback` keeps its endpoint open to avoid audible latency. Drop a cue while the previous
one is queued instead of building a repeated-key rattle. Disposal frees buffers only after
successful native teardown; on a teardown refusal, retain them until process exit instead of freeing
memory the driver may still reference.

Across all interop code, preserve exact native layouts, bounds checks, handle/COM ownership, and
callback lifetimes. Unsafe code needs a local, auditable reason.

## Power policy invariants

Hybrid core placement is exposed as the three Windows processor settings it actually is, not as an
invented set of composite performance modes. Capability comes from Windows: efficiency classes from
CPU set information, accepted values from the setting's published list. Report an empty list rather
than a guessed range, and preserve a policy value the enumeration does not name.

Writes stay single-shot and per power source. The library does not activate a scheme on the caller's
behalf, does not retry, and does not roll back a partial write; `ReadHybridCores` is the snapshot
the caller persists and restores from.

Modern Standby wake control is per named device and never wholesale. There is no call that disables
every wake source, and the power button, sleep button and lid are reported by `Query` rather than
being writable at all. Enumeration is the actionable set (programmable, plus armed-but-fixed), not
every device that supports waking from S0; a source that cannot safely be changed is reported as
`Fixed` instead of being written to. `TrySetWakeArmed` re-reads programmability at the moment of the
write, so a stale record cannot drive one. Restore touches only devices the snapshot observed,
attempts each of them once and reports the ones that failed rather than stopping at the first.

The enumeration's size argument is not an output: Windows leaves it at the buffer size it was given,
so a device name ends at its terminator. The end of the list and a genuine failure both return
FALSE, and only `ERROR_NO_MORE_ITEMS` separates them. Do not treat every FALSE as the end, which
reports a partial device list as a complete one.

## Testing

Keep automated tests deterministic and hardware-independent. Extract pure decision logic behind
internal seams when it allows safety behavior to be tested without changing the public API. A test
never reaches native state: argument checks are small internal helpers tested directly, not facade
calls that stay off the hardware only because validation happens to come first. Native layouts are
pinned by hand-written byte buffers at the offsets the Windows headers give, never by values copied
from the decoder, and a test that restates a source literal or predicate guards nothing.

Add or update tests for:

- aggregation, ordering, classification, and exact identity;
- profile collision, preservation, and rollback;
- callback/token idempotence;
- transactional role rollback;
- native buffer/layout boundaries;
- resource lifetime decisions.

Changes involving drivers, consent, pairing ceremonies, radios, audio topology, or panel hardware
also require explicit real-device validation. Report what hardware and Windows build were tested; do
not present unit-test success as proof of hardware compatibility.

## Validation

`Directory.Build.props` and `.editorconfig` at the repository root are roots: nothing from a parent
checkout applies, so the library and its tests build the same standalone and as a submodule. The
test project targets both frameworks the library ships for.

`WindowsDeviceControl.slnx` at the root lists the library and its tests. Build it, then run the
suite on each framework:

```powershell
dotnet build .\WindowsDeviceControl.slnx --configuration Release
dotnet test .\WindowsDeviceControl.slnx --configuration Release --no-build -f net8.0-windows10.0.19041.0
dotnet test .\WindowsDeviceControl.slnx --configuration Release --no-build -f net10.0-windows10.0.19041.0
```

For package changes, also verify packing from the already validated Release build:

```powershell
dotnet pack .\src\WindowsDeviceControl\WindowsDeviceControl.csproj --configuration Release --no-build
```

The build treats warnings, missing public-member documentation, and partially documented parameter
lists as errors. Do not suppress CS1591 or CS1573 in the library to make a change pass.

Do not commit `bin/`, `obj/`, or generated package output. Keep functional changes focused and avoid
unrelated formatting.
