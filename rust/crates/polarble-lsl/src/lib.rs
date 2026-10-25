//! A minimal binding to [liblsl](https://github.com/sccn/liblsl), the LabStreamingLayer
//! library: just enough to advertise outlets and push `f32` chunks, which is all PolarBLE
//! needs.
//!
//! liblsl is loaded at run time rather than linked, so building needs no native toolchain or
//! import library, and the same binary works with the `lsl.dll` that ships in
//! `third_party/liblsl` or with any liblsl installed on the system. See [`Liblsl::load`] for
//! where it is looked for.
//!
//! All `unsafe` code in PolarBLE lives in this crate, behind the safe [`Liblsl`] and
//! [`LslOutlet`] types.

mod library;
mod outlet;

pub use library::{LIBRARY_ENV_VAR, Liblsl, LoadError};
pub use outlet::{LslOutlet, OutletError};
