# Optional native model downloads

Components → Model source offers two optional checkboxes:

- Original native export: about 148 MB download.
- Lightweight native export: about 114 MB download.

Use **Download selected native models**, or check them before the normal
component update. They are off by default. Opening Components does not download
anything. Unchecking a box does not remove an installed model.

The files are fetched from the pinned `bdsqlsz/DLSSNR3.19.16.0` revision
`c305bfc0db1bb94bcc8e25b074904b5158d7cbd8`. Every source file has a pinned SHA-256.
The built-in C# converter reverses the canonical tensor storage and requires
every packed stage to reproduce its upstream hash. Source downloads and temporary
conversion files are removed after publication; only the packed model and its
small manifest remain in `<component library>/Models`.

Both exports reproduce the corresponding NVIDIA model payloads exactly: 153
Original records and 77 Lightweight records. They are alternative weight sources
for those networks, not two additional trained models. Phoenix uses its existing
native C++ engines at supported arbitrary frame dimensions, with the normal
granular controls. Python, PyTorch, ONNX and wrapper executables are not installed.
Fixed-256 exports are deliberately absent from the download catalogue.

The installer adds the central library path to Phoenix.json and ReShade settings,
including when the NVIDIA DLL is used initially. Phoenix shows **Shared native
models** only when supported packages are available there. Choosing a package
selects its model-folder path and Original/Lightweight architecture together.
Future Phoenix-only installs prefer a matching installed export unless the caller
explicitly supplies a source. NVIDIA execution/comparison still needs its DLL.

Downloads are staged, size-limited, cancellable and checksum-verified. Completed
models are immutable. The index changes atomically only after successful native
conversion; a cancelled or failed package cannot replace a working one. Models
not selected for download are retained. Game uninstall does not remove shared
models. Moving/deleting the component library invalidates its game references.

Validation uses a dedicated CPU probe:

```
dotnet run --project tests/ModelLibraryProbe -- <canonical-export-checkout> <test-payload> <reference-310.9.16-DLL>
```

It feeds the real pinned source bytes through an HTTP fixture handler into the
production downloader/converter, publishes both models, validates their index and
installer configuration, and independently compares all payload records with the
data-only mapped DLL. It never executes the DLL or launches a game/GPU workload.
The probe writes `model-library-validation.json` into its test payload.

Focused unit/UI tests cover missing/changed packages, traversal rejection,
checksum failure preserving existing models, cancellation, verified reuse,
installer paths, checkbox bindings and Components layout at two window sizes.
UI captures are in `docs/current-model-library`.
