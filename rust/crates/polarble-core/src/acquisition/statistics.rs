use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

/// Live counters for one measurement stream.
///
/// Written by the task that processes notifications and read concurrently by whatever
/// displays them, typically through an `Arc`. They are what tells a stalled or lossy stream
/// apart from a healthy one while a recording is in progress.
///
/// Every method that depends on the current time has an `_at` variant taking the time
/// explicitly, which is what makes idle-time behaviour testable without sleeping.
#[derive(Debug)]
pub struct StreamStatistics {
    origin: Instant,
    frames: AtomicU64,
    samples: AtomicU64,
    unsupported_frames: AtomicU64,
    gaps: AtomicU64,
    samples_lost: AtomicU64,
    /// Nanoseconds after `origin` at which the last frame arrived; zero until then, so idle
    /// time counts from creation.
    last_activity_ns: AtomicU64,
}

impl StreamStatistics {
    /// Creates zeroed counters whose idle time starts now.
    #[must_use]
    pub fn new() -> Self {
        Self::starting_at(Instant::now())
    }

    /// Creates zeroed counters whose idle time starts at `origin`.
    #[must_use]
    pub fn starting_at(origin: Instant) -> Self {
        Self {
            origin,
            frames: AtomicU64::new(0),
            samples: AtomicU64::new(0),
            unsupported_frames: AtomicU64::new(0),
            gaps: AtomicU64::new(0),
            samples_lost: AtomicU64::new(0),
            last_activity_ns: AtomicU64::new(0),
        }
    }

    /// Records a decoded frame that was pushed to the sink.
    pub fn record_frame(&self, sample_count: usize, samples_lost_before: u64) {
        self.record_frame_at(sample_count, samples_lost_before, Instant::now());
    }

    /// Records a decoded frame that arrived at `now`.
    pub fn record_frame_at(&self, sample_count: usize, samples_lost_before: u64, now: Instant) {
        self.frames.fetch_add(1, Ordering::Relaxed);
        self.samples
            .fetch_add(sample_count as u64, Ordering::Relaxed);
        if samples_lost_before > 0 {
            self.gaps.fetch_add(1, Ordering::Relaxed);
            self.samples_lost
                .fetch_add(samples_lost_before, Ordering::Relaxed);
        }

        self.last_activity_ns
            .store(self.nanos_since_origin(now), Ordering::Relaxed);
    }

    /// Records a frame whose frame type cannot be decoded.
    pub fn record_unsupported_frame(&self) {
        self.unsupported_frames.fetch_add(1, Ordering::Relaxed);
    }

    /// Reads every counter at once.
    #[must_use]
    pub fn snapshot(&self) -> StatisticsSnapshot {
        self.snapshot_at(Instant::now())
    }

    /// Reads every counter, measuring idle time up to `now`.
    #[must_use]
    pub fn snapshot_at(&self, now: Instant) -> StatisticsSnapshot {
        let last_activity = Duration::from_nanos(self.last_activity_ns.load(Ordering::Relaxed));

        StatisticsSnapshot {
            frames: self.frames.load(Ordering::Relaxed),
            samples: self.samples.load(Ordering::Relaxed),
            unsupported_frames: self.unsupported_frames.load(Ordering::Relaxed),
            gaps: self.gaps.load(Ordering::Relaxed),
            samples_lost: self.samples_lost.load(Ordering::Relaxed),
            idle: now.saturating_duration_since(self.origin + last_activity),
        }
    }

    fn nanos_since_origin(&self, now: Instant) -> u64 {
        let elapsed = now.saturating_duration_since(self.origin).as_nanos();
        u64::try_from(elapsed).unwrap_or(u64::MAX)
    }
}

impl Default for StreamStatistics {
    fn default() -> Self {
        Self::new()
    }
}

/// A point-in-time copy of a [`StreamStatistics`].
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct StatisticsSnapshot {
    /// Frames decoded and pushed.
    pub frames: u64,
    /// Samples pushed.
    pub samples: u64,
    /// Frames dropped because their frame type is unknown.
    pub unsupported_frames: u64,
    /// Intervals in which notifications were lost on the radio link.
    pub gaps: u64,
    /// Estimated total samples lost in those gaps.
    pub samples_lost: u64,
    /// Time since the last frame arrived, or since the statistics were created.
    pub idle: Duration,
}

impl StatisticsSnapshot {
    /// True if any data has been lost or discarded.
    #[must_use]
    pub fn has_data_loss(&self) -> bool {
        self.gaps > 0 || self.unsupported_frames > 0
    }

    /// Whether no frame has arrived for at least `threshold`.
    #[must_use]
    pub fn is_stalled(&self, threshold: Duration) -> bool {
        self.idle >= threshold
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SECOND: Duration = Duration::from_secs(1);

    #[test]
    fn record_frame_accumulates_samples_and_gaps() {
        let statistics = StreamStatistics::new();

        statistics.record_frame(73, 0);
        statistics.record_frame(73, 146);
        statistics.record_unsupported_frame();

        let snapshot = statistics.snapshot();
        assert_eq!(snapshot.frames, 2);
        assert_eq!(snapshot.samples, 146);
        assert_eq!(snapshot.gaps, 1);
        assert_eq!(snapshot.samples_lost, 146);
        assert_eq!(snapshot.unsupported_frames, 1);
        assert!(snapshot.has_data_loss());
    }

    #[test]
    fn idle_time_counts_from_creation_until_the_first_frame() {
        let origin = Instant::now();
        let statistics = StreamStatistics::starting_at(origin);

        assert!(
            statistics
                .snapshot_at(origin + 4 * SECOND)
                .is_stalled(3 * SECOND)
        );
    }

    #[test]
    fn idle_time_restarts_with_each_frame() {
        let origin = Instant::now();
        let statistics = StreamStatistics::starting_at(origin);

        statistics.record_frame_at(1, 0, origin + 10 * SECOND);
        let snapshot = statistics.snapshot_at(origin + 11 * SECOND);

        assert_eq!(snapshot.idle, SECOND);
        assert!(!snapshot.is_stalled(3 * SECOND));
        assert!(!snapshot.has_data_loss());
    }
}
