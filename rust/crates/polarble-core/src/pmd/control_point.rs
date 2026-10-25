//! Builds PMD control-point requests and decodes their responses.
//!
//! A start-measurement request is `0x02 <measurementType>` followed by one type/length/value
//! triple per setting: the setting id, an array length (the number of 16-bit values that
//! follow, always 1 here), then the value little-endian.
//!
//! A response arrives as an indication on the control point and is
//! `[0xF0][opCode][measurementType][errorCode][...]`. The error code is the only way to tell
//! a rejected configuration apart from a healthy stream that has not started yet, so callers
//! must enable those indications and check every response.

use super::{MeasurementType, OpCode, PmdError, SettingType};

/// First byte of every control-point response frame.
pub const RESPONSE_CODE: u8 = 0xF0;

/// Minimum length of a well-formed control-point response.
pub const MIN_RESPONSE_LEN: usize = 4;

/// One setting: type, array length, then a single little-endian 16-bit value.
const SETTING_LEN: usize = 4;

/// A single control-point setting: a type paired with a 16-bit value.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Setting {
    /// Which setting is being specified.
    pub kind: SettingType,
    /// The value, encoded little-endian on the wire.
    pub value: u16,
}

impl Setting {
    /// Creates a setting.
    #[must_use]
    pub const fn new(kind: SettingType, value: u16) -> Self {
        Self { kind, value }
    }
}

/// Builds a start-measurement request.
#[must_use]
pub fn build_start(measurement_type: MeasurementType, settings: &[Setting]) -> Vec<u8> {
    let mut request = Vec::with_capacity(2 + settings.len() * SETTING_LEN);
    request.push(OpCode::StartMeasurement.into());
    request.push(measurement_type.into());

    for setting in settings {
        request.push(setting.kind as u8);
        request.push(0x01); // one 16-bit value follows
        request.extend_from_slice(&setting.value.to_le_bytes());
    }

    request
}

/// Builds a stop-measurement request. Without one the strap keeps streaming, and draining its
/// battery, after the application has gone.
#[must_use]
pub fn build_stop(measurement_type: MeasurementType) -> [u8; 2] {
    [OpCode::StopMeasurement.into(), measurement_type.into()]
}

/// A decoded PMD control-point response.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Response {
    /// The operation the sensor is responding to.
    pub op_code: OpCode,
    /// The measurement the response concerns.
    pub measurement_type: MeasurementType,
    /// The outcome; [`PmdError::Success`] means accepted.
    pub error: PmdError,
}

impl Response {
    /// Decodes a control-point indication, or returns `None` if it is not a response frame.
    #[must_use]
    pub fn parse(frame: &[u8]) -> Option<Self> {
        match frame {
            [RESPONSE_CODE, op_code, measurement_type, error, ..] => Some(Self {
                op_code: (*op_code).into(),
                measurement_type: (*measurement_type).into(),
                error: (*error).into(),
            }),
            _ => None,
        }
    }

    /// True when the sensor accepted the request.
    #[must_use]
    pub fn is_success(&self) -> bool {
        self.error == PmdError::Success
    }

    /// Whether this response answers `request`, judged by the operation code and measurement
    /// type that every request starts with.
    ///
    /// Matching guards against attributing a late or unsolicited indication, for example the
    /// answer to a previous request that already timed out, to the request now in flight.
    #[must_use]
    pub fn is_response_to(&self, request: &[u8]) -> bool {
        matches!(
            request,
            [op_code, measurement_type, ..]
                if *op_code == u8::from(self.op_code)
                    && *measurement_type == u8::from(self.measurement_type)
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::pmd::h10;

    /// The exact byte arrays the original implementation hand-wrote and shipped. They are
    /// known to work against real hardware, so the builders must reproduce them verbatim.
    const ORIGINAL_ECG_WRITE: [u8; 10] =
        [0x02, 0x00, 0x00, 0x01, 0x82, 0x00, 0x01, 0x01, 0x0E, 0x00];
    const ORIGINAL_ACC_WRITE: [u8; 14] = [
        0x02, 0x02, 0x00, 0x01, 0xC8, 0x00, 0x01, 0x01, 0x10, 0x00, 0x02, 0x01, 0x08, 0x00,
    ];

    #[test]
    fn build_ecg_start_reproduces_the_original_byte_sequence() {
        assert_eq!(h10::build_ecg_start(), ORIGINAL_ECG_WRITE);
    }

    #[test]
    fn build_acc_start_reproduces_the_original_byte_sequence() {
        assert_eq!(h10::build_acc_start(), ORIGINAL_ACC_WRITE);
    }

    #[test]
    fn build_start_encodes_setting_values_little_endian() {
        let request = build_start(
            MeasurementType::Acc,
            &[Setting::new(SettingType::SampleRate, 200)],
        );

        // opcode, measurement type, then type / array length / uint16 little-endian
        assert_eq!(request, [0x02, 0x02, 0x00, 0x01, 0xC8, 0x00]);
    }

    #[test]
    fn build_stop_is_opcode_and_measurement_type() {
        assert_eq!(build_stop(MeasurementType::Ecg), [0x03, 0x00]);
    }

    #[test]
    fn parse_reads_a_successful_acknowledgement() {
        let response = Response::parse(&[0xF0, 0x02, 0x00, 0x00, 0x00]).unwrap();

        assert_eq!(response.op_code, OpCode::StartMeasurement);
        assert_eq!(response.measurement_type, MeasurementType::Ecg);
        assert_eq!(response.error, PmdError::Success);
        assert!(response.is_success());
    }

    #[test]
    fn parse_surfaces_the_error_code() {
        // A rejected configuration: precisely the case that is invisible without responses.
        let response = Response::parse(&[0xF0, 0x02, 0x02, 0x05]).unwrap();

        assert_eq!(response.measurement_type, MeasurementType::Acc);
        assert_eq!(response.error, PmdError::InvalidParameter);
        assert!(!response.is_success());
    }

    #[test]
    fn parse_rejects_malformed_frames() {
        for frame in [&[0xF0, 0x02, 0x00][..], &[0x00, 0x02, 0x00, 0x00], &[]] {
            assert_eq!(Response::parse(frame), None, "{frame:02X?}");
        }
    }

    #[test]
    fn is_response_to_matches_on_op_code_and_measurement_type() {
        let response = Response {
            op_code: OpCode::StartMeasurement,
            measurement_type: MeasurementType::Acc,
            error: PmdError::Success,
        };

        assert!(response.is_response_to(&h10::build_acc_start()));
        assert!(!response.is_response_to(&h10::build_ecg_start()));
        assert!(!response.is_response_to(&build_stop(MeasurementType::Acc)));
        assert!(!response.is_response_to(&[]));
    }
}
