use std::ffi::{CStr, CString, NulError, c_ulong};
use std::fmt;
use std::ptr::NonNull;
use std::sync::Arc;

use polarble_core::streaming::{SampleSink, SinkError, StreamDefinition};

use crate::Liblsl;
use crate::library::{CHANNEL_FORMAT_FLOAT32, Handle};

/// How much data liblsl buffers for a consumer that falls behind. With a nominal rate, liblsl
/// interprets this as seconds.
const MAX_BUFFERED_SECONDS: i32 = 360;

/// Zero lets the sender decide the chunk granularity, which is what we want: one chunk is
/// pushed per Bluetooth notification already.
const OUTLET_CHUNK_SIZE: i32 = 0;

/// A LabStreamingLayer outlet that advertises one stream and receives its samples.
///
/// The outlet is visible on the network from creation until it is dropped. Dropping it is
/// what removes the stream; a recorder still bound to it then sees a clean end of stream.
///
/// Samples are pushed a whole chunk at a time with no timestamp, so liblsl stamps each chunk
/// at push time and derives the earlier samples in it from the stream's nominal rate.
pub struct LslOutlet {
    // Declared before `info` so that it is dropped first: the outlet was made from the info.
    outlet: OwnedHandle,
    info: OwnedHandle,
    channel_count: usize,
    name: String,
}

impl LslOutlet {
    /// Creates the outlet described by `definition` and advertises it.
    ///
    /// Besides the channel layout, the stream's description carries a `channels` block
    /// (label, unit and type per channel) and an `acquisition` block (manufacturer, model,
    /// device address and every entry of [`StreamDefinition::acquisition`]).
    ///
    /// # Errors
    ///
    /// Returns [`OutletError`] if a text field contains a NUL byte, the stream has no
    /// channels, or liblsl refuses to create the stream.
    pub fn new(library: &Arc<Liblsl>, definition: &StreamDefinition) -> Result<Self, OutletError> {
        let channel_count = i32::try_from(definition.channels.len())
            .ok()
            .filter(|&count| count > 0)
            .ok_or(OutletError::InvalidChannelCount(definition.channels.len()))?;

        let name = c_string("name", &definition.name)?;
        let stream_type = c_string("type", &definition.stream_type)?;
        let source_id = c_string("source_id", &definition.source_id)?;
        let api = library.api();

        // SAFETY: every pointer is a live, NUL-terminated string for the duration of the call.
        let info = unsafe {
            (api.lsl_create_streaminfo)(
                name.as_ptr(),
                stream_type.as_ptr(),
                channel_count,
                definition.sample_rate_hz,
                CHANNEL_FORMAT_FLOAT32,
                source_id.as_ptr(),
            )
        };
        let info = OwnedHandle::new(library, info, api.lsl_destroy_streaminfo)
            .ok_or_else(|| OutletError::Liblsl(library.last_error()))?;

        describe(library, &info, definition)?;

        // SAFETY: `info` is a live stream info; liblsl copies what it needs from it.
        let outlet = unsafe {
            (api.lsl_create_outlet)(info.as_ptr(), OUTLET_CHUNK_SIZE, MAX_BUFFERED_SECONDS)
        };
        let outlet = OwnedHandle::new(library, outlet, api.lsl_destroy_outlet)
            .ok_or_else(|| OutletError::Liblsl(library.last_error()))?;

        Ok(Self {
            outlet,
            info,
            channel_count: definition.channels.len(),
            name: definition.name.clone(),
        })
    }

    /// The advertised stream name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// True when at least one consumer is currently subscribed.
    #[must_use]
    pub fn has_consumers(&self) -> bool {
        // SAFETY: the outlet is live for as long as `self`.
        unsafe { (self.outlet.library.api().lsl_have_consumers)(self.outlet.as_ptr()) > 0 }
    }

    /// The stream's full description as liblsl advertises it, as XML.
    #[must_use]
    pub fn info_xml(&self) -> String {
        let api = self.info.library.api();

        // SAFETY: the info is live; the returned string is owned by us and released below.
        let xml = unsafe { (api.lsl_get_xml)(self.info.as_ptr()) };
        if xml.is_null() {
            return String::new();
        }

        // SAFETY: non-null and NUL-terminated per liblsl's contract.
        let text = unsafe { CStr::from_ptr(xml) }
            .to_string_lossy()
            .into_owned();
        // SAFETY: `xml` came from lsl_get_xml and has not been released yet.
        unsafe { (api.lsl_destroy_string)(xml) };
        text
    }
}

impl SampleSink for LslOutlet {
    fn channel_count(&self) -> usize {
        self.channel_count
    }

    fn push_chunk(&mut self, interleaved: &[f32]) -> Result<(), SinkError> {
        if interleaved.is_empty() {
            return Ok(());
        }

        if !interleaved.len().is_multiple_of(self.channel_count) {
            return Err(Box::new(OutletError::RaggedChunk {
                values: interleaved.len(),
                channels: self.channel_count,
            }));
        }

        let elements =
            c_ulong::try_from(interleaved.len()).map_err(|_| OutletError::ChunkTooLarge)?;

        // SAFETY: the outlet is live and `interleaved` holds exactly `elements` floats, a whole
        // number of samples; liblsl copies them before returning.
        let code = unsafe {
            (self.outlet.library.api().lsl_push_chunk_f)(
                self.outlet.as_ptr(),
                interleaved.as_ptr(),
                elements,
            )
        };
        if code < 0 {
            return Err(Box::new(OutletError::Push(code)));
        }

        Ok(())
    }
}

