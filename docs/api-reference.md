# API and source reference

All library code is under `src/WindowsDeviceControl` in the `WindowsDeviceControl` namespace. This
index covers every implementation file and callable public operation. Follow a source link for the
complete XML documentation on parameters, result properties, enum values, exceptions and ownership.
Generated record constructors/properties, equality and deconstruction follow their positional
declarations; do not replace persisted display members or infer identity from display labels.

For behavior spanning several files, read [How it works](how-it-works.md). For examples and build
commands, read the [README](../README.md).

## Radio and Wi-Fi

Members in this section belong to `WindowsRadio` unless another owner is named.

| Source                                                                               | Public operations and contracts                                                                                                                | Implementation responsibility                                                                                                |
| ------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| [WindowsRadio.cs](../src/WindowsDeviceControl/WindowsRadio.cs)                       | Radio enums; Wi-Fi/Bluetooth records; `WifiNetworkKey` constructor, `Ssid`, `Security`, `Hex`, `DisplayText`, equality, hashing and `ToString` | Shared public vocabulary; immutable copied SSID bytes; identity is bytes plus security                                       |
| [WindowsRadio.Power.cs](../src/WindowsDeviceControl/WindowsRadio.Power.cs)           | `GetPower`, `RequestAccess`, `SetPower`, `GetConsent`, `AggregatePower`                                                                        | WinRT radios, brief enumeration cache, deterministic state aggregation, per-adapter write results and diagnostic consent     |
| [WindowsRadio.Wifi.cs](../src/WindowsDeviceControl/WindowsRadio.Wifi.cs)             | `GetWifiStatus`, `RequestWifiScan`, `ListWifiNetworks`, `ConnectWifi`, `DisconnectWifi`, `ForgetWifi`, `ReasonText`                            | WLAN operations, interface/profile selection, profile parse cache, all-user writes and exact rollback                        |
| [WindowsRadio.WifiRules.cs](../src/WindowsDeviceControl/WindowsRadio.WifiRules.cs)   | `GetReasonVerdict`                                                                                                                             | Pure security/reason classification, collision-free profile naming, network merge and refusal decisions                      |
| [WindowsRadio.WifiWatch.cs](../src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs)   | `StartWifiWatch`                                                                                                                               | Independent notification registration, serialized delivery, scoped connection verdict and handle/delegate lifetime           |
| [WindowsRadio.WlanNative.cs](../src/WindowsDeviceControl/WindowsRadio.WlanNative.cs) | No separate public operations                                                                                                                  | WLAN P/Invoke and structs, counted list decoding, SSID bytes and owned WLAN handles                                          |
| [WindowsRadio.Bluetooth.cs](../src/WindowsDeviceControl/WindowsRadio.Bluetooth.cs)   | `ListBluetoothDevices`, `ConnectedBluetoothCount`, `StartBluetoothWatch`, `PairBluetoothAsync`, `RespondToPairing`, `UnpairBluetooth`          | Classic/LE discovery, container normalization, endpoint watch state, attempt-scoped pairing deferrals/deadline and unpairing |
| [WifiProfile.cs](../src/WindowsDeviceControl/WifiProfile.cs)                         | `WifiProfile.CreateOpen`, `CreatePsk`, `TryReadSsid`, `PassphraseIsValid`; `PskFlavor`                                                         | Pure XML authoring/parsing, exact SSID hex, escaping, PSK validation and WPA/OWE shapes                                      |

`WindowsRadio` contains these public data types:

- Access and radio state: `Access`, `Consent`, `Power`, `RadioKind`, `RadioPowerResult` and
  `RadioAdapterResult`.
- Wi-Fi: `WifiConnectionState`, `WifiConnectOutcome`, `WifiConnectRefusal`, `WifiFailureKind`,
  `WifiSecurity`, `WifiWatchEvent`, `WifiNetwork`, `WifiStatus`, `WifiConnectResult`,
  `WifiForgetResult` and `WifiNetworkKey`.
