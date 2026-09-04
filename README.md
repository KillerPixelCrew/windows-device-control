# WindowsDeviceControl

Wi-Fi, Bluetooth, audio and display-brightness control for .NET on Windows — the parts that are
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
```

| Type | Role |
| --- | --- |
| `WindowsRadio` | Radio power, Wi-Fi scan/list/connect/forget, Bluetooth discovery and pairing, change watches (`StartWifiWatch`, `StartBluetoothWatch`) |
| `WifiProfile` | Builds profile XML: `CreateOpen`, `CreatePsk` in WPA3-transition / WPA2-AES / WPA-TKIP shapes; survives a non-UTF-8 SSID |
| `CoreAudio` | Endpoints, default-endpoint switching, volume and mute per direction, Bluetooth audio connect/disconnect |
| `Backlight` | Internal panel brightness over the ACPI backlight device |
| `WaveOutFeedback` | The short click Windows itself plays for volume feedback |

Every public member is documented and the build fails on one that is not, so IntelliSense is the
reference — including which callbacks arrive on a Windows service thread and which calls return
before the work they started has finished.

Two integer contracts are kept on purpose, because renaming them would hide what they are:
`ConnectWifi` returns Windows' raw WLAN reason code (pass it to `ReasonText` or
`GetReasonVerdict`), and the `CoreAudio` methods returning `int` return HRESULTs. Everything else — radio kind,
audio direction, network security, connection state, pairing kind and outcome, watch events,
volume-key commands, Wi-Fi failure classification — is a named enum.

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
