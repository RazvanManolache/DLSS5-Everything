# README demo images

Created 20 October 2026. These are staged, fictional examples with actual Phoenix
output, not captures of the author's desktop, camera, phone or private accounts.

## What is real, and what is staged?

- **Game library:** an offscreen render of the actual current app in its preview
  mode, using built-in example titles and simulated installed states. No game
  folders were scanned, installed, launched or verified for this screenshot.
  It illustrates the game workflow, not gameplay output or compatibility.
- **Desktop:** a programmatically staged Windows-style desktop and YouTube-style
  browser page, with the video inside the normal page rather than fullscreen.
  The landscape still, channel, page address, files and clock are fictional.
  The complete 1280×720 frame was processed using the application's production
  `ScreenMediaConverter` → `ScreenStudioSession` pipeline.
- **Camera:** a generated fictional adult portrait resized to 1280×720 and
  processed through that same pipeline, which shares its Phoenix host with
  Camera. It is not a physical webcam capture or a virtual-camera delivery test.
  The separate Camera controls screenshot is a stopped preview with an example device.
- **Android:** a staged 540×960 travel-app screen. The actual PC-side
  `LanPhoenixBackend` accepted its RGBA pixels and returned a completed processed
  frame. The surrounding phone frames and data-flow labels are explanatory
  artwork. This run did not use a physical phone, Android capture, TLS transport,
  Wi-Fi or the Android overlay. Those features have their own
  [validation scope](lan-frame-service.md).

The finished side-by-side JPEGs resize the returned images to fit the README.
There is no hand-painted enhancement, difference amplification or independent
color grading of the output. Source/output PNGs below retain the original pixels.
Stills do not establish live FPS, latency, temporal stability, universal quality
improvement or device compatibility. Neural enhancement also changes colors and
details; compare the result rather than assuming every change is an improvement.

## Processing recipe

- Phoenix addon from the freshly built **0.9 Windows package**, native RTX path.
- Processing hardware: **NVIDIA GeForce RTX 5080**, Windows x64. No timing or
  compatibility claim for other GPUs is derived from these images.
- Original model, compatible separately supplied 310.8 DLL source. No model or
  vendor DLL is included with these demo assets.
- One pass, processing scale 1.0; tone **1.35**, structure **1.25**, skin **0.70**.
- Screen/camera images used the normal residual composition at strength 1.0.
  The Android example uses the phone backend's returned frame directly.
- No artificial depth, motion or surface-ID quality claim: the ordinary color-only
  pipeline supplies its normal flat depth and zero motion.
- No private camera, monitor, physical phone, credentials or pairing details were read.

| Scene | Input | Actual output | Mean absolute RGB change / 255 |
| --- | --- | --- | ---: |
| Staged desktop, 1280×720 | [PNG](images/demos/evidence/desktop-input.png) | [PNG](images/demos/evidence/desktop-output.png) | 3.817 |
| Fictional camera portrait, 1280×720 | [PNG](images/demos/evidence/webcam-input.png) | [PNG](images/demos/evidence/webcam-output.png) | 21.771 |
| Staged Android app, 540×960 | [PNG](images/demos/evidence/android-input.png) | [PNG](images/demos/evidence/android-output.png) | 3.899 |

Pixel differences establish that processing changed the image, not that the
change is objectively better. Exact input/output and binary hashes, dimensions,
settings and changed-pixel counts are in [the processing receipt](images/demos/processing.json).

## Generated source prompts

Source photographs were generated with the **built-in image-generation tool**,
then placed inside code-rendered fixtures. No photograph was downloaded from a
third-party site or taken from a real user's device.

**Landscape source**

> Create a photorealistic source image for a fictional product demo video. Wide
> 16:9 frame: a tiny red sailboat on a turquoise alpine lake, dramatic rugged
> mountains, golden evening light, pine trees, weathered wooden dock in foreground.
> Rich natural detail, believable amateur travel-video frame rather than a perfect
> advertisement. No people, no text, no UI, no watermark, no logo. Entirely fictional
> location. This is an INPUT image that will later be processed by a separate real
> image enhancement program; do not make a before/after comparison.

**Webcam source**

> Create a photorealistic source frame from a fictional webcam. Wide 16:9
> composition, chest-up portrait of a fictional adult woman with short curly dark
> hair, medium brown skin and round glasses, wearing a teal sweater, smiling
> naturally toward the camera. A plain tidy home studio with a small plant, warm
> lamp and neutral acoustic wall behind her. Normal diffuse daylight, modest
> consumer webcam detail, authentic skin texture, no plastic retouching. No name,
> no screens, no personal photos, no text, no UI, no logo, no watermark. This is an
> INPUT image for later real enhancement, not a before/after pair.

The YouTube-style page is fictional interface artwork, not an affiliation with
YouTube. OBS, YouTube and other product names retain their respective owners.
