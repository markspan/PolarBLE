# PolarBLE

**PolarBLE** is a C# .NET 8 WinForms application that connects to a **Polar H10 ECG sensor** over **Bluetooth Low Energy (BLE)** and streams raw ECG and accelerometer data in real time to **LabStreamingLayer (LSL)**.

This project is a port of my previous [PolarBand2lsl](https://github.com/markspan/PolarBand2lsl) Python/Kivy implementation.
I took info from the project [Dont-hold-your-breath](https://github.com/kieranabrennan/dont-hold-your-breath) by Kieran Brennan. Look it up!

![Screenshot](https://github.com/markspan/PolarBLE/blob/main/Screenshot.png)

---

## 🧠 Features

- Scan for nearby **Polar** straps via BLE
- Connect and start the Polar Measurement Data (PMD) ECG and accelerometer streams
- Decode and stream **130 Hz** ECG data to **LSL**
- Decode and stream **200 Hz** accelerometer data to **LSL**, including delta-compressed frames
- Live telemetry for both streams: sample counts, frames lost on the radio link, and stalls
- Battery level, updated live for the duration of the recording
- Clean teardown: the strap is told to stop streaming on disconnect and when the window closes

---

## 📡 The LSL streams

Two outlets are advertised. `<device>` is the strap's advertised name, e.g. `Polar H10 12345678`, and `<address>` is its Bluetooth address in hex.

| Stream name | Type | Channels | Unit | Rate | `source_id` |
|---|---|---|---|---|---|
| `<device>_ECG` | `ECG` | `ECG` | microvolts | 130 Hz | `PolarBLE:<address>:ECG` |
| `<device>_ACC` | `Accelerometer` | `X`, `Y`, `Z` | milli-g | 200 Hz | `PolarBLE:<address>:ACC` |

The `source_id` is derived from the Bluetooth address, so a recorder can re-bind to the same physical strap after a dropout, and two straps recorded simultaneously never collide.

Each stream also carries an `acquisition` metadata block naming the manufacturer, model, device address and the exact PMD settings that were requested: sample rate, resolution, and for the accelerometer the **±8 g** range.

### A note on timestamps

PolarBLE does not compute timestamps itself. Each burst of decoded samples is pushed to LSL in one `push_chunk` call with no timestamp argument, so liblsl stamps it at push time and derives the earlier samples in the burst from the stream's nominal rate. That is one call per BLE notification rather than one per sample.

### Detecting lost data

Every PMD frame carries the strap's own timestamp of its last sample. PolarBLE compares consecutive timestamps with the number of samples that actually arrived: when the interval is clearly longer than the samples account for, notifications were lost on the radio link. These gaps, the estimated number of samples lost, frames with an unknown encoding, and streams that have been idle for more than three seconds are all shown in red in the window. Detection is diagnostic only; the data itself is never altered or re-timed.

---

## 🦀 Rust version

A parallel implementation in Rust lives in [`rust/`](rust/README.md). It is a command-line tool that runs on Windows, Linux and macOS, produces the same LSL streams (including the same `source_id` for the same strap), and adds a `simulate` command that streams a synthetic strap through the full pipeline to test an LSL setup without hardware.

---

## 💻 Requirements

- Windows 10 (build 17763) or later
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) to run a release build, or the [.NET SDK](https://dotnet.microsoft.com/en-us/download) 8.0 or newer to build from source
- Bluetooth LE–capable adapter (internal or external)
- Polar H10 ECG sensor

---

## 🚀 Quickstart

Download the latest release from the GitHub repository, unzip and run.

1. Press **Scan**. Nearby Polar straps appear in the list. The strap has to be worn, or its electrodes moistened, before it advertises.
2. Select a strap and press **Connect**, or double-click it. Connecting can take up to a minute.
3. Once connected, the window shows the running sample count of both streams. Lost frames, unreadable frames and stalls are reported in red next to the stream they affect.
4. Press **Disconnect**, or close the window, to stop the measurement cleanly. This matters: without it the strap keeps streaming and draining its battery.

If the strap refuses a configuration, or a required service is missing, the reason is reported in the window instead of leaving an empty stream advertised on the network.

---

## 🔨 Building from source

```
git clone https://github.com/markspan/PolarBLE.git
cd PolarBLE
dotnet build PolarBLE.sln -c Release
dotnet test PolarBLE.sln
```

Or open `PolarBLE.sln` in Visual Studio 2022 and press F5.

### Project layout

| Path | Purpose |
|---|---|
| `src/PolarBLE.Core/Polar` | The PMD protocol codec: frame headers, ECG and accelerometer decoders, control-point requests and responses, and the H10 configuration. |
| `src/PolarBLE.Core/Acquisition` | The platform-free data path: routing notifications to streams, decoding, gap detection and live statistics. |
| `src/PolarBLE.Core/Streaming` | The sample sink abstraction and its LSL implementation. |
| `src/PolarBLE.App/Bluetooth` | WinRT Bluetooth: scanning, the connection session, the control-point channel and battery monitoring. |
| `src/PolarBLE.App` | The WinForms user interface. |
| `tests/PolarBLE.Core.Tests` | xUnit tests for the codec and the whole acquisition path. No hardware required. |
| `third_party/liblsl` | Vendored, unmodified [liblsl](https://github.com/sccn/liblsl) C# binding and its native `lsl.dll`. |
| `rust` | The Rust implementation; see [its README](rust/README.md). |

The separation is deliberate. Core targets plain `net8.0`, with no WinRT and no WinForms, and every byte-level decoding rule is a pure function over a `ReadOnlySpan<byte>`. The whole path from a raw notification to the samples handed to LSL is therefore covered by tests, rather than by hoping a recording looks plausible. The app layer only moves bytes between WinRT and that pipeline.

The Core library and its tests build and run on any OS. To build the Windows app on Linux or macOS as well, add `-p:EnableWindowsTargeting=true`.

---

## 📜 Licence

Distributed under the **GNU Lesser General Public License v2.1**. See [LICENSE](LICENSE).

The vendored liblsl binding in `third_party/liblsl` is covered by its own upstream licence.
