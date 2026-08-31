# Radio and Bluetooth integration

This file records the Windows constraints behind Wi-Fi, Bluetooth, pairing, and Bluetooth audio. The
implementation is managed code in `Interop\WindowsRadio.cs`, `Interop\WifiProfile.cs`, and
`Interop\CoreAudio.cs`; there is no separate radio DLL or probe executable.

## Radio power

Radio access and power use `Windows.Devices.Radios.Radio`. Always request access before changing
state, enumerate every adapter of the requested kind, and apply the requested state to all of them.
Handhelds can expose more than one Bluetooth or Wi-Fi radio; changing only the first leaves the UI
claiming a state that does not describe the machine. Aggregate state is deterministic: On wins over
Off, then Disabled, then Unknown; an empty adapter set is Absent.

Radio enumeration returns adapters only when the process architecture matches Windows, so WSGM
remains an explicit `win-x64` application rather than AnyCPU. `SetStateAsync` is separately gated by
the “Allow apps to control device radios” privacy decision; enumeration and state reads are not.

The adapter list is cached briefly because WinRT enumeration can stall; each cached radio still
reports its live state, and the enumeration expires so newly attached adapters are discovered.
Failures remain feature-local: an unavailable radio must not take down the shell or stop the other
radio from refreshing.

## Wi-Fi

Wi-Fi enumeration and connection use the WLAN API, not `WiFiAdapter`. WSGM is an unpackaged desktop
process, and the WinRT adapter API is not a dependable replacement for WLANAPI in that process
model. Windows 11 24H2 can deny scan-derived information until the user has granted location access;
the radio probe reports the location and radios consent registry state for diagnosis but never
writes it. Consent-store values are never used as a precondition: on a Windows 11 25H2 machine the
store reported `radios = Deny` while `Radio.RequestAccessAsync` returned Allowed, proving that the
owning API remains the authoritative answer for an unpackaged process.

Every WLAN interface participates. Scans are requested on all adapters, network results are merged
by their raw SSID bytes, and the strongest observation supplies the display signal. Saved profiles
are matched through the SSID in their XML rather than assuming the profile name equals the SSID.
That distinction matters after Windows or an administrator renames a profile.

Profile generation preserves these rules:

- XML-escape every display value and include `<hex>` for the exact SSID bytes. This preserves
  non-ASCII and otherwise ambiguous network names.
- Validate personal-network keys before asking Windows to connect: 8-63 Unicode characters, or
  exactly 64 hexadecimal characters for a raw PSK.
- Use WPA3-SAE transition mode first for modern personal networks, then WPA2-AES. Legacy WPA-PSK
  additionally gets the WPA-TKIP form Windows expects.
- Enhanced Open uses OWE and is not presented as an unsecured legacy network. Enterprise and WEP
  networks stay visible but WSGM does not invent an EAP or WEP credential flow.
- Pick a collision-free temporary profile name. A failed key/authentication attempt removes the
  profile it authored, while a pre-existing saved profile is never overwritten or deleted as
  rollback.
- Create all-user profiles. A user-scope profile cannot be used from WSGM's shell-less/elevated
  diagnostic contexts and would not appear in Windows' normal network list.

`WlanConnect` accepting a request is not success. The connection path registers a callback before
issuing the request, scopes the verdict to the selected interface and profile, waits for the ACM
completion/failure notification, and falls back to the current interface state only when the event
does not arrive. Reason codes are mapped separately so the UI asks for the password again only when
Windows reports an authentication/key failure; an association timeout is not blamed on the user's
typing. Live UI refresh uses ACM/MSM notifications, with ACM-only fallback on drivers that reject
the combined subscription.

## Bluetooth discovery and pairing

Discovery watches both classic and LE Association Endpoints. Device rows retain the Windows AEP id,
container id, display name, paired/can-pair flags, and connectivity state. The classic and LE
selectors must remain separate; relying on one silently loses devices from the other transport. The
legacy Win32 Bluetooth API was rejected because it cannot discover LE devices. 32feet.NET was also
tested and rejected: its power-off mode did not power down the radio, its WinRT path expected a
`CoreWindow` that Avalonia does not provide, and its numeric-comparison handler accepted without
presenting the question to WSGM's UI.

The watcher publishes additions and updates immediately and removes rows absent at the end of a
completed sweep. Stop revokes every event handler before returning because `DeviceWatcher.Stop()` is
asynchronous and can otherwise deliver into a manager that has already been disposed.

Pairing uses `Custom.PairAsync` with every ceremony supported by the panel: ConfirmOnly, DisplayPin,
ProvidePin, and ConfirmPinMatch. The `PairingRequested` deferral must stay alive until the UI answer
is applied. The answer itself runs on an MTA worker; completing the WinRT deferral from the Avalonia
thread was observed to hang pairing indefinitely. Pairing is bounded to 90 seconds and a
cancel/timeout completes pending deferrals before cancelling the operation. Some devices reject the
first ceremony mask but accept DisplayPin, so the managed path retains that one retry. Unpairing
uses the same Association Endpoint id and is a separate destructive action from audio disconnect.

## Bluetooth audio

Audio devices are identified by their Core Audio endpoint container GUID, not their Bluetooth
friendly name. WSGM enumerates active and unplugged render/capture endpoints and ORs endpoint state
per container. Only rows backed by an audio endpoint get a Connect/Disconnect action.

Windows exposes no generic “connect this paired Bluetooth device” API. HID, gamepad, and BLE
peripherals reconnect when used. The button Windows exposes for headsets is specifically a
Bluetooth-audio driver operation, so WSGM offers the soft action only for audio-backed rows and
keeps Remove Pairing separate.

The action is the Windows one-shot used by the sound control path: activate `IDeviceTopology` on an
endpoint, traverse its connected connector to `IKsControl`, and send the Bluetooth audio reconnect
or disconnect property. The endpoint is not kept open and the UI confirms the result from the next
endpoint snapshot. COM interfaces and PROPVARIANT cleanup stay private to `Interop\CoreAudio.cs`.

## Touch keyboard boundary

The radio panel's credential and PIN entry uses WSGM's own `Controls\OnScreenKeyboard`; it never
depends on `TabTip.exe`. The Windows touch keyboard still depends on Explorer completing its normal
unelevated per-session initialization before game-mode takeover. That shell invariant is documented
in `boot-and-shell.md` and must not be weakened as part of radio work.

## Diagnostics and verification

`WSGM.exe --radio-probe` is the read-only diagnostic. It records process elevation, Explorer
presence, radio power/access, consent state, WLAN scan/list/status, and Bluetooth enumeration in the
normal WSGM log. `--radio-pair <name>` is intentionally separate because it changes pairing state
and requires attended use. Compile and isolated tests prove the managed contracts; power, discovery,
pairing ceremonies, audio reconnection, location consent, and shell-less/elevated behavior still
need device verification on the reference handheld.
