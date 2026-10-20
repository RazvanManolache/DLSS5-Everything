# Scripted Phoenix controls

The regression suite uses the same typed control client as the application's
Controls window. Commands require the exact executable and running process ID.
Changing settings additionally requires a session obtained from a fresh status.
There is no offline-edit fallback and no process launch/termination in this CLI.

```powershell
Dlss5CompatApp.exe --phoenix-control C:\Tests\Fixture.exe --pid 1234 `
  --op status --output C:\Tests\status.json

Dlss5CompatApp.exe --phoenix-control C:\Tests\Fixture.exe --pid 1234 `
  --session "<session from status>" --op set `
  --settings '{"Backend":3,"OverrideStrengths":true,"Tone":1.25}' `
  --output C:\Tests\reply.json
```

Operations are `status`, `set`, `reset_history`, `screenshot`, `save` and
`validate_model`. `--timeout` accepts 1..30 seconds. Duplicate/unknown options,
duplicate setting names, wrong JSON types and out-of-range values are rejected
before sending a mutation. Existing mailbox serialization, expiration, process
creation-time checks and session guards still apply.

A successful reply acknowledges a command. It does not prove completed model
execution or presentation. The live suite separately waits for applied settings,
matching selection/presentation revisions and advancing completed outputs.

Export the current typed core-settings contract without connecting to a game:

```powershell
Dlss5CompatApp.exe --phoenix-control --contract C:\Tests\contract.json
```

This export covers the core settings shared with Phoenix. Compatibility transport
preferences such as experimental x86 execution remain separate configuration;
their availability is not inferred from a core-settings export.
