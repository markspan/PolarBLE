//! Request/response access to the PMD control point.

use std::time::Duration;

use anyhow::{Context, Result, anyhow};
use btleplug::api::{Characteristic, Peripheral as _, WriteType};
use btleplug::platform::Peripheral;
use polarble_core::pmd::control_point::Response;
use tokio::sync::mpsc::UnboundedReceiver;
use tokio::time::{Instant, timeout, timeout_at};

/// How long the strap may take to answer a request.
const RESPONSE_TIMEOUT: Duration = Duration::from_secs(10);

/// How long a best-effort teardown write may take before it is abandoned.
pub(crate) const TEARDOWN_TIMEOUT: Duration = Duration::from_secs(2);

/// The PMD control point: a request is a write, and its response arrives later as an
/// indication, forwarded here by the notification pump.
///
/// Sending takes `&mut self`, so only one request is ever in flight, which is all the control
/// point services. Each indication is matched to the request it answers, so a late answer to
/// an abandoned request can never be mistaken for the current one. Interpreting a response is
/// left to the caller, which knows what a refusal means.
pub(crate) struct ControlPoint {
    peripheral: Peripheral,
    characteristic: Characteristic,
    responses: UnboundedReceiver<Vec<u8>>,
}

impl ControlPoint {
    pub(crate) fn new(
        peripheral: Peripheral,
        characteristic: Characteristic,
        responses: UnboundedReceiver<Vec<u8>>,
    ) -> Self {
        Self {
            peripheral,
            characteristic,
            responses,
        }
    }

    /// Writes a request and waits for the strap's response to it.
    ///
    /// `what` names the request in error messages, e.g. "ECG start".
    pub(crate) async fn send(&mut self, request: &[u8], what: &str) -> Result<Response> {
        // Anything still queued answers an earlier request.
        while self.responses.try_recv().is_ok() {}

        let deadline = Instant::now() + RESPONSE_TIMEOUT;
        timeout_at(
            deadline,
            self.peripheral
                .write(&self.characteristic, request, WriteType::WithResponse),
        )
        .await
        .map_err(|_| anyhow!("writing the {what} command timed out"))?
        .with_context(|| format!("writing the {what} command failed"))?;

        loop {
            let indication = timeout_at(deadline, self.responses.recv())
                .await
                .map_err(|_| {
                    anyhow!(
                        "the strap did not acknowledge the {what} command within {} seconds",
                        RESPONSE_TIMEOUT.as_secs()
                    )
                })?
                .ok_or_else(|| {
                    anyhow!("the connection closed while waiting for the {what} response")
                })?;

            if let Some(response) = Response::parse(&indication)
                && response.is_response_to(request)
            {
                return Ok(response);
            }
        }
    }

    /// Writes a request without waiting for a response. Never fails and never hangs: the
    /// strap may already be out of range.
    pub(crate) async fn try_send(&self, request: &[u8]) {
        let write = self
            .peripheral
            .write(&self.characteristic, request, WriteType::WithResponse);
        let _ = timeout(TEARDOWN_TIMEOUT, write).await;
    }
}
