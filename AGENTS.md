# WindowsDeviceControl contributor guide

## Scope and sources of truth

WindowsDeviceControl is a public, pre-1.0 .NET library for Windows radio, Wi-Fi, Bluetooth, audio,
and internal-panel brightness control from an ordinary unpackaged process.

Read `README.md` for the public behavior and `docs/radios.md` before changing any Windows API path.
The latter records live platform findings and rejected alternatives; do not replace a proven route
with a superficially simpler API without new evidence that addresses the documented failure.

Every public member must have complete XML documentation. IntelliSense is part of the contract,
including callback threading, completion timing, consent, error meanings, and ownership.

## Repository map

- `WindowsRadio.cs`: radio access and power, native WLAN, Wi-Fi watches, Bluetooth discovery,
  pairing, and unpairing.
- `WifiProfile.cs`: exact WLAN profile XML, SSID bytes, security shapes, and passphrase validation.
- `CoreAudio.cs`: endpoint enumeration, default-role transactions, volume/mute, and Bluetooth audio
  connection.
- `Backlight.cs`: ACPI internal-panel brightness through `\\.\LCD`.
- `WaveOutFeedback.cs`: reusable low-latency volume cue.
- `docs/radios.md`: platform rationale, failure modes, and rejected approaches.
- `tests/WindowsDeviceControl.Tests/SafetyTests.cs`: deterministic safety and rollback contracts.

Source paths without a leading directory in the map above are relative to
`src/WindowsDeviceControl`. There is no native companion, helper process, packaged identity, or COM
registration. Preserve ordinary unpackaged use unless an intentional public design change says
otherwise.

## Public contracts

The library targets both `net8.0-windows10.0.19041.0` and `net10.0-windows10.0.19041.0`. Keep the
Windows platform floor and both target frameworks aligned with the APIs used.

Two raw integer contracts are intentional:

- `WindowsRadio.ConnectWifi` returns the Windows WLAN reason code.
- `CoreAudio` methods that return `int` return HRESULTs.

Do not replace those with invented success/failure enums that discard platform detail. Preserve the
named enums used for all other semantic state.

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

Keep COM declarations and `PROPVARIANT` cleanup private to `CoreAudio.cs`.

Default endpoint changes are transactions across Console, Multimedia, and Communications roles.
Snapshot all previous defaults before writing. On failure, roll back every changed role in reverse
order, attempt every rollback even if one fails, and return per-role apply/rollback HRESULTs.

Internal-panel brightness uses the ACPI backlight device. Do not substitute WMI or DDC/CI for this
contract. Set AC and DC policy together. Absence of a controllable internal panel is normal:
`TryReadBrightness` and `TrySetBrightness` report it without inventing success.

`WaveOutFeedback` keeps its endpoint open to avoid audible latency. Drop a cue while the previous
one is queued instead of building a repeated-key rattle. Disposal must release all native resources.

Across all interop code, preserve exact native layouts, bounds checks, handle/COM ownership, and
callback lifetimes. Unsafe code needs a local, auditable reason.

## Testing

Keep automated tests deterministic and hardware-independent. Extract pure decision logic behind
internal seams when it allows safety behavior to be tested without changing the public API.

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

Build the multi-target library first, then run the test project:

```powershell
dotnet build .\src\WindowsDeviceControl\WindowsDeviceControl.csproj --configuration Release
dotnet test .\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj --configuration Release
```

For package changes, also verify packing from the already validated Release build:

```powershell
dotnet pack .\src\WindowsDeviceControl\WindowsDeviceControl.csproj --configuration Release --no-build
```

The build treats warnings, missing public-member documentation, and partially documented parameter
lists as errors. Do not suppress CS1591 or CS1573 in the library to make a change pass.

Do not commit `bin/`, `obj/`, or generated package output. Keep functional changes focused and avoid
unrelated formatting.