- Bluetooth: `BluetoothChangeKind`, `PairingKind`, `PairingOutcome`, `BluetoothDevice`,
  `BluetoothChange`, `PairingRequest`, `PairingResult` and `BluetoothUnpairResult`.

Important distinctions: `WifiConnectResult.ReasonCode` is a WLAN connection reason;
`WifiForgetResult.Status` is the deletion's native status. `RadioAdapterResult.Access` can be null
when an adapter threw, in which case its HRESULT records the failure. A Bluetooth watch's Removed
record has only meaningful identity, and EnumerationCompleted/Stopped carry no device row.
`WifiProfile.CreatePsk` authors XML; callers using it directly validate the passphrase explicitly.
The XML includes key material, so treat generated protected-network profiles as credentials.

## Audio, preview and brightness

Members in `CoreAudio` partials belong to `CoreAudio`, including their nested records and enums.

| Source                                                                       | Public operations and contracts                                                                                                                                                                                                       | Implementation responsibility                                                                                  |
| ---------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| [CoreAudio.cs](../src/WindowsDeviceControl/CoreAudio.cs)                     | `ApplyCommand`; `GetVolume` for default render, direction or endpoint ID; `SetVolume` for default render or direction; `SetMuted` for default render or endpoint ID; `ListEndpoints`; `SetDefaultEndpoint` with optional role results | Endpoint lookup, volume/mute, sorting and three-role default transaction with reverse rollback                 |
| [CoreAudio.Native.cs](../src/WindowsDeviceControl/CoreAudio.Native.cs)       | No separate public operations                                                                                                                                                                                                         | COM contracts, `IPolicyConfig` vtable, native audio layouts, property cleanup and shared MTA enumerator        |
| [CoreAudio.Watch.cs](../src/WindowsDeviceControl/CoreAudio.Watch.cs)         | `StartVolumeWatch`, `StartEndpointWatch`; `AudioEndpointChange`, `AudioEndpointWatchEvent`                                                                                                                                            | Native volume/endpoint callback interfaces, callback detachment, endpoint ownership and console-role filtering |
| [CoreAudio.Bluetooth.cs](../src/WindowsDeviceControl/CoreAudio.Bluetooth.cs) | `ListBluetoothAudioContainers`, `SetBluetoothAudioConnection`; `BluetoothAudioContainer`                                                                                                                                              | Container joins across both flows/all states, topology/KS reconnect and disconnect requests                    |
| [CoreAudio.Spatial.cs](../src/WindowsDeviceControl/CoreAudio.Spatial.cs)     | `GetSpatialAudio`, `SetSpatialAudio`; `SpatialAudioFormats`, `SpatialAudioSetStatus`, `SpatialAudioState`                                                                                                                             | Endpoint existence, WinRT render-ID conversion, known-format probes and semantic licence/access results        |
| [CoreAudio.Formats.cs](../src/WindowsDeviceControl/CoreAudio.Formats.cs)     | `GetDeviceFormat`, `SetDeviceFormat`, `ListSupportedDeviceFormats`, `UnsupportedFormat`; `AudioDeviceFormat` and its `Pcm` factory                                                                                                    | PCM/float format construction, channel masks, unmanaged allocations and finite driver probing                  |
| [AudioFilePreview.cs](../src/WindowsDeviceControl/AudioFilePreview.cs)       | `AudioFilePreview.Play`, `Stop`, `Dispose`, `Failed`; `AudioPreviewFailure`                                                                                                                                                           | One MediaPlayer, local-file validation, stale-failure suppression and default-route playback                   |
| [WaveOutFeedback.cs](../src/WindowsDeviceControl/WaveOutFeedback.cs)         | `WaveOutFeedback.Open`, `Play`, `Dispose`                                                                                                                                                                                             | Prepared 80 ms cue, retained waveOut endpoint/buffers, queued-cue dropping and safe teardown                   |
| [Backlight.cs](../src/WindowsDeviceControl/Backlight.cs)                     | `Backlight.TryReadBrightness`, `TrySetBrightness`                                                                                                                                                                                     | ACPI LCD IOCTLs, bounded three-byte brightness packet and simultaneous AC/DC write                             |

