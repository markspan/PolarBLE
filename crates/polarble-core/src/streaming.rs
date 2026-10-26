//! Outgoing streams: what they are called, what their channels mean, and where the samples
//! go.

use std::error::Error;
use std::fmt::Debug;

/// One channel of an outgoing stream.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ChannelDefinition {
    /// Channel name, e.g. `ECG` or `X`.
    pub label: String,
    /// Physical unit, e.g. `microvolts` or `milli-g`.
    pub unit: String,
    /// Channel type, e.g. `ECG` or `AccelerometerX`.
    pub channel_type: String,
}

impl ChannelDefinition {
    /// Creates a channel definition.
    #[must_use]
    pub fn new(label: &str, unit: &str, channel_type: &str) -> Self {
        Self {
            label: label.to_owned(),
            unit: unit.to_owned(),
            channel_type: channel_type.to_owned(),
        }
    }
}

/// Everything needed to advertise a stream on the network, including the metadata a
/// downstream analyst needs in order to interpret the numbers years later.
#[derive(Clone, Debug, PartialEq)]
pub struct StreamDefinition {
    /// Stream name as it appears to consumers.
    pub name: String,
    /// Content type, following the XDF vocabulary where one exists.
    pub stream_type: String,
    /// Stable identifier for this data source. It must survive an application restart so that
    /// a recorder can re-bind to the same physical strap after a dropout, and it must differ
    /// between two straps recorded simultaneously.
    pub source_id: String,
    /// Nominal sample rate in hertz.
    pub sample_rate_hz: f64,
    /// The channels, in the order they are interleaved in a pushed chunk.
    pub channels: Vec<ChannelDefinition>,
    /// Device manufacturer, recorded in the acquisition metadata block.
    pub manufacturer: String,
    /// Device model, recorded in the acquisition metadata block.
    pub model: String,
    /// Bluetooth address of the strap, recorded in the acquisition metadata block.
    pub device_address: String,
    /// Additional acquisition metadata, such as the exact PMD settings the sensor was asked
    /// for, in the order it should be written.
    pub acquisition: Vec<(String, String)>,
}

/// The error type a sink reports a failed push with.
pub type SinkError = Box<dyn Error + Send + Sync>;

/// Destination for decoded samples.
///
/// The abstraction keeps the acquisition pipeline free of liblsl, so that it can be exercised
/// in tests without opening a network outlet. A sink is owned by exactly one measurement
/// stream, which is why pushing takes `&mut self`.
pub trait SampleSink: Debug + Send {
    /// Number of channels each sample carries.
    fn channel_count(&self) -> usize;

    /// Pushes one frame of channel-interleaved samples.
    ///
    /// The length of `interleaved` is a whole multiple of [`SampleSink::channel_count`]. No
    /// timestamp is supplied: an LSL-backed sink lets liblsl stamp the chunk with the time it
    /// is pushed and derive the earlier samples from the stream's nominal sample rate.
    ///
    /// # Errors
    ///
    /// Returns whatever the underlying transport reports.
    fn push_chunk(&mut self, interleaved: &[f32]) -> Result<(), SinkError>;
}
