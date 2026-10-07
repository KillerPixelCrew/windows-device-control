# WindowsDeviceControl documentation

Start with the [README](../README.md) for supported platforms and usage. All public types live in
the `WindowsDeviceControl` namespace; radio and audio result types are generally nested in their
owning static class. The package targets Windows 10 build 19041 or later on .NET 8 and .NET 10.

| Document                                     | Use it for                                                                          |
| -------------------------------------------- | ----------------------------------------------------------------------------------- |
| [How it works](how-it-works.md)              | Follow a request from caller to Windows, through ownership, completion and recovery |
| [API and source reference](api-reference.md) | Find every source file, public operation, data contract and test area               |
| [Windows platform findings](radios.md)       | Understand API selection, consent, driver behavior and rejected alternatives        |
| [Contributor guide](../AGENTS.md)            | Change public contracts, preserve native lifetimes and choose validation            |

Source XML is the member-level reference and ships as `WindowsDeviceControl.xml` beside the
assembly. The build rejects undocumented public members and partially documented parameter lists.
The guides explain relationships across members; they do not replace the return values, exceptions
and lifetime contracts beside each declaration.

The source audit for this documentation was made on 2026-10-07. It is not a new hardware test. Dated
Windows observations in the README and platform findings retain their original dates and scope.
Compilation and deterministic tests cannot establish that a particular driver, pairing ceremony,
display mode or Windows policy works on a user's machine.

This library owns Windows primitives. Its caller owns UI dispatch, confirmation, persistent recovery
snapshots, policy ordering and when to refresh observations. Loading the assembly starts no polling,
hardware lifecycle or background policy service. Explicit calls perform the work; watches and owned
requests stay alive until disposed.
