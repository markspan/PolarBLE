//! Platform-free core of PolarBLE: the Polar Measurement Data (PMD) protocol codec, the
//! acquisition pipeline that turns raw notifications into samples, and the sink abstraction
//! those samples are pushed to.
//!
//! Nothing in this crate touches Bluetooth, the operating system or liblsl, so every rule
//! about how bytes become samples is covered by tests that need no hardware.
//!
//! * [`pmd`] decodes and encodes the wire format: frame headers, ECG and accelerometer
//!   payloads, and control-point requests and responses.
//! * [`acquisition`] routes each notification to its measurement stream, decodes it, checks
//!   it for lost frames and keeps live statistics.
//! * [`streaming`] describes outgoing streams and defines the [`streaming::SampleSink`] that
//!   receives the decoded samples.

#![forbid(unsafe_code)]

pub mod acquisition;
pub mod pmd;
pub mod streaming;
