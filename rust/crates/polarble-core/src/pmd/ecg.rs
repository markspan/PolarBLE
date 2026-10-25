//! Decodes the payload of a PMD ECG frame into microvolt samples.
//!
//! Samples are 3-byte little-endian two's-complement integers. The value is already in
//! microvolts; no scaling is applied.

/// The only ECG frame type the H10 emits.
pub const UNCOMPRESSED_FRAME_TYPE: u8 = 0x00;

/// Bytes occupied by one ECG sample.
pub const BYTES_PER_SAMPLE: usize = 3;

/// Number of channels in one ECG sample.
pub const CHANNELS: usize = 1;

/// Whether frames of the given type can be decoded.
#[must_use]
pub fn is_supported(frame_type: u8) -> bool {
    frame_type == UNCOMPRESSED_FRAME_TYPE
}

/// Number of whole samples in a payload. A trailing partial sample is excluded.
#[must_use]
pub fn sample_count(payload: &[u8]) -> usize {
    payload.len() / BYTES_PER_SAMPLE
}

/// Iterates over every complete sample in `payload`, in microvolts.
///
/// A trailing partial sample is ignored, so a ragged payload can never cause a read past its
/// end.
pub fn samples(payload: &[u8]) -> impl ExactSizeIterator<Item = i32> + '_ {
    payload.chunks_exact(BYTES_PER_SAMPLE).map(read_i24)
}

/// Reads a little-endian 24-bit two's-complement integer.
fn read_i24(bytes: &[u8]) -> i32 {
    // Place the three bytes in the top of an i32, then shift back down: the arithmetic shift
    // sign-extends from 24 to 32 bits.
    i32::from_le_bytes([0, bytes[0], bytes[1], bytes[2]]) >> 8
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decode_sign_extends_twenty_four_bit_samples() {
        let cases: [([u8; 3], i32); 6] = [
            ([0x00, 0x00, 0x00], 0),
            ([0x01, 0x00, 0x00], 1),
            ([0xFF, 0xFF, 0xFF], -1),
            ([0x00, 0x00, 0x80], -8_388_608), // most negative 24-bit value
            ([0xFF, 0xFF, 0x7F], 8_388_607),  // most positive 24-bit value
            ([0xD0, 0x07, 0x00], 2000),
        ];

        for (payload, expected) in cases {
            assert_eq!(
                samples(&payload).collect::<Vec<_>>(),
                [expected],
                "{payload:02X?}"
            );
        }
    }

    #[test]
    fn decode_reads_every_sample_in_order() {
        let payload = [0x01, 0x00, 0x00, 0x02, 0x00, 0x00, 0xFF, 0xFF, 0xFF];

        assert_eq!(samples(&payload).collect::<Vec<_>>(), [1, 2, -1]);
    }

    #[test]
    fn decode_ignores_a_trailing_partial_sample() {
        for length in [1, 2, 4, 5] {
            let payload = vec![0; length];

            assert_eq!(sample_count(&payload), length / 3);
            assert_eq!(samples(&payload).len(), length / 3);
        }
    }

    #[test]
    fn only_the_uncompressed_frame_type_is_supported() {
        assert!(is_supported(UNCOMPRESSED_FRAME_TYPE));
        assert!(!is_supported(0x01));
    }
}
