//! Finding Polar straps over Bluetooth LE.

use std::collections::HashSet;
use std::fmt;
use std::pin::Pin;
use std::time::Duration;

use anyhow::{Context, Result, bail};
use btleplug::api::{BDAddr, Central, CentralEvent, Manager as _, Peripheral as _, ScanFilter};
use btleplug::platform::{Adapter, Manager, Peripheral, PeripheralId};
use futures::{Stream, StreamExt};
use tokio::time::{Instant, timeout_at};

/// How long to keep listening after the first strap appears, when no filter picks one, so
/// that a second strap in range is noticed rather than silently ignored.
const SETTLE_TIME: Duration = Duration::from_secs(2);

/// A Polar strap seen in an advertisement.
#[derive(Clone, Debug)]
pub(crate) struct Strap {
    /// The connection handle.
    pub(crate) peripheral: Peripheral,
    /// The advertised local name, e.g. `Polar H10 12345678`.
    pub(crate) name: String,
    /// The Bluetooth address as twelve upper-case hex digits, as used in stream source ids.
    pub(crate) address: String,
}

impl Strap {
    fn matches(&self, filter: &str) -> bool {
        let filter = filter.to_lowercase();
        self.name.to_lowercase().contains(&filter) || self.address.to_lowercase().contains(&filter)
    }
}

impl fmt::Display for Strap {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{} [{}]", self.name, self.address)
    }
}

/// The first Bluetooth adapter on this machine.
pub(crate) async fn first_adapter() -> Result<Adapter> {
    let manager = Manager::new().await.context("Bluetooth is not available")?;
    manager
        .adapters()
        .await
        .context("could not list Bluetooth adapters")?
        .into_iter()
        .next()
        .context("no Bluetooth adapter was found; check that Bluetooth is on")
}

/// Reports each distinct Polar strap once, for as long as it is polled.
pub(crate) struct Scanner<'a> {
    adapter: &'a Adapter,
    events: Pin<Box<dyn Stream<Item = CentralEvent> + Send>>,
    seen: HashSet<PeripheralId>,
}

impl<'a> Scanner<'a> {
    /// Starts scanning.
    pub(crate) async fn start(adapter: &'a Adapter) -> Result<Self> {
        let events = adapter
            .events()
            .await
            .context("could not listen for Bluetooth events")?;
        adapter
            .start_scan(ScanFilter::default())
            .await
            .context("could not start scanning; check that Bluetooth is on")?;

        Ok(Self {
            adapter,
            events,
            seen: HashSet::new(),
        })
    }

    /// Waits for the next strap not reported before, or `None` if scanning ends.
    pub(crate) async fn next_strap(&mut self) -> Option<Strap> {
        while let Some(event) = self.events.next().await {
            // The name often arrives in a scan response after the device was first seen, so
            // updates are inspected as well as discoveries.
            let (CentralEvent::DeviceDiscovered(id) | CentralEvent::DeviceUpdated(id)) = event
            else {
                continue;
            };

            if self.seen.contains(&id) {
                continue;
            }

            if let Some(strap) = self.inspect(&id).await {
                self.seen.insert(id);
                return Some(strap);
            }
        }

        None
    }

    /// Stops scanning. Connecting is faster, and more reliable on some adapters, without a
    /// scan running.
    pub(crate) async fn stop(self) {
        // Nothing useful can be done if the radio already stopped the scan itself.
        let _ = self.adapter.stop_scan().await;
    }

    async fn inspect(&self, id: &PeripheralId) -> Option<Strap> {
        let peripheral = self.adapter.peripheral(id).await.ok()?;
        let properties = peripheral.properties().await.ok()??;

        // Match any Polar strap, not just the H10. An empty local name carries no identity.
        let name = properties.local_name.filter(|name| {
            name.get(..5)
                .is_some_and(|prefix| prefix.eq_ignore_ascii_case("polar"))
        })?;

        let address = address_text(properties.address, id);
        Some(Strap {
            peripheral,
            name,
            address,
        })
    }
}

/// Scans until a strap matching `filter` appears, or, without a filter, until exactly one
/// strap has been found.
pub(crate) async fn find_strap(
    adapter: &Adapter,
    filter: Option<&str>,
    scan_timeout: Duration,
) -> Result<Strap> {
    let mut scanner = Scanner::start(adapter).await?;
    let mut deadline = Instant::now() + scan_timeout;
    let mut found = Vec::new();

    while let Ok(Some(strap)) = timeout_at(deadline, scanner.next_strap()).await {
        match filter {
            Some(filter) if strap.matches(filter) => {
                found = vec![strap];
                break;
            }
            Some(_) => {}
            None => {
                if found.is_empty() {
                    deadline = deadline.min(Instant::now() + SETTLE_TIME);
                }
                found.push(strap);
            }
        }
    }
    scanner.stop().await;

    match (found.len(), filter) {
        (1, _) => Ok(found.remove(0)),
        (0, Some(filter)) => bail!("no Polar strap matching `{filter}` was found; is it worn?"),
        (0, None) => bail!("no Polar strap was found; a strap only advertises while it is worn"),
        _ => {
            let list: Vec<String> = found.iter().map(|strap| format!("  {strap}")).collect();
            bail!(
                "several Polar straps are in range; choose one with --device:\n{}",
                list.join("\n")
            )
        }
    }
}

/// The text used for a strap's address in stream source ids.
///
/// macOS hides Bluetooth addresses; there the stable per-machine peripheral id is used.
fn address_text(address: BDAddr, id: &PeripheralId) -> String {
    hex_address(address).unwrap_or_else(|| id.to_string().replace('-', "").to_uppercase())
}

/// Formats an address as twelve upper-case hex digits, identical to the .NET implementation,
/// so that both produce the same stream source ids for the same strap. Returns `None` for
/// the all-zero address a platform reports when it hides the real one.
fn hex_address(address: BDAddr) -> Option<String> {
    (address != BDAddr::default()).then(|| format!("{:012X}", u64::from(address)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn addresses_are_formatted_like_the_dotnet_implementation() {
        let address = BDAddr::from([0xA0, 0xB1, 0xC2, 0xD3, 0xE4, 0x05]);

        assert_eq!(hex_address(address).as_deref(), Some("A0B1C2D3E405"));
    }

    #[test]
    fn a_hidden_address_is_not_used() {
        assert_eq!(hex_address(BDAddr::default()), None);
    }
}
