use serde::Deserialize;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct TraceEvent {
    pub(crate) test_id: String,
    pub(crate) method_full_name: String,
    pub(crate) file_path: Option<String>,
    #[serde(default)]
    pub(crate) line: i32,
    #[serde(default)]
    pub(crate) parent_call_id: Option<i64>,
    pub(crate) call_id: i64,
    pub(crate) ordinal: i32,
    pub(crate) args_digest: Option<String>,
    pub(crate) args_rendered: Option<String>,
    pub(crate) return_digest: Option<String>,
    pub(crate) return_rendered: Option<String>,
    pub(crate) exception_type: Option<String>,
    #[serde(default)]
    pub(crate) is_harness: bool,
}

#[derive(Clone, Debug)]
pub(crate) struct LoadedEvent {
    pub(crate) event: TraceEvent,
    pub(crate) process_key: String,
    pub(crate) line_number: usize,
    pub(crate) relative_path: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct MemberEntry {
    pub(crate) assembly: String,
    #[serde(rename = "method")]
    pub(crate) method_full_name: Option<String>,
    pub(crate) status: String,
    pub(crate) skip_reason: Option<String>,
    #[serde(default)]
    pub(crate) is_test_root: bool,
    pub(crate) source_resolution: Option<String>,
    pub(crate) detail: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct AssemblyEntry {
    pub(crate) assembly: String,
    pub(crate) discovery: String,
    #[serde(default)]
    pub(crate) instrumented: bool,
    #[serde(default)]
    pub(crate) source_unavailable: bool,
    #[serde(default)]
    pub(crate) source_partial: bool,
}

#[derive(Clone, Debug)]
pub(crate) struct RunData {
    pub(crate) name: String,
    pub(crate) root: String,
    pub(crate) events: Vec<LoadedEvent>,
    pub(crate) members: Vec<MemberEntry>,
    pub(crate) assemblies: Vec<AssemblyEntry>,
    pub(crate) trace_file_count: usize,
    pub(crate) schema: String,
    pub(crate) language: String,
}

impl RunData {
    pub(crate) fn subject_event_count(&self) -> usize {
        self.events
            .iter()
            .filter(|loaded| !loaded.event.is_harness)
            .count()
    }

    pub(crate) fn harness_event_count(&self) -> usize {
        self.events
            .iter()
            .filter(|loaded| loaded.event.is_harness)
            .count()
    }
}
