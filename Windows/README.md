# Lucid-Stream (Windows, v1)

Lucid-Stream holds the LucidPro (CGX) patch's connection on a Windows PC and publishes the signals to
**Lab Streaming Layer (LSL)** and a **localhost WebSocket API**, raw and processed. It is a generic streamer:
any LSL tool (LabRecorder, MNE-LSL, OpenViBE, MATLAB) or WebSocket client (Python, a native app, a web page)
can read the signals.

The behavior follows the *CGX Patch EEG Streaming Spec: LSL and WebSocket* (Lucid-Stream project doc).
Research use only. Not for diagnosis or clinical decision-making.

## What is in this folder

| Path | What it is |
| --- | --- |
| `src/LucidStream.Core` | Everything that matters: device sources, stream core (timestamps, sample index, gaps), filters and features, LSL outlets, WebSocket server (Origin allowlist, Host check, tokens, framing, resume, backpressure), session files, replay. No NuGet packages. |
| `src/LucidStream.App` | The Windows app (WPF). Main view: Connect, Start streaming, compact patch status (link, battery, contact), and the live signal drawn as in the Lucidify Cloud viewer: an oscilloscope sweep (window, amplitude, gain, signal) and a jet spectrogram that builds from the left (window, max frequency, FFT size, overlap, brightness, gamma, line-noise hiding, channels), plus band power. View settings are saved in settings.json. Everything else (source, outputs, Lucidify Cloud, connected apps and pairing, markers, developer options, log) is under the settings cog. Bluetooth adapter in `Ble/`. |
| `src/LucidStream.Cli` | `lucid-stream` headless host on the same engine (USB dongle, simulator or replay), for lab PCs and web-client development. |
| `tests/LucidStream.SelfTest` | 100 checks, no test packages needed. Exit code 0 = pass. |
| `examples/python/ws_client.py` | Research client: pairing code, binary frames, band power. |
| `config/allowlist.json` | Origin allowlist shipped with the app. |
| `tools/build.ps1` | Build, test and publish in one command (see below). |
| `tools/get-liblsl.ps1` | Installs liblsl v1.17.7 (`lsl.dll`, the official Windows x64 build shipped in pylsl on PyPI, hash-checked) into `lib\`. Called by `build.ps1` when needed. |

## Build and run

Needs Windows 10 or 11 (x64) and the .NET 8 SDK or later. Visual Studio 2022 (17.8 or later) with the
".NET desktop development" workload includes it.

One command fetches liblsl if it is missing, builds everything and runs the self-tests:

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1
```

| Option | What it does |
| --- | --- |
| `-Publish` | Also publish the app and the CLI to `artifacts\publish` and zip the app. Target PCs need the .NET 8 Desktop Runtime and ASP.NET Core Runtime 8 (x64). |
| `-Publish -SelfContained` | Include the .NET runtime, so target PCs need nothing installed. |
| `-Run` | Start the app after a successful build. |
| `-Clean` | Delete `bin` and `obj` first. |
| `-StopRunning` | Close a running Lucid-Stream built from this folder first (it locks the build output). |
| `-SkipTests`, `-SkipLsl`, `-Configuration Debug` | As named. |

The script exits 0 on success and 1 on failure, and writes every command and its output to `artifacts\build.log`.
Each build also puts **Lucid-Stream** in the Start menu (type the name in Start, or right-click it to pin it to the
taskbar). Only one copy runs at a time: starting it again brings the running window forward.
In Visual Studio, open `LucidStream.sln`, set `LucidStream.App` as the startup project and press F5.

Without `lsl.dll` everything still runs; LSL output is simply disabled and the app says so.

### Lucidify Cloud

