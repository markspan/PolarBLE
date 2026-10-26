use super::{ChannelMismatch, MeasurementDecoder, MeasurementStream};
use crate::pmd::MeasurementType;
use crate::pmd::control_point::build_stop;
use crate::streaming::{SampleSink, StreamDefinition};

/// Everything needed to acquire one measurement: how to start and stop it on the strap, how
/// to decode its frames, and how to advertise the resulting stream.
///
/// Describing measurements as data lets a session start, route and stop any number of them
/// through one code path instead of repeating the same steps per measurement.
#[derive(Clone, Debug)]
pub struct MeasurementConfiguration {
    /// Short name shown in telemetry, e.g. "ECG" or "ACC".
    pub label: &'static str,
    /// Name used in sentences, e.g. "ECG" or "accelerometer".
    pub display_name: &'static str,
    /// The start-measurement request to write to the control point.
    pub start_command: Vec<u8>,
    /// Decodes this measurement's frames.
    pub decoder: &'static dyn MeasurementDecoder,
    /// The stream that carries the decoded samples.
    pub stream: StreamDefinition,
}

impl MeasurementConfiguration {
    /// The measurement being configured.
    #[must_use]
    pub fn measurement_type(&self) -> MeasurementType {
        self.decoder.measurement_type()
    }

    /// The stop-measurement request for this measurement.
    #[must_use]
    pub fn stop_command(&self) -> [u8; 2] {
        build_stop(self.measurement_type())
    }

    /// Creates the stream that acquires this measurement into `sink`.
    ///
    /// # Errors
    ///
    /// Returns [`ChannelMismatch`] if the sink's channel count does not fit the decoder.
    pub fn create_stream(
        &self,
        sink: Box<dyn SampleSink>,
    ) -> Result<MeasurementStream, ChannelMismatch> {
        MeasurementStream::new(self.label, self.decoder, self.stream.sample_rate_hz, sink)
    }
}
