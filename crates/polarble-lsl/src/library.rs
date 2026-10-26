use std::ffi::{CStr, OsString, c_char, c_double, c_int, c_ulong, c_void};
use std::fmt;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use libloading::Library;

/// Environment variable that names the liblsl library to load, overriding the search.
pub const LIBRARY_ENV_VAR: &str = "POLARBLE_LSL_LIBRARY";

/// The file names liblsl is known by on this platform, most specific first.
#[cfg(windows)]
const LIBRARY_NAMES: &[&str] = &["lsl.dll"];
#[cfg(target_os = "macos")]
const LIBRARY_NAMES: &[&str] = &["liblsl.dylib"];
#[cfg(all(unix, not(target_os = "macos")))]
const LIBRARY_NAMES: &[&str] = &["liblsl.so", "liblsl.so.2", "liblsl.so.1"];

/// An opaque liblsl object handle.
pub(crate) type Handle = *mut c_void;

/// `cft_float32` in liblsl's `lsl_channel_format_t`.
pub(crate) const CHANNEL_FORMAT_FLOAT32: c_int = 1;

/// Declares the table of liblsl functions this crate calls, and how to resolve it, from one
/// list, so a signature can never be declared in one place and loaded with another.
macro_rules! liblsl_api {
    ($( $(#[$doc:meta])* $field:ident: fn($($arg:ty),*) $(-> $ret:ty)?; )+) => {
        /// The liblsl C functions, resolved once when the library is loaded.
        #[derive(Clone, Copy, Debug)]
        pub(crate) struct Api {
            $( $(#[$doc])* pub(crate) $field: unsafe extern "C" fn($($arg),*) $(-> $ret)?, )+
        }

        impl Api {
            /// Resolves every function.
            ///
            /// # Safety
            ///
            /// `library` must be liblsl, so that each symbol has the declared signature.
            unsafe fn resolve(library: &Library) -> Result<Self, (&'static str, libloading::Error)> {
                Ok(Self {
                    $(
                        // SAFETY: the caller guarantees `library` is liblsl, whose C API
                        // declares this symbol with exactly this signature.
                        $field: unsafe {
                            *library
                                .get::<unsafe extern "C" fn($($arg),*) $(-> $ret)?>(stringify!($field))
                                .map_err(|error| (stringify!($field), error))?
                        },
                    )+
                })
            }
        }
    };
}

liblsl_api! {
    /// `lsl_streaminfo lsl_create_streaminfo(name, type, channel_count, nominal_srate, channel_format, source_id)`
    lsl_create_streaminfo: fn(*const c_char, *const c_char, i32, c_double, c_int, *const c_char) -> Handle;
    /// `void lsl_destroy_streaminfo(lsl_streaminfo)`
    lsl_destroy_streaminfo: fn(Handle);
    /// `lsl_xml_ptr lsl_get_desc(lsl_streaminfo)`
    lsl_get_desc: fn(Handle) -> Handle;
    /// `lsl_xml_ptr lsl_append_child(lsl_xml_ptr, name)`
    lsl_append_child: fn(Handle, *const c_char) -> Handle;
    /// `lsl_xml_ptr lsl_append_child_value(lsl_xml_ptr, name, value)`
    lsl_append_child_value: fn(Handle, *const c_char, *const c_char) -> Handle;
    /// `char *lsl_get_xml(lsl_streaminfo)`, released with `lsl_destroy_string`
    lsl_get_xml: fn(Handle) -> *mut c_char;
    /// `void lsl_destroy_string(char *)`
    lsl_destroy_string: fn(*mut c_char);
    /// `lsl_outlet lsl_create_outlet(lsl_streaminfo, chunk_size, max_buffered)`
    lsl_create_outlet: fn(Handle, i32, i32) -> Handle;
    /// `void lsl_destroy_outlet(lsl_outlet)`
    lsl_destroy_outlet: fn(Handle);
    /// `int32_t lsl_push_chunk_f(lsl_outlet, const float *data, unsigned long data_elements)`
    lsl_push_chunk_f: fn(Handle, *const f32, c_ulong) -> i32;
    /// `int32_t lsl_have_consumers(lsl_outlet)`
    lsl_have_consumers: fn(Handle) -> i32;
    /// `int32_t lsl_library_version(void)`
    lsl_library_version: fn() -> i32;
    /// `const char *lsl_last_error(void)`
    lsl_last_error: fn() -> *const c_char;
}

/// A loaded liblsl.
///
/// Cheap to share: every [`crate::LslOutlet`] holds an `Arc` to the library it was created
/// from, so the library stays loaded for as long as any outlet exists.
pub struct Liblsl {
    api: Api,
    path: PathBuf,
    // Declared last so it is dropped last; the function pointers above point into it.
    _library: Library,
}

impl Liblsl {
    /// Loads liblsl, looking in this order:
    ///
    /// 1. the path in the `POLARBLE_LSL_LIBRARY` environment variable, if it is set, and
    ///    nowhere else, so an explicit choice never silently falls back to another library;
    /// 2. the directory of the running executable;
    /// 3. the operating system's normal library search path.
    ///
    /// # Errors
    ///
    /// Returns [`LoadError`] listing every place that was tried, or naming a missing symbol.
    pub fn load() -> Result<Arc<Self>, LoadError> {
        let executable_dir = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(Path::to_path_buf));
        let candidates = candidates(std::env::var_os(LIBRARY_ENV_VAR), executable_dir.as_deref());

        let mut attempts = Vec::with_capacity(candidates.len());
        for candidate in candidates {
            match Self::load_from(&candidate) {
                Ok(library) => return Ok(library),
                Err(LoadError::NotFound {
                    attempts: mut failed,
                }) => attempts.append(&mut failed),
                Err(other) => return Err(other),
            }
        }

        Err(LoadError::NotFound { attempts })
    }

    /// Loads liblsl from exactly `path`.
    ///
    /// # Errors
    ///
    /// Returns [`LoadError`] if the library cannot be opened or lacks a required symbol.
    pub fn load_from(path: &Path) -> Result<Arc<Self>, LoadError> {
        // SAFETY: loading a library runs its initialisers. liblsl's are its own static
        // constructors, which is the documented way to use it; the caller chose this path.
        let library = unsafe { Library::new(path) }.map_err(|error| LoadError::NotFound {
            attempts: vec![(path.to_path_buf(), error)],
        })?;

        // SAFETY: the library was found under one of liblsl's names, or the user said it is
        // liblsl. A different library that happens to export every one of these names with
        // other signatures is not a case any loader can defend against.
        let api = unsafe { Api::resolve(&library) }.map_err(|(symbol, source)| {
            LoadError::MissingSymbol {
                library: path.to_path_buf(),
                symbol,
                source,
            }
        })?;

        Ok(Arc::new(Self {
            api,
            path: path.to_path_buf(),
            _library: library,
        }))
    }

    /// The path the library was loaded from.
    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// liblsl's version, as major * 100 + minor, e.g. 116 for 1.16.
    #[must_use]
    pub fn version(&self) -> i32 {
        // SAFETY: takes no arguments and only reads a constant.
        unsafe { (self.api.lsl_library_version)() }
    }

    /// The resolved C functions.
    pub(crate) fn api(&self) -> &Api {
        &self.api
    }

    /// liblsl's description of the last error on this thread, if it has one.
    pub(crate) fn last_error(&self) -> Option<String> {
        // SAFETY: returns a pointer to a thread-local, NUL-terminated buffer owned by liblsl,
        // or null; it is copied out immediately.
        let message = unsafe { (self.api.lsl_last_error)() };
        if message.is_null() {
            return None;
        }

        // SAFETY: non-null, and NUL-terminated per liblsl's contract.
        let message = unsafe { CStr::from_ptr(message) }.to_string_lossy();
        (!message.is_empty()).then(|| message.into_owned())
    }
}

impl fmt::Debug for Liblsl {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Liblsl")
            .field("path", &self.path)
            .field("version", &self.version())
            .finish_non_exhaustive()
    }
}

/// The places to look for liblsl, in order. Pure, so the search order is testable.
fn candidates(env_override: Option<OsString>, executable_dir: Option<&Path>) -> Vec<PathBuf> {
    if let Some(path) = env_override.filter(|path| !path.is_empty()) {
        return vec![PathBuf::from(path)];
    }

    let beside_executable = executable_dir
        .into_iter()
        .flat_map(|dir| LIBRARY_NAMES.iter().map(move |name| dir.join(name)));
    let on_search_path = LIBRARY_NAMES.iter().map(PathBuf::from);

    beside_executable.chain(on_search_path).collect()
}

/// Why liblsl could not be loaded.
#[derive(Debug)]
pub enum LoadError {
    /// No candidate could be opened.
    NotFound {
        /// Every path that was tried, with the reason it failed.
        attempts: Vec<(PathBuf, libloading::Error)>,
    },
    /// A library was opened but is missing a function this crate needs.
    MissingSymbol {
        /// The library that was opened.
        library: PathBuf,
        /// The missing function.
        symbol: &'static str,
        /// The loader's error.
        source: libloading::Error,
    },
}

impl fmt::Display for LoadError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotFound { attempts } => {
                write!(
                    f,
                    "liblsl could not be loaded; set {LIBRARY_ENV_VAR} to its path. Tried:"
                )?;
                for (path, error) in attempts {
                    write!(f, "\n  {}: {error}", path.display())?;
                }
                Ok(())
            }
            Self::MissingSymbol {
                library, symbol, ..
            } => write!(
                f,
                "{} is not a usable liblsl: it does not export {symbol}",
                library.display()
            ),
        }
    }
}

