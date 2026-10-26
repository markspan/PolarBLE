# PolarBLE (Rust)

**PolarBLE** connects to a **Polar H10 ECG sensor** over **Bluetooth Low Energy (BLE)** and streams its raw ECG and accelerometer data in real time to **LabStreamingLayer (LSL)**. This is the Rust implementation: a command-line tool that runs on **Windows, Linux and macOS**, which suits unattended or headless recording.

The original Windows application, written in C# with a WinForms window, lives on the [`main`](https://github.com/markspan/PolarBLE/tree/main) branch. The two produce interchangeable streams: same names, same channel layout and metadata, and the same `source_id` for the same strap, so a recorder re-binds to a strap whichever implementation is streaming it.

PolarBLE is a port of my earlier [PolarBand2lsl](https://github.com/markspan/PolarBand2lsl) Python/Kivy implementation, and took information from [Dont-hold-your-breath](https://github.com/kieranabrennan/dont-hold-your-breath) by Kieran Brennan.

---

## 🚀 Quickstart

```
polarble scan                      # list the straps in range
polarble stream                    # connect to the only strap in range and stream
polarble stream --device 1A2B3C4D  # pick a strap by part of its name or address
polarble simulate                  # synthetic strap, to test an LSL setup without hardware
```

A strap only advertises while it is worn, or while its electrodes are moistened. Without `--device`, `stream` uses the only strap in range; if there are several, it lists them and asks you to choose.

While streaming, one telemetry line is printed every five seconds (`--interval` changes that):

```
  ECG       1,095 samples | ACC       1,620 samples | battery 87%
! ECG       2,190 samples, 1 gap, 73 lost | ACC       3,240 samples | battery 87%
```

A line starting with `!` reports a problem: notifications lost on the radio link, frames with an unknown encoding, or a stream that has been idle for more than three seconds.

Press **Ctrl+C** to stop. The strap is then told to stop measuring, the outlets are closed, and the connection is released. This matters: without it the strap keeps streaming and draining its battery. If the strap refuses a configuration, or a required service is missing, the reason is reported instead of leaving an empty stream advertised on the network.

### Finding liblsl

liblsl is loaded at run time, from the first of:

1. the path given with `--lsl-library`, or else in the `POLARBLE_LSL_LIBRARY` environment variable;
2. the directory of the `polarble` executable;
3. the system library path.

On Windows, put the `lsl.dll` from `third_party/liblsl` next to `polarble.exe`; the release zip already does. On Linux and macOS, install liblsl from [its releases](https://github.com/sccn/liblsl/releases), or use the copy bundled with the `pylsl` Python package.

---

## 📡 The LSL streams

Two outlets are advertised. `<device>` is the strap's advertised name, e.g. `Polar H10 12345678`, and `<address>` is its Bluetooth address as twelve hex digits.

| Stream name | Type | Channels | Unit | Rate | `source_id` |
|---|---|---|---|---|---|
| `<device>_ECG` | `ECG` | `ECG` | microvolts | 130 Hz | `PolarBLE:<address>:ECG` |
| `<device>_ACC` | `Accelerometer` | `X`, `Y`, `Z` | milli-g | 200 Hz | `PolarBLE:<address>:ACC` |

Each stream also carries an `acquisition` metadata block naming the manufacturer, model, device address and the exact PMD settings that were requested: sample rate, resolution, and for the accelerometer the **±8 g** range. macOS hides Bluetooth addresses, so there the stable per-machine peripheral id takes the address's place.

PolarBLE does not compute timestamps itself. Each burst of decoded samples is pushed to LSL in one chunk with no timestamp, so liblsl stamps it at push time and derives the earlier samples in the burst from the stream's nominal rate.

Every PMD frame carries the strap's own timestamp of its last sample. PolarBLE compares consecutive timestamps with the number of samples that actually arrived: when the interval is clearly longer than the samples account for, notifications were lost on the radio link. That detection is diagnostic only; the data itself is never altered or re-timed.

---

## 🔨 Building

Requires Rust 1.88 or newer. On Linux, btleplug also needs the D-Bus headers (`sudo apt install libdbus-1-dev pkg-config` on Debian and Ubuntu).

```
git clone -b rust https://github.com/markspan/PolarBLE.git
cd PolarBLE
cargo build --release
cargo test
```

The liblsl tests need the native library, so they are ignored by default:

```
POLARBLE_LSL_LIBRARY=/path/to/liblsl.so cargo test -p polarble-lsl -- --include-ignored
```

### Workspace layout

| Path | Purpose |
|---|---|
| `crates/polarble-core` | The PMD protocol codec and the acquisition pipeline: routing, decoding, gap detection, statistics. No dependencies and no `unsafe`, so every byte-level rule is tested without a strap or a radio. |
| `crates/polarble-lsl` | A minimal liblsl binding, loaded at run time, implementing the sample sink. All of the workspace's `unsafe` code lives here. |
| `crates/polarble` | The command-line tool: Bluetooth through [btleplug](https://github.com/deviceplug/btleplug), the session, telemetry and the simulator. |
| `third_party/liblsl` | The native `lsl.dll` for Windows, unmodified. |

Measurements are described as data, one router hands each notification to its stream, every control-point response is matched to its request and checked, and connecting, failing and disconnecting all share a single teardown path. The start commands are pinned by tests to the byte sequences known to work against real hardware.

`polarble simulate` encodes genuine PMD frames and sends them through the same pipeline as a real strap, from header parsing to the outlets, so an LSL setup can be tested end to end without hardware.

---

## 📜 Licence

Distributed under the **GNU Lesser General Public License v2.1**. See [LICENSE](LICENSE).

The `lsl.dll` in `third_party/liblsl` is covered by liblsl's own licence; see [its notice](third_party/liblsl/README.md).
