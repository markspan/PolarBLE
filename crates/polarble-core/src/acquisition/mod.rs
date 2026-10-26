//! The platform-free data path from a raw PMD notification to samples in a sink.
//!
//! A [`PmdDataRouter`] reads each notification's header and hands the frame to the
//! [`MeasurementStream`] for its measurement. The stream decodes it with its
//! [`MeasurementDecoder`], checks it for lost frames with a [`FrameGapDetector`], pushes the
//! samples to its sink and updates its [`StreamStatistics`]. A [`MeasurementConfiguration`]
//! describes one measurement as data, so any number of them share this one path.
//!
//! ```
//! use polarble_core::acquisition::{PmdDataRouter, Routed};
//! use polarble_core::pmd::h10;
//! use polarble_core::streaming::{SampleSink, SinkError};
//!
//! /// A sink that discards everything, standing in for an LSL outlet.
//! #[derive(Debug)]
//! struct Discard(usize);
//!
//! impl SampleSink for Discard {
//!     fn channel_count(&self) -> usize {
//!         self.0
//!     }
//!
//!     fn push_chunk(&mut self, _interleaved: &[f32]) -> Result<(), SinkError> {
//!         Ok(())
//!     }
//! }
//!
//! let streams = h10::measurements("Polar H10 1234", "A0B1C2D3E4F5")
//!     .iter()
//!     .map(|measurement| measurement.create_stream(Box::new(Discard(measurement.stream.channels.len()))))
//!     .collect::<Result<Vec<_>, _>>()?;
//! let mut router = PmdDataRouter::new(streams)?;
//!
//! // An ECG frame: measurement type, 8-byte timestamp, frame type, then two 24-bit samples.
//! let notification = [0x00, 1, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x10, 0x00, 0x00, 0xF0, 0xFF, 0xFF];
//!
//! assert_eq!(router.route(&notification)?, Routed::Processed);
//! assert_eq!(router.streams()[0].statistics().snapshot().samples, 2);
//! # Ok::<(), Box<dyn std::error::Error>>(())
//! ```

mod configuration;
mod decoder;
mod gap_detector;
mod router;
mod statistics;
mod stream;

pub use configuration::MeasurementConfiguration;
pub use decoder::{ACC_DECODER, AccDecoder, ECG_DECODER, EcgDecoder, MeasurementDecoder};
pub use gap_detector::FrameGapDetector;
pub use router::{DuplicateMeasurement, PmdDataRouter, Routed};
pub use statistics::{StatisticsSnapshot, StreamStatistics};
pub use stream::{ChannelMismatch, MeasurementStream, ProcessError};
