# Windows constraints behind radio, Wi-Fi, Bluetooth and audio control

What the platform actually does, and the approaches that were tried and disproven. It lives here
rather than in comments because most of it spans several files, and because anyone re-deriving this
surface from Microsoft's documentation will make the same wrong turns. The implementation is
`src\WindowsDeviceControl\WindowsRadio.cs`, `WifiProfile.cs`, `CoreAudio.cs`, `Backlight.cs` and
`WaveOutFeedback.cs`; there is no native component and no helper process.

## Radio power

Radio access and power use `Windows.Devices.Radios.Radio`. Access is requested before any state
change. Every adapter of the requested kind is enumerated and the requested state applied to all of
them: a machine can expose more than one Bluetooth or Wi-Fi radio (handhelds routinely do), and
changing only the first leaves a UI claiming a state that does not describe the machine. Aggregate
state is deterministic — On wins, then Disabled, then Off, then Unknown; an empty adapter set is
Absent — so a machine-wide or hardware block cannot be hidden by an adapter that is merely off.

The adapter list is cached briefly because WinRT enumeration can stall. Each cached radio still
reports its live state, and the cache expires so newly attached adapters are discovered. Failures
stay feature-local: an unavailable radio does not stop the other radio from refreshing.

### Radio enumeration needs a process architecture that matches Windows

An AnyCPU process that lands on x86 under x64 Windows enumerates nothing and reports Absent, which
looks exactly like a machine with no radios. Target the machine's architecture explicitly.

### Reading power state and changing it are gated separately

`SetStateAsync` is gated by the "Allow apps to control device radios" privacy decision; enumeration
and state reads are not. A caller can report power state perfectly and still be unable to change
it.

## Wi-Fi

Wi-Fi enumeration and connection use the WLAN API. Every WLAN interface participates: scans are
requested on all adapters, results are merged by their raw SSID bytes, and the strongest observation
supplies the display signal. Saved profiles are matched through the SSID inside their XML rather than
by assuming the profile name equals the SSID, which matters after Windows or an administrator renames
a profile.

### `WiFiAdapter` is not a replacement for WLANAPI

Tried and disproven: WinRT's `WiFiAdapter` for an unpackaged desktop process. It needs the
`wiFiControl` capability, which an unpackaged process cannot declare, so it is not dependable there.

### The consent store is a diagnostic, not a precondition

Windows 11 24H2 can deny scan-derived information until the user has granted location access. The
consent-store registry values are worth reporting for diagnosis, but a Windows 11 25H2 machine
reported `radios = Deny` in the store while `Radio.RequestAccessAsync` returned Allowed. The owning
API is the authoritative answer for an unpackaged process; the store is never used as a gate.

### Profile generation rules

- XML-escape every display value and include `<hex>` for the exact SSID bytes, so non-ASCII and
  otherwise ambiguous network names survive.
- Validate personal-network keys before asking Windows to connect: 8-63 printable characters, or
  exactly 64 hexadecimal characters for a raw PSK. Windows' own refusal for a bad key arrives from
  the driver and says nothing a user can act on.
- Use WPA3-SAE transition mode first for modern personal networks, then WPA2-AES. Legacy WPA-PSK
  additionally gets the WPA-TKIP form Windows expects.
- Enhanced Open uses OWE and is not presented as an unsecured legacy network. Enterprise and WEP
  networks stay visible, but the library does not invent an EAP or WEP credential flow.
- Pick a collision-free temporary profile name and fail if the bounded suffix space is exhausted.
- Create all-user profiles. A user-scope profile cannot be used from a shell-less or elevated
  context and would not appear in Windows' normal network list.

When credentials replace a profile for the same exact SSID, its XML is snapshotted first and
restored on failure. Unreadable profile XML is never treated as an SSID and is never overwritten or
deleted by inference. A failed key or authentication attempt removes a newly authored profile.

### `WlanConnect` accepting a request is not success

The connection path registers a callback before issuing the request, scopes the verdict to the
selected interface and profile, waits for the ACM completion or failure notification, and falls back
to reading the current interface state only when the event does not arrive.

### Only an authentication or key failure re-prompts for the password

