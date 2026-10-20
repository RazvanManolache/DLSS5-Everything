# NVIDIA and community runtime downloads

Startup and **Update components** download the inspected **310.9.16.0 OpenNR
FP16 compatibility runtime**, distributed in
[NODIX DLSS-5-MANAGER 1.3.7](https://github.com/NODIX-TECH/DLSS-5-MANAGER/releases/tag/1.3.7).
This is an additional supported runtime edition. NVIDIA's original runtimes,
manual DLL imports and the optional [shared native exports](optional-model-library.md)
remain supported. The exploratory Swapper/FF7R automatic download sources have
been removed; previously downloaded files are preserved.

Only `mod files/dlss 5/nvngx_dlssnr.dll` is extracted from `DLSS.5.MANAGER.rar`.
NODIX Manager, its installer and unrelated payloads are not installed or run.
Extraction uses the app's managed C# archive reader, without Python or an external
extractor. The release is pinned instead of silently accepting a different build
from a future release.

| Verified item | Identity |
| --- | --- |
| Release / archive | `1.3.7 / DLSS.5.MANAGER.rar` |
| Archive size / SHA-256 | 707,717,184 bytes / `ac6ddca163ea5753d951af2e2d373197feaf1bdd4a027d308a38d5df491ee910` |
| NR DLL version / size | `310.9.16.0` / 276,162,560 bytes |
| NR DLL SHA-256 | `cdfa6c1ec784867182164c7836f324f087b5c9f374d0c9fe46f54999feabf3ed` |
| Original resource SHA-256 | `836f445d06ecd2e59bb9f17b84b91c143396fd76ccda1c9dc7fe81d5edd548f4` |
| Lightweight FP8 resource SHA-256 | `f743e8eb320f42bc9bdc992ee2bf51f7bb7c8688ced4ecf5ce6807ec4eef7962` |

The DLL matches [OpenNR's published build identity](https://github.com/clshortfuse/openNR/blob/main/profiles/compat-fp16-310.9.16.json).
Both model resources match our NVIDIA 310.9.16 reference byte for byte. This is
modified runtime code with the existing two networks, not a newly trained model
or an NVIDIA-signed replacement. The downloaded DLL retains its upstream terms.

## Central storage and selection

```text
<component directory>\Nvidia\nodix-compat\1.3.7-ac6ddca163ea\
    nvngx_dlssnr.dll
    package.json
```

Downloads are staged under `.download-cache`, bounded by size, authenticated
against GitHub's published archive SHA-256 and the compiled archive pin, then
checked for x64 PE validity and the pinned DLL hash, size and version. Cache
reuse rehashes the file and enforces the compiled identity again. Repairs publish
another immutable directory instead of overwriting files a game may have mapped.

`nvidia-downloads.json` records source status; component details identify the
NODIX/OpenNR edition and its two models. The scanner excludes staging and gives
explicit imports priority over managed downloads. Within managed packages, NR
version and bundle publication order determine selection. A missing upstream
release preserves cached packages and imports. Component checks do not modify
game installations. This download supplies NR only; existing support for NVIDIA
SR, RR, FG and Streamline DLLs remains separate.

## Older RTX cards

The compatibility DLL includes device images targeting `sm_75`, `sm_86`, `sm_89`
and `sm_120`, relevant to RTX 20/30/40/50-series hardware. Select the **NVIDIA
DLSS5** execution backend to use its code. A Phoenix build with this container's
hash in its picker policy is required; both model resources still undergo their
normal identity and layout validation.

Published **Native RTX Phoenix** kernels target `sm_120`. Current Phoenix source
also adds experimental native `sm_75/86/89` builds, separately from this DLL's
vendor execution code. Those require numerical, visual and performance checks on
the corresponding devices; downloading this DLL does not upgrade a previously
installed Phoenix addon. See [Phoenix's architecture notes](https://github.com/RazvanManolache/dlss5-phoenix-addon/blob/main/docs/NATIVE_ARCHITECTURES.md)
when using a build containing that work. Portable providers retain their own
device requirements.

OpenNR documents D3D11/D3D12 compatibility patches and leaves comprehensive
runtime validation outstanding; it does not claim its CUDA/Vulkan backends work.
See its [support boundary](https://github.com/clshortfuse/openNR#support-boundary).
No older-card inference, output parity or performance claim is made by this
downloader change. Compatible drivers and the actual API/transport route still
matter; download success is not a device capability test.

## Validation

`tests/NvidiaRuntimeProbe` exercises the production HTTPS downloader, real RAR
extraction, central discovery and authenticated cache reuse. It reads DLL
metadata without executing the runtime:

```text
dotnet run --project tests/NvidiaRuntimeProbe -- <test-payload>
```

The retained [validation receipt](current-nvidia-runtime-validation.json) records
the real downloaded archive and extracted DLL. Focused tests cover checksum,
size, PE and pinned-build rejection, cache tampering/repair, cancellation,
duplicate/traversal ZIP entries, source failure, import priority and staging
exclusion. Phoenix's separate CPU-only model-selection checks exercise both
NVIDIA references and this compatibility edition. No GPU inference or game was
run for this change.
