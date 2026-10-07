# How WindowsDeviceControl works

WindowsDeviceControl is an in-process managed library around Win32, COM and WinRT. It supplies typed
identities, native ownership and bounded recovery at the API boundary. A host supplies its own UI,
policy, persisted settings and recovery storage. There is no bundled driver, service, COM
registration or application-specific configuration. Session actions use Windows' own `shutdown.exe`;
the other operations call Windows APIs directly.

The [API reference](api-reference.md) links every implementation file. The
[platform findings](radios.md) explain why the radio and audio paths use these APIs.

## Calls, state and completion

Most calls are synchronous and should run on workers: native operations can block in a service or
driver even when their managed wrapper is short. Pure helpers such as `WifiProfile`,
`WindowsRadio.AggregatePower`, `DisplayLayouts.Describe`, `DisplayRefresh` and
`WindowsWakeSecurity.IsSignInDisabled` operate on supplied values. Pairing and explicit power
actions return tasks. Task completion, accepted writes and observed device state are different
contracts; read the operation's result before choosing a follow-up.

| Boundary                                                                | Meaning                                                                                                    |
| ----------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------- |
| Radio power, display apply, brightness and ordinary power-policy writes | Windows accepted or refused the operation; no mandatory readback confirms the write                        |
| Wi-Fi connect                                                           | Waits for a scoped WLAN verdict, with a current-connection fallback; `Pending` can still complete later    |
| Bluetooth audio connect/disconnect                                      | Driver request accepted; refresh a later endpoint snapshot to observe the transition                       |
| Audio volume commands                                                   | Apply the command, then read volume/mute for outputs; an output-read failure can follow an accepted change |
| Spatial audio set                                                       | HRESULT describes whether the call completed; `SpatialAudioSetStatus` describes Windows' semantic answer   |
| Suspend                                                                 | Completes when `SetSuspendState` returns, normally after resume                                            |
| Shutdown, restart, sign-out                                             | Completes when the system tool exits successfully, before the session transition necessarily finishes      |

Read failures must remain distinguishable from unsupported hardware and empty successful results.
WLAN failures use `Win32Exception.NativeErrorCode`; audio integer returns use HRESULTs; display
results preserve CCD or `DISP_CHANGE_*` statuses; power-request enumeration carries NTSTATUS.
`WaveOutFeedback` encodes an `MMRESULT` in an HRESULT, and standby timing preserves NTSTATUS in a
`Win32Exception`. These number spaces are not interchangeable.

The library keeps a few process-local caches: briefly cached WinRT radio objects, a read-only WLAN
status handle, saved profile parses invalidated around mutations/notifications, and a lazily created
Core Audio enumerator. A stale WLAN status handle can be reopened once for a read. An audio-service
failure drops the cached enumerator so a later call can recreate it. Neither mechanism retries a
hardware write.

## Radio and Wi-Fi path

`GetPower` enumerates the requested radio kind and folds states in priority order: On, Disabled,
Off, Unknown, then Absent for no adapters. `SetPower` asks `Radio.RequestAccessAsync`, then writes
every adapter once. A denied access result has no adapter operations; individual adapter refusals
and HRESULTs remain in `RadioPowerResult`. A consent-store registry read is diagnostic and never
overrides the owning API. Match the process architecture to Windows, since x86-on-x64 enumeration
can look like no hardware.

Wi-Fi uses native WLANAPI. `RequestWifiScan` requests scans on every interface and returns before
the scan completes. `StartWifiWatch` reports scan completion and connection changes so a host can
refresh `ListWifiNetworks`. Lists merge exact SSID bytes plus security into `WifiNetworkKey`; the
strongest observation supplies signal, while saved/connectable/connected facts are combined. Hidden
SSIDs are omitted. Profile identity comes from SSID bytes inside the profile XML, not its name.
Multi-adapter scans/lists succeed if an adapter succeeds and throw if all fail; a successful partial
result has no per-adapter failure list. `GetWifiStatus` chooses one current interface and returns
Unknown for no interfaces, while operations requiring an interface throw.

A connection follows this sequence:

