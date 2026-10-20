# Phoenix DLSS5 LAN frame service

Status: implementation preview. Raw and bidirectional Pyrowave have passed phone-to-PC
frame tests, including real Phoenix DLSS5 processing. Continuous phone capture performance
is not certified. Android client source and APKs are maintained separately and
are not part of this repository or the Windows release.

An external device can send frames to a Windows PC running DLSS5 Everything and
receive Phoenix-processed frames. The client owns capture and presentation; this
is not a remote desktop, virtual display, audio stream or input-control service.

## Start and pair

1. Download the normal components and select a compatible Phoenix DLSS5 model locally.
2. Open **Streamer**, select the PC's private IPv4 address and port, then **Start streamer**.
   Starting the listener alone does not start Phoenix DLSS5 inference or screen capture.
3. Copy the certificate SHA-256 fingerprint and pairing token to the client over a
   trusted channel. Both change each time the listener starts. Do not publish them.
   **Export pairing** saves these private connection details as JSON for tooling;
   treat that file as a password. Obtain any client separately. The page displays the active transport,
   returned frame count, dimensions and PC processing time while connected.
4. If needed, allow inbound TCP for that port in Windows Firewall, restricted to
   the intended client IP and Private profile. The app does not change firewall
   rules, enable UPnP, request administrator rights or create Internet port forwards.
5. Connect using TLS 1.2 or newer. Verify the certificate fingerprint **before**
   sending the pairing token. Never blindly trust the self-signed certificate.
6. Open a session, send one frame, receive its result, and repeat. Update settings
   between frames. Close the session when finished; stop the server to revoke access.

Only one device/session is served at a time. The server binds a single selected
IPv4 address, not all interfaces; it accepts loopback, RFC1918 or link-local IPv4
peers. IPv6 and public addresses are unsupported. The listener is stopped by default;
local tooling can explicitly request startup with `--page Streamer --start-streamer`.
The pairing token grants GPU use until the listener stops. Use a trusted LAN:
address checks are not a substitute for authentication or firewall restrictions.
Disconnect, timeout, Stop and app exit terminate the owned native host. A Windows
kill-on-close job guards the host if the app crashes. Session files are temporary
under local app data; normal cleanup deletes them, but an OS/power failure may leave
local host diagnostics or configuration paths there. No incoming frames are saved.
No client filenames, paths, executable code or model paths are accepted.

## Android Client

Phoenix Link is a separately maintained Android 14+/ARM64 implementation
preview, with pairing, single-app and follow-active-app capture, a floating
result/control overlay, staged Phoenix DLSS5 controls, Raw and Pyrowave. Generated-frame
phone/network/GPU tests passed on one physical Android device; follow-active-app
capture and its controls passed emulator tests. Its guide explains permissions,
protected-window exclusions and capture limits. The phone does not run Phoenix DLSS5 inference.
Its code, build tools, device tests and APKs are not included here. The PC-side
server and reference client remain available independently.

## Reference client

`scripts/lan_frame_client.py` uses only Python's standard library and pins the
certificate before transmitting credentials. The command asks for the token
without echoing it or putting it in shell history:

```text
python scripts/lan_frame_client.py 192.168.1.10 input.rgba output.rgba --width 1920 --height 1080 --fingerprint YOUR_SHA256_PIN
```

That path is for a source checkout. App builds include the client at
`Tools/lan_frame_client.py` and this guide at `Docs/lan-frame-service.md`.

For capture integrations, import `PhoenixClient` and call `frame(rgba)`. Its returned
bytes are the actual native feeder output, not a screenshot of a preview window.
`update(tone=1.5, structure=1.2, iterations=2)` patches settings while preserving
unspecified values. `ping()` keeps an idle session alive. The client is sequential,
not thread-safe, and does not automatically retry frames or reconnect after failure.

## Format and bandwidth

Version 1 sends **raw, tightly packed RGBA8 sRGB**, top row first. Treat processed
output as opaque; preservation of input alpha is not guaranteed.
The output has exactly the input dimensions. There is no extra SR/DLAA pass at 1:1.
No desktop capture, mirror swapchain or per-frame image-file conversion is involved.
The native D3D12 Phoenix DLSS5 host and its CPU transport stay alive across frames,
preserving temporal history. First frame, settings updates and requested resets
reset that history. Frames do not include depth, motion vectors or stereo metadata;
this has the same limitations as other color-only feeder inputs.

Raw transport avoids video encode/decode and lossy compression, but it is not
zero-copy: network buffers, TLS, CPU upload and GPU readback still cost time.
At 1920 x 1080, each direction uses 8,294,400 bytes/frame; 30 FPS requires about
1.99 Gbit/s **in each direction**, before network overhead. Gigabit Ethernet is
not sufficient for 1080p30 raw. At 1280 x 720, 30 FPS is about 0.885 Gbit/s each
direction before overhead. Inference may be the lower limit regardless of link speed.
Wired multi-gigabit LAN is preferable. No throughput or latency claims are validated.

Dimensions support 1..16384 per axis, additionally capped at **64 MiB pixel data**
per frame (16,777,216 pixels). This bounds network allocations independently of
Phoenix DLSS5's resolution support. A 3840 x 2160 frame fits; a 7680 x 4320 frame does not.
One request/result at a time provides backpressure; there is no unbounded frame
queue and no intentional dropping of accepted frames. A broken connection or
timeout can abort a frame, and there is no exactly-once retry guarantee.

