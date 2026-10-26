//! The one-line telemetry printed while streaming.

use std::fmt::Write as _;
use std::time::Duration;

use polarble_core::acquisition::StatisticsSnapshot;

/// How long a stream may go without a frame before it is reported as stalled.
pub(crate) const STALL_THRESHOLD: Duration = Duration::from_secs(3);

/// What a real strap reports besides its streams; a simulation has none of it.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct DeviceStatus {
    /// The most recent battery percentage, if one has arrived.
    pub(crate) battery_percent: Option<u8>,
    /// Notifications that could not be pushed to their stream.
    pub(crate) pipeline_errors: u64,
}

/// One telemetry line and whether it reports a problem.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Telemetry {
    pub(crate) line: String,
    pub(crate) has_problems: bool,
}

/// Formats the state of every stream, plus the device's battery and errors, as one line.
///
/// Sample counts are right-aligned in a fixed width, so consecutive lines stay in columns as
/// the counts grow during a recording.
pub(crate) fn format(
    streams: &[(&str, StatisticsSnapshot)],
    device: Option<DeviceStatus>,
) -> Telemetry {
    let mut line = String::new();
    let mut has_problems = false;

    for (label, stats) in streams {
        if !line.is_empty() {
            line.push_str(" | ");
        }

        let _ = write!(line, "{label} {:>11} samples", grouped(stats.samples));
        if stats.gaps > 0 {
            let _ = write!(
                line,
                ", {}, {} lost",
                counted(stats.gaps, "gap"),
                grouped(stats.samples_lost)
            );
        }
        if stats.unsupported_frames > 0 {
            let _ = write!(line, ", {} unreadable", grouped(stats.unsupported_frames));
        }

        let stalled = stats.is_stalled(STALL_THRESHOLD);
        if stalled {
            let _ = write!(line, ", idle {} s", stats.idle.as_secs());
        }

        has_problems |= stats.has_data_loss() || stalled;
    }

    if let Some(device) = device {
        match device.battery_percent {
            Some(percent) => {
                let _ = write!(line, " | battery {percent}%");
            }
            None => line.push_str(" | battery --"),
        }

        if device.pipeline_errors > 0 {
            let _ = write!(line, " | {}", counted(device.pipeline_errors, "error"));
            has_problems = true;
        }
    }

    Telemetry { line, has_problems }
}

/// Formats a count of things, e.g. `1 gap` or `1,234 gaps`.
fn counted(count: u64, noun: &str) -> String {
    let suffix = if count == 1 { "" } else { "s" };
    format!("{} {noun}{suffix}", grouped(count))
}

/// Formats a count with thousands separators, e.g. `1,234,567`.
fn grouped(value: u64) -> String {
    let digits = value.to_string();
    let mut grouped = String::with_capacity(digits.len() + digits.len() / 3);
    for (index, digit) in digits.chars().enumerate() {
        if index > 0 && (digits.len() - index).is_multiple_of(3) {
            grouped.push(',');
        }
        grouped.push(digit);
    }
    grouped
}

#[cfg(test)]
mod tests {
    use super::*;

    fn healthy(samples: u64) -> StatisticsSnapshot {
        StatisticsSnapshot {
            frames: 1,
            samples,
            ..StatisticsSnapshot::default()
        }
    }

    #[test]
    fn groups_thousands() {
        assert_eq!(grouped(0), "0");
        assert_eq!(grouped(999), "999");
        assert_eq!(grouped(1_000), "1,000");
        assert_eq!(grouped(1_234_567), "1,234,567");
    }

    #[test]
    fn counts_agree_with_their_nouns() {
        assert_eq!(counted(1, "gap"), "1 gap");
        assert_eq!(counted(1_200, "gap"), "1,200 gaps");
    }

    #[test]
    fn a_healthy_session_reports_counts_and_battery() {
        let device = DeviceStatus {
            battery_percent: Some(87),
            pipeline_errors: 0,
        };
        let telemetry = format(
            &[("ECG", healthy(12_345)), ("ACC", healthy(678))],
            Some(device),
        );

        assert_eq!(
            telemetry.line,
            "ECG      12,345 samples | ACC         678 samples | battery 87%"
        );
        assert!(!telemetry.has_problems);
    }

    #[test]
    fn losses_stalls_and_errors_are_reported_as_problems() {
        let lossy = StatisticsSnapshot {
            gaps: 2,
            samples_lost: 146,
            unsupported_frames: 1,
            idle: Duration::from_secs(5),
            ..healthy(100)
        };

        let device = DeviceStatus {
            battery_percent: None,
            pipeline_errors: 3,
        };
        let telemetry = format(&[("ECG", lossy)], Some(device));

        assert_eq!(
            telemetry.line,
            "ECG         100 samples, 2 gaps, 146 lost, 1 unreadable, idle 5 s | battery -- | 3 errors"
        );
        assert!(telemetry.has_problems);
    }

    #[test]
    fn a_simulation_reports_streams_only() {
        let telemetry = format(&[("ECG", healthy(5))], None);

        assert_eq!(telemetry.line, "ECG           5 samples");
    }
}