1. Validate the key/passphrase, read current adapter/network/profile facts and choose an interface.
2. Reuse the matching saved profile, preferring the connected profile then Windows priority order,
   or generate collision-free all-user XML. Exact SSID hex and XML escaping preserve identity.
   Personal profiles try the applicable WPA shapes; unsupported enterprise/WEP flows remain typed
   refusals instead of invented credentials.
3. Capture any overwritten profile XML, register a notification verdict scoped to adapter and
   profile, then issue `WlanConnect`. Registration failure prevents dispatch.
4. Wait up to 25 seconds for completion/failure. A successful event is Joined. Otherwise a current
   connection with the requested SSID can still report Joined; without a verdict it is Pending.
5. On a definite failure, restore an overwritten profile once. Remove a new profile for classified
   key/security rejection. Pending attempts retain their profiles because association can finish
   later. A cleanup failure is reported together with the original failure, never retried.

Only `GetReasonVerdict` classifying a key failure justifies a credential re-prompt. A WLAN API error
and a connection reason are separate: preserve the exception/status and `ReasonCode` respectively.
`DisconnectWifi` leaves profiles and Windows auto-connect behavior intact. `ForgetWifi` deletes
every matching readable profile, reports each delete status and skips unreadable identities.

Each Wi-Fi watch owns a WLAN handle and rooted delegate. Disposal unregisters, waits for active
delivery and closes the handle. Never dispose from its callback or synchronously wait on a thread
that may dispose it. WLAN service restart ends the feed silently; the host decides when to restart
the registration.

## Bluetooth path

Discovery uses one combined classic/LE Association Endpoint selector. Snapshot enumeration groups by
normalized container GUID, falling back to endpoint identity when no container exists. A live watch
instead maintains endpoint-ID records and emits Added, Updated, Removed, EnumerationCompleted and
Stopped. Those are intentionally different views. Disposal revokes handlers and stops the watcher;
`DeviceWatcher.Stop` alone would leave asynchronous callbacks possible.

`PairBluetoothAsync` resolves the AEP, subscribes to custom pairing and starts one 90-second
deadline. ConfirmOnly, ProvidePin and ConfirmPinMatch are offered first. Only
`RequiredHandlerNotRegistered` triggers one DisplayPin fallback inside that same deadline. Each
question owns a deferral and token; the host presents `PairingKind` and answers with
`RespondToPairing`. Expired/repeated tokens are harmless. Cancellation completes only that attempt's
pending deferrals before cancelling its WinRT wait. A callback exception declines its question and
faults the attempt. Unpairing removes pairing identity and is separate from an audio disconnect.

Bluetooth audio joins the Bluetooth container GUID to Core Audio containers from both flows and all
endpoint states. Only audio-backed devices have this action. The implementation traverses
`IDeviceTopology` to `IKsControl`, tries matching endpoints until one accepts the request and
releases temporary interfaces. A later snapshot observes the connection; returning from the request
does not prove immediate connection, and there is no generic equivalent for arbitrary Bluetooth
peripherals.

## Audio and brightness path

The Core Audio enumerator is created on an MTA thread and shared. Per-operation endpoints,
collections, interfaces, format allocations and `PROPVARIANT` values have scoped cleanup. A service
failure invalidates the cache without releasing an RCW another caller or watch may still use.

Endpoint lists contain active render or capture endpoints, with the console default first.
`SetDefaultEndpoint` determines the selected endpoint's direction, captures all three role defaults
in that direction, then writes Console, Multimedia and Communications through `IPolicyConfig`. If an
apply fails, each already-changed role gets one reverse-order rollback. The primary HRESULT remains
the apply failure; detailed role results retain rollback failures.

Default-volume calls address the console default. Endpoint-ID overloads explicitly address one
endpoint. `SetVolume` clamps 0–100, writes volume, unmutes positive values and reads mute; these
steps are not transactional. `ApplyCommand` applies Windows' own volume step or toggles mute and
then reads outputs. An error can therefore follow an accepted mutation; repeating a step on error
can apply it twice.

Volume watches bind to the endpoint that was default at registration time. An endpoint watch reports
console-default changes so the host can replace the old volume watch. Endpoint property changes and
multimedia/communications role notifications are filtered. Native callbacks contain consumer
exceptions and must remain short; marshal work to the host's dispatcher and dispose registrations
outside native callbacks.

