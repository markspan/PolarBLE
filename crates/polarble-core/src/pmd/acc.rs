//! Decodes the payload of a PMD accelerometer frame into milli-g samples.
//!
//! Two encodings exist and they are distinguished by the frame type, not by a resolution
//! field. Frame type 0x01 is uncompressed: six bytes per sample, three little-endian 16-bit
//! axes. Frame type 0x02 is delta-compressed: an uncompressed reference sample, then a series
//! of blocks, each introduced by a delta bit width and a sample count, whose bit-packed
//! signed deltas accumulate onto the running value.
//!
//! Malformed input is never guessed at. A truncated or impossible delta block ends decoding
//! at the last complete sample, and an unknown frame type is rejected outright.

use std::iter::FusedIterator;
use std::slice::ChunksExact;

use super::UnsupportedFrameType;

/// Uncompressed frame: three 16-bit axes per sample.
pub const UNCOMPRESSED_FRAME_TYPE: u8 = 0x01;

/// Delta-compressed frame: a reference sample followed by packed deltas.
pub const DELTA_FRAME_TYPE: u8 = 0x02;

/// Number of axes in one accelerometer sample.
pub const AXES: usize = 3;

/// Bytes occupied by one uncompressed sample.
pub const UNCOMPRESSED_BYTES_PER_SAMPLE: usize = AXES * size_of::<i16>();

/// The widest delta field this decoder accepts; anything wider is malformed.
pub const MAX_DELTA_BIT_WIDTH: u32 = 32;

/// Length of a delta block header: the bit width, then the sample count.
const BLOCK_HEADER_LEN: usize = 2;

/// One accelerometer sample: x, y and z in milli-g.
pub type Sample = [i16; AXES];

/// Whether frames of the given type can be decoded.
#[must_use]
pub fn is_supported(frame_type: u8) -> bool {
    matches!(frame_type, UNCOMPRESSED_FRAME_TYPE | DELTA_FRAME_TYPE)
}

/// Counts the samples a payload will decode to, without decoding them.
///
/// # Errors
///
/// Returns [`UnsupportedFrameType`] if `frame_type` is unknown.
pub fn sample_count(frame_type: u8, payload: &[u8]) -> Result<usize, UnsupportedFrameType> {
    match frame_type {
        UNCOMPRESSED_FRAME_TYPE => Ok(payload.len() / UNCOMPRESSED_BYTES_PER_SAMPLE),
        DELTA_FRAME_TYPE => Ok(
            match payload.split_at_checked(UNCOMPRESSED_BYTES_PER_SAMPLE) {
                // The reference sample is itself an output sample.
                Some((_, blocks)) => {
                    1 + DeltaBlocks::new(blocks)
                        .map(|block| block.sample_count)
                        .sum::<usize>()
                }
                None => 0,
            },
        ),
        other => Err(UnsupportedFrameType(other)),
    }
}

/// Iterates over the samples of a payload.
///
/// # Errors
///
/// Returns [`UnsupportedFrameType`] if `frame_type` is unknown.
pub fn samples(frame_type: u8, payload: &[u8]) -> Result<Samples<'_>, UnsupportedFrameType> {
    let encoding = match frame_type {
        UNCOMPRESSED_FRAME_TYPE => {
            Encoding::Uncompressed(payload.chunks_exact(UNCOMPRESSED_BYTES_PER_SAMPLE))
        }
        DELTA_FRAME_TYPE => Encoding::Delta(DeltaSamples::new(payload)),
        other => return Err(UnsupportedFrameType(other)),
    };
    Ok(Samples(encoding))
}

/// The samples of one accelerometer frame, created by [`samples`].
#[derive(Clone, Debug)]
pub struct Samples<'a>(Encoding<'a>);