`CoreAudio.cs` also declares `AudioDirection`, `AudioRole`, `VolumeCommand`, `AudioEndpoint` and
`DefaultEndpointRoleResult`. `SpatialAudioFormats` names Off, WindowsSonic, DolbyAtmosForHeadphones,
DolbyAtmosForSpeakers, DolbyAtmosForHomeTheater, DtsHeadphoneX and DtsXUltra. Unknown default/active
spatial GUIDs are retained rather than relabeled.

Most `CoreAudio` methods return HRESULTs instead of throwing COM failures. Bluetooth audio methods
have exception contracts. `SetSpatialAudio` has two result levels: HRESULT and semantic status.
`AudioPreviewFailure` carries `MediaPlayerError`, HRESULT and message. `WaveOutFeedback` HRESULTs
carry `MMRESULT`, not ordinary Win32 error codes. `Backlight` uses bool and exposes no native
failure code; false is unavailable/failed, not evidence that no physical panel exists.

## Displays

| Source                                                                                                                                                     | Public operations and contracts                                                                                                                      | Implementation responsibility                                                                                                                   |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| [DisplayTopology.cs](../src/WindowsDeviceControl/DisplayTopology.cs)                                                                                       | `DisplayTopology.CaptureActive`                                                                                                                      | Active enumeration, per-target fault isolation, rematching, bounded sizing retries and shared display-write gate                                |
| [DisplayTopology.Types.cs](../src/WindowsDeviceControl/DisplayTopology.Types.cs)                                                                           | `DisplayTargetIdentity.Matches`; topology, mode and setting result records/enums                                                                     | Stable identity versus current route; named outcomes with native statuses; `Applied`/`Succeeded` projections                                    |
| [DisplayTopology.Native.cs](../src/WindowsDeviceControl/DisplayTopology.Native.cs)                                                                         | No separate public operations                                                                                                                        | Shared internal CCD native layouts, unions, constants and calls                                                                                 |
| [DisplayLayouts.cs](../src/WindowsDeviceControl/DisplayLayouts.cs), [DisplayLayouts.Inventory.cs](../src/WindowsDeviceControl/DisplayLayouts.Inventory.cs) | `DisplayLayouts.Observe`, `ObserveConnected`, `Capture`, `Describe`, `Validate`, `Apply`; `DisplayRefresh.Default`, `Hertz`, `FromHertz`, `ToString` | Editable value contracts, CCD observation and connected-interface inventory, native validation, saved apply and one rollback, per-output extras |
| [DisplayLayoutPlanner.cs](../src/WindowsDeviceControl/DisplayLayoutPlanner.cs)                                                                             | No separate public operations; rules surfaced through `DisplayLayouts.Describe`                                                                      | Pure arrangement constraints and source/path planning over supplied native arrays                                                               |
| [DisplayModes.cs](../src/WindowsDeviceControl/DisplayModes.cs)                                                                                             | `DisplayModes.Read`, `Apply`, `ReadPrimaryMode`, `EnumeratePrimaryModes`, `TestPrimaryMode`, `ApplyPrimaryModeTransient`                             | Driver mode enumeration/tests, route checks, transient writes and route-scoped rollback for snapshot apply                                      |
| [DisplayEdid.cs](../src/WindowsDeviceControl/DisplayEdid.cs)                                                                                               | `DisplayEdid.ReadModes`, `ReadIdentity`, `ParseIdentity`; `DisplayEdidStatus`, `DisplayEdidModes`                                                    | Exact-interface EDID timings and checksum-validated raw or cached serial identity in `DisplayEdid.Identity.cs`                                  |
| [DisplayScaling.cs](../src/WindowsDeviceControl/DisplayScaling.cs)                                                                                         | `DisplayScaling.TryRead`, `TryReadRange`, `Set`, `Snap`                                                                                              | Active source resolution and relative DPI step conversion, clamping and one write                                                               |
| [DisplayColor.cs](../src/WindowsDeviceControl/DisplayColor.cs)                                                                                             | `DisplayColor.TryReadHdr`, `SetHdr`                                                                                                                  | Advanced-colour support/state reads and one target write                                                                                        |