Reason codes are classified so a UI asks for the password again only when Windows reports an
authentication or key failure. An association timeout blamed on the user's typing makes them retype
a password that was already correct, while the real cause — range — goes unmentioned.

### Change notification

Live change notification uses ACM and MSM sources, with an ACM-only fallback for drivers that reject
the combined subscription. Connection-attempt failures are observable changes too. Watch start, stop
and callback delivery are serialized so replacing or stopping a watch cannot leave a native callback
targeting discarded state.

## Bluetooth discovery and pairing

Discovery queries classic and LE Association Endpoints through one combined selector. A point-in-time
snapshot groups duplicate endpoints by container identity, while the live watcher keys records by
Windows AEP id and publishes Added, Updated, and Removed changes as Windows emits them. Device rows
retain the endpoint id, container id, display name, paired/can-pair flags, and connectivity state.
Stop revokes every event handler before returning, because `DeviceWatcher.Stop()` is asynchronous
and can otherwise deliver an event into an object that has already been disposed.

### The legacy Win32 Bluetooth API cannot discover LE devices

Tried and disproven: the classic Win32 Bluetooth API for discovery. It does not see LE devices at
all.

### 32feet.NET fails on three counts

Tried and disproven: 32feet.NET. Its power-off mode did not power down the radio; its WinRT path
expected a `CoreWindow` that a non-UWP UI framework does not provide; and its numeric-comparison
handler accepted the pairing without presenting the question to the application.

### Pairing ceremonies

Pairing uses `Custom.PairAsync` with every ceremony a general UI can present: ConfirmOnly,
DisplayPin, ProvidePin and ConfirmPinMatch. Two constraints hang pairing rather than failing it:

- The `PairingRequested` deferral must stay alive until the answer is applied.
- Each request token must complete that deferral at most once, including when a timeout races a
  late UI answer.

Pairing is bounded to 90 seconds. A cancel or timeout completes only that attempt's pending
deferrals before cancelling the operation; concurrent attempts cannot cancel one another. Repeating
an answer for an expired token is harmless. Some devices reject the first ceremony mask but accept
DisplayPin, so one retry with that ceremony is retained. Unpairing uses the same Association Endpoint
id and is a separate, destructive action from an audio disconnect.

## Bluetooth audio

Audio devices are identified by their Core Audio endpoint container GUID, not their Bluetooth
friendly name. Active and unplugged render and capture endpoints are enumerated and endpoint state is
OR-ed per container, so only rows genuinely backed by an audio endpoint get a connect or disconnect
action.

### There is no generic "connect this paired device" API

HID, gamepad and BLE peripherals reconnect when used. The button Windows itself exposes for headsets
is specifically a Bluetooth-audio driver operation, which is why the soft action is offered only for
audio-backed devices and pairing removal stays separate.

The action is the one-shot the Windows sound control path uses: activate `IDeviceTopology` on an
endpoint, traverse its connected connector to `IKsControl`, and send the Bluetooth audio reconnect
or disconnect property. The endpoint is not kept open, and the result is confirmed from the next
endpoint snapshot rather than from the call's return.

## Default endpoint switching

COM interface declarations and PROPVARIANT cleanup stay private to `CoreAudio.cs`. Changing the
default playback endpoint snapshots all three previous role defaults before the first write. A later
failure rolls every changed role back in reverse order, attempts every rollback, and returns the
per-role apply/rollback HRESULTs to callers that use the detailed overload.

## Panel brightness

`Backlight.cs` drives the internal panel through the ACPI display driver's brightness ioctls on
`\\.\LCD`. The alternatives do not cover an internal handheld panel: DDC/CI is an external-monitor
protocol, and the WMI brightness classes are absent on machines whose OEM does not implement them.

### Brightness is set for AC and DC policy at once

Setting only the current power source produces a panel that changes brightness by itself when the
charger is plugged in or pulled out.

## Volume feedback

`WaveOutFeedback.cs` makes a volume-change cue audible without a delay. Opening a waveOut endpoint
takes long enough to be heard as lag, so the stream is opened once and kept. A cue is dropped while
the previous one is still queued: holding a volume key repeats faster than the sound lasts, and
queueing every repeat turns the feedback into a rattle.
