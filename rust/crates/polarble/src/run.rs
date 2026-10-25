//! The commands.

use std::io::Write as _;
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context, Result, bail};
use polarble_lsl::Liblsl;
use tokio::sync::watch;
use tokio::time::{Instant, MissedTickBehavior, interval, sleep, timeout_at};

use crate::ble::{self, Scanner};
use crate::cli::{Cli, Command, OutputArgs};
use crate::session::{Cancelled, Session, StreamHandle};
use crate::simulate::{self, Simulation};
use crate::telemetry::{self, DeviceStatus};

/// Runs the command the user asked for.
pub(crate) async fn run(cli: Cli) -> Result<()> {
    let interrupt = Interrupt::listen();

    match cli.command {
        Command::Scan { duration } => scan(duration, &interrupt).await,
        Command::Stream {
            device,
            scan_timeout,
            output,
        } => stream(device.as_deref(), scan_timeout, &output, &interrupt).await,
        Command::Simulate { duration, output } => simulate(duration, &output, &interrupt).await,
    }
}

async fn scan(duration: Duration, interrupt: &Interrupt) -> Result<()> {
    let adapter = ble::first_adapter().await?;
    eprintln!("Scanning for {} s…", duration.as_secs_f64());

    let mut scanner = Scanner::start(&adapter).await?;
    let deadline = Instant::now() + duration;
    let mut found = 0_usize;
    loop {
        tokio::select! {
            () = interrupt.wait() => break,
            next = timeout_at(deadline, scanner.next_strap()) => match next {
                Ok(Some(strap)) => {
                    print_line(&strap.to_string());
                    found += 1;
                }
                Ok(None) | Err(_) => break,
            },
        }
    }
    scanner.stop().await;

    if found == 0 {
        eprintln!("No Polar strap found. A strap only advertises while it is worn.");
    }
    Ok(())
}

async fn stream(
    device: Option<&str>,
    scan_timeout: Duration,
    output: &OutputArgs,
    interrupt: &Interrupt,
) -> Result<()> {
    let liblsl = load_liblsl(output.lsl_library.as_deref())?;
    let adapter = ble::first_adapter().await?;

    eprintln!("Scanning for a Polar strap…");
    let strap = tokio::select! {
        strap = ble::find_strap(&adapter, device, scan_timeout) => strap?,
        () = interrupt.wait() => return Ok(()),
    };

    eprintln!("Connecting to {strap}… this can take up to a minute.");
    let session = match Session::connect(&adapter, strap, &liblsl, interrupt.wait()).await {
        Ok(session) => session,
        Err(error) if error.is::<Cancelled>() => {
            eprintln!("Cancelled.");
            return Ok(());
        }
        Err(error) => return Err(error),
    };
    eprintln!("Streaming from {}. Press Ctrl+C to stop.", session.name());

    let stop = report_until(
        async {
            tokio::select! {
                () = interrupt.wait() => Stop::Interrupted,
                () = session.connection_lost() => Stop::ConnectionLost,
            }
        },
        output.interval,
        session.streams(),
        || Some(session.status().snapshot()),
    )
    .await;

    eprintln!("Stopping the strap…");
    session.close().await;

    match stop {
        Stop::Interrupted | Stop::Finished => {
            eprintln!("Stopped.");
            Ok(())
        }
        Stop::ConnectionLost => {
            bail!("the strap disconnected; run the command again to resume recording")
        }
    }
}

async fn simulate(
    duration: Option<Duration>,
    output: &OutputArgs,
    interrupt: &Interrupt,
) -> Result<()> {
    let liblsl = load_liblsl(output.lsl_library.as_deref())?;
    let simulation = Simulation::start(&liblsl)?;
    eprintln!(
        "Simulating {}: streams {}_ECG and {}_ACC. Press Ctrl+C to stop.",
        simulate::DEVICE_NAME,
        simulate::DEVICE_NAME,
        simulate::DEVICE_NAME
    );

    report_until(
        async {
            tokio::select! {
                () = interrupt.wait() => Stop::Interrupted,
                () = sleep_for(duration) => Stop::Finished,
            }
        },
        output.interval,
        simulation.streams(),
        || None,
    )
    .await;

    simulation.stop().await;
    eprintln!("Stopped.");
    Ok(())
}

/// Why streaming ended.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Stop {
    /// The user pressed Ctrl+C.
    Interrupted,
    /// The requested duration elapsed.
    Finished,
    /// The strap went away.
    ConnectionLost,
}

/// Prints a telemetry line every `every` until `stop` completes, then returns its output.
///
/// `device` supplies the battery and error status of a real strap, or `None` for a
/// simulation, which has neither.
async fn report_until<T>(
    stop: impl Future<Output = T>,
    every: Duration,
    streams: &[StreamHandle],
    device: impl Fn() -> Option<DeviceStatus>,
) -> T {
    let mut ticker = interval(every);
    ticker.set_missed_tick_behavior(MissedTickBehavior::Delay);
    // The first tick completes immediately, before anything has arrived.
    ticker.tick().await;

    tokio::pin!(stop);
    loop {
        tokio::select! {
            result = &mut stop => return result,
            _ = ticker.tick() => {
                let snapshots: Vec<_> = streams
                    .iter()
                    .map(|(label, statistics)| (*label, statistics.snapshot()))
                    .collect();
                let report = telemetry::format(&snapshots, device());
                let marker = if report.has_problems { "!" } else { " " };
                print_line(&format!("{marker} {}", report.line));
            }
        }
    }
}

/// Writes a line of results to standard output. Unlike `println!`, it does not panic when
/// the output is a pipe whose reader has gone, as with `polarble scan | head -1`.
fn print_line(line: &str) {
    let _ = writeln!(std::io::stdout().lock(), "{line}");
}

/// Sleeps for `duration`, or forever if there is none.
async fn sleep_for(duration: Option<Duration>) {
    match duration {
        Some(duration) => sleep(duration).await,
        None => std::future::pending().await,
    }
}

/// Loads liblsl from `path`, or searches for it, and says which one is used.
fn load_liblsl(path: Option<&Path>) -> Result<Arc<Liblsl>> {
    let liblsl = match path {
        Some(path) => Liblsl::load_from(path),
        None => Liblsl::load(),
    }
    .context("LabStreamingLayer is not available")?;

    let version = liblsl.version();
    eprintln!(
        "Using liblsl {}.{} from {}",
        version / 100,
        version % 100,
        liblsl.path().display()
    );
    Ok(liblsl)
}

/// Ctrl+C, observable any number of times.
///
/// Once a handler is installed, Ctrl+C no longer terminates the process, so every command
/// waits on this instead and shuts down in an orderly way: in particular, a connected strap
/// is always told to stop streaming.
struct Interrupt {
    pressed: watch::Receiver<bool>,
}

impl Interrupt {
    fn listen() -> Self {
        let (sender, pressed) = watch::channel(false);
        tokio::spawn(async move {
            if tokio::signal::ctrl_c().await.is_ok() {
                sender.send_replace(true);
            }
        });
        Self { pressed }
    }

    /// Resolves once Ctrl+C has been pressed.
    async fn wait(&self) {
        let mut pressed = self.pressed.clone();
        if pressed.wait_for(|&pressed| pressed).await.is_err() {
            // Listening failed, so Ctrl+C keeps its default behaviour; nothing will resolve this.
            std::future::pending::<()>().await;
        }
    }
}