Display contract groups:

- Identity and active observations: `DisplayTargetIdentity`, `ActiveDisplayPath`,
  `DisplayTopologySnapshot`.
- Editable arrangements: `DisplayRefresh`, `DisplayLayoutOutput` (`IsPrimary`), `DisplayLayout`,
  `DisplayTargetObservation`, `DisplayArrangement`.
- Layout results: `DisplayLayoutOutcome`, `DisplayLayoutProblem`, `DisplayOutputWarningKind`,
  `DisplayOutputWarning`, `DisplayLayoutResult` (`Applied`, `RollbackSucceeded`).
- Driver modes: `DisplayMode`, `DisplayModeSnapshot`, `PrimaryDisplayMode`, `DisplayModeOutcome`,
  `DisplayModeResult` (`Applied`).
- Per-display settings: `DisplaySetOutcome`, `DisplaySetResult`, `DisplayScaleResult` (`Succeeded`).
- EDID candidates: `DisplayEdidStatus`, `DisplayEdidModes`.

`Validate` returning Applied means validation passed without writing. `Apply` returning
AlreadyActive means topology was unchanged, but requested extras were still applied.
`RollbackSucceeded` means the topology rollback was accepted, not that every extra or visible pixel
was confirmed. Nullable scaling/HDR in a captured layout means leave that extra alone on apply.
Primary-display helpers lack the snapshot-based protections of `Read`/`Apply`; callers choose
restoration explicitly.

## Power, wake security and storage

| Source                                                                                     | Public operations and contracts                                                                                                                                                                          | Implementation responsibility                                                                                   |
| ------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------- |
| [WindowsPower.cs](../src/WindowsDeviceControl/WindowsPower.cs)                             | `WindowsPower.EnumerateScheme`, `EnumerateSchemes`, `ReadSchemeName`, `GetActiveScheme`, `SetActiveScheme`                                                                                               | Scheme enumeration, localized names, active scheme allocation/cleanup and native error propagation              |
| [WindowsPower.Policy.cs](../src/WindowsDeviceControl/WindowsPower.Policy.cs)               | `WindowsPower.ReadSetting`, `WriteSetting`, `GetEffectiveMode`, `SetActiveMode`                                                                                                                          | Separate AC/DC stored values and effective overlay requests                                                     |
| [WindowsPower.HybridCores.cs](../src/WindowsDeviceControl/WindowsPower.HybridCores.cs)     | `WindowsPower.QueryHybridCores`, `ReadHybridCores`, `WriteHybridCores`, `RefreshActiveScheme`, `ReadPossibleValue`; processor subgroup/setting GUIDs                                                     | CPU set parsing, per-scheme configurability, Windows-published values and ordered partial writes                |
| [WindowsPower.Status.cs](../src/WindowsDeviceControl/WindowsPower.Status.cs)               | `WindowsPower.TryGetStatus`; `WindowsPowerStatus`                                                                                                                                                        | Native battery/AC snapshot with unchanged unknown sentinels                                                     |
| [WindowsPower.Actions.cs](../src/WindowsDeviceControl/WindowsPower.Actions.cs)             | `WindowsPower.SuspendAsync`, `RequestActionAsync`; `WindowsPowerAction`                                                                                                                                  | Off-thread suspend and system-tool session actions, with explicit cancellation boundaries                       |
| [WindowsPower.Notifications.cs](../src/WindowsDeviceControl/WindowsPower.Notifications.cs) | `WindowsPower.RegisterSettingNotification`, `RegisterSuspendResumeNotification`; `PowerNotificationRegistration`                                                                                         | Caller-window registrations and matching SafeHandle unregistration                                              |
| [WindowsPowerRequest.cs](../src/WindowsDeviceControl/WindowsPowerRequest.cs)               | `WindowsPowerRequest` constructor, `Acquire`, `Release`, `IsHeld`, `Dispose`; `WindowsPowerRequestKind`                                                                                                  | Thread-safe lazy native request and reason-buffer ownership                                                     |
| [PowerRequestList.cs](../src/WindowsDeviceControl/PowerRequestList.cs)                     | `PowerRequestList.Query`; `PowerRequestEntry`, `PowerRequestListStatus`                                                                                                                                  | Growing query buffer and guarded, build-specific 64-bit system request decoding                                 |
| [WindowsWakeSecurity.cs](../src/WindowsDeviceControl/WindowsWakeSecurity.cs)               | `WindowsWakeSecurity.Capture`, `IsSignInDisabled`, `SetConsoleLockPolicy`, `SetSchemeConsoleLock`, `SetNoLockScreen`, `Restore`                                                                          | Exact registry/policy capture, pure precedence, explicit writes and complete best-effort restore plan           |
| [ModernStandby.cs](../src/WindowsDeviceControl/ModernStandby.cs)                           | `ModernStandby.Query`, `WasLastResumeUnattended`, `ReadStandbyTiming`, `EnumerateWakeDevices`, `TrySetWakeArmed`, `CaptureWakeDevices`, `RestoreWakeDevices`; sleep/none subgroup and wake-setting GUIDs | S0 capabilities, native list ownership, named programmable wake writes, recovery plans and interrupt-time reads |
| [WindowsStorage.cs](../src/WindowsDeviceControl/WindowsStorage.cs)                         | `WindowsStorage.DescribeVolumes`, `DiskNumberFor(string)`, `DiskNumberFor(char)`; `StorageVolume`                                                                                                        | Read-only local drive-letter metadata and volume-to-disk-number IOCTL                                           |
| [Interop.cs](../src/WindowsDeviceControl/Interop.cs)                                       | No public surface                                                                                                                                                                                        | Shared native error helpers, kernel device calls, bounded fixed-string decoding and blocking WinRT waits        |

