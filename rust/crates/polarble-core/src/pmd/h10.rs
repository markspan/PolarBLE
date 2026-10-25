//! The measurement configuration this application requests from a Polar H10, and the stream
//! definitions that describe the resulting data.
//!
//! The byte sequences produced here are pinned by tests to the requests known to work against
//! real hardware, and they are identical to the ones the .NET implementation sends.

use crate::acquisition::{ACC_DECODER, ECG_DECODER, MeasurementConfiguration};
use crate::pmd::MeasurementType;
use crate::pmd::SettingType;
use crate::pmd::control_point::{Setting, build_start};
use crate::streaming::{ChannelDefinition, StreamDefinition};

/// ECG sample rate in hertz.
pub const ECG_SAMPLE_RATE_HZ: u16 = 130;

/// ECG resolution in bits.
pub const ECG_RESOLUTION_BITS: u16 = 14;

/// Accelerometer sample rate in hertz.
pub const ACC_SAMPLE_RATE_HZ: u16 = 200;

/// Accelerometer resolution in bits.
pub const ACC_RESOLUTION_BITS: u16 = 16;

/// Accelerometer range in g.
pub const ACC_RANGE_G: u16 = 8;

/// Every measurement this application acquires from a strap, in the order they are started.
///
/// `device_address` is the strap's Bluetooth address as twelve upper-case hex digits; using
/// the same format as the .NET implementation keeps the stream source ids identical, so a
/// recorder re-binds to the same strap whichever implementation is streaming it.
#[must_use]
pub fn measurements(device_name: &str, device_address: &str) -> Vec<MeasurementConfiguration> {
    vec![
        MeasurementConfiguration {
            label: "ECG",
            display_name: "ECG",
            start_command: build_ecg_start(),
            decoder: &ECG_DECODER,
            stream: ecg_stream(device_name, device_address),
        },
        MeasurementConfiguration {
            label: "ACC",
            display_name: "accelerometer",
            start_command: build_acc_start(),
            decoder: &ACC_DECODER,
            stream: acc_stream(device_name, device_address),
        },
    ]
}

/// Builds the ECG start-measurement request.
#[must_use]
pub fn build_ecg_start() -> Vec<u8> {
    build_start(
        MeasurementType::Ecg,
        &[
            Setting::new(SettingType::SampleRate, ECG_SAMPLE_RATE_HZ),
            Setting::new(SettingType::Resolution, ECG_RESOLUTION_BITS),
        ],
    )
}

/// Builds the accelerometer start-measurement request.
#[must_use]
pub fn build_acc_start() -> Vec<u8> {
    build_start(
        MeasurementType::Acc,
        &[
            Setting::new(SettingType::SampleRate, ACC_SAMPLE_RATE_HZ),
            Setting::new(SettingType::Resolution, ACC_RESOLUTION_BITS),
            Setting::new(SettingType::Range, ACC_RANGE_G),
        ],
    )
}

/// Describes the ECG stream.
#[must_use]
pub fn ecg_stream(device_name: &str, device_address: &str) -> StreamDefinition {
    StreamDefinition {
        name: format!("{device_name}_ECG"),
        stream_type: "ECG".to_owned(),
        source_id: format!("PolarBLE:{device_address}:ECG"),
        sample_rate_hz: ECG_SAMPLE_RATE_HZ.into(),
        channels: vec![ChannelDefinition::new("ECG", "microvolts", "ECG")],
        manufacturer: "Polar".to_owned(),
        model: "H10".to_owned(),
        device_address: device_address.to_owned(),
        acquisition: vec![
            (
                "resolution_bits".to_owned(),
                ECG_RESOLUTION_BITS.to_string(),
            ),
            ("sample_rate_hz".to_owned(), ECG_SAMPLE_RATE_HZ.to_string()),
        ],
    }
}

/// Describes the accelerometer stream.
#[must_use]
pub fn acc_stream(device_name: &str, device_address: &str) -> StreamDefinition {
    StreamDefinition {
        name: format!("{device_name}_ACC"),
        stream_type: "Accelerometer".to_owned(),
        source_id: format!("PolarBLE:{device_address}:ACC"),
        sample_rate_hz: ACC_SAMPLE_RATE_HZ.into(),
        channels: vec![
            ChannelDefinition::new("X", "milli-g", "AccelerometerX"),
            ChannelDefinition::new("Y", "milli-g", "AccelerometerY"),
            ChannelDefinition::new("Z", "milli-g", "AccelerometerZ"),
        ],
        manufacturer: "Polar".to_owned(),
        model: "H10".to_owned(),
        device_address: device_address.to_owned(),
        acquisition: vec![
            (
                "resolution_bits".to_owned(),
                ACC_RESOLUTION_BITS.to_string(),
            ),
            ("sample_rate_hz".to_owned(), ACC_SAMPLE_RATE_HZ.to_string()),
            ("range_g".to_owned(), ACC_RANGE_G.to_string()),
        ],
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::pmd::control_point::build_stop;

    #[test]
    fn measurements_start_ecg_then_accelerometer() {
        let measurements = measurements("Polar H10 1234", "A0B1C2D3E4F5");

        let [ecg, acc] = measurements.as_slice() else {
            panic!("expected exactly two measurements");
        };
        assert_eq!(ecg.measurement_type(), MeasurementType::Ecg);
        assert_eq!(ecg.start_command, build_ecg_start());
        assert_eq!(ecg.stream.name, "Polar H10 1234_ECG");
        assert_eq!(acc.measurement_type(), MeasurementType::Acc);
        assert_eq!(acc.start_command, build_acc_start());
        assert_eq!(acc.stream.source_id, "PolarBLE:A0B1C2D3E4F5:ACC");
    }

    #[test]
    fn decoders_match_their_streams_channel_counts() {
        for measurement in measurements("Polar H10", "0") {
            assert_eq!(
                measurement.stream.channels.len(),
                measurement.decoder.channel_count()
            );
        }
    }

    #[test]
    fn stop_command_addresses_the_configured_measurement() {
        let acc = &measurements("Polar H10", "0")[1];

        assert_eq!(acc.stop_command(), build_stop(MeasurementType::Acc));
    }
}
