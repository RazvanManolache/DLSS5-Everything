# Swapper Feature Audit

Reference: read-only [DLSS5-Swapper](https://github.com/rakanki911/DLSS5-Swapper), `origin/main`
at `9fb0b7c3563d350aa46eaa01d3e5ca4cbab4a7cb` (Swapper 2.2.9), inspected
2026-10-11. This is a bounded .NET/Phoenix DLSS5 implementation, not a claim of full
Swapper parity or equivalent runtime compatibility. The reference checkout
was inspected without modifying its source.

## Implemented In This Change

| Capability | Implementation | Boundary |
| --- | --- | --- |
| Running game/helper check | `InstallPreflight.Check` inspects process executable paths within the root, skips its own PID, and conservatively matches game/helper names when paths are unavailable | Process enumeration failures and limits are visible; a matching name without a path may be a false positive |
| Locked or inaccessible files | Existing executable and planned destinations are opened without writing; locked, read-only destinations and directory collisions block installation | Supply the exact write/delete plan and rerun immediately before mutation; no process is killed |
| Anti-cheat warnings | Bounded inspection of actual Easy Anti-Cheat, BattlEye, EA AntiCheat, launcher and Vanguard filename/directory indicators | Warning and acknowledgement flag only; UI/CLI must enforce acknowledgement. No title-based guess, bypass, or guarantee of safe injection |
| Existing mod conflicts | Unowned planned proxies block; other known proxy/ASI/add-on markers warn; detected unowned RenoDX/OptiScaler markers block the Phoenix DLSS5-only combination | Filename evidence is conservative. The check never removes mods, and does not certify loader compatibility |
| Managed modpacks | Mod Organizer plus `Stock Game` in up to five executable ancestors blocks direct deployment | Specific bounded layout detection, not universal mod-manager recognition |
| Visible preflight output | Structured findings plus `DisplayText`, `CanInstall`, `HasWarnings`, `RequiresAntiCheatAcknowledgement`, `ThrowIfBlocked`; the UI calls the installer preflight and presents warnings before continuing | UI integration is separately owned. Service tests do not certify every rendered state or consent path |
| Backend settings profiles | `BackendProfileStore` snapshots exact bytes for five configuration names in three fixed folders, with atomic profile replacement and bounded storage; installer calls load/snapshot before route restoration and applies owned saved settings through tracked writes | Cross-executable switches must use the outgoing manifest's executable for snapshots. Installer recovery and switch integration need their own tests |
| Restore validation | Entire profile schema, game/executable/route identity, entries, sizes, hashes, safe paths and ownership checked before destination writes | A checksum detects corruption, not malicious re-authoring. The caller's managed-file list must come from a validated manifest |
| Emulator metadata and scanner hints | 19 profiles and all aliases, renderer choices/labels and setup hints ported from the pinned MIT table; scanner uses known names even without imported backends | Observed evidence remains primary and first in the choices; metadata choices append without duplicates. Detection text marks hints and runtime compatibility as unverified; existing executable exclusions still apply |
| Explicit Native / Feeder selection | Per-game Advanced route choice, saved with the profile and validated before installation | Native requires actual x64 D3D12 NGX calls; selecting it cannot create those calls |
| ReShade proxy choice | Automatic, `dxgi.dll`, or DX11 `d3d11.dll`, saved per executable; tracked proxy switching and restoration | Invalid combinations block before writes; unowned mods are not silently replaced |
| Component version inspection | Installed and selected file versions/checksums, current/different/missing states, session-based file filters | Inspection is bounded and read-only; unchecked games appear under All components. File equality is not runtime activation |
| Vulkan layer setup | Explicit, confirmed per-user x64 layer setup and reference removal; reuses the supported official layout | Separate recovery state from game-folder backups; no automatic layer-disable bypass or x86 Vulkan support |
| Live Phoenix DLSS5 controls | Typed settings, staged single Apply, comparison preset, model validation, screenshots/capture and runtime/timing snapshots | Session acknowledgements are not completed rendering evidence; offline settings require restart |

## Integration Contracts

```csharp
var result = InstallPreflight.Check(game.Root, game.ExePath,
    filesToWrite: plannedRootRelativePaths,
    managedFiles: validatedOwnedRootRelativePaths,
    cancellationToken: cancellationToken);
// Present result.Findings or result.DisplayText. Require explicit acknowledgement
// when result.RequiresAntiCheatAcknowledgement is true, then enforce CanInstall.
result.ThrowIfBlocked();

var store = new BackendProfileStore(game.Root, game.ExePath);
store.Snapshot(oldRoute);                   // Before restoring the old install.
var settings = store.Load(newRoute);        // Fully validated relative path -> byte[].
// Prefer writing settings through the installation's durable tracked-write API.
// Standalone alternative, only after the new install owns every destination:
store.Restore(newRoute, validatedOwnedRootRelativePaths);

var emulator = EmulatorProfiles.ProfileFor(game.ExePath);
var choices = EmulatorProfiles.ApiChoices(emulator); // dxgi / vulkan / opengl.
```

All route identifiers must be 1-64 ASCII letters/digits/underscore/hyphen and
start with a letter; `InstallRoute.ToString()` is suitable. Profiles are
case-insensitive by route and executable-relative identity. Each profile also
binds to the absolute game root; copying it to another root fails validation.
Installer/UI/model files are owned separately. `GameScanner.cs` is changed only
for the explicitly assigned emulator metadata integration.

`managedFiles` is a list of **root-relative files**, not folders and not absolute
paths. Pass both added and replaced files from a validated active manifest.
Preflight's default write plan includes common proxies; omitting ownership on an
existing installation can therefore block the app's own proxy. Do not suppress
conflict checks globally to work around that integration error.

Keep `_DLSS5_Compat_Backup/backend-profiles` when restoring/uninstalling an active
route. Profiles are separate from original-file backups and must never replace
or become entries in those backups. Snapshot the old manifest's executable and
route, which may differ from the newly selected executable or route.

## Profile Safety And Limits

- Allowlisted names: `ReShade.ini`, `ReShadeVR.ini`, `ReShadePreset.ini`,
  `Phoenix.json`, `dlss5-feed.cfg`.
- Allowlisted folders: beside the selected executable, its `host64` folder,
  and its `_DLSS5_OpenXR/host64` folder. Nested executables retain paths relative
  to the game root. No recursive arbitrary configuration collection occurs.
- Per configuration: 256 KiB. Per serialized profile: 4 MiB. Per game store:
  at most 32 entries and 32 MiB. All store entries count toward limits;
  unexpected subdirectories fail. Capacity exhaustion refuses the new snapshot
  without evicting or deleting old profiles.
- Snapshots use base64 bytes with SHA-256 checksums, preserving encoding, BOM,
  newlines and binary bytes. A temporary file is flushed before atomic rename;
  a named mutex serializes profile operations for the same store.
- Absolute/network/device paths, traversal segments, ADS, reserved Windows
  device names, empty segments, trailing dots/spaces, and reparse points are
  rejected. Duplicate properties and duplicate entry identities are rejected.
  Every restore destination must match the fixed allowlist and current ownership.
- `Restore` validates all entries/ownership and opens all existing destinations
  before staging any contents. Each final replacement is atomic. It is **not a
  multi-file transaction**: the installation journal must cover crash recovery
  and failures between final replacements. Local filesystem permissions, races
  with external writers, and hostile directory replacement remain OS-level concerns.
- Missing profiles return an empty dictionary; malformed, oversized, future-schema,
  mismatched or corrupt profiles fail explicitly. Oversized/locked source files
  cannot silently replace a prior complete snapshot.
- Absent files are not deleted on restore. Custom preset paths referenced from
  INI files are not followed. Model binaries, runtime bundles, injector DLLs,
  arbitrary mod files and original game files are never profile payloads.

Preflight examines at most 2,000 directory entries by default to depth two,
including separate starting points for deeply nested executable folders,
fixed host folders and planned destination parents. It
skips backup, `.git`, `node_modules` and linked directories. The default process
inspection budget is 4,096 records. Limits are adjustable within hard caps;
cancellation propagates. A warning about incomplete inspection is not a clean
compatibility certificate. These APIs run locally and do not upload anything.

## Wider Parity And Deliberate Differences

| Swapper surface | .NET/Phoenix DLSS5 state and limitations |
| --- | --- |
| Library, store discovery, search, renderer override, favourites/hidden games, artwork and themes | Local services and library/convenience UI provide these workflows. Not evidence of every Swapper filter/counter/context action or identical discovery behavior |
| Backup, history, partial-install recovery | Installer uses durable pending journals and validates restore manifests separately. Profile tests do not certify installer crash recovery or migration of older backups |
| Diagnostics, activity logs and release checks | Local runtime diagnostics, history and update services are present. Diagnostic export applies redaction and opens a review/save dialog; redaction is best-effort and the user reviews remaining private information. No automatic support-report upload |
| Renderer/backend switching | Installer calls the profile APIs and journals tracked writes. Original backups remain separate. Explicit per-user Vulkan setup has its own recovery state |
| Emulator support | Exact detection metadata and scanner hints integrated; no promise that all 19 emulators, every listed renderer, or every ROM/game works |
| Native DLSS / feeder / multipass routes | Phoenix DLSS5 is the consumer in this application. Configuration and installation do not demonstrate neural inference, image quality, timing, driver or GPU parity |
| RenoDX consumers and pinned Swapper runtime bundles | Swapper ships independently licensed consumers. RenoDX installation conflicts with this task's no-RenoDX requirement; it is not reproduced or relabeled Phoenix DLSS5 |
| OptiScaler and pre-SR multipass fork | Separate GPL-licensed consumers (Swapper's notices identify GNU GPL v3), with their own redistribution/source obligations, downloads, configuration and validation. Phoenix DLSS5 backends are alternatives under the no-RenoDX constraint; they do not implement identical SR interception, pre-SR ordering, multipass or output semantics. Porting UI metadata cannot supply consumer parity |
| F8 desktop overlay, themes and resized per-game overlay | Swapper's desktop overlay is replaced by Phoenix DLSS5 native controls, subject to the installed Phoenix DLSS5 build. Overlay transport, bindings and themes are not parity claims |
| Hosted community compatibility reports, comments, votes and profiles | Not implemented. Requires a separate hosted service, persistence, moderation, authentication/identity and user-approved reporting flows; a resource link is not a community implementation |
| Live chat, screenshots, mentions, reactions and notifications | Not implemented. Requires the separate hosted chat/community stack and network lifecycle |
| 38 languages and RTL | Convenience UI includes a 38-language common-action catalog, language selection and RTL metadata. Non-English coverage is explicitly partial and missing/native-only strings fall back to English; this is not full Swapper localization parity |
| Linux/Proton and packaging/updater behavior | Windows .NET application. No Linux/Proton execution, Electron installer parity, self-update installation or new packaging guarantees added here |

## Attribution

The metadata table is adapted from [Swapper's pinned emulators.js](https://github.com/rakanki911/DLSS5-Swapper/blob/9fb0b7c3563d350aa46eaa01d3e5ca4cbab4a7cb/src/core/emulators.js).
Swapper credits [DLSS5-Autopilot](https://github.com/Kizzuwatnaa/DLSS5-Autopilot)
for its MIT detection table. `EmulatorProfiles.cs` retains the MIT permission
notice and copyrights for Rakan Alkhaldi (2026) and DLSS 5 Autopilot contributors
(2026), as recorded by the pinned repository's LICENSE and THIRD_PARTY_NOTICES.md.
Renderer strings and aliases preserve the reference table; no upstream binaries
or hosted service code are incorporated by this change.

## Verification Scope

Tests use temporary synthetic executables/configurations and Windows directory
junctions. They check process matching via injected process records, real file
sharing/permission failures, bounded anti-cheat/mod scanning, traversal and link
rejection, exact profile bytes, backend/executable separation, full validation
before writes, corrupt schemas/checksums, storage bounds, and emulator metadata.
No games or emulators are launched. No GPU/inference, anti-cheat product,
network upload, live hosted service or visual/runtime parity is tested.
The initial scoped run passed **85/85 tests, zero failures and zero skips** on
2026-10-11 using the normal shared test project, without excluding any source
files. This includes the emulator scanner integration and exclusion regressions.
The existing `PeAndIniTests.cs` analyzer warning is outside this change. Results:
`tests/Dlss5CompatApp.Tests/TestResults/swapper-parity.trx`. This is a focused
verification result, not a claim that the full shared suite or UI/runtime
acceptance has passed. The final full application run passed 644/644 tests;
see the [migration validation checkpoint](phoenix-validation.md) for the
separate UI, review and isolated-runtime evidence and remaining limits.
