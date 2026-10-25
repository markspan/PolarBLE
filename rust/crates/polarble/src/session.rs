//! One connected Polar H10: the GATT connection, the measurement streams and their LSL
//! outlets, and their deterministic teardown.

use std::pin::Pin;
use std::sync::Arc;
use std::sync::atomic::{AtomicU8, AtomicU64, Ordering};
use std::time::Duration;

use anyhow::{Context, Result, anyhow, bail};
use btleplug::api::Central as _;
use btleplug::api::{
    CentralEvent, CharPropFlags, Characteristic, Peripheral as _, ValueNotification,
};
use btleplug::platform::{Adapter, Peripheral};
use futures::{Stream, StreamExt};
use polarble_core::acquisition::{MeasurementConfiguration, PmdDataRouter, StreamStatistics};
use polarble_core::pmd::{PmdError, h10, uuids};
use polarble_lsl::{Liblsl, LslOutlet};
use tokio::sync::{mpsc, oneshot, watch};
use tokio::time::timeout;
use uuid::Uuid;

use crate::ble::Strap;
use crate::control_point::{ControlPoint, TEARDOWN_TIMEOUT};
use crate::task::BackgroundTask;
use crate::telemetry::DeviceStatus;

const PMD_CONTROL_POINT: Uuid = Uuid::from_u128(uuids::PMD_CONTROL_POINT);
const PMD_DATA: Uuid = Uuid::from_u128(uuids::PMD_DATA);
const BATTERY_LEVEL: Uuid = Uuid::from_u128(uuids::BATTERY_LEVEL);

/// How long connecting, and then discovering the services, may each take.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(30);