impl std::error::Error for LoadError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::NotFound { attempts } => attempts.first().map(|(_, error)| error as _),
            Self::MissingSymbol { source, .. } => Some(source),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_explicit_path_is_the_only_candidate() {
        let found = candidates(Some("/opt/lsl/custom.so".into()), Some(Path::new("/app")));

        assert_eq!(found, [PathBuf::from("/opt/lsl/custom.so")]);
    }

    #[test]
    fn the_executable_directory_is_searched_before_the_system_path() {
        let found = candidates(None, Some(Path::new("/app")));

        assert_eq!(found[0], Path::new("/app").join(LIBRARY_NAMES[0]));
        assert_eq!(
            found.last().unwrap(),
            &PathBuf::from(LIBRARY_NAMES[LIBRARY_NAMES.len() - 1])
        );
        assert_eq!(found.len(), 2 * LIBRARY_NAMES.len());
    }

    #[test]
    fn an_empty_override_is_ignored() {
        assert_eq!(
            candidates(Some(OsString::new()), None).len(),
            LIBRARY_NAMES.len()
        );
    }

    #[test]
    fn a_missing_library_reports_where_it_looked() {
        let error = Liblsl::load_from(Path::new("/nonexistent/liblsl.so")).unwrap_err();

        assert!(
            error.to_string().contains("/nonexistent/liblsl.so"),
            "{error}"
        );
    }
}
