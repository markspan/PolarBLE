use std::fmt;

use super::{MeasurementStream, ProcessError};
use crate::pmd::{FrameHeader, MeasurementType};

/// Dispatches raw notifications from the PMD data characteristic to the stream that owns
/// their measurement type.
///
/// All measurements share one characteristic, so the router reads each header once and hands
/// the frame to exactly one stream.
#[derive(Debug)]
pub struct PmdDataRouter {
    streams: Vec<MeasurementStream>,
    ignored_notifications: u64,
}

/// What became of a routed notification.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Routed {
    /// A stream processed the frame.
    Processed,
    /// The notification was too short to be a frame, or no stream carries its measurement.
    Ignored,
}

impl PmdDataRouter {
    /// Creates a router over the given streams.
    ///
    /// # Errors
    ///
    /// Returns [`DuplicateMeasurement`] if two streams carry the same measurement type.
    pub fn new(streams: Vec<MeasurementStream>) -> Result<Self, DuplicateMeasurement> {
        for (index, stream) in streams.iter().enumerate() {
            let measurement_type = stream.measurement_type();
            if streams[..index]
                .iter()
                .any(|earlier| earlier.measurement_type() == measurement_type)
            {
                return Err(DuplicateMeasurement(measurement_type));
            }
        }

        Ok(Self {
            streams,
            ignored_notifications: 0,
        })
    }

    /// The streams, in the order they were given.
    #[must_use]
    pub fn streams(&self) -> &[MeasurementStream] {
        &self.streams
    }

    /// Number of notifications that were too short or had no matching stream.
    #[must_use]
    pub fn ignored_notifications(&self) -> u64 {
        self.ignored_notifications
    }

    /// Routes one notification.
    ///
    /// # Errors
    ///
    /// Returns the [`ProcessError`] of the stream the frame was routed to.
    pub fn route(&mut self, notification: &[u8]) -> Result<Routed, ProcessError> {
        let Some((header, payload)) = FrameHeader::parse(notification) else {
            return Ok(self.ignore());
        };

        let Some(stream) = self
            .streams
            .iter_mut()
            .find(|stream| stream.measurement_type() == header.measurement_type)
        else {
            return Ok(self.ignore());
        };

        stream.process(&header, payload)?;
        Ok(Routed::Processed)
    }

    fn ignore(&mut self) -> Routed {
        self.ignored_notifications += 1;
        Routed::Ignored
    }
}

/// Two streams were registered for the same measurement.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DuplicateMeasurement(pub MeasurementType);

impl fmt::Display for DuplicateMeasurement {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "more than one stream was registered for {:?}", self.0)
    }
}

impl std::error::Error for DuplicateMeasurement {}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::acquisition::stream::tests::{RecordingSink, ecg_frame, frame};
    use crate::acquisition::{ACC_DECODER, ECG_DECODER};
    use crate::pmd::acc;

    #[test]
    fn route_sends_each_frame_only_to_its_own_stream() {
        let (ecg_sink, ecg_chunks) = RecordingSink::new(1);
        let (acc_sink, acc_chunks) = RecordingSink::new(3);
        let mut router = PmdDataRouter::new(vec![
            MeasurementStream::new("ECG", &ECG_DECODER, 130.0, ecg_sink).unwrap(),
            MeasurementStream::new("ACC", &ACC_DECODER, 200.0, acc_sink).unwrap(),
        ])
        .unwrap();

        assert_eq!(router.route(&ecg_frame(1, 2)).unwrap(), Routed::Processed);
        let acc_frame = frame(
            MeasurementType::Acc,
            1,
            acc::UNCOMPRESSED_FRAME_TYPE,
            &[0; 6],
        );
        assert_eq!(router.route(&acc_frame).unwrap(), Routed::Processed);

        assert_eq!(ecg_chunks.lock().unwrap().len(), 1);
        assert_eq!(acc_chunks.lock().unwrap().len(), 1);
    }

    #[test]
    fn route_ignores_a_notification_shorter_than_a_header() {
        let mut router = PmdDataRouter::new(Vec::new()).unwrap();

        assert_eq!(
            router.route(&[0; FrameHeader::LEN - 1]).unwrap(),
            Routed::Ignored
        );
        assert_eq!(router.ignored_notifications(), 1);
    }

    #[test]
    fn route_ignores_a_measurement_with_no_stream() {
        let (sink, chunks) = RecordingSink::new(1);
        let mut router = PmdDataRouter::new(vec![
            MeasurementStream::new("ECG", &ECG_DECODER, 130.0, sink).unwrap(),
        ])
        .unwrap();

        let ppg = frame(MeasurementType::Ppg, 1, 0x00, &[1, 2, 3]);
        assert_eq!(router.route(&ppg).unwrap(), Routed::Ignored);
        assert!(chunks.lock().unwrap().is_empty());
    }

    #[test]
    fn new_rejects_two_streams_for_one_measurement() {
        let stream = || {
            let (sink, _) = RecordingSink::new(1);
            MeasurementStream::new("ECG", &ECG_DECODER, 130.0, sink).unwrap()
        };

        assert_eq!(
            PmdDataRouter::new(vec![stream(), stream()]).unwrap_err(),
            DuplicateMeasurement(MeasurementType::Ecg),
        );
    }
}
