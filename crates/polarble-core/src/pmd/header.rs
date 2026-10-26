use std::time::{Duration, SystemTime};

use super::MeasurementType;

/// Seconds from the Unix epoch to the Polar epoch, 2000-01-01T00:00:00Z.
const POLAR_EPOCH_UNIX_SECONDS: u64 = 946_684_800;

/// The ten-byte header that opens every PMD data frame.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FrameHeader {
    /// Which measurement this frame carries.
    pub measurement_type: MeasurementType,
    /// Device timestamp of the last sample in the frame, in nanoseconds since the Polar epoch
    /// (2000-01-01).
    pub timestamp_ns: u64,
    /// Encoding of the payload. Its meaning is measurement-specific: for ECG, 0x00 is
    /// uncompressed; for the accelerometer, 0x01 is uncompressed and 0x02 is delta-compressed.
    pub frame_type: u8,
}

impl FrameHeader {
    /// Length of the header in bytes.
    pub const LEN: usize = 10;

    /// Splits a raw notification into its header and payload.
    ///
    /// Returns `None` if the notification is too short to be a PMD frame.
    #[must_use]
    pub fn parse(frame: &[u8]) -> Option<(Self, &[u8])> {
        let (header, payload) = frame.split_at_checked(Self::LEN)?;
        let mut timestamp = [0; 8];
        timestamp.copy_from_slice(&header[1..9]);

        let header = Self {
            measurement_type: MeasurementType::from(header[0]),
            timestamp_ns: u64::from_le_bytes(timestamp),
            frame_type: header[9],
        };
        Some((header, payload))
    }

    /// The device timestamp as wall-clock time.
    #[must_use]
    pub fn device_time(&self) -> SystemTime {
        SystemTime::UNIX_EPOCH
            + Duration::from_secs(POLAR_EPOCH_UNIX_SECONDS)
            + Duration::from_nanos(self.timestamp_ns)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_splits_header_from_payload() {
        let frame = [
            0x02, // measurement type: ACC
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // timestamp = 1 ns
            0x01, // frame type: uncompressed
            0xAA, 0xBB, // payload
        ];

        let (header, payload) = FrameHeader::parse(&frame).unwrap();
        assert_eq!(header.measurement_type, MeasurementType::Acc);
        assert_eq!(header.timestamp_ns, 1);
        assert_eq!(header.frame_type, 0x01);
        assert_eq!(payload, [0xAA, 0xBB]);
    }

    #[test]
    fn parse_reads_the_timestamp_as_little_endian_nanoseconds() {
        let frame = [0x00, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01, 0x00];

        let (header, payload) = FrameHeader::parse(&frame).unwrap();
        assert_eq!(header.timestamp_ns, 0x0102_0304_0506_0708);
        assert!(payload.is_empty());
    }

    #[test]
    fn device_time_counts_from_the_polar_epoch() {
        let (header, _) = FrameHeader::parse(&[0; FrameHeader::LEN]).unwrap();

        let since_unix = header
            .device_time()
            .duration_since(SystemTime::UNIX_EPOCH)
            .unwrap();
        assert_eq!(since_unix, Duration::from_secs(946_684_800));
    }

    #[test]
    fn parse_rejects_a_frame_shorter_than_the_header() {
        assert!(FrameHeader::parse(&[]).is_none());
        assert!(FrameHeader::parse(&[0; 9]).is_none());
    }
}
