//! `polarble`: streams a Polar H10's ECG and accelerometer data to LabStreamingLayer.
//!
//! This binary is a thin front end. Everything about the PMD protocol and the data path lives
//! in `polarble-core`, and LabStreamingLayer in `polarble-lsl`; the modules here only move
//! bytes between Bluetooth (through `btleplug`, so it runs on Windows, Linux and macOS) and
//! that pipeline.

#![forbid(unsafe_code)]

mod ble;
mod cli;
mod control_point;
mod run;
mod session;
mod simulate;
mod task;
mod telemetry;

use std::process::ExitCode;

use clap::Parser;

#[tokio::main]
async fn main() -> ExitCode {
    let cli = cli::Cli::parse();

    match run::run(cli).await {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("error: {error:#}");
            ExitCode::FAILURE
        }
    }
}
