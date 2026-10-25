//! The Polar Measurement Data (PMD) wire format.
//!
//! A PMD data notification is a ten-byte [`FrameHeader`] followed by a measurement-specific
//! payload, decoded by [`ecg`] or [`acc`]. Measurements are started and stopped through the
//! [`control_point`]. [`h10`] fixes the exact configuration this application requests.

pub mod acc;
pub mod control_point;
pub mod ecg;
pub mod h10;
pub mod uuids;

mod header;
mod types;

pub use header::FrameHeader;
pub use types::{MeasurementType, OpCode, PmdError, SettingType, UnsupportedFrameType};
