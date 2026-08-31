# Windows constraints behind radio, Wi-Fi, Bluetooth and audio control

This file records what the platform actually does, including the approaches that were tried and
disproven. It is here rather than in comments because most of it spans several files, and because
the reasoning is worth more than the code: anyone re-deriving this surface from Microsoft's
documentation will make the same wrong turns.

The implementation is `src\WindowsDeviceControl\WindowsRadio.cs`, `WifiProfile.cs`, `CoreAudio.cs`,
`Backlight.cs` and `WaveOutFeedback.cs`. There is no native component and no helper process.

## Radio power

Radio access and power use `Windows.Devices.Radios.Radio`. Always request access before changing
state, enumerate every adapter of the requested kind, and apply the requested state to all of them.
A machine can expose more than one Bluetooth or Wi-Fi radio — handhelds routinely do — and changing
only the first leaves a UI claiming a state that does not describe the machine. Aggregate state is
deterministic: On wins over Off, then Disabled, then Unknown; an empty adapter set is Absent.

Radio enumeration returns adapters only when the process architecture matches Windows. An AnyCPU
process that lands on x86 under an x64 Windows enumerates nothing and reports Absent, which looks
exactly like a machine with no radios. Consumers should target the machine's architecture
explicitly.

`SetStateAsync` is separately gated by the "Allow apps to control device radios" privacy decision;
enumeration and state reads are not. So a caller can find itself able to report power state
perfectly and unable to change it.

The adapter list is cached briefly because WinRT enumeration can stall; each cached radio still
reports its live state, and the enumeration expires so newly attached adapters are discovered.
Failures stay feature-local: an unavailable radio must not stop the other radio from refreshing.

## Wi-Fi

Wi-Fi enumeration and connection use the WLAN API, not `WiFiAdapter`. For an unpackaged desktop
process the WinRT adapter API is not a dependable replacement for WLANAPI.

Windows 11 24H2 can deny scan-derived information until the user has granted location access. The
consent-store registry values are worth reporting for diagnosis but must never be used as a
precondition: on a Windows 11 25H2 machine the store reported `radios = Deny` while
`Radio.RequestAccessAsync` returned Allowed. The owning API remains the authoritative answer for an
unpackaged process.

Every WLAN interface participates. Scans are requested on all adapters, network results are merged
by their raw SSID bytes, and the strongest observation supplies the display signal. Saved profiles
are matched through the SSID inside their XML rather than by assuming the profile name equals the
SSID — a distinction that matters after Windows or an administrator renames a profile.

Profile generation preserves these rules:

- XML-escape every display value and include `<hex>` for the exact SSID bytes. This preserves
  non-ASCII and otherwise ambiguous network names.
- Validate personal-network keys before asking Windows to connect: 8-63 printable characters, or
  exactly 64 hexadecimal characters for a raw PSK. Windows' own refusal for a bad key arrives from
  the driver and says nothing a user can act on.
- Use WPA3-SAE transition mode first for modern personal networks, then WPA2-AES. Legacy WPA-PSK
  additionally gets the WPA-TKIP form Windows expects.
- Enhanced Open uses OWE and is not presented as an unsecured legacy network. Enterprise and WEP
  networks stay visible, but this library does not invent an EAP or WEP credential flow.
- Pick a collision-free temporary profile name. A failed key or authentication attempt removes the
  profile it authored, while a pre-existing saved profile is never overwritten or deleted as
  rollback.
- Create all-user profiles. A user-scope profile cannot be used from a shell-less or elevated
  context and would not appear in Windows' normal network list.

`WlanConnect` accepting a request is not success. The connection path registers a callback before
issuing the request, scopes the verdict to the selected interface and profile, waits for the ACM
completion or failure notification, and falls back to reading the current interface state only when
the event does not arrive.

Reason codes are classified so a UI asks for the password again only when Windows reports an
authentication or key failure. An association timeout must not be blamed on the user's typing: they
will retype a password that was already correct, and the real cause — range — goes unmentioned.

Live change notification uses ACM and MSM sources, with an ACM-only fallback for drivers that reject
the combined subscription.

## Bluetooth discovery and pairing

Discovery watches both classic and LE Association Endpoints. Device rows retain the Windows AEP id,
container id, display name, paired/can-pair flags, and connectivity state. The classic and LE
selectors must remain separate; relying on one silently loses devices from the other transport.

Two alternatives were tried and rejected. The legacy Win32 Bluetooth API cannot discover LE devices
at all. 32feet.NET failed on three counts: its power-off mode did not power down the radio, its
WinRT path expected a `CoreWindow` that a non-UWP UI framework does not provide, and its
numeric-comparison handler accepted the pairing without presenting the question to the application.

The watcher publishes additions and updates immediately and removes rows absent at the end of a
completed sweep. Stop revokes every event handler before returning, because `DeviceWatcher.Stop()`
is asynchronous and can otherwise deliver an event into an object that has already been disposed.

Pairing uses `Custom.PairAsync` with every ceremony a general UI can present: ConfirmOnly,
DisplayPin, ProvidePin and ConfirmPinMatch. Two constraints are easy to get wrong and both hang
pairing rather than failing it:

- The `PairingRequested` deferral must stay alive until the answer is applied.
- The answer must be completed on an MTA worker. Completing the WinRT deferral from a UI thread was
  observed to hang pairing indefinitely.

Pairing is bounded to 90 seconds, and a cancel or timeout completes pending deferrals before
cancelling the operation. Some devices reject the first ceremony mask but accept DisplayPin, so one
retry with that ceremony is retained. Unpairing uses the same Association Endpoint id and is a
separate, destructive action from an audio disconnect.

## Bluetooth audio

Audio devices are identified by their Core Audio endpoint container GUID, not their Bluetooth
friendly name. Active and unplugged render and capture endpoints are enumerated and endpoint state
is OR-ed per container, so only rows genuinely backed by an audio endpoint get a connect or
disconnect action.

Windows exposes no generic "connect this paired Bluetooth device" API. HID, gamepad and BLE
peripherals reconnect when used. The button Windows itself exposes for headsets is specifically a
Bluetooth-audio driver operation, which is why the soft action is offered only for audio-backed
devices and pairing removal stays separate.

The action is the same one-shot the Windows sound control path uses: activate `IDeviceTopology` on
an endpoint, traverse its connected connector to `IKsControl`, and send the Bluetooth audio
reconnect or disconnect property. The endpoint is not kept open, and the result is confirmed from
the next endpoint snapshot rather than from the call's return.

COM interface declarations and PROPVARIANT cleanup stay private to `CoreAudio.cs`.

## Panel brightness

`Backlight.cs` drives the internal panel through the ACPI display driver's brightness ioctls on
`\\.\LCD`. This exists because the alternatives do not cover an internal handheld panel: DDC/CI is
an external-monitor protocol, and the WMI brightness classes are absent on machines whose OEM does
not implement them.

Brightness is set for both AC and DC policy at once. Setting only the current power source produces
a panel that changes brightness by itself when the charger is plugged in or pulled out.

## Volume feedback

`WaveOutFeedback.cs` exists to make a volume-change cue audible without a delay. Opening a waveOut
endpoint takes long enough to be heard as lag, so the stream is opened once and kept. A cue is
dropped while the previous one is still queued: holding a volume key repeats faster than the sound
lasts, and queueing every repeat turns the feedback into a rattle.