Spatial audio validates the Core Audio endpoint, constructs its WinRT render interface ID and calls
`SpatialAudioDeviceConfiguration`. The supported list probes the library's known format GUIDs;
default/active GUIDs can identify other provider formats. Support is not a licence. A zero HRESULT
from `SetSpatialAudio` still requires checking its separate status for licence, access or support
refusals.

Default audio formats use `IPolicyConfig` with integer PCM plus float mix format.
`AudioDeviceFormat` describes channel mask, rate and depth; 24-bit PCM uses a 32-bit container.
Listing probes a finite set of channels/rates/depths using exclusive-mode `IsFormatSupported`,
without opening streams. Setting issues one write and can restart open streams; unsupported format
is a native refusal, with no retry or rollback.

`AudioFilePreview` owns one `MediaPlayer` on the default route. `Play` stops the prior preview;
media failures carry their error class/HRESULT on a native callback, with stale-player failures
ignored. `Stop` releases playback and `Dispose` ends the owner. It requires serialized owner calls.
`WaveOutFeedback` similarly has one owner, but keeps its waveOut stream and unmanaged sample/header
buffers open for low latency. It drops cues while one is queued. If driver teardown fails, disposal
retains the buffers for process lifetime to prevent use-after-free.

`Backlight` opens `\\.\LCD` per operation and transfers the three-byte `DISPLAY_BRIGHTNESS` packet.
A write clamps its value and writes AC and DC together. False means the device could not be used or
the operation failed; absence of a controllable internal panel is ordinary. No WMI or external
monitor DDC/CI fallback is attempted.

## Display path

CCD queries size their buffers from Windows and retry an insufficient-buffer sizing race at most
four times. Unreadable individual identities are skipped without hiding readable monitors.
`DisplayTargetIdentity.Matches` compares EDID manufacturer/product and serial identity when both
observations have it, even when device paths differ. `DisplayEdid.ReadIdentity` reads the exact
interface's cached registry descriptor and accepts only a checksum-valid base block with a numeric
or text serial. Persist `EdidIdentity` with the target. If either serial identity is unavailable,
matching uses case-insensitive device paths; manufacturer/product is a fallback only when both paths
are empty. These IDs alone cannot distinguish identical monitor models. Ambiguous current routes are
refused before a write. An identity with neither path nor EDID IDs matches nothing. Adapter/target
coordinates and GDI names are current routes, not persisted physical identity.

`CaptureActive` returns active routes. `DisplayLayouts.Observe` reads possible routes and collapses
them into monitor observations, including inactive monitors. Its fingerprint includes identity,
availability, active state, placement, resolution and rational refresh, but excludes rotation,
scaling and HDR. The host owns settling waits and cancellation. `Capture` returns editable active
outputs, carrying nullable scaling/HDR where those observations are unavailable.

`Describe` runs pure layout rules: a nonempty set, valid resolutions, exactly one primary at the
origin, unique identities, no overlaps, supported scaling range and a connected arrangement.
`Validate` adds current Windows path selection and native validation but changes no display. Its
Applied result means the arrangement passed validation, not that it was written; it does not
preflight per-output HDR/scaling writes.

`Apply` holds the shared display-write gate throughout these stages:

1. Check rules and monitor availability. Absent requested monitors return TargetsAbsent.
2. If topology matches, skip its write and apply requested HDR/scaling; return AlreadyActive with
   any extras warnings. Default refresh accepts the observed rate; other refreshes match within half
   a hertz, and rotation zero means preserve current rotation.
3. Plan unique source routes, disable omitted targets and ask Windows to validate a complete
   supplied configuration. The planner uses 32-bpp source modes and progressive requested timings.
4. Capture native active topology and readable extras for rollback, then apply once with
   `SDC_SAVE_TO_DATABASE`. Success is accepted without readback, followed by HDR/scaling. A refused
   extra becomes a warning and does not undo accepted topology.
5. If topology apply is refused, write its captured topology back once, also persisting it. When
   rollback is accepted, restore captured extras best effort. `RollbackSucceeded` covers topology
   only; rollback-extra failures are not surfaced in `Warnings`.

