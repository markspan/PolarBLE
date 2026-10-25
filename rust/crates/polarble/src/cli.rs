//! Command-line interface.

use std::path::PathBuf;
use std::time::Duration;

use clap::{Args, Parser, Subcommand};

/// Stream Polar H10 ECG and accelerometer data to LabStreamingLayer.
#[derive(Debug, Parser)]
#[command(version, about, long_about = None)]
pub(crate) struct Cli {
    #[command(subcommand)]
    pub(crate) command: Command,
}

#[derive(Debug, Subcommand)]
pub(crate) enum Command {
    /// List the Polar straps in range.
    ///
    /// A strap only advertises while it is worn, or while its electrodes are moistened.
    Scan {
        /// How long to listen for advertisements, in seconds.
        #[arg(long, default_value = "10", value_parser = parse_seconds)]
        duration: Duration,
    },

    /// Connect to a strap and stream its ECG and accelerometer data until interrupted.
    ///
    /// Press Ctrl+C to stop. The strap is then told to stop measuring, so it does not keep
    /// streaming and draining its battery.
    Stream {
        /// Connect to the strap whose name or address contains this text, ignoring case.
        /// Without it, the only strap in range is used; if there are several, they are listed.
        #[arg(long, short)]
        device: Option<String>,

        /// How long to scan for the strap before giving up, in seconds.
        #[arg(long, default_value = "30", value_parser = parse_seconds)]
        scan_timeout: Duration,

        #[command(flatten)]
        output: OutputArgs,
    },

    /// Stream a synthetic signal through the full pipeline, without a strap.
    ///
    /// The streams look exactly like a real strap's apart from their name and source id, so
    /// this tests an LSL recording setup end to end.
    Simulate {
        /// Stop after this many seconds; without it, run until interrupted.
        #[arg(long, value_parser = parse_seconds)]
        duration: Option<Duration>,

        #[command(flatten)]
        output: OutputArgs,
    },
}

/// Options shared by the commands that stream.
#[derive(Debug, Args)]
pub(crate) struct OutputArgs {
    /// How often to print telemetry, in seconds.
    #[arg(long, default_value = "5", value_parser = parse_seconds)]
    pub(crate) interval: Duration,

    /// Path to liblsl. Defaults to `POLARBLE_LSL_LIBRARY`, then the executable's directory,
    /// then the system library path.
    #[arg(long)]
    pub(crate) lsl_library: Option<PathBuf>,
}

/// Parses a positive number of seconds, fractions allowed.
fn parse_seconds(text: &str) -> Result<Duration, String> {
    let seconds: f64 = text
        .parse()
        .map_err(|_| format!("`{text}` is not a number of seconds"))?;
    if seconds.is_finite() && seconds > 0.0 {
        Duration::try_from_secs_f64(seconds).map_err(|error| error.to_string())
    } else {
        Err(format!("`{text}` must be a positive number of seconds"))
    }
}

#[cfg(test)]
mod tests {
    use clap::CommandFactory;

    use super::*;

    #[test]
    fn the_command_line_definition_is_consistent() {
        Cli::command().debug_assert();
    }

    #[test]
    fn seconds_may_be_fractional_but_must_be_positive() {
        assert_eq!(parse_seconds("2.5"), Ok(Duration::from_millis(2500)));
        assert!(parse_seconds("0").is_err());
        assert!(parse_seconds("-1").is_err());
        assert!(parse_seconds("soon").is_err());
    }
}
