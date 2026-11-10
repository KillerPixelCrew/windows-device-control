---
name: windows-device-control
description:
  Implement or review WindowsDeviceControl Windows primitives, native APIs, state watches and
  restore contracts. Use for reusable Windows mechanisms; consumer policy and UI stay outside.
---

# WindowsDeviceControl contributions

Resolve aliases to this owning repository. Read AGENTS.md, README.md, docs/how-it-works.md and the
relevant part of docs/api-reference.md. Read docs/radios.md for radio/audio route decisions before
substituting an API. Follow the existing primitive and its native/session lifetime.

Keep app policy outside Windows primitives. Public XML docs include threading, completion, consent,
ownership and error meaning. Follow repository settings; C# uses expanded braces, var locals,
explicit construction types where needed, named literal arguments and no multiline trailing commas.

Separate API acknowledgement from independent readback; unavailable readback cannot gate a write.
Track dispatch before every native effect in multi-step operations. Preserve partial results and
uncertainty on timeout/cancellation; do not return overall success after swallowing an effect or
restore failure. Snapshot the state owned by this operation, preserve external changes and release
only owned handles/registrations. Watch callbacks publish observations, not new persisted intent.
Trace repeated calls, external changes, subscription disposal, partial startup and cleanup against
current tests. Compilation/fakes do not prove actual Windows state or physical hardware behavior.
