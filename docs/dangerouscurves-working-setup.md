# DangerousCurves Working DX9 Setup

This records the DangerousCurves DX9 setup that was confirmed working on 2026-09-01 after isolating the file combination. Use this as the known-good reference before changing the installer logic again.

Do not treat this as proof that the installer package is fully fixed. The confirmed finding is that DangerousCurves worked when the game folder contained the x86 feeder, the host64 bridge, the newer RenoDX DLSS5 add-on, and the matching NVIDIA 310.0.8 DLSSNR runtime.

## Game folder

Game root placeholder:

```text
<DangerousCurves>
```

Working top-level structure:

```text
<DangerousCurves>
|-- _DLSS5_Compat_Backup\
|-- Audio\
|-- host64\
|-- reshade-shaders\
|-- D3D9.dll
|-- dlss5-feed.addon32
|-- dlss5-feed.cfg
|-- ReShade.ini
|-- ReShadePreset.ini
|-- fmod.dll
|-- Logo2.tga
|-- Sushi.ati
|-- Sushi.ini
|-- SushiDX.exe
`-- Tiny.bmp
```

Runtime logs such as `dlss5-feed.log`, `ReShade.log`, `Error.txt`, and `host64\dlss5-feed-host.log` were present during testing, but they should not be committed or packaged.

## Host64 folder

Working `host64` structure:

```text
<DangerousCurves>\host64
|-- dlss5-feed-host64.exe
|-- dxgi.dll
|-- nvngx_dlss.dll
|-- nvngx_dlssg.dll
|-- nvngx_dlssnr.dll
|-- renodx-dlss5.addon64
|-- ReShade.ini
|-- sl.common.dll
|-- sl.dlss.dll
|-- sl.dlss_g.dll
|-- sl.dlss_nr.dll
|-- sl.interposer.dll
|-- sl.nis.dll
|-- sl.pcl.dll
`-- sl.reflex.dll
```

Runtime logs such as `ReShade.log` and `dlss5-feed-host.log` were present during testing, but they should not be committed or packaged.

## Known-good binaries

These were the important binaries in the working setup:

| File | Size | Version | SHA-256 |
| --- | ---: | --- | --- |
| `D3D9.dll` | 4,398,080 | ReShade 6.8.0.2156, x86 | `DA430E0A9C6EECEFA0D1B27D05E16C426FB5D04E808B194D914EAAC4B31BC0F8` |
| `dlss5-feed.addon32` | 84,480 | local x86 feeder build | `EEC91DD5A821AC92EDEF6918B8F66338F615A8CBCB96905A1F6A435B0715529B` |
| `host64\dlss5-feed-host64.exe` | 98,304 | local host64 build | `C8DFB52D90B74960E673EF0190F373274702BD621A76446E11C8A08B74BB6250` |
| `host64\dxgi.dll` | 5,592,064 | ReShade 6.8.0.2155, x64 | `0CEE63F9C9F13F3AC909C5B4903F4DBB4B719A7AB3B4F13B0DEAF83C814B94F7` |
| `host64\nvngx_dlss.dll` | 58,956,400 | 310.0.8.0 | `C85F971CE023C9F3492FC7455F0B01A24BA18EA39636407A846902C4360B0B7E` |
| `host64\nvngx_dlssnr.dll` | 165,840,496 | 310.0.8.0 | `E16BCF15E16E13F527491CDF7845B2FE6521A738D8F7C9C721866A8496E1FC8E` |
| `host64\renodx-dlss5.addon64` | 1,703,424 | 0.2026.0828.0517 | `245C06137AD13B1CA03AFAAD5100C1E8F0DCE8C11FE50A9272EA562F33CEA601` |

The successful DLSSNR runtime was the `E16BCF15...` 310.0.8 `nvngx_dlssnr.dll`. The older/smaller runtime that hashed as `CEB6432F...` did not produce the same working result in this test.

## Feeder config

Working `dlss5-feed.cfg`:

```ini
enabled=1
mode=2
hdr=1
depth_inverted=1
flags=-1
reset_every=0
log_frames=6
host_window=0
hotkey_toggle=0
hotkey_compare=120
hotkey_screenshot=44
compare_mode=2
iterations=3
render_scale=1.000
mv_scale_x=4.000
mv_scale_y=4.000
```

## Host ReShade config

Working `host64\ReShade.ini`:

```ini
[GENERAL]
EffectSearchPaths=.\reshade-shaders\Shaders\**
TextureSearchPaths=.\reshade-shaders\Textures\**
PresetPath=.\ReShadePreset.ini
TutorialProgress=4

[INPUT]
KeyOverlay=36,0,0,0
KeyScreenshot=0,0,0,0

[RenoDX.DLSS5]
NeuralUplift=1
NREnableUpscaling=1
NRPreset=3
NRStyle=1
NRIntensity=2.000000
NRLocalStructure=2.000000
NRLocalTone=2.000000
NRSkinStructure=2.000000
NRAutoMask=1
NRUICorrection=1
NRDepthMode=2
NRMVecScaleX=4.000000
NRMVecScaleY=4.000000
NRPaperWhiteScale=16.000000
NRTransferStrength=2.000000
NRColorStrength=2.000000
EnableHooks=2
```

## Validation evidence

The host ReShade log confirmed that the correct DLSSNR runtime was loaded:

```text
signed runtime sha256 E16BCF15E16E13F527491CDF7845B2FE6521A738D8F7C9C721866A8496E1FC8E (reference match)
```

The host log also confirmed that feature 18 was created and evaluated:

```text
feature 18 created via the signed snippet after DLSS/DLAA for NR input 2560x1080 -> output 2560x1080 with guides 2560x1080
inline feature 18 evaluation succeeded
```

The feeder output differed from the source frame after the fix. Example observed differences:

| Frame | Changed bytes | Absolute sum | Max delta | Mean delta |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 1,927,413 | 21,025,893 | 255 | 1.9012 |
| 2 | 2,104,259 | 23,637,729 | 255 | 2.1374 |
| 3 | 2,237,812 | 24,357,108 | 255 | 2.2024 |
| 6 | 2,658,307 | 25,700,803 | 255 | 2.3239 |

On normal shutdown, the logs showed a clean teardown:

```text
native D3D9 device destroyed; shutting down
shut down cleanly
pipe closed by the game
pending game fence waits released; exiting
```

## Known-bad combination

The older RenoDX add-on and older DLSSNR runtime were not the working combination:

| File | Size | Version | SHA-256 |
| --- | ---: | --- | --- |
| `renodx-dlss5.addon64` | 391,168 | 0.2026.0827.2036 | `87AEF9DDD937C724...` |
| `nvngx_dlssnr.dll` | older runtime | unknown | `CEB6432F6FBDF44D...` |

That combination produced feature-18 creation failure with `0xbad00002` and much weaker output differences, around mean delta `0.43`.

The newer RenoDX add-on was still not enough by itself with the older `CEB6432F...` runtime. The working fix was the newer RenoDX add-on plus the `E16BCF15...` 310.0.8 `nvngx_dlssnr.dll`.

## Installer implication

For DangerousCurves-style DX9 deployment, the installer should copy or download the newer RenoDX DLSS5 add-on and the matching 310.0.8 DLSSNR runtime. If a deployment has the feeder controls and split-screen working but the image does not visibly change, verify the `host64\nvngx_dlssnr.dll` hash first.
