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
- Per-sample timestamps on a continuous grid, rather than one timestamp per arriving burst
- Live throughput, dropped-frame and gap counters, so a stalled stream is visible
- Battery level, updated live for the duration of the recording

---

## 📡 The LSL streams

Two outlets are advertised. `<device>` is the strap's advertised name, e.g. `Polar H10 12345678`, and `<address>` is its Bluetooth address in hex.

| Stream name | Type | Channels | Unit | Rate | `source_id` |
|---|---|---|---|---|---|
| `<device>_ECG` | `ECG` | `ECG` | microvolts | 130 Hz | `PolarBLE:<address>:ECG` |
| `<device>_ACC` | `Accelerometer` | `X`, `Y`, `Z` | milli-g | 200 Hz | `PolarBLE:<address>:ACC` |

The `source_id` is derived from the Bluetooth address, so a recorder can re-bind to the same physical strap after a dropout, and two straps recorded simultaneously never collide.

Each stream also carries an `acquisition` metadata block naming the manufacturer, model, device address and the exact PMD settings that were requested — sample rate, resolution, and for the accelerometer the **±8 g** range.

### A note on timestamps

The strap delivers samples in bursts: roughly 73 ECG samples arrive in a single BLE notification. Stamping them all at arrival time, which is what LSL does by default, collapses a whole burst onto one instant and leaves a gap until the next notification, misplacing samples by up to about half a second.

PolarBLE instead maintains a continuous sample grid. The last sample of a frame is taken to arrive now, the rest are back-dated at the nominal sample interval, and the grid is nudged gently towards observed arrival times so that it tracks clock drift without jittering. A disagreement larger than half a second is treated as a real discontinuity: the grid resynchronises, and the event is counted and shown in the window rather than being silently smoothed away. The device's own nanosecond frame timestamps are used to detect frames lost in transit.

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
3. Once connected, the window shows the measured sample rate of both streams. Dropped frames and timing gaps are counted and shown in red.
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
| `src/PolarBLE.Core` | Platform-free core: PMD protocol codec, sample timing, the LSL sink. Targets plain `net8.0` so it can be tested without a radio. |
| `src/PolarBLE.App` | Windows shell: WinRT Bluetooth and the WinForms user interface. |
| `tests/PolarBLE.Core.Tests` | xUnit tests for the codec and the sample clock. No hardware required. |
| `third_party/liblsl` | Vendored, unmodified [liblsl](https://github.com/sccn/liblsl) C# binding and its native `lsl.dll`. |

The separation is deliberate: every byte-level decoding rule is a pure function over a `ReadOnlySpan<byte>`, so the places where a silent data-corruption bug could hide are covered by tests rather than by hoping a recording looks plausible.

---

## 📜 Licence

Distributed under the **GNU Lesser General Public License v2.1**. See [LICENSE](LICENSE).

The vendored liblsl binding in `third_party/liblsl` is covered by its own upstream licence.