**Open Cloud view** (Lucidify Cloud card) opens a second window: the live trace straight from the patch on top, the
Lucidify Cloud viewer (https://lucidify-cloud.web.app/cloud_session_viewer.html) below. Sign in there with Google as
on the website; the app keeps the sign-in in its own browser profile (%LOCALAPPDATA%\LucidStream\WebView2).

1. Connect the patch and **Start streaming**.
2. In the Cloud view, pick the organization and patient.
3. **Record to Cloud**, confirm. The app creates a new Cloud session for that patient and switches the viewer to it.
4. **Stop** (or stop streaming) ends the session; the app finishes the upload and finalizes it.

Uploads work like the Android app: unchanged raw 36-byte packets in 5-second chunks, encrypted on this PC first
(Windows data protection, %LOCALAPPDATA%\LucidStream\cloud), newest chunk first so the Cloud view stays live, each
checked by SHA-256, then finalized with a manifest readback. Markers are sent as session events. The delay line shows
how far Cloud is behind the patch (at least the 5-second chunk plus upload time). If the app closes mid-recording,
the remaining chunks are uploaded and the session finalized the next time the Cloud view opens and is signed in.
Cloud recording needs the real patch; the simulator and replays have no raw packets to send.

### Try it without a patch

1. Start the app, open **Developer**, tick **Developer mode**.
2. Pick **Simulated patch (developer)**, **Connect**, **Start streaming**.
3. Read it with any LSL tool, or run `python examples\python\ws_client.py` and enter the pairing code the app shows
   under **Connected apps**. The simulator alternates 30 s eyes-open and eyes-closed blocks, so alpha rises and falls.

Same thing headless: `dotnet run --project src\LucidStream.Cli -- --simulate --dev --pair read:raw,read:processed`.

### Connecting a LucidPro patch

**Connect** picks the link by itself:

- **CGX USB dongle** if one is plugged in. CGX recommends this on PCs: the dongle is paired to one patch and
  connects to it as soon as the patch is switched on. It uses FTDI's driver, which Windows installs for the dongle.
- **Bluetooth** otherwise. The app scans for the CGX service, the same way the Android app does. The patch can
  only be connected to one thing at a time, so close the Android app's connection or unplug the dongle first.

To force one, set `"connection": "dongle"` or `"bluetooth"` in the `ble` section of settings.json.

If the patch goes out of range or is switched off, streaming keeps running and the app reconnects by itself as soon
as the patch is back (the header shows "patch out of range, reconnecting"). The missing stretch is marked as a gap
in every output, and a Cloud recording continues in the same session.

What comes out, per CGX's *Patch EEG Bluetooth Data Interface Specification*:

| Signal | Rate | Units and notes |
| --- | --- | --- |
| EEG `AFz`, `Fp2` | 500 Hz | Microvolts (4.8 / (6 x 2^24) V per count), DC-coupled, both against the patch reference electrode. CGX does not name the reference site. |
| Accelerometer X, Y, Z | 50 Hz | g (6.4e-5 g per count), averaged from 500 Hz. |
| Impedance per channel | 1 Hz | kOhm, demodulated in the patch firmware. |
| Battery, SD card | 1 Hz | Volts and percent (3.5-4.15 V); SD card present or not. |
| Contact | 1 Hz | Good while the channel's DC offset stays within +/-300 mV, as CGX recommends. |

Lost packets are counted from the patch's packet counter and filled or marked as gaps. In the first two seconds
of real data the log reports a **data format check** (CGX packs 7 bits per byte so 0xFF only marks the start of a
packet) and the accelerometer magnitude (about 1 g when still).

Note for the other Lucidify apps: the Android app and Cloud viewer currently read each 3-byte sample as a plain
24-bit number. CGX's format packs 7 bits per byte, so those decodes are off (about half amplitude, small steps,
a jump when a channel crosses 0 V). Cloud keeps the raw packets, so recordings can be decoded again correctly.
Lucid-Stream follows the CGX format. `"packing": "plain24"` in settings.json reproduces the old decode for comparison.

### Replay