Protocol v2 additionally implements [Pyrowave](https://github.com/Themaister/pyrowave).
This is a codec integration, not Steam Remote Play interoperability. Version 1 remains
raw-only and backward compatible. Both endpoint libraries are pinned in the build script.

Pyrowave's initial adapter converts RGBA to/from full-range BT.709 YUV 4:4:4 on CPU,
with GPU encode/decode and CPU upload/readback. It is not a zero-copy path. The Windows
codec runs in a separate kill-on-close process, not inside the UI process. Both
directions have passed real-device encode/decode and processed-frame checks, but
this does not establish fluid native-resolution streaming performance.

## Wire protocol

All messages use the same header: `type:u8`, `payload_bytes:u32 little-endian`.
TLS is mandatory. No plaintext fallback or HTTP endpoints exist.

| Type | Direction | Body |
| --- | --- | --- |
| 1 | Both | UTF-8 JSON, 1..4096 bytes |
| 2 | Client to server | `sequence:u64 LE`, `flags:u32 LE`, then exactly `width*height*4` RGBA bytes |
| 3 | Server to client | `sequence:u64 LE`, `revision:i64 LE`, `processing_us:u64 LE`, then RGBA bytes |

For v2/Pyrowave, replace the pixel bytes in types 2 and 3 with an encoded envelope:
`packet_count:u32 LE`, then that many repetitions of `packet_bytes:u32 LE` and the
unmodified Pyrowave packet. There are at most 1024 packets of at most 16384 bytes,
with at most 16 MiB for the entire envelope. The wrapper validates coefficient
payload lengths, geometry, sequences and active-block counts before GPU decoding.
This is not a security certification of the upstream codec; only trusted paired
LAN clients should be permitted. Raw v2 still uses exactly the v1 frame body.

V2 open example:

```json
{"op":"open","version":2,"codec":"pyrowave","bitrate_mbps":100,"fps":15,"token":"64_HEX_CHARACTERS","width":1280,"height":720}
```

Use `codec:"raw"` to select raw explicitly. The server confirms the requested
version/codec and adds `max_encoded_bytes` and `chroma:"444"`. A missing Pyrowave
runtime yields `codec_unavailable`; initialization/processing failures close the
session rather than silently switching codecs. Bitrate is 20..500 Mbit/s and FPS
is 1..120; the per-frame codec budget is clamped to 4096..8 MiB. These are ceilings,
not promised throughput. Codec, bitrate, FPS and dimensions are fixed per connection.
V2 `update`, `ping` and `close` controls must also contain `"version":2`.

Handshake, authentication and the first control packet each have a 10-second
deadline. Startup and each full command/frame operation have a 120-second deadline.
After a reply, the next packet header must arrive within 30 seconds; send `ping`
when idle. The client's timeout must exceed the host processing deadline.

First control packet:

```json
{"op":"open","version":1,"token":"64_HEX_CHARACTERS","width":1280,"height":720,"settings":{"tone":1,"structure":1,"iterations":1}}
```

The reply contains `ok`, `op`, `version`, an opaque `session` ID, `width`, `height`,
`format:"rgba8_srgb"`, `codec:"raw"`, `revision:1`, effective `settings`,
`max_in_flight:1` and `max_pixel_bytes`. Dimensions are immutable for that connection;
close and reconnect to resize. A session is not resumable and needs no ID on each
message because it belongs to its authenticated TLS connection.

Update controls:

```json
{"op":"update","settings":{"tone":1.5,"structure":1.2,"iterations":2}}
{"op":"ping"}
{"op":"close"}
```

| Setting | Default | Range |
| --- | --- | --- |
| `enabled` | true | Boolean; false returns input bypass |
| `iterations` | 1 | Integer, 1..10 |
| `tone` | 1 | Finite number, 0..2 |
| `structure` | 1 | Finite number, 0..2 |
| `skin` | -1 | Finite number, -1..2 |
| `auto_mask` | false | Boolean |

Updates are acknowledged only after Phoenix DLSS5 accepts controls. Successful updates
increment the revision; output frames identify the effective revision. Native
controls can take time to apply; update between frames rather than on every frame.
Frame sequences start at 1 and strictly increase. Flag bit 0 resets history; all
other flag bits must be zero. `processing_us` covers the local feeder transaction
(upload, inference and readback), including codec decode/encode for v2/Pyrowave,
excluding network upload/download. Unknown fields,
commands, invalid sizes, sequences, credentials or processing failures end the
connection, with a generic `{"ok":false,"error":"session_failed"}` reply when possible.
Clients must never treat a failed request as a valid processed frame.

## Current Validation

The full .NET suite passes 827 tests (seven opt-in live tests skipped), including
47 focused protocol/TLS tests; four Android unit tests pass. Android 15
emulator instrumentation passes Raw roundtrips, mutable controls, certificate and
token rejection/recovery, UI-thread cancellation, overlay pixel rendering and
cleanup, encrypted storage, UI snapshots, and single-app capture through both a
predictable test processor and the real Phoenix DLSS5 host. Real Phoenix DLSS5 at 257 x 255
returns exact bypass bytes when disabled and changed output when enabled.

Windows Pyrowave roundtrips pass at 257 x 255 and 1280 x 720. Android Pyrowave is
not validated: SwiftShader Vulkan 1.1 cannot initialize it, and Lavapipe Vulkan 1.3
cannot create its encoder (8-wide subgroups versus the required 16/32/64).
Real-phone GPU support, image quality, touch routing, sustained LAN
throughput, latency, hostile-frame fuzzing and long-running lifecycle stress remain
open. Client instrumentation procedures are maintained with the separate companion.

The server uses a fresh identity and pairing token on every start. Windows Schannel
requires a temporary user key container for its self-signed certificate; the app
does not export identity files, and certificate disposal releases that container.
Pairing tokens are not persisted by the server.