/// A stream's label paired with a shared handle to its live counters.
pub(crate) type StreamHandle = (&'static str, Arc<StreamStatistics>);

/// The live state a session shares with whatever displays it.
#[derive(Debug, Default)]
pub(crate) struct SessionStatus {
    battery: AtomicU8,
    pipeline_errors: AtomicU64,
}

impl SessionStatus {
    const BATTERY_UNKNOWN: u8 = u8::MAX;

    fn new() -> Self {
        Self {
            battery: AtomicU8::new(Self::BATTERY_UNKNOWN),
            pipeline_errors: AtomicU64::new(0),
        }
    }

    /// The current battery level and error count.
    pub(crate) fn snapshot(&self) -> DeviceStatus {
        DeviceStatus {
            battery_percent: self.battery_percent(),
            pipeline_errors: self.pipeline_errors.load(Ordering::Relaxed),
        }
    }

    fn battery_percent(&self) -> Option<u8> {
        Some(self.battery.load(Ordering::Relaxed))
            .filter(|&percent| percent != Self::BATTERY_UNKNOWN)
    }

    fn report_battery(&self, value: &[u8]) {
        if let Some(&percent) = value.first() {
            self.battery.store(percent.min(100), Ordering::Relaxed);
        }
    }
}

/// A connected strap.
///
/// The session is built up step by step, and every resource is recorded the moment it is
/// acquired. [`Session::close`] releases whatever is present, so a failure at any step of
/// connecting is rolled back through the same, single teardown path a normal stop uses.
pub(crate) struct Session {
    peripheral: Peripheral,
    name: String,
    measurements: Vec<MeasurementConfiguration>,
    streams: Vec<StreamHandle>,
    status: Arc<SessionStatus>,
    control_point: Option<ControlPoint>,
    subscriptions: Vec<Characteristic>,
    pump: Option<BackgroundTask>,
    disconnect_watch: Option<BackgroundTask>,
    lost: watch::Receiver<bool>,
    lost_sender: Arc<watch::Sender<bool>>,
}

impl Session {
    /// Connects to `strap` and starts every measurement, streaming into LSL outlets.
    ///
    /// If `cancel` completes first, whatever was set up so far is torn down properly, the
    /// strap included, before [`Cancelled`] is returned.
    pub(crate) async fn connect(
        adapter: &Adapter,
        strap: Strap,
        liblsl: &Arc<Liblsl>,
        cancel: impl Future<Output = ()>,
    ) -> Result<Self> {
        let (lost_sender, lost) = watch::channel(false);
        let mut session = Self {
            measurements: h10::measurements(&strap.name, &strap.address),
            peripheral: strap.peripheral,
            name: strap.name,
            streams: Vec::new(),
            status: Arc::new(SessionStatus::new()),
            control_point: None,
            subscriptions: Vec::new(),
            pump: None,
            disconnect_watch: None,
            lost,
            lost_sender: Arc::new(lost_sender),
        };

        let opened = tokio::select! {
            result = session.open(adapter, liblsl) => result,
            () = cancel => Err(Cancelled.into()),
        };

        match opened {
            Ok(()) => Ok(session),
            Err(error) => {
                session.close().await;
                Err(error)
            }
        }
    }

    /// The strap's name.
    pub(crate) fn name(&self) -> &str {
        &self.name
    }

    /// The measurement streams, in the order they were started.
    pub(crate) fn streams(&self) -> &[StreamHandle] {
        &self.streams
    }

    /// Battery level and error counts.
    pub(crate) fn status(&self) -> &SessionStatus {
        &self.status
    }

    /// Resolves when the strap disconnects on its own.
    pub(crate) async fn connection_lost(&self) {
        let mut lost = self.lost.clone();
        // An error means the sender is gone, which only happens once the session is closed.
        let _ = lost.wait_for(|&lost| lost).await;
    }

    /// Stops every measurement, unsubscribes, closes the outlets and disconnects.
    ///
    /// Never fails, and returns promptly even when the strap is already out of range.
    /// Stopping the measurements matters: without it the strap keeps streaming, and draining
    /// its battery, after the application has gone.
    pub(crate) async fn close(mut self) {
        // Dropping the watch aborts it; there is nothing left for it to report.
        self.disconnect_watch = None;

        // Radio traffic to a strap that is gone can only time out, so skip it.
        let connected = timeout(TEARDOWN_TIMEOUT, self.peripheral.is_connected())
            .await
            .is_ok_and(|connected| connected.unwrap_or(false));

        if connected {
            if let Some(control_point) = &self.control_point {
                for measurement in &self.measurements {
                    control_point.try_send(&measurement.stop_command()).await;
                }
            }

            for characteristic in &self.subscriptions {
                let _ = timeout(
                    TEARDOWN_TIMEOUT,
                    self.peripheral.unsubscribe(characteristic),
                )
                .await;
            }
        }

        // Ending the pump drops the router and with it every outlet, which removes the streams
        // from the network: a recorder still bound to them sees a clean end of stream.
        if let Some(pump) = self.pump.take() {
            pump.stop().await;
        }

        if connected {
            let _ = timeout(TEARDOWN_TIMEOUT, self.peripheral.disconnect()).await;
        }
    }

    async fn open(&mut self, adapter: &Adapter, liblsl: &Arc<Liblsl>) -> Result<()> {
        timeout(CONNECT_TIMEOUT, self.peripheral.connect())
            .await
            .map_err(|_| {
                anyhow!("the strap did not accept the connection within {CONNECT_TIMEOUT:?}")
            })?
            .context(
                "could not connect; make sure the strap is worn and not connected elsewhere",
            )?;
        self.watch_for_disconnect(adapter).await?;

        timeout(CONNECT_TIMEOUT, self.peripheral.discover_services())
            .await
            .map_err(|_| anyhow!("discovering the strap's services timed out"))?
            .context("could not discover the strap's services")?;

        let characteristics = self.peripheral.characteristics();
        let find = |uuid: Uuid| characteristics.iter().find(|c| c.uuid == uuid).cloned();
        let control_point = find(PMD_CONTROL_POINT)
            .context("the Polar measurement service was not available; is this a Polar H10?")?;
        let data =
            find(PMD_DATA).context("a required Polar measurement characteristic was missing")?;
        let battery = find(BATTERY_LEVEL);

        // The outlets exist before notifications are enabled, so no sample can arrive ahead
        // of the stream that should carry it.
        let router = self.create_router(liblsl)?;

        // Taken before subscribing, so that no notification can slip past unobserved.
        let notifications = self
            .peripheral
            .notifications()
            .await
            .context("could not listen for notifications")?;
        let (responses, response_receiver) = mpsc::unbounded_channel();
        let status = Arc::clone(&self.status);
        let lost = Arc::clone(&self.lost_sender);
        self.pump = Some(BackgroundTask::spawn(move |shutdown| {
            pump(notifications, router, responses, status, lost, shutdown)
        }));

        // Responses arrive as indications; without them a refused configuration would be
        // indistinguishable from a stream that has simply not started yet.
        self.subscribe(&control_point, "the PMD control point")
            .await?;
        self.subscribe(&data, "PMD data").await?;

        // Stored before anything is started, so that a refusal part-way through still lets
        // `close` stop the measurements that did start.
        let control = self.control_point.insert(ControlPoint::new(
            self.peripheral.clone(),
            control_point,
            response_receiver,
        ));
        for measurement in &self.measurements {
            start_measurement(control, measurement).await?;
        }

        if let Some(battery) = battery {
            self.start_battery_monitoring(&battery).await;
        }

        Ok(())
    }

    fn create_router(&mut self, liblsl: &Arc<Liblsl>) -> Result<PmdDataRouter> {
        let mut streams = Vec::with_capacity(self.measurements.len());
        for measurement in &self.measurements {
            let outlet = LslOutlet::new(liblsl, &measurement.stream)
                .with_context(|| format!("could not create the {} outlet", measurement.label))?;
            let stream = measurement.create_stream(Box::new(outlet))?;
            self.streams.push((measurement.label, stream.statistics()));
            streams.push(stream);
        }

        Ok(PmdDataRouter::new(streams)?)
    }

    async fn subscribe(&mut self, characteristic: &Characteristic, what: &str) -> Result<()> {
        self.peripheral
            .subscribe(characteristic)
            .await
            .with_context(|| format!("could not subscribe to {what}"))?;
        self.subscriptions.push(characteristic.clone());
        Ok(())
    }

    /// Takes a first battery reading and subscribes to changes where the strap supports it.
    /// Battery reporting is a convenience, so every failure here is ignored.
    async fn start_battery_monitoring(&mut self, battery: &Characteristic) {
        if let Ok(Ok(value)) = timeout(TEARDOWN_TIMEOUT, self.peripheral.read(battery)).await {
            self.status.report_battery(&value);
        }

        if battery.properties.contains(CharPropFlags::NOTIFY) {
            let _ = self.subscribe(battery, "the battery level").await;
        }
    }

    async fn watch_for_disconnect(&mut self, adapter: &Adapter) -> Result<()> {
        let mut events = adapter
            .events()
            .await
            .context("could not listen for Bluetooth events")?;
        let id = self.peripheral.id();
        let lost = Arc::clone(&self.lost_sender);

        self.disconnect_watch = Some(BackgroundTask::spawn(move |mut shutdown| async move {
            loop {
                tokio::select! {
                    _ = &mut shutdown => return,
                    event = events.next() => match event {
                        Some(CentralEvent::DeviceDisconnected(disconnected)) if disconnected == id => {
                            lost.send_replace(true);
                            return;
                        }
                        Some(_) => {}
                        None => return,
                    },
                }
            }
        }));
        Ok(())
    }
}

/// Starts one measurement and insists that the strap accepted it.
async fn start_measurement(
    control: &mut ControlPoint,
    measurement: &MeasurementConfiguration,
) -> Result<()> {
    let what = format!("{} start", measurement.display_name);
    let mut response = control.send(&measurement.start_command, &what).await?;

    if response.error == PmdError::AlreadyInState {
        // The strap is still streaming from a session that ended without stopping it.
        // Restart, so that it runs with exactly the settings requested now.
        let stop = format!("{} stop", measurement.display_name);
        control.send(&measurement.stop_command(), &stop).await?;
        response = control.send(&measurement.start_command, &what).await?;
    }

    if !response.is_success() {
        bail!(
            "the strap refused the {} configuration: {:?}",
            measurement.display_name,
            response.error
        );
    }

    Ok(())
}

/// Distributes every notification from the strap: measurement frames to the router, control
/// point indications to the waiting request, battery readings to the shared status.
///
/// It owns the router, and therefore every outlet: they live exactly as long as this task.
async fn pump(
    mut notifications: Pin<Box<dyn Stream<Item = ValueNotification> + Send>>,
    mut router: PmdDataRouter,
    responses: mpsc::UnboundedSender<Vec<u8>>,
    status: Arc<SessionStatus>,
    lost: Arc<watch::Sender<bool>>,
    mut shutdown: oneshot::Receiver<()>,
) {
    loop {
        let notification = tokio::select! {
            biased;
            _ = &mut shutdown => return,
            notification = notifications.next() => notification,
        };

        let Some(ValueNotification { uuid, value, .. }) = notification else {
            // The notification stream only ends when the connection does.
            lost.send_replace(true);
            return;
        };

        match uuid {
            PMD_DATA => {
                if let Err(error) = router.route(&value) {
                    // Reported once; after that the count in the telemetry says it persists.
                    if status.pipeline_errors.fetch_add(1, Ordering::Relaxed) == 0 {
                        eprintln!("warning: {error}");
                    }
                }
            }
            // The receiver is gone only once connecting has failed or finished.
            PMD_CONTROL_POINT => drop(responses.send(value)),
            BATTERY_LEVEL => status.report_battery(&value),
            _ => {}
        }
    }
}

/// Connecting was cancelled before it finished.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Cancelled;

impl std::fmt::Display for Cancelled {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("connecting was cancelled")
    }
}

impl std::error::Error for Cancelled {}
