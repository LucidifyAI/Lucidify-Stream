# Lucid-Stream (Windows, v1)

Lucid-Stream holds the LucidPro (CGX) patch's connection on a Windows PC and publishes the signals to
**Lab Streaming Layer (LSL)** and a **localhost WebSocket API**. Its main use case is **Listen**: the wearer taps
Listen, the approved web page opens on the same PC, connects to `ws://localhost:8765/v1/stream`, and turns
the live EEG into sound in the browser.

The behavior follows the *CGX Patch EEG Streaming Spec: LSL and WebSocket* (Lucid-Stream project doc).
Research use only. Not for diagnosis or clinical decision-making.

## What is in this folder

| Path | What it is |
| --- | --- |
| `src/LucidStream.Core` | Everything that matters: device sources, stream core (timestamps, sample index, gaps), filters and features, LSL outlets, WebSocket server (Origin allowlist, Host check, tokens, framing, resume, backpressure), session files, replay. No NuGet packages. |
| `src/LucidStream.App` | The Windows app (WPF): patch, outputs, Start/Stop, Listen, connected apps, pairing codes, markers, live trace and band bars, tray icon. Bluetooth adapter in `Ble/`. |
| `src/LucidStream.Cli` | `lucid-stream` headless host on the same engine (simulator or replay), for lab PCs and web-client development. |
| `tests/LucidStream.SelfTest` | 73 checks, no test packages needed. Exit code 0 = pass. |
| `web/listen-dev` | Reference Listen page (Web Audio sonification). Served at `http://localhost:8766` in developer mode. |
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
In Visual Studio, open `LucidStream.sln`, set `LucidStream.App` as the startup project and press F5.

Without `lsl.dll` everything still runs; LSL output is simply disabled and the app says so.

### Try the whole Listen flow without a patch

1. Start the app, open **Developer**, tick **Developer mode**.
2. Pick **Simulated patch (developer)**, **Connect**, **Start streaming**.
3. Tap **Listen**. The bundled page opens at `http://localhost:8766`, reads the token from the URL fragment
   (and removes it from the address bar), and on **Start listening** plays one voice per EEG band.
   The simulator alternates 30 s eyes-open and eyes-closed blocks, so the alpha voice rises and falls.

Same thing headless: `dotnet run --project src\LucidStream.Cli -- --simulate --dev --listen`.

### Replay

Every streaming session can save a session folder (Documents\Lucid-Stream\Sessions by default). In developer
mode choose **Replay a recording**, pick that folder, and it streams through LSL and WebSocket exactly as if
live, marked `mode: "replay"` in welcome, flag bit 1 in binary frames, and `<replay>` plus a `:replay` source id in LSL.

## WebSocket API in one screen

- Connect to `ws://localhost:8765/v1/stream` (LAN mode adds `ws://<pc-ip>:8765/v1/stream`, off by default).
- The server checks `Host` (must be `localhost`, `127.0.0.1` or `[::1]` with the port) and `Origin`
  (must be on the allowlist) before reading anything. Failures close with 4006 and 4005.
- Send `{"type":"hello","schema":"1.0","token":"..."}` (Listen page) or `{"type":"hello","schema":"1.0","pairing_code":"123456"}` (other apps).
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
| CGX Bluetooth protocol: service/characteristic UUIDs, packet layout, scale to microvolts, sample counter, accelerometer rate, battery/impedance fields | `Core/Devices/CgxPacketParser.cs`, `settings.json` (`ble` section) | The only piece needed to stream a real patch. Everything downstream is done and tested. |
| Approved Listen domain | `config/allowlist.json`, setting `ListenUrl` | Placeholder is `https://listen.example.com`. |
| Allowlist signing key | `Core/Ws/OriginPolicy.cs` (`LucidifyPublicKeyPem`) | Until set, signed allowlist updates are ignored and the shipped list is used. |
| Lucidify session file format | `Core/Recording/SessionFiles.cs` | Stand-in CSV folder today; switch so recordings flow into Lucidify Cloud tools. |
| BLE latency correction | setting `BleLatencyMs` (40 ms placeholder) | Measure with the tap test in the spec. |
| Patch input range for the saturation check | `QualityExtractor.SaturationUv` | Quality index heuristic. |
| SDK sample recordings | record with the app, then ship 2-3 short sessions | Spec, developer replay mode. |

## Notes

- Settings live in `%LOCALAPPDATA%\LucidStream\settings.json`.
- Closing the window while streaming keeps Lucid-Stream in the tray; use the tray menu to stop and exit.
- Chrome asks once before a public HTTPS page can reach `localhost`. The reference page explains this before the
  first connection and shows how to re-enable it if blocked. Managed PCs can pre-grant it by policy.