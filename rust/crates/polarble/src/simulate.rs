//! A synthetic Polar H10, for testing an LSL recording setup without a strap.
//!
//! The simulator encodes genuine PMD notifications and feeds them through the same
//! [`PmdDataRouter`] a real session uses, so everything from header parsing to the outlets
//! is exercised; only the radio is missing.

use std::f64::consts::TAU;
use std::sync::Arc;
use std::time::{Duration, SystemTime};

use anyhow::{Context, Result};
use polarble_core::acquisition::PmdDataRouter;
use polarble_core::pmd::{MeasurementType, acc, ecg, h10};
use polarble_lsl::{Liblsl, LslOutlet};
use tokio::time::{MissedTickBehavior, interval};

use crate::session::StreamHandle;
use crate::task::BackgroundTask;

/// The simulated strap's name; the streams are called `Polar H10 SIMULATED_ECG` and `_ACC`.
pub(crate) const DEVICE_NAME: &str = "Polar H10 SIMULATED";

/// Stands in for the Bluetooth address in the source ids, so they never collide with a real
/// strap's.
const DEVICE_ADDRESS: &str = "SIMULATED";

/// ECG samples per notification, as a real H10 sends them at its default MTU.
const ECG_SAMPLES_PER_FRAME: u64 = 73;

/// Uncompressed accelerometer samples per notification.
const ACC_SAMPLES_PER_FRAME: u64 = 36;

/// Seconds from the Unix epoch to the Polar epoch, 2000-01-01T00:00:00Z.
const POLAR_EPOCH_UNIX_SECONDS: u64 = 946_684_800;

/// A running simulation: the outlets are advertised until [`Simulation::stop`].
pub(crate) struct Simulation {
    streams: Vec<StreamHandle>,
    generator: BackgroundTask,
}

impl Simulation {
    /// Advertises the simulated streams and starts producing frames in real time.
    pub(crate) fn start(liblsl: &Arc<Liblsl>) -> Result<Self> {
        let mut streams = Vec::new();
        let mut handles = Vec::new();
        for measurement in h10::measurements(DEVICE_NAME, DEVICE_ADDRESS) {
            let outlet = LslOutlet::new(liblsl, &measurement.stream)
                .with_context(|| format!("could not create the {} outlet", measurement.label))?;
            let stream = measurement.create_stream(Box::new(outlet))?;
            handles.push((measurement.label, stream.statistics()));
            streams.push(stream);
        }
        let mut router = PmdDataRouter::new(streams)?;

        let generator = BackgroundTask::spawn(move |mut shutdown| async move {
            let start_ns = polar_now_ns();
            let mut ecg = SyntheticStream::ecg(start_ns);
            let mut acc = SyntheticStream::acc(start_ns);
            let mut ecg_ticks = ecg.ticker();
            let mut acc_ticks = acc.ticker();

            loop {
                let frame = tokio::select! {
                    _ = &mut shutdown => return,
                    _ = ecg_ticks.tick() => ecg.next_frame(),
                    _ = acc_ticks.tick() => acc.next_frame(),
                };

                if let Err(error) = router.route(&frame) {
                    eprintln!("warning: {error}");
                }
            }
        });

        Ok(Self {
            streams: handles,
            generator,
        })
    }

    /// The simulated streams.
    pub(crate) fn streams(&self) -> &[StreamHandle] {
        &self.streams
    }

    /// Stops producing frames and closes the outlets.
    pub(crate) async fn stop(self) {
        self.generator.stop().await;
    }
}

/// One simulated measurement, producing consecutive PMD frames.
struct SyntheticStream {
    measurement_type: MeasurementType,
    frame_type: u8,
    sample_rate_hz: u16,
    samples_per_frame: u64,
    start_ns: u64,
    next_sample: u64,
    encode_sample: fn(f64, &mut Vec<u8>),
}

impl SyntheticStream {
    fn ecg(start_ns: u64) -> Self {
        Self {
            measurement_type: MeasurementType::Ecg,
            frame_type: ecg::UNCOMPRESSED_FRAME_TYPE,
            sample_rate_hz: h10::ECG_SAMPLE_RATE_HZ,
            samples_per_frame: ECG_SAMPLES_PER_FRAME,
            start_ns,
            next_sample: 0,
            encode_sample: |seconds, payload| {
                payload.extend_from_slice(&ecg_microvolts(seconds).to_le_bytes()[..3]);
            },
        }
    }

    fn acc(start_ns: u64) -> Self {
        Self {
            measurement_type: MeasurementType::Acc,
            frame_type: acc::UNCOMPRESSED_FRAME_TYPE,
            sample_rate_hz: h10::ACC_SAMPLE_RATE_HZ,
            samples_per_frame: ACC_SAMPLES_PER_FRAME,
            start_ns,
            next_sample: 0,
            encode_sample: |seconds, payload| {
                for axis in acc_milli_g(seconds) {
                    payload.extend_from_slice(&axis.to_le_bytes());
                }
            },
        }
    }

