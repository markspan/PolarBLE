use std::fmt;

/// Declares an enum for a one-byte protocol field.
///
/// Decoding never fails: a byte this crate does not recognise is preserved as `Unknown`, so
/// a newer firmware's values survive a round trip and can still be reported.
macro_rules! wire_enum {
    (
        $(#[$meta:meta])*
        $name:ident {
            $( $(#[$variant_meta:meta])* $variant:ident = $value:literal, )+
        }
    ) => {
        $(#[$meta])*
        #[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
        pub enum $name {
            $( $(#[$variant_meta])* $variant, )+
            /// A value this crate does not recognise, preserved as received.
            Unknown(u8),
        }

        impl From<u8> for $name {
            fn from(value: u8) -> Self {
                match value {
                    $( $value => Self::$variant, )+
                    other => Self::Unknown(other),
                }
            }
        }

        impl From<$name> for u8 {
            fn from(value: $name) -> Self {
                match value {
                    $( $name::$variant => $value, )+
                    $name::Unknown(other) => other,
                }
            }
        }
    };
}

wire_enum! {
    /// Measurement types addressable through the PMD control point.
    MeasurementType {
        /// Electrocardiogram, 3-byte signed samples in microvolts.
        Ecg = 0x00,
        /// Photoplethysmogram (not present on the H10).
        Ppg = 0x01,
        /// Accelerometer, three signed axes in milli-g.
        Acc = 0x02,
        /// Peak-to-peak interval (not streamed by this application).
        Ppi = 0x03,
        /// Gyroscope (not present on the H10).
        Gyroscope = 0x05,
        /// Magnetometer (not present on the H10).
        Magnetometer = 0x06,
    }
}

wire_enum! {
    /// Control-point operation codes.
    OpCode {
        /// Ask the sensor which settings a measurement supports.
        GetMeasurementSettings = 0x01,
        /// Begin streaming a measurement.
        StartMeasurement = 0x02,
        /// Stop streaming a measurement.
        StopMeasurement = 0x03,
    }
}

wire_enum! {
    /// Error codes returned in the fourth byte of a PMD control-point response.
    PmdError {
        /// The request was accepted.
        Success = 0x00,
        /// The operation code is not recognised.
        InvalidOpCode = 0x01,
        /// The measurement type is not recognised.
        InvalidMeasurementType = 0x02,
        /// The sensor does not support this measurement.
        NotSupported = 0x03,
        /// One of the requested settings has an invalid length.
        InvalidLength = 0x04,
        /// One of the requested settings has an invalid parameter.
        InvalidParameter = 0x05,
        /// The measurement is already in the requested state.
        AlreadyInState = 0x06,
        /// The requested resolution is invalid.
        InvalidResolution = 0x07,
        /// The requested sample rate is invalid.
        InvalidSampleRate = 0x08,
        /// The requested range is invalid.
        InvalidRange = 0x09,
        /// The requested MTU is invalid.
        InvalidMtu = 0x0A,
        /// The requested number of channels is invalid.
        InvalidNumberOfChannels = 0x0B,
        /// The sensor is in an invalid state for this request.
        InvalidState = 0x0C,
        /// The sensor is charging and cannot measure.
        DeviceInCharger = 0x0D,
    }
}

/// Setting identifiers used in the control-point type/length/value triples.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum SettingType {
    /// Sample rate in hertz.
    SampleRate = 0x00,
    /// Resolution in bits.
    Resolution = 0x01,
    /// Measurement range; for the accelerometer, in g.
    Range = 0x02,
    /// Number of channels.
    ChannelCount = 0x04,
}

/// A frame type that the decoder for its measurement does not understand.
///
/// Unknown encodings are reported, never guessed at.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct UnsupportedFrameType(pub u8);

impl fmt::Display for UnsupportedFrameType {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "unsupported frame type 0x{:02X}", self.0)
    }
}

impl std::error::Error for UnsupportedFrameType {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn known_values_round_trip() {
        for byte in [0x00, 0x02, 0x06] {
            assert_eq!(u8::from(MeasurementType::from(byte)), byte);
        }
        assert_eq!(MeasurementType::from(0x02), MeasurementType::Acc);
    }

    #[test]
    fn unknown_values_are_preserved_rather_than_rejected() {
        assert_eq!(PmdError::from(0x42), PmdError::Unknown(0x42));
        assert_eq!(u8::from(PmdError::Unknown(0x42)), 0x42);
    }
}
