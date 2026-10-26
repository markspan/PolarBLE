use std::fmt;
use std::sync::Arc;

use super::{FrameGapDetector, MeasurementDecoder, StreamStatistics};
use crate::pmd::{FrameHeader, MeasurementType};
use crate::streaming::{SampleSink, SinkError};

/// The complete path for one measurement: decode each frame, check it for gaps, push it to
/// the sink and keep count.
///
/// A stream is driven by `&mut self` from the single task that consumes notifications, so
/// the gap detector always sees frames in arrival order and the decode buffer is reused
/// without any locking. Its [`StreamStatistics`] are shared, so they can be read elsewhere.
#[derive(Debug)]
pub struct MeasurementStream {
    label: String,
    decoder: &'static dyn MeasurementDecoder,
    sink: Box<dyn SampleSink>,
    gap_detector: FrameGapDetector,
    statistics: Arc<StreamStatistics>,
    buffer: Vec<f32>,
}

impl MeasurementStream {
    /// Creates a stream that owns `sink`; the sink is dropped with the stream.
    ///
    /// # Errors
    ///
    /// Returns [`ChannelMismatch`] if the sink and decoder disagree on the channel count.
    ///
    /// # Panics
    ///
    /// Panics if `nominal_sample_rate_hz` is not a positive, finite number.
    pub fn new(
        label: impl Into<String>,
        decoder: &'static dyn MeasurementDecoder,
        nominal_sample_rate_hz: f64,
        sink: Box<dyn SampleSink>,
    ) -> Result<Self, ChannelMismatch> {
        if sink.channel_count() != decoder.channel_count() {
            return Err(ChannelMismatch {
                sink: sink.channel_count(),
                decoder: decoder.channel_count(),
            });
        }

        Ok(Self {
            label: label.into(),
            decoder,
            sink,
            gap_detector: FrameGapDetector::new(nominal_sample_rate_hz),
            statistics: Arc::new(StreamStatistics::new()),
            buffer: Vec::new(),
        })
    }

    /// Short name shown to users, e.g. "ECG".
    #[must_use]
    pub fn label(&self) -> &str {
        &self.label
    }

    /// The measurement this stream carries.
    #[must_use]
    pub fn measurement_type(&self) -> MeasurementType {
        self.decoder.measurement_type()
    }

    /// A shared handle to this stream's live counters.
    #[must_use]
    pub fn statistics(&self) -> Arc<StreamStatistics> {
        Arc::clone(&self.statistics)
    }

    /// Decodes one frame and pushes its samples.
    ///
    /// A frame with an unknown encoding is counted and dropped, never guessed at; that is
    /// not an error.
    ///
    /// # Errors
    ///
    /// Returns [`ProcessError::WrongMeasurement`] for a frame addressed to another
    /// measurement, and [`ProcessError::Sink`] if the sink rejects the samples.
    pub fn process(&mut self, header: &FrameHeader, payload: &[u8]) -> Result<(), ProcessError> {
        if header.measurement_type != self.measurement_type() {
            return Err(ProcessError::WrongMeasurement {
                stream: self.measurement_type(),
                frame: header.measurement_type,
            });
        }

        let Ok(count) = self
            .decoder
            .decode_into(header.frame_type, payload, &mut self.buffer)
        else {
            self.statistics.record_unsupported_frame();
            return Ok(());
        };

        if count == 0 {
            return Ok(());
        }

        let lost = self.gap_detector.observe(header.timestamp_ns, count);
        self.sink
            .push_chunk(&self.buffer)
            .map_err(ProcessError::Sink)?;
        self.statistics.record_frame(count, lost);
        Ok(())
    }
}

/// A sink and a decoder that disagree on how many channels a sample has.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ChannelMismatch {
    /// Channels the sink expects.
    pub sink: usize,
    /// Channels the decoder produces.
    pub decoder: usize,
}

impl fmt::Display for ChannelMismatch {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "the sink has {} channels but the decoder produces {}",
            self.sink, self.decoder
        )
    }
}

impl std::error::Error for ChannelMismatch {}

/// Why a frame could not be processed.
#[derive(Debug)]
pub enum ProcessError {
    /// The frame belongs to a different measurement than the stream it was given to.
    WrongMeasurement {
        /// The stream's measurement.
        stream: MeasurementType,
        /// The frame's measurement.
        frame: MeasurementType,
    },
    /// The sink rejected the samples.
    Sink(SinkError),
}

impl fmt::Display for ProcessError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::WrongMeasurement { stream, frame } => {
                write!(f, "a {frame:?} frame was sent to the {stream:?} stream")
            }
            Self::Sink(error) => write!(f, "the sink rejected a frame: {error}"),
        }
    }
}