    /// Ticks once per frame, at the real cadence of this measurement.
    fn ticker(&self) -> tokio::time::Interval {
        let frame = Duration::from_secs(self.samples_per_frame) / u32::from(self.sample_rate_hz);
        let mut ticker = interval(frame);
        // Catch up after a hiccup, as the strap's own buffering would.
        ticker.set_missed_tick_behavior(MissedTickBehavior::Burst);
        ticker
    }

    /// Encodes the next frame, stamped like a real one with the time of its last sample.
    fn next_frame(&mut self) -> Vec<u8> {
        let rate = f64::from(self.sample_rate_hz);
        let mut payload = Vec::new();
        for index in self.next_sample..self.next_sample + self.samples_per_frame {
            #[expect(
                clippy::cast_precision_loss,
                reason = "a sample index stays far below 2^52"
            )]
            (self.encode_sample)(index as f64 / rate, &mut payload);
        }
        self.next_sample += self.samples_per_frame;

        let last_sample = self.next_sample - 1;
        let timestamp_ns =
            self.start_ns + last_sample * 1_000_000_000 / u64::from(self.sample_rate_hz);
        encode_frame(
            self.measurement_type,
            timestamp_ns,
            self.frame_type,
            &payload,
        )
    }
}

/// A plausible ECG at 60 beats per minute: an R peak and a T wave each second, in microvolts.
#[expect(
    clippy::cast_possible_truncation,
    reason = "the waveform stays within a few millivolts"
)]
fn ecg_microvolts(seconds: f64) -> i32 {
    let phase = seconds.fract();
    let bump = |centre: f64, width: f64, height: f64| {
        height * (-((phase - centre) / width).powi(2) / 2.0).exp()
    };
    (bump(0.20, 0.012, 1200.0) - bump(0.23, 0.010, 250.0) + bump(0.50, 0.050, 300.0)).round() as i32
}

/// A strap lying flat with a slow breathing sway, in milli-g.
#[expect(
    clippy::cast_possible_truncation,
    reason = "the sway stays within ±60 milli-g"
)]
fn acc_milli_g(seconds: f64) -> [i16; acc::AXES] {
    let sway = (60.0 * (TAU * 0.25 * seconds).sin()).round() as i16;
    [sway, 0, 1000]
}

/// Builds a PMD data notification: measurement type, timestamp, frame type, then the payload.
fn encode_frame(
    measurement_type: MeasurementType,
    timestamp_ns: u64,
    frame_type: u8,
    payload: &[u8],
) -> Vec<u8> {
    let mut frame = Vec::with_capacity(10 + payload.len());
    frame.push(measurement_type.into());
    frame.extend_from_slice(&timestamp_ns.to_le_bytes());
    frame.push(frame_type);
    frame.extend_from_slice(payload);
    frame
}

/// The current time in nanoseconds since the Polar epoch.
fn polar_now_ns() -> u64 {
    let since_unix = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default();
    let since_polar = since_unix.saturating_sub(Duration::from_secs(POLAR_EPOCH_UNIX_SECONDS));
    u64::try_from(since_polar.as_nanos()).unwrap_or(u64::MAX)
}

#[cfg(test)]
mod tests {
    use polarble_core::acquisition::{ACC_DECODER, ECG_DECODER};
    use polarble_core::acquisition::{FrameGapDetector, MeasurementDecoder};
    use polarble_core::pmd::FrameHeader;

    use super::*;

    #[test]
    #[expect(clippy::float_cmp, reason = "integers decoded into an f32 are exact")]
    fn simulated_ecg_frames_decode_to_the_generated_signal() {
        let mut stream = SyntheticStream::ecg(0);
        let frame = stream.next_frame();

        let (header, payload) = FrameHeader::parse(&frame).unwrap();
        let mut values = Vec::new();
        let count = ECG_DECODER
            .decode_into(header.frame_type, payload, &mut values)
            .unwrap();

        assert_eq!(header.measurement_type, MeasurementType::Ecg);
        assert_eq!(count, 73);
        #[expect(clippy::cast_precision_loss, reason = "test values are small")]
        let expected = ecg_microvolts(26.0 / 130.0) as f32;
        assert_eq!(values[26], expected, "sample 26 is at the R peak");
    }

    #[test]
    fn simulated_accelerometer_frames_decode_to_the_generated_signal() {
        let mut stream = SyntheticStream::acc(0);
        let frame = stream.next_frame();

        let (header, payload) = FrameHeader::parse(&frame).unwrap();
        let mut values = Vec::new();
        let count = ACC_DECODER
            .decode_into(header.frame_type, payload, &mut values)
            .unwrap();

        assert_eq!(count, 36);
        assert_eq!(values[..3], [0.0, 0.0, 1000.0]);
    }

    #[test]
    fn consecutive_simulated_frames_show_no_gaps() {
        let mut stream = SyntheticStream::ecg(0);
        let mut detector = FrameGapDetector::new(130.0);

        for _ in 0..100 {
            let frame = stream.next_frame();
            let (header, _) = FrameHeader::parse(&frame).unwrap();
            assert_eq!(detector.observe(header.timestamp_ns, 73), 0);
        }
    }
}
