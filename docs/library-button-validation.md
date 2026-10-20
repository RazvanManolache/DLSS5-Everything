# Library Button Validation

This checkpoint uses Windows .NET/WinForms integration tests, synthetic entries and temporary files. No Computer Use, game launch, GPU inference or real Vulkan registration is involved.

The final full application suite passed **699/699 tests**, with no failures or skips. The dedicated button suite passed **13/13**. Distribution-notice checks passed **37/37**; the clean local build contained no external payloads, logs, debug symbols or personal path strings.

## Actual Click Handlers Exercised

| Surface | Checked outcome |
| --- | --- |
| List / Previews | Both directions, exclusive active styling, retained selection and visible controls at 1060x740, 1400x900 and 1920x1080 |
| Navigation and component setup link | Correct page becomes active and visible |
| Favorite, hide, show again | Library state and filtered rows change correctly |
| Sort menu | Selected column and ascending/descending order change |
| Artwork menu | Automatic-download preference changes; service tests cover local Steam art, matching, caching, retries, fallback and rejected downloads |
| Play / Open folder | Different process requests with correct arguments; injected process boundary prevents real execution |
| Resource links | HTTPS destinations reach the injected system-launch boundary without opening browsers |
| Copy activity / selected history | Correct text reaches an injected clipboard boundary without changing the user's clipboard |
| Add-on enable / disable | Selected temporary add-on is renamed and restored byte-for-byte |
| Controls / Compare | Real settings dialog opens on Rendering / Comparison, then closes without changing the temporary configuration |
| Right-click menu and unavailable actions | Availability matches main actions; missing selection/configuration and busy operations disable applicable commands |

The earlier ToolStrip layout responded to handlers while appearing blank in rendered previews. Library view controls now use ordinary WinForms buttons. Off-screen form renders were inspected at the three sizes above; heading and footer clipping was also corrected.

## Other Buttons And Boundaries

File/folder pickers, imports, downloads, installer/remove/recovery, Vulkan setup and app updates were checked against their bound handlers and prerequisite states. Existing service suites exercise their validation, cancellation, ownership and rollback with synthetic inputs or mocked remote/registry state. This checkpoint does not claim real dialog selections, live downloads, administrator changes or game installations were performed by clicking every button.

No files or artwork from personal game installations are required. Rendered test images and test receipts remain ignored under `.local-validation/` and are not distribution files.
