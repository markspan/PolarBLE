use std::fmt::Debug;

use crate::pmd::{MeasurementType, UnsupportedFrameType, acc, ecg};

/// Turns the payload of one PMD data frame into channel-interleaved samples.
///
/// Implementations are stateless: every frame carries everything needed to decode it, so a
/// single instance, typically a `static`, serves any number of streams.
pub trait MeasurementDecoder: Debug + Send + Sync {
    /// The measurement whose frames this decoder understands.
    fn measurement_type(&self) -> MeasurementType;

    /// Number of interleaved channels in each decoded sample.
    fn channel_count(&self) -> usize;

    /// Replaces the contents of `out` with the frame's samples, channel-interleaved, as the
    /// `f32` values a sink receives.
    ///
    /// Returns the number of samples (not values) decoded.
    ///
    /// # Errors
    ///
    /// Returns [`UnsupportedFrameType`] if this decoder does not understand `frame_type`;
    /// `out` is then left empty.
    fn decode_into(
        &self,
        frame_type: u8,
        payload: &[u8],
        out: &mut Vec<f32>,
    ) -> Result<usize, UnsupportedFrameType>;
}

/// ECG: one channel in microvolts.
#[derive(Clone, Copy, Debug, Default)]
pub struct EcgDecoder;

/// The shared ECG decoder.
pub static ECG_DECODER: EcgDecoder = EcgDecoder;

impl MeasurementDecoder for EcgDecoder {
    fn measurement_type(&self) -> MeasurementType {
        MeasurementType::Ecg
    }

    fn channel_count(&self) -> usize {
        ecg::CHANNELS
    }

    #[expect(
        clippy::cast_precision_loss,
        reason = "ECG samples are 24-bit, and every 24-bit integer is exact in an f32"
    )]
    fn decode_into(
        &self,
        frame_type: u8,
        payload: &[u8],
        out: &mut Vec<f32>,
    ) -> Result<usize, UnsupportedFrameType> {
        out.clear();
        if !ecg::is_supported(frame_type) {
            return Err(UnsupportedFrameType(frame_type));
        }

        out.extend(ecg::samples(payload).map(|microvolts| microvolts as f32));
        Ok(out.len())
    }
}

/// Accelerometer: three interleaved axes in milli-g.
#[derive(Clone, Copy, Debug, Default)]
pub struct AccDecoder;

/// The shared accelerometer decoder.
pub static ACC_DECODER: AccDecoder = AccDecoder;

impl MeasurementDecoder for AccDecoder {
    fn measurement_type(&self) -> MeasurementType {
        MeasurementType::Acc
    }

    fn channel_count(&self) -> usize {
        acc::AXES
    }

    fn decode_into(
        &self,
        frame_type: u8,
        payload: &[u8],
        out: &mut Vec<f32>,
    ) -> Result<usize, UnsupportedFrameType> {
        out.clear();
        out.extend(acc::samples(frame_type, payload)?.flatten().map(f32::from));
        Ok(out.len() / acc::AXES)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ecg_represents_every_twenty_four_bit_value_exactly() {
        let mut out = Vec::new();
        let count = ECG_DECODER
            .decode_into(
                ecg::UNCOMPRESSED_FRAME_TYPE,
                &[0x00, 0x00, 0x80, 0xFF, 0xFF, 0x7F],
                &mut out,
            )
            .unwrap();

        assert_eq!(count, 2);
        assert_eq!(out, [-8_388_608.0, 8_388_607.0]);
    }

    #[test]
    fn acc_interleaves_axes() {
        let mut out = Vec::new();
        let count = ACC_DECODER
            .decode_into(
                acc::UNCOMPRESSED_FRAME_TYPE,
                &[0x01, 0x00, 0xFF, 0xFF, 0xFF, 0x7F],
                &mut out,
            )
            .unwrap();

        assert_eq!(count, 1);
        assert_eq!(out, [1.0, -1.0, 32767.0]);
    }

    #[test]
    fn an_unsupported_frame_type_leaves_the_buffer_empty() {
        let mut out = vec![1.0, 2.0];

        assert_eq!(
            ECG_DECODER.decode_into(0x7F, &[0; 3], &mut out),
            Err(UnsupportedFrameType(0x7F))
        );
        assert!(out.is_empty());
    }
}
