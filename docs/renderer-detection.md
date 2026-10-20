# Renderer Detection

Detection chooses an installation route; it does not by itself prove that
ReShade loaded, inference completed, or processed output reached the screen.

## DirectX 10 and 10.1

The scanner distinguishes DirectX 10 and DirectX 10.1 imports and dynamic
creation entry points. Both retain the DXGI installation hook, with x86 and
x64 routes selected by executable architecture. An API label does not imply
that a different proxy DLL is required.

Verified-renderer profiles distinguish Direct3D 10, 10.1, and 11. Older profiles
that incorrectly classified concrete DirectX 10 evidence as DirectX 11 no
longer override that evidence.

## ANGLE

Recognized ANGLE installations use a runtime-selected backend. Static detection
reports an ANGLE / DXGI presentation route instead of assuming DirectX 11.
Detected capabilities remain selectable, but are not proof of the active
backend. Verified runtime output can refine the route to the observed API.

## Translation Wrappers

Candidates retain original API evidence separately from recognized translators
and their presentation API. An existing wrapper is hooked at its output. If
that wrapper is removed, a verified profile retains the original legacy input
route so the next installation requests the necessary translator again.
Current concrete wrapper evidence takes precedence over a stale cached output
API. Verified profiles remain bound to executable content and architecture.

Reinstallation must retain the translator requirement even when the detected
presentation API is DXGI. Restoring an outgoing installation can remove its
installer-owned wrapper DLLs; the replacement plan derives the required
dgVoodoo translator from the preserved original API evidence and reinstalls it.

## Software Rendering

Explicit WARP/reference capability indicators produce a warning. A matching
message in the local ReShade log is labeled previous-run evidence, not proof
of the current configuration. A title containing only the word WARP does not
trigger this warning.

Warnings appear in candidate details and installation preflight. They do not
hide missing components or unsupported routes, and do not hard-block an
installation based on static capabilities or old logs. Stock ReShade skips
WARP/reference DirectX 10/11 devices; software DirectX 12 routes still require
runtime verification and a compatible hardware inference route. These detector
changes do not implement a software-renderer compatibility layer.

## Validation Scope

Regression coverage includes exact API selection, both executable architectures,
ANGLE runtime refinement, software-renderer warnings, translator restoration,
and synthetic install/restore operations. Read-only fixture inspection checks
classification without launching games or changing fixture files. Actual
injection, output quality, and performance require separate live validation.
