# Open Tuner – HB9IIU QOCAST version

An HB9IIU version of **[Open Tuner](https://github.com/tomvdb/open_tuner) by ZR6TG**,
open-source Windows software for DATV reception with MiniTiouner and PicoTuner
tuners. It is the receiver behind **QOCAST**, the browser-controlled QO-100 DATV
console: QOCAST starts it, tunes it, and shows its picture and receiver status in
the browser (RX page and on-air monitor).

All credit for Open Tuner itself goes to ZR6TG and its contributors.

This repository is the source code of the Open Tuner shipped in the
[QOCAST releases](https://github.com/HB9IIU/QOCAST/releases) (GNU GPL v3).

## Started by QOCAST: `--qocast`

QOCAST starts Open Tuner with `--qocast`, which:

- starts **minimized** (the picture is shown in the browser);
- selects the Minitiouner source and **connects by itself**: when the tuner type
  is set to "ask", the tuner is recognised from its USB name (PicoTuner, or an
  FT2232H MiniTiouner board);
- shows **no dialogs**; with no tuner plugged in it retries every 5 seconds;
- turns on the **QOCAST stream** (below).

Before starting it, QOCAST closes any other Open Tuner (it would keep the tuner
busy). When QOCAST quits, it closes Open Tuner too.

## Interfaces used by QOCAST

**Control API** (local only): `http://127.0.0.1:8090/api/v1`

| Request | Purpose |
|---|---|
| `GET /health` | Running? Source and whether the tuner is connected |
| `GET /status` | Lock, MER, D margin, frequency, symbol rate, MODCOD, service, codecs |
| `POST /tune` | Tune tuner 1: `frequency_khz` (downlink), `symbol_rate_ksps` |

**QOCAST stream**: tuner 1's picture and sound, re-encoded by FFmpeg to H.264/AAC
fragmented MP4 (browser-playable), sent over TCP to `127.0.0.1:5001`. It starts
when tuner 1 locks, restarts after a retune or lost lock, and retries every
5 seconds while QOCAST isn't listening. Settings: `settings\qocast_stream_settings.json`
(older `jetson_stream_settings.json` files are taken over automatically).

## FFmpeg

Open Tuner ships its own FFmpeg 6.0 (shared build, GPL v3) in its `ffmpeg`
folder: its built-in video player needs these exact DLLs, and the QOCAST stream
uses its `ffmpeg.exe`.

## Origin

This version started from the HB9IIU Open Tuner version made for the
[HB9IIU Jetson DATV Transmitter](https://github.com/HB9IIU/HB9IIU-Jetson-DATV-transmitter).
Its stream and status features were renamed and extended for QOCAST.

Other changes inherited from it:

- Fixed a startup crash caused by the tuner status thread accessing the recorder
  and streamer lists before initialization.
- Build, run and debug configuration for VS Code (`.vscode`), using Visual Studio
  2022 Build Tools.
- The window title shows the actual build date.

## Building

1. Install **Visual Studio 2022 Build Tools** with the *.NET desktop build tools*
   workload.
2. Download the FFmpeg 6.0 shared build `ffmpeg-6.0-full_build-shared.zip` from
   [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) and unzip it to
   `packages\ffmpeg-native-6.0\` (so that
   `packages\ffmpeg-native-6.0\ffmpeg-6.0-full_build-shared\bin\` exists).
3. Run `BUILD-QOCAST-OpenTuner.cmd`. The program is in `bin\Release`.

QOCAST's own build script copies the result into the portable QOCAST package
(`OpenTuner` folder).

## Original Open Tuner

- Source: <https://github.com/tomvdb/open_tuner>
- Project information: <https://www.zr6tg.co.za/open-tuner/>
- Compiled beta/test versions by ZR6TG: <https://www.buymeacoffee.com/zr6tg/posts>

## License

Open Tuner and this version are licensed under the **GNU GPL v3**. See [LICENSE](LICENSE).

QOCAST is a separate program; it talks to Open Tuner over the local network
interfaces above.

73 de HB9IIU
