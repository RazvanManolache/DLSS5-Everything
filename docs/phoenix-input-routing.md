# Phoenix input routing

Input selection and execution selection are separate choices:

- **Backbuffer input** captures the image the game presents. A modern addon can process it on the same D3D12 device with Phoenix's optimized RTX network. Backbuffer input does not require an external helper.
- **Native NGX input** uses buffers supplied by the game's DLSS integration. A DLL or import alone cannot establish that the engine's guides and dimensions are compatible. Some older addons require DLAA/native-resolution guides and reject scaled DLSS input.
- **Execution mode** chooses where processing runs. The addon's Automatic mode can select an eligible in-process implementation, with an external helper as fallback.

For modern standalone-capable addons, Everything's **Automatic route** uses backbuffer input even when a game contains a DLSS DLL. An explicit **Native NGX input** selection remains authoritative. Native-only addons retain the older input route; preflight warns when the installed component contains the older native-size restriction.

The installer writes one consistent contract to `Phoenix.json`, the ReShade compatibility settings and the installation journal. Verification restores that recorded choice instead of turning an automatic direct-addon installation into forced game-DLSS input. Existing journals without an input-provider field retain their previous interpretation until reinstalled or repaired.

Routing/configuration tests cover automatic and explicit input, older addons, reinstall, verification and an in-place journaled repair that preserves the model, addon, execution preference and strength controls. A synthetic output check is evidence for its tested dimensions and format; it does not certify every game's native buffers or HDR presentation.