Power and wake data contracts:

- `HybridSchedulingPolicy`, `HybridCoreClass`, `HybridCoreSupport` (`Hybrid`) and `HybridCoreState`.
  GUIDs name `SubgroupProcessor`, `SettingHeterogeneousPolicy`, `SettingThreadSchedulingPolicy` and
  `SettingShortThreadSchedulingPolicy`.
- `WakeSecurityScheme`, `WakeSecuritySnapshot`, `WakeSecuritySetting`, `WakeSecurityRestoreFailure`
  and `WakeSecurityRestoreResult` (`Succeeded`).
- `WakeDeviceControl`, `WakeDevice`, `WakeDeviceSnapshot`, `WakeDeviceRestoreFailure`,
  `ModernStandbySupport` and `StandbyTiming` (`Slept`, `SinceWake`).
- Modern Standby GUIDs name `SubgroupSleep`, `SubgroupNone`, `SettingAllowWakeTimers`,
  `SettingAllowAwayMode`, `SettingUnattendedSleepTimeout`, `SettingConnectivityInStandby` and
  `SettingDisconnectedStandby`.

Keep failure meaning precise: `PowerRequestList` returns null entries for any non-Read status;
AccessDenied and QueryFailed preserve NTSTATUS. `WindowsPowerStatus` has 255/`uint.MaxValue`
sentinels. Wake restore results describe applicable steps, skipping disappeared schemes/devices;
they do not prove a later policy writer will leave restored state alone. Storage `DiskNumber = -1`
is unknown and must never be used to group volumes as one disk.

## Build and package files