`DisplayModes.Read` instead enumerates and driver-tests modes for one active, non-cloned source.
`Apply` requires a selected mode from that snapshot, rechecks route and advertisement, validates,
then writes transiently. A refusal gets one original-mode write-back only while the route is still
the same. Primary-display helpers are lower-level: enumeration can contain duplicates, testing is
explicit, and transient apply has no snapshot-based route validation or automatic rollback.

`DisplayEdid.ReadModes` uses the exact monitor interface, including a disabled source, with a
three-second interface-lookup budget. Base EDID, CTA and DisplayID parsing checks bounds/checksums
and skips unrecognized/interlaced timings. A read descriptor can yield an empty mode list.
Candidates are not driver-validated, and the lookup budget does not bound all subsequent descriptor
work. Layout validation remains necessary.

Scaling is stored as steps relative to Windows' recommended source scale. `Set` reads that input,
clamps/snaps the request and writes once; `Snap` alone uses the library's known steps and does not
query a display. HDR addresses the CCD target and reads support/current state before a write.
Unsupported-on is refused; unsupported-off is already satisfied. These prerequisite reads compute
valid requests, not post-write confirmations. All display writes share the gate; reads do not, and
external Windows writers remain independent.

## Power, wake and storage path

`WindowsPower` enumerates schemes, reads localized names and exposes stored AC/DC values and power
mode overlays. A policy write neither activates the scheme nor rolls back other writes.
`RefreshActiveScheme` explicitly reapplies the active scheme. Hybrid support reads CPU efficiency
classes, scheme values and Windows' published value lists. `WriteHybridCores` writes HETEROPOLICY,
SCHEDPOLICY and SHORTSCHEDPOLICY in order for one power source; partial failure leaves earlier
writes stored. Persist `HybridCoreState` for caller-owned restoration and preserve unnamed numeric
values.

`TryGetStatus` keeps battery/AC unknown sentinels intact. Notification registrations target a
caller-owned window receiving `WM_POWERBROADCAST`; they do not supply a managed callback or message
loop. Dispose each registration before discarding its window. Suspend runs `SetSuspendState` on a
worker and cancellation affects admission only. Session actions start the absolute system
`shutdown.exe` path; cancelling its wait does not reverse a dispatched operation.

`WindowsPowerRequest` starts inert. `Acquire` creates a native handle and reason buffer and sets one
request; repeat success is inert. `Release` clears once, and failure leaves `IsHeld` true. Disposal
closes the handle and frees the reason without repeating a failed clear. All owner methods are
locked. Windows still decides battery limits and honors explicit user sleep.

`PowerRequestList.Query` grows a native query buffer until the full system list fits, then decodes a
build-specific 64-bit layout with checked offsets. Only Read carries entries; unsupported bitness,
access denial, native failure or unknown layout carry null, never a false empty/all-clear list.

`ModernStandby.Query` reads S0 capabilities and mandatory wake paths. Wake-device enumeration unions
programmable devices with armed-but-fixed devices, not every endpoint that can wake. A process-local
gate owns the native device list across enumeration and mutation. A named write rechecks
programmability, skips an already-matching state and issues one elevated write. Capture stores
known/armed names; restore touches only captured devices that remain programmable, reports every
failed write, and skips devices that disappeared or became fixed. Neither method disables mandatory
power-button/lid paths. Resume attribution and sequential interrupt-time reads support a host's
policy; the library never decides to suspend again on its own.

`WindowsWakeSecurity.Capture` reads exact stored DWORD values and absence before any mutation. The
host persists this recovery snapshot. `SetConsoleLockPolicy`, `SetSchemeConsoleLock` and
`SetNoLockScreen` are elevated primitives; deletion uses the documented -1 sentinel. Restore
attempts policy, still-installed captured schemes, active-scheme refresh and personalization,
reporting failures while continuing other steps. Retain the snapshot while any step fails. A
successful stored value does not guarantee every Windows edition honors that policy.

`WindowsStorage` is read-only. `DriveInfo` supplies local drive-letter metadata and a zero-access
volume handle supplies `IOCTL_STORAGE_GET_DEVICE_NUMBER`. Unknown disk numbers remain -1, not a
shared device identity. Not-ready rows have zero sizes; inaccessible metadata can omit a row. There
are no mount, eject, format or storage-write operations in this library.
