/// Detects frames lost between the strap and this application by comparing consecutive PMD
/// device timestamps against the number of samples that actually arrived.
///
/// Every PMD frame is stamped by the strap with the time of its last sample. Between two
/// consecutive frames, the timestamps should therefore advance by exactly the duration of the
/// second frame's samples. When they advance by noticeably more, whole notifications were
/// lost on the radio link, and the surplus says roughly how many samples went with them.
///
/// This is diagnostic only: it never alters or re-times data. Comparing each frame only with
/// its predecessor means a small mismatch between the nominal and the true sample rate cannot
/// accumulate into a false alarm.
#[derive(Clone, Debug)]
pub struct FrameGapDetector {
    samples_per_nanosecond: f64,
    tolerance_frames: f64,
    previous_timestamp: Option<u64>,
}

impl FrameGapDetector {
    /// The default tolerance: half a frame sits well clear of timestamp jitter yet well below
    /// the loss of one whole notification.
    pub const DEFAULT_TOLERANCE_FRAMES: f64 = 0.5;

    /// Creates a detector for a stream with the given nominal rate and the default tolerance.
    ///
    /// # Panics
    ///
    /// Panics if `nominal_sample_rate_hz` is not a positive, finite number.
    #[must_use]
    pub fn new(nominal_sample_rate_hz: f64) -> Self {
        Self::with_tolerance(nominal_sample_rate_hz, Self::DEFAULT_TOLERANCE_FRAMES)
    }

    /// Creates a detector with an explicit tolerance: how much longer than expected an
    /// interval may be, as a fraction of the arriving frame, before it counts as a gap.
    ///
    /// # Panics
    ///
    /// Panics if either argument is not a positive, finite number.
    #[must_use]
    pub fn with_tolerance(nominal_sample_rate_hz: f64, tolerance_frames: f64) -> Self {
        assert!(
            nominal_sample_rate_hz.is_finite() && nominal_sample_rate_hz > 0.0,
            "the nominal sample rate must be positive, not {nominal_sample_rate_hz}"
        );
        assert!(
            tolerance_frames.is_finite() && tolerance_frames > 0.0,
            "the tolerance must be positive, not {tolerance_frames}"
        );

        Self {
            samples_per_nanosecond: nominal_sample_rate_hz / 1e9,
            tolerance_frames,
            previous_timestamp: None,
        }
    }

    /// Records a frame and reports how many samples appear to be missing before it.
    ///
    /// Returns the estimated number of samples lost since the previous frame, or zero when
    /// the interval is consistent with the samples received. The first frame, and a frame
    /// whose timestamp does not advance (a device clock reset), only establish a new baseline.
    #[expect(
        clippy::cast_precision_loss,
        clippy::cast_possible_truncation,
        clippy::cast_sign_loss,
        reason = "sample estimates are approximate by nature; the float-to-int cast saturates \
                  and only ever sees a positive surplus"
    )]
    pub fn observe(&mut self, timestamp_ns: u64, sample_count: usize) -> u64 {
        let previous = self.previous_timestamp.replace(timestamp_ns);
        let Some(baseline) = previous.filter(|&baseline| timestamp_ns > baseline) else {
            return 0;
        };

        let samples_spanned = (timestamp_ns - baseline) as f64 * self.samples_per_nanosecond;
        let surplus = samples_spanned - sample_count as f64;
        let threshold = (self.tolerance_frames * sample_count as f64).max(1.0);

        if surplus > threshold {
            surplus.round() as u64
        } else {
            0
        }
    }

    /// Forgets the baseline, so the next frame starts a fresh comparison.
    pub fn reset(&mut self) {
        self.previous_timestamp = None;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // At 100 Hz one sample lasts exactly 10 ms, which keeps the arithmetic readable.
    const RATE_HZ: f64 = 100.0;
    const SAMPLE_NS: u64 = 10_000_000;

    #[test]
    fn first_frame_only_establishes_the_baseline() {
        assert_eq!(FrameGapDetector::new(RATE_HZ).observe(123_456_789, 50), 0);
    }

    #[test]
    fn contiguous_frames_report_no_gap() {
        let mut detector = FrameGapDetector::new(RATE_HZ);
        detector.observe(50 * SAMPLE_NS, 50);

        assert_eq!(detector.observe(100 * SAMPLE_NS, 50), 0);
        assert_eq!(detector.observe(150 * SAMPLE_NS, 50), 0);
    }

    #[test]
    fn a_lost_notification_is_reported_with_its_sample_count() {
        let mut detector = FrameGapDetector::new(RATE_HZ);
        detector.observe(50 * SAMPLE_NS, 50);

        // The frame ending at 100 never arrived; the one ending at 150 did.
        assert_eq!(detector.observe(150 * SAMPLE_NS, 50), 50);
    }

    #[test]
    fn jitter_within_tolerance_is_not_a_gap() {
        let mut detector = FrameGapDetector::new(RATE_HZ);
        detector.observe(50 * SAMPLE_NS, 50);

        // Ten samples late is well under half of a fifty-sample frame.
        assert_eq!(detector.observe(110 * SAMPLE_NS, 50), 0);
    }

    #[test]
    fn a_timestamp_that_does_not_advance_resets_the_baseline() {
        let mut detector = FrameGapDetector::new(RATE_HZ);
        detector.observe(500 * SAMPLE_NS, 50);

        assert_eq!(detector.observe(10 * SAMPLE_NS, 50), 0);
        assert_eq!(detector.observe(60 * SAMPLE_NS, 50), 0);
    }

    #[test]
    fn reset_forgets_the_baseline() {
        let mut detector = FrameGapDetector::new(RATE_HZ);
        detector.observe(50 * SAMPLE_NS, 50);
        detector.reset();

        assert_eq!(detector.observe(5_000 * SAMPLE_NS, 50), 0);
    }

    #[test]
    fn a_huge_clock_jump_is_reported_without_overflowing() {
        let mut detector = FrameGapDetector::new(RATE_HZ);
        detector.observe(1, 50);

        assert!(detector.observe(u64::MAX, 50) > 1_000_000_000);
    }

    #[test]
    #[should_panic(expected = "must be positive")]
    fn a_non_positive_rate_is_rejected() {
        let _ = FrameGapDetector::new(0.0);
    }
}