| File                                                                                   | Responsibility                                                                                           |
| -------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------- |
| [WindowsDeviceControl.slnx](../WindowsDeviceControl.slnx)                              | Library and deterministic test project topology                                                          |
| [WindowsDeviceControl.csproj](../src/WindowsDeviceControl/WindowsDeviceControl.csproj) | Both Windows TFMs, platform floor, package metadata/version, XML docs and warnings as errors             |
| [Directory.Build.props](../Directory.Build.props)                                      | Standalone build root, nullable/style settings, cross-platform Windows targeting and NuGet audit setting |
| [.editorconfig](../.editorconfig)                                                      | Standalone code style root                                                                               |
| [.gitignore](../.gitignore)                                                            | Generated build/package output exclusions                                                                |
| [LICENSE](../LICENSE)                                                                  | MIT licence                                                                                              |

The package includes the README and compiler-generated XML reference. There is no public API
generator or website build to run separately. Keep the source XML, this index and the behavioral
guide aligned whenever the API surface changes.

## Tests

The [test project](../tests/WindowsDeviceControl.Tests/WindowsDeviceControl.Tests.csproj) targets
both shipped frameworks. Tests use pure/internal seams and synthetic buffers; they do not establish
hardware compatibility. Follow the repository's manual-first validation requirements before running
test-bearing gates.

| Source                                                                                           | Coverage area                                                                                                                 |
| ------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------- |
| [WindowsRadioTests.cs](../tests/WindowsDeviceControl.Tests/WindowsRadioTests.cs)                 | Radio aggregation, identity/security/profile decisions, reason classification, native WLAN layouts and unknown pairing tokens |
| [CoreAudioTests.cs](../tests/WindowsDeviceControl.Tests/CoreAudioTests.cs)                       | Endpoint ordering, role rollback, WinRT endpoint-ID conversion, format construction/decoding and audio native layouts         |
| [DisplayTopologyTests.cs](../tests/WindowsDeviceControl.Tests/DisplayTopologyTests.cs)           | Monitor matching, EDID validity flags and native topology contracts                                                           |
| [DisplayLayoutTests.cs](../tests/WindowsDeviceControl.Tests/DisplayLayoutTests.cs)               | Pure arrangement constraints, path planning, matching and observation fingerprints                                            |
| [DisplayEdidTests.cs](../tests/WindowsDeviceControl.Tests/DisplayEdidTests.cs)                   | EDID parser boundaries, checksums and recognized timing formats                                                               |
| [ClawAdvertisedTimingTests.cs](../tests/WindowsDeviceControl.Tests/ClawAdvertisedTimingTests.cs) | Captured monitor descriptor fixture and advertised timing expectations                                                        |
| [WindowsPowerTests.cs](../tests/WindowsDeviceControl.Tests/WindowsPowerTests.cs)                 | Localized scheme-name decoding and power-action dispatch/cancellation seams                                                   |
| [HybridCoreTests.cs](../tests/WindowsDeviceControl.Tests/HybridCoreTests.cs)                     | Synthetic CPU set records, efficiency grouping, processor groups and malformed entry bounds                                   |
| [PowerRequestTests.cs](../tests/WindowsDeviceControl.Tests/PowerRequestTests.cs)                 | Native request/reason lifetime, idempotence and failure ownership                                                             |
| [PowerRequestListTests.cs](../tests/WindowsDeviceControl.Tests/PowerRequestListTests.cs)         | Synthetic request layouts, offsets, malformed buffers and unknown-result behavior                                             |
| [WakeSecurityTests.cs](../tests/WindowsDeviceControl.Tests/WakeSecurityTests.cs)                 | Stored-value interpretation, restoration planning and failure continuation                                                    |
| [ModernStandbyTests.cs](../tests/WindowsDeviceControl.Tests/ModernStandbyTests.cs)               | Capability layouts, wake-name decoding, per-device restore and timing values                                                  |
| [TestFixtures.cs](../tests/WindowsDeviceControl.Tests/TestFixtures.cs)                           | Shared deterministic builders/assertions; no native hardware fixture                                                          |

No source edits in this documentation refresh change runtime behavior. Hardware observations remain
the dated evidence in the README and platform findings, not claims of a fresh live pass.