#[derive(Clone, Debug)]
enum Encoding<'a> {
    Uncompressed(ChunksExact<'a, u8>),
    Delta(DeltaSamples<'a>),
}

impl Iterator for Samples<'_> {
    type Item = Sample;

    fn next(&mut self) -> Option<Sample> {
        match &mut self.0 {
            Encoding::Uncompressed(chunks) => chunks.next().map(read_sample),
            Encoding::Delta(deltas) => deltas.next(),
        }
    }
}

impl FusedIterator for Samples<'_> {}

/// Reads one uncompressed sample of three little-endian 16-bit axes.
fn read_sample(bytes: &[u8]) -> Sample {
    std::array::from_fn(|axis| i16::from_le_bytes([bytes[2 * axis], bytes[2 * axis + 1]]))
}

/// Decodes a delta-compressed frame sample by sample.
#[derive(Clone, Debug)]
struct DeltaSamples<'a> {
    /// The running value, held at full width so that no delta can wrap it.
    current: [i32; AXES],
    reference_pending: bool,
    blocks: DeltaBlocks<'a>,
    block: Option<BlockCursor<'a>>,
}

impl<'a> DeltaSamples<'a> {
    fn new(payload: &'a [u8]) -> Self {
        match payload.split_at_checked(UNCOMPRESSED_BYTES_PER_SAMPLE) {
            Some((reference, blocks)) => Self {
                current: read_sample(reference).map(i32::from),
                reference_pending: true,
                blocks: DeltaBlocks::new(blocks),
                block: None,
            },
            None => Self {
                current: [0; AXES],
                reference_pending: false,
                blocks: DeltaBlocks::new(&[]),
                block: None,
            },
        }
    }

    fn current_sample(&self) -> Sample {
        // The running value is clamped to the i16 range on every step, so this cannot fail.
        self.current
            .map(|axis| i16::try_from(axis).expect("running value is clamped to i16"))
    }
}

impl Iterator for DeltaSamples<'_> {
    type Item = Sample;

    fn next(&mut self) -> Option<Sample> {
        if self.reference_pending {
            self.reference_pending = false;
            return Some(self.current_sample());
        }

        loop {
            if let Some(cursor) = &mut self.block
                && cursor.remaining > 0
            {
                cursor.remaining -= 1;
                for axis in &mut self.current {
                    let delta = cursor.bits.read_signed(cursor.bit_width);
                    *axis = axis
                        .saturating_add(delta)
                        .clamp(i16::MIN.into(), i16::MAX.into());
                }
                return Some(self.current_sample());
            }

            self.block = Some(BlockCursor::new(self.blocks.next()?));
        }
    }
}

/// Progress through the deltas of one block.
#[derive(Clone, Debug)]
struct BlockCursor<'a> {
    bits: BitReader<'a>,
    bit_width: u32,
    remaining: usize,
}

impl<'a> BlockCursor<'a> {
    fn new(block: DeltaBlock<'a>) -> Self {
        Self {
            bits: BitReader::new(block.packed_deltas),
            bit_width: block.bit_width,
            remaining: block.sample_count,
        }
    }
}

/// One block of a delta-compressed frame.
#[derive(Clone, Copy, Debug)]
struct DeltaBlock<'a> {
    /// Width in bits of every delta in the block.
    bit_width: u32,
    /// Number of samples, each carrying one delta per axis.
    sample_count: usize,
    /// The bit-packed deltas, exactly as long as the block needs.
    packed_deltas: &'a [u8],
}

/// Walks the blocks of a delta-compressed frame. Counting and decoding share this single
/// walker, so the two can never disagree about where a frame ends.
#[derive(Clone, Debug)]
struct DeltaBlocks<'a> {
    remaining: &'a [u8],
}

impl<'a> DeltaBlocks<'a> {
    fn new(blocks: &'a [u8]) -> Self {
        Self { remaining: blocks }
    }
}

impl<'a> Iterator for DeltaBlocks<'a> {
    type Item = DeltaBlock<'a>;