Every streaming session can save a session folder (Documents\Lucid-Stream\Sessions by default). In developer
mode choose **Replay a recording**, pick that folder, and it streams through LSL and WebSocket exactly as if
live, marked `mode: "replay"` in welcome, flag bit 1 in binary frames, and `<replay>` plus a `:replay` source id in LSL.

## WebSocket API in one screen

- Connect to `ws://localhost:8765/v1/stream` (LAN mode adds `ws://<pc-ip>:8765/v1/stream`, off by default).
- The server checks `Host` (must be `localhost`, `127.0.0.1` or `[::1]` with the port) and `Origin`
  (must be on the allowlist) before reading anything. Failures close with 4006 and 4005.
- Send `{"type":"hello","schema":"1.0","pairing_code":"123456"}` with the code shown in the app. The `welcome` reply
  carries a `token`; reconnect later with `{"type":"hello","schema":"1.0","token":"..."}`.
- Web pages must also be on the allowed list (Developer > Web pages allowed to connect, or `allowed_origins` in
  settings.json). Apps outside a browser send no Origin and need only the pairing code.
- Receive `welcome` (session, mode, scopes, device, clock, stream catalog with ids) and a `status` message.
- Send `{"type":"subscribe","streams":["eeg.raw"],"encoding":"binary"}`; repeat for other streams/encodings.
- Data: JSON `{"type":"data","stream":"bandpower","index":..,"t0":..,"data":[[...]]}` (NaN as `null`) or binary `LCDF` frames.
- Reconnect: send a new `hello` with the same token and `"resume_from":{"eeg.raw":<last index>}`; up to 5 s is refilled.
- Other messages: `marker` (needs `write:markers`), `time_sync`, `unsubscribe`; server sends `gap`, `status`, `error`.

Binary frame: 32-byte little-endian header (`LCDF`, version 1, stream id, flags, seq, first index, t0 on the LSL
clock, sample count, channel count) then float32 samples interleaved by channel.

Close codes: 4001 invalid token, 4002 schema, 4003 unknown stream, 4004 scope, 4005 origin, 4006 host,
4007 token expired or session ended, 4008 too many clients, 4009 superseded by a newer connection.

Streams: `eeg.raw`, `eeg.filtered` (0.5-40 Hz + notch), `acc`, `bandpower` (2 s Hann window every 250 ms;
per channel 5 absolute then 5 relative bands), `quality` (impedance + heuristic 0-1 index, 1 Hz), `status`, `markers`.
`lucidify.lsi`, `lucidify.sleepstage` and `lucidify.delirium` are reserved and refused in v1.

## What still needs Lucidify input (search the code for `TODO(`)

| Item | Where | Why it matters |
| --- | --- | --- |
| Check on a real patch: data format check passes, accelerometer reads about 1 g at rest, impedance matches CGX Flowpoint | the app's log | The decode follows CGX's documents; these confirm it on hardware. |
| Reference electrode site | `DeviceInfo.PatchReference` | CGX's documents name the two channels (AFz, Fp2) but not the reference. |
| Allowlist signing key | `Core/Ws/OriginPolicy.cs` (`LucidifyPublicKeyPem`) | Until set, signed allowlist updates are ignored and the shipped list is used. |
| Lucidify session file format | `Core/Recording/SessionFiles.cs` | Stand-in CSV folder today; switch so recordings flow into Lucidify Cloud tools. |
| BLE latency correction | setting `BleLatencyMs` (40 ms placeholder) | Measure with the tap test in the spec. |
| SDK sample recordings | record with the app, then ship 2-3 short sessions | Spec, developer replay mode. |

## Notes

- Settings live in `%LOCALAPPDATA%\LucidStream\settings.json`.
- Lucid-Stream runs only while its window is open. Closing it stops streaming, disconnects the patch and stops any
  Cloud recording (its upload is finished, or completed the next time the Cloud view opens).
- Chrome asks once before a public HTTPS page can reach `localhost`. A good web app warns about that prompt before its
  first connection and shows how to re-enable it if blocked. Managed PCs can pre-grant it by policy.