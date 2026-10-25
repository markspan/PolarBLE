# PolarBLE for Rust

A Rust implementation of PolarBLE that runs alongside the .NET one. It streams a **Polar H10**'s raw ECG and accelerometer data to **LabStreamingLayer (LSL)**, and runs on **Windows, Linux and macOS**.

It is a command-line tool rather than a window, which suits unattended or headless recording. The streams it produces are interchangeable with the .NET application's: same names, same channel layout and metadata, and the same `source_id` for the same strap, so a recorder re-binds to a strap whichever implementation is streaming it.

---

## 🚀 Quickstart

```
polarble scan                      # list the straps in range
polarble stream                    # connect to the only strap in range and stream
polarble stream --device 1A2B3C4D  # pick a strap by part of its name or address
polarble simulate                  # synthetic strap, to test an LSL setup without hardware
```

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

## 🔨 Building

Requires Rust 1.88 or newer. On Linux, btleplug also needs the D-Bus headers (`sudo apt install libdbus-1-dev pkg-config` on Debian and Ubuntu).

```
cd rust
cargo build --release
cargo test
```

The liblsl tests need the native library, so they are ignored by default:

```
POLARBLE_LSL_LIBRARY=/path/to/liblsl.so cargo test -p polarble-lsl -- --include-ignored
```

### Workspace layout

| Crate | Purpose | .NET counterpart |
|---|---|---|
| `polarble-core` | The PMD protocol codec and the acquisition pipeline: routing, decoding, gap detection, statistics. No dependencies and no `unsafe`. | `PolarBLE.Core` |
| `polarble-lsl` | A minimal liblsl binding, loaded at run time, implementing the sample sink. All of the workspace's `unsafe` code lives here. | `third_party/liblsl` and `LslSampleSink` |
| `polarble` | The command-line tool: Bluetooth through [btleplug](https://github.com/deviceplug/btleplug), the session, telemetry and the simulator. | `PolarBLE.App` |

The two implementations share their design: measurements are described as data, one router hands each notification to its stream, every control-point response is matched to its request and checked, and connecting, failing and disconnecting all share a single teardown path. The core tests mirror the .NET suite, and both pin the start commands to the same byte sequences known to work against real hardware.

### What differs from .NET

- **Cross-platform Bluetooth.** btleplug uses WinRT on Windows, BlueZ on Linux and CoreBluetooth on macOS. macOS hides Bluetooth addresses, so there the stable per-machine peripheral id takes the address's place in the `source_id`.
- **Ownership instead of locks.** One task owns the router, and with it every stream and outlet, so decoding needs no locking; only the statistics are shared.
- **Iterators instead of buffers.** The decoders yield samples, so a destination buffer that is too small cannot exist.
- **A simulator.** `polarble simulate` encodes genuine PMD frames and sends them through the same pipeline as a real strap, from header parsing to the outlets, so an LSL setup can be tested end to end without hardware.