    /// Yields the next non-empty block. Iteration ends at the end of the frame, and also on
    /// a truncated block or an impossible bit width: whatever follows malformed data cannot
    /// be trusted, so decoding stops at the last complete sample.
    fn next(&mut self) -> Option<DeltaBlock<'a>> {
        loop {
            let (&[width_byte, count_byte], rest) =
                self.remaining.split_first_chunk::<BLOCK_HEADER_LEN>()?;
            let bit_width = u32::from(width_byte);
            let sample_count = usize::from(count_byte);

            let length = (usize::from(width_byte) * AXES * sample_count).div_ceil(8);
            let packed = (bit_width <= MAX_DELTA_BIT_WIDTH)
                .then(|| rest.get(..length))
                .flatten();

            let Some(packed_deltas) = packed else {
                self.remaining = &[];
                return None;
            };

            self.remaining = &rest[length..];
            if sample_count > 0 {
                return Some(DeltaBlock {
                    bit_width,
                    sample_count,
                    packed_deltas,
                });
            }
        }
    }
}

impl FusedIterator for DeltaBlocks<'_> {}

/// Reads LSB-first bit fields that are packed without byte alignment.
#[derive(Clone, Debug)]
struct BitReader<'a> {
    data: &'a [u8],
    bit_position: usize,
}

impl<'a> BitReader<'a> {
    fn new(data: &'a [u8]) -> Self {
        Self {
            data,
            bit_position: 0,
        }
    }

