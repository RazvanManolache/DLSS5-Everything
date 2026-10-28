# Legacy DirectDraw COM compatibility

The normal installer detects legacy DirectX executables that import CoCreateInstance and contain DirectDraw or DirectPlay Lobby class identities. It installs an architecture-matched local DDraw proxy, the original dgVoodoo renderer as PhoenixDgVoodooDDraw.dll, and PhoenixLegacy.ini. These files are managed by the normal installation manifest and backup mechanism.

DirectDraw COM activation otherwise bypasses the local DDraw wrapper and can create the Windows renderer instead. The proxy redirects those class factory requests to the local dgVoodoo implementation. Unrelated COM classes keep the original activation path. Real factory and API failures remain failures.

For legacy DirectPlay application registration, the bridge mirrors missing machine application registrations into the current user registration tree, preserving existing user entries. Only the exact DirectPlay Applications root requests from dplayx.dll are redirected to that user tree. This avoids machine registry write requirements. No installed executable, system DLL, or machine registration is patched. User registration entries persist like normal DirectPlay registration.

The bridge must load before the game registers with DirectPlay. A ReShade addon loads too late, so the shipped implementation is an early DirectDraw proxy. It forwards every renderer export by name and ordinal; class factory and registry operations execute when the game requests them, outside DLL initialization.

Validation: tools/build_legacy_com.py builds x86/x64, runs 20 CPU contract checks per architecture, and checks exact renderer export forwarding. LegacyComPlanTests verifies independent class detection and route gating. Re-Volt normal installer validation confirmed successful real DirectPlay registration, local DirectDraw COM activation, live Phoenix processing, scripted input, and race capture. Gameplay replay acceptance is recorded separately in the coverage ledger.
