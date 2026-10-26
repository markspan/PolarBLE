//! GATT identifiers used to talk to a Polar H10, as 128-bit values.
//!
//! The PMD (Polar Measurement Data) service is proprietary; the battery service is the
//! standard Bluetooth SIG one. They are plain integers so that this crate stays free of any
//! Bluetooth library; convert with, for example, `uuid::Uuid::from_u128`.

/// Polar Measurement Data service.
pub const PMD_SERVICE: u128 = 0xFB00_5C80_02E7_F387_1CAD_8ACD_2D8D_F0C8;

/// PMD control point: write requests here, receive responses as indications.
pub const PMD_CONTROL_POINT: u128 = 0xFB00_5C81_02E7_F387_1CAD_8ACD_2D8D_F0C8;

/// PMD data characteristic: measurement frames arrive here as notifications.
pub const PMD_DATA: u128 = 0xFB00_5C82_02E7_F387_1CAD_8ACD_2D8D_F0C8;

/// Standard Bluetooth SIG battery service.
pub const BATTERY_SERVICE: u128 = 0x0000_180F_0000_1000_8000_0080_5F9B_34FB;

/// Standard Bluetooth SIG battery level characteristic (percent, one byte).
pub const BATTERY_LEVEL: u128 = 0x0000_2A19_0000_1000_8000_0080_5F9B_34FB;