    /// Reads a two's-complement signed field of the given width (0 to 32 bits).
    ///
    /// The caller guarantees that the data holds enough bits; [`DeltaBlocks`] only yields
    /// blocks whose packed length covers every delta they declare.
    fn read_signed(&mut self, bit_width: u32) -> i32 {
        if bit_width == 0 {
            return 0;
        }

        let mut value = 0_u32;
        for bit_index in 0..bit_width {
            let byte = self.data[self.bit_position / 8];
            let bit = (byte >> (self.bit_position % 8)) & 1;
            value |= u32::from(bit) << bit_index;
            self.bit_position += 1;
        }

        // Shift the field's sign bit up to bit 31, then arithmetic-shift back to sign-extend.
        let unused_bits = 32 - bit_width;
        (value << unused_bits).cast_signed() >> unused_bits
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn decode(frame_type: u8, payload: &[u8]) -> Vec<Sample> {
        samples(frame_type, payload).unwrap().collect()
    }

    #[test]
    fn uncompressed_decodes_interleaved_signed_axes() {
        let payload = [
            0x01, 0x00, 0xFF, 0xFF, 0xFF, 0x7F, // (1, -1, 32767)
            0x00, 0x80, 0x00, 0x00, 0x05, 0x00, // (-32768, 0, 5)
        ];

        assert_eq!(
            decode(UNCOMPRESSED_FRAME_TYPE, &payload),
            [[1, -1, 32767], [-32768, 0, 5]]
        );
    }

    #[test]
    fn uncompressed_ignores_a_trailing_partial_sample() {
        let payload = [0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0xAA, 0xBB, 0xCC];

        assert_eq!(sample_count(UNCOMPRESSED_FRAME_TYPE, &payload), Ok(1));
        assert_eq!(decode(UNCOMPRESSED_FRAME_TYPE, &payload), [[1, 2, 3]]);
    }

    /// The case that matters most: inferring a fixed stride for a delta frame and reading
    /// the packed delta bits as absolute axes silently emits noise.
    #[test]
    fn delta_accumulates_packed_deltas_onto_the_reference_sample() {
        // Reference (100, -200, 300), then one block of 2 samples at 4 bits per delta.
        // Deltas, in axis order: (+1, +2, -1) then (-2, 0, +3), packed LSB-first.
        let payload = [
            0x64, 0x00, // x =  100
            0x38, 0xFF, // y = -200
            0x2C, 0x01, // z =  300
            0x04, // 4 bits per delta
            0x02, // 2 samples in this block
            0x21, 0xEF, 0x30, // packed nibbles: 1, 2, -1, -2, 0, 3
        ];

        assert_eq!(sample_count(DELTA_FRAME_TYPE, &payload), Ok(3));
        assert_eq!(
            decode(DELTA_FRAME_TYPE, &payload),
            [[100, -200, 300], [101, -198, 299], [99, -198, 302]],
        );
    }

    #[test]
    fn delta_handles_multiple_blocks_in_one_frame() {
        let payload = [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // reference (0, 0, 0)
            0x04, 0x01, 0x21, 0x03, // block 1: 1 sample, deltas (1, 2, 3)
            0x04, 0x01, 0x21, 0x03, // block 2: 1 sample, deltas (1, 2, 3)
        ];

        assert_eq!(
            decode(DELTA_FRAME_TYPE, &payload),
            [[0, 0, 0], [1, 2, 3], [2, 4, 6]]
        );
    }

    #[test]
    fn delta_ignores_a_truncated_block_rather_than_reading_past_the_end() {
        let payload = [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, //
            0x08, 0x0A, 0x01, // claims 10 samples at 8 bits but supplies one byte
        ];

        assert_eq!(sample_count(DELTA_FRAME_TYPE, &payload), Ok(1));
        assert_eq!(decode(DELTA_FRAME_TYPE, &payload).len(), 1);
    }

    #[test]
    fn delta_stops_at_a_block_with_an_impossible_bit_width() {
        let payload = [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // reference (0, 0, 0)
            0x04, 0x01, 0x21, 0x03, // valid block: deltas (1, 2, 3)
            0x21, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, // 33-bit deltas cannot exist
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        ];

        assert_eq!(sample_count(DELTA_FRAME_TYPE, &payload), Ok(2));
        assert_eq!(decode(DELTA_FRAME_TYPE, &payload).len(), 2);
    }

    #[test]
    fn delta_clamps_the_running_value_to_the_sixteen_bit_range() {
        let payload = [
            0xFF, 0x7F, 0x00, 0x80, 0x00, 0x00, // reference (32767, -32768, 0)
            0x04, 0x01, 0xF1, 0x00, // deltas (+1, -1, 0)
        ];

        assert_eq!(
            decode(DELTA_FRAME_TYPE, &payload)[1],
            [i16::MAX, i16::MIN, 0]
        );
    }

    #[test]
    fn delta_saturates_instead_of_wrapping_on_a_full_width_delta() {
        let payload = [
            0xFF, 0x7F, 0x00, 0x00, 0x00, 0x00, // reference (32767, 0, 0)
            0x20, 0x01, // one sample of 32-bit deltas
            0xFF, 0xFF, 0xFF, 0x7F, // x: +2^31 - 1
            0x00, 0x00, 0x00, 0x00, // y: 0
            0x00, 0x00, 0x00, 0x00, // z: 0
        ];

        assert_eq!(decode(DELTA_FRAME_TYPE, &payload)[1], [i16::MAX, 0, 0]);
    }

    #[test]
    fn delta_frame_shorter_than_the_reference_has_no_samples() {
        assert_eq!(sample_count(DELTA_FRAME_TYPE, &[0x01, 0x02]), Ok(0));
        assert!(decode(DELTA_FRAME_TYPE, &[0x01, 0x02]).is_empty());
    }

    #[test]
    fn unknown_frame_type_is_rejected_instead_of_guessed_at() {
        assert_eq!(
            sample_count(0x7F, &[0; 12]),
            Err(UnsupportedFrameType(0x7F))
        );
        assert!(samples(0x7F, &[0; 12]).is_err());
    }

    #[test]
    fn is_supported_accepts_only_the_known_encodings() {
        assert!(is_supported(UNCOMPRESSED_FRAME_TYPE));
        assert!(is_supported(DELTA_FRAME_TYPE));
        assert!(!is_supported(0x00));
        assert!(!is_supported(0x7F));
    }
}
