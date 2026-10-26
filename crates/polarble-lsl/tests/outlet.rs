//! End-to-end tests against a real liblsl.
//!
//! They are ignored by default because they need the native library. Run them with the
//! library's path in `POLARBLE_LSL_LIBRARY`:
//!
//! ```text
//! POLARBLE_LSL_LIBRARY=/path/to/liblsl.so cargo test -p polarble-lsl -- --include-ignored
//! ```

use std::sync::Arc;

use polarble_core::pmd::h10;
use polarble_core::streaming::SampleSink;
use polarble_lsl::{Liblsl, LslOutlet};

fn liblsl() -> Arc<Liblsl> {
    Liblsl::load().unwrap_or_else(|error| panic!("{error}"))
}

#[test]
#[ignore = "needs liblsl: set POLARBLE_LSL_LIBRARY and pass --include-ignored"]
fn loads_a_supported_version() {
    assert!(
        liblsl().version() >= 114,
        "liblsl 1.14 or newer is required"
    );
}

#[test]
#[ignore = "needs liblsl: set POLARBLE_LSL_LIBRARY and pass --include-ignored"]
fn advertises_the_full_stream_description() {
    let definition = h10::acc_stream("Polar H10 TEST", "A0B1C2D3E4F5");
    let outlet = LslOutlet::new(&liblsl(), &definition).unwrap();

    let xml = outlet.info_xml();
    for expected in [
        "<name>Polar H10 TEST_ACC</name>",
        "<type>Accelerometer</type>",
        "<channel_count>3</channel_count>",
        "<nominal_srate>200",
        "<channel_format>float32</channel_format>",
        "<source_id>PolarBLE:A0B1C2D3E4F5:ACC</source_id>",
        "<label>Z</label>",
        "<unit>milli-g</unit>",
        "<type>AccelerometerZ</type>",
        "<manufacturer>Polar</manufacturer>",
        "<device_address>A0B1C2D3E4F5</device_address>",
        "<range_g>8</range_g>",
    ] {
        assert!(xml.contains(expected), "missing {expected} in\n{xml}");
    }
}

#[test]
#[ignore = "needs liblsl: set POLARBLE_LSL_LIBRARY and pass --include-ignored"]
fn pushes_whole_samples_and_rejects_ragged_chunks() {
    let definition = h10::acc_stream("Polar H10 PUSH", "000000000001");
    let mut outlet = LslOutlet::new(&liblsl(), &definition).unwrap();

    outlet.push_chunk(&[1.0, 2.0, 3.0, 4.0, 5.0, 6.0]).unwrap();
    outlet.push_chunk(&[]).unwrap();
    assert!(outlet.push_chunk(&[1.0, 2.0]).is_err());
    assert!(!outlet.has_consumers());
}

#[test]
#[ignore = "needs liblsl: set POLARBLE_LSL_LIBRARY and pass --include-ignored"]
fn rejects_text_with_a_nul_byte() {
    let mut definition = h10::ecg_stream("Polar H10", "0");
    definition.name = "bad\0name".to_owned();

    assert!(LslOutlet::new(&liblsl(), &definition).is_err());
}