impl std::error::Error for ProcessError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::WrongMeasurement { .. } => None,
            Self::Sink(error) => Some(error.as_ref()),
        }
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use std::sync::{Arc, Mutex};

    use super::*;
    use crate::acquisition::{ACC_DECODER, ECG_DECODER};
    use crate::pmd::{acc, ecg, h10};

    /// The chunks a [`RecordingSink`] received, shared with the test that inspects them.
    pub(crate) type Chunks = Arc<Mutex<Vec<Vec<f32>>>>;

    /// Records every chunk pushed to it, in place of a network outlet.
    #[derive(Debug)]
    pub(crate) struct RecordingSink {
        channels: usize,
        chunks: Chunks,
    }

    impl RecordingSink {
        /// Returns the sink and a handle to the chunks it will record.
        pub(crate) fn new(channels: usize) -> (Box<Self>, Chunks) {
            let chunks = Arc::default();
            let sink = Self {
                channels,
                chunks: Arc::clone(&chunks),
            };
            (Box::new(sink), chunks)
        }
    }

    impl SampleSink for RecordingSink {
        fn channel_count(&self) -> usize {
            self.channels
        }

        fn push_chunk(&mut self, interleaved: &[f32]) -> Result<(), SinkError> {
            self.chunks.lock().unwrap().push(interleaved.to_vec());
            Ok(())
        }
    }

    /// Builds a raw PMD data notification.
    pub(crate) fn frame(
        measurement_type: MeasurementType,
        timestamp_ns: u64,
        frame_type: u8,
        payload: &[u8],
    ) -> Vec<u8> {
        let mut frame = vec![measurement_type.into()];
        frame.extend_from_slice(&timestamp_ns.to_le_bytes());
        frame.push(frame_type);
        frame.extend_from_slice(payload);
        frame
    }

    /// An ECG notification of `samples` samples, each equal to its index.
    pub(crate) fn ecg_frame(timestamp_ns: u64, samples: u8) -> Vec<u8> {
        let payload: Vec<u8> = (0..samples).flat_map(|i| [i, 0, 0]).collect();
        frame(
            MeasurementType::Ecg,
            timestamp_ns,
            ecg::UNCOMPRESSED_FRAME_TYPE,
            &payload,
        )
    }

    const ECG_SAMPLE_NS: u64 = 1_000_000_000 / h10::ECG_SAMPLE_RATE_HZ as u64;

    fn ecg_stream() -> (MeasurementStream, Chunks) {
        let (sink, chunks) = RecordingSink::new(1);
        let stream = MeasurementStream::new("ECG", &ECG_DECODER, 130.0, sink).unwrap();
        (stream, chunks)
    }

    fn process(stream: &mut MeasurementStream, notification: &[u8]) -> Result<(), ProcessError> {
        let (header, payload) = FrameHeader::parse(notification).unwrap();
        stream.process(&header, payload)
    }

    #[test]
    fn process_decodes_a_frame_and_pushes_it_as_one_chunk() {
        let (mut stream, chunks) = ecg_stream();

        process(&mut stream, &ecg_frame(1, 3)).unwrap();

        assert_eq!(*chunks.lock().unwrap(), [vec![0.0, 1.0, 2.0]]);
        assert_eq!(stream.statistics().snapshot().samples, 3);
    }

    #[test]
    fn process_decodes_accelerometer_axes_interleaved() {
        let (sink, chunks) = RecordingSink::new(3);
        let mut stream = MeasurementStream::new("ACC", &ACC_DECODER, 200.0, sink).unwrap();

        let payload = [0x01, 0x00, 0xFF, 0xFF, 0x10, 0x00];
        process(
            &mut stream,
            &frame(
                MeasurementType::Acc,
                1,
                acc::UNCOMPRESSED_FRAME_TYPE,
                &payload,
            ),
        )
        .unwrap();

        assert_eq!(*chunks.lock().unwrap(), [vec![1.0, -1.0, 16.0]]);
    }

    #[test]
    fn process_counts_an_unsupported_frame_type_without_pushing_anything() {
        let (mut stream, chunks) = ecg_stream();

        process(
            &mut stream,
            &frame(MeasurementType::Ecg, 1, 0x7F, &[1, 2, 3]),
        )
        .unwrap();

        assert!(chunks.lock().unwrap().is_empty());
        assert_eq!(stream.statistics().snapshot().unsupported_frames, 1);
    }

    #[test]
    fn process_records_a_gap_when_a_notification_is_lost() {
        let (mut stream, chunks) = ecg_stream();

        process(&mut stream, &ecg_frame(10 * ECG_SAMPLE_NS, 10)).unwrap();
        process(&mut stream, &ecg_frame(30 * ECG_SAMPLE_NS, 10)).unwrap();

        let snapshot = stream.statistics().snapshot();
        assert_eq!(snapshot.gaps, 1);
        assert_eq!(snapshot.samples_lost, 10);

        // Diagnostics never alter the data: both frames are still delivered.
        assert_eq!(chunks.lock().unwrap().len(), 2);
    }

    #[test]
    fn process_rejects_a_frame_for_another_measurement() {
        let (mut stream, _) = ecg_stream();
        let notification = frame(MeasurementType::Acc, 1, acc::UNCOMPRESSED_FRAME_TYPE, &[]);

        assert!(matches!(
            process(&mut stream, &notification),
            Err(ProcessError::WrongMeasurement { .. })
        ));
    }

    #[test]
    fn new_rejects_a_sink_with_the_wrong_channel_count() {
        let (sink, _) = RecordingSink::new(3);

        assert_eq!(
            MeasurementStream::new("ECG", &ECG_DECODER, 130.0, sink).unwrap_err(),
            ChannelMismatch {
                sink: 3,
                decoder: 1
            },
        );
    }
}