impl fmt::Debug for LslOutlet {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("LslOutlet")
            .field("name", &self.name)
            .field("channel_count", &self.channel_count)
            .finish_non_exhaustive()
    }
}

// SAFETY: liblsl outlets and stream infos are not tied to the thread that created them, and
// liblsl documents pushing to an outlet as thread-safe. `LslOutlet` exposes pushing only
// through `&mut self`, so it is never even used from two threads at once.
unsafe impl Send for LslOutlet {}

/// Writes the `channels` and `acquisition` blocks into the stream's description.
fn describe(
    library: &Liblsl,
    info: &OwnedHandle,
    definition: &StreamDefinition,
) -> Result<(), OutletError> {
    let api = library.api();
    // SAFETY: `info` is a live stream info; its description element lives as long as it does.
    let description = unsafe { (api.lsl_get_desc)(info.as_ptr()) };
    let element = XmlElement {
        library,
        handle: description,
    };

    let channels = element.append_child("channels")?;
    for channel in &definition.channels {
        let node = channels.append_child("channel")?;
        node.append_value("label", &channel.label)?;
        node.append_value("unit", &channel.unit)?;
        node.append_value("type", &channel.channel_type)?;
    }

    let acquisition = element.append_child("acquisition")?;
    acquisition.append_value("manufacturer", &definition.manufacturer)?;
    acquisition.append_value("model", &definition.model)?;
    acquisition.append_value("device_address", &definition.device_address)?;
    for (key, value) in &definition.acquisition {
        acquisition.append_value(key, value)?;
    }

    Ok(())
}

/// An element of a stream description. Borrowed from, and owned by, its stream info.
struct XmlElement<'a> {
    library: &'a Liblsl,
    handle: Handle,
}

impl XmlElement<'_> {
    fn append_child(&self, name: &str) -> Result<Self, OutletError> {
        let name = c_string("element name", name)?;
        // SAFETY: the element belongs to a live stream info and `name` is NUL-terminated.
        let handle = unsafe { (self.library.api().lsl_append_child)(self.handle, name.as_ptr()) };
        Ok(Self {
            library: self.library,
            handle,
        })
    }

    fn append_value(&self, name: &str, value: &str) -> Result<(), OutletError> {
        let c_name = c_string("element name", name)?;
        let c_value = c_string(name, value)?;
        // SAFETY: as for `append_child`; liblsl copies both strings.
        unsafe {
            (self.library.api().lsl_append_child_value)(
                self.handle,
                c_name.as_ptr(),
                c_value.as_ptr(),
            );
        }
        Ok(())
    }
}

/// A liblsl object that is destroyed with its matching function when dropped.
struct OwnedHandle {
    library: Arc<Liblsl>,
    handle: NonNull<std::ffi::c_void>,
    destroy: unsafe extern "C" fn(Handle),
}

impl OwnedHandle {
    /// Takes ownership of `handle`, or returns `None` if liblsl returned null.
    fn new(
        library: &Arc<Liblsl>,
        handle: Handle,
        destroy: unsafe extern "C" fn(Handle),
    ) -> Option<Self> {
        NonNull::new(handle).map(|handle| Self {
            library: Arc::clone(library),
            handle,
            destroy,
        })
    }

    fn as_ptr(&self) -> Handle {
        self.handle.as_ptr()
    }
}

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        // SAFETY: the handle came from the constructor matching `destroy`, is released exactly
        // once, here, and the library it lives in is kept loaded by `self.library`.
        unsafe { (self.destroy)(self.handle.as_ptr()) };
    }
}

fn c_string(field: &str, value: &str) -> Result<CString, OutletError> {
    CString::new(value).map_err(|source| OutletError::InvalidText {
        field: field.to_owned(),
        source,
    })
}

/// Why an outlet could not be created or pushed to.
#[derive(Debug)]
pub enum OutletError {
    /// A text field of the stream definition contains a NUL byte.
    InvalidText {
        /// The field that was rejected.
        field: String,
        /// Where the NUL byte is.
        source: NulError,
    },
    /// The stream has no channels, or more than liblsl can describe.
    InvalidChannelCount(usize),
    /// liblsl refused to create the stream info or the outlet.
    Liblsl(Option<String>),
    /// A pushed chunk is not a whole number of samples.
    RaggedChunk {
        /// Values in the chunk.
        values: usize,
        /// Channels per sample.
        channels: usize,
    },
    /// A pushed chunk is larger than liblsl can accept in one call.
    ChunkTooLarge,
    /// liblsl reported an error code while pushing.
    Push(i32),
}

impl fmt::Display for OutletError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidText { field, .. } => {
                write!(f, "the stream's {field} contains a NUL byte")
            }
            Self::InvalidChannelCount(count) => write!(f, "a stream cannot have {count} channels"),
            Self::Liblsl(Some(message)) => {
                write!(f, "liblsl refused to create the stream: {message}")
            }
            Self::Liblsl(None) => f.write_str("liblsl refused to create the stream"),
            Self::RaggedChunk { values, channels } => {
                write!(
                    f,
                    "a chunk of {values} values is not a whole number of {channels}-channel samples"
                )
            }
            Self::ChunkTooLarge => f.write_str("the chunk is too large to push in one call"),
            Self::Push(code) => write!(f, "liblsl failed to push a chunk (error {code})"),
        }
    }
}

impl std::error::Error for OutletError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::InvalidText { source, .. } => Some(source),
            _ => None,
        }
    }
}
