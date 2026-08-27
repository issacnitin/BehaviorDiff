use crate::model::{AssemblyEntry, MemberEntry};
use crate::DiffOptions;
use serde::ser::{SerializeSeq, SerializeStruct};
use serde::{Deserialize, Serialize, Serializer};
use serde_json::json;
use std::borrow::Cow;
use std::cell::RefCell;
use std::cmp::Ordering;
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};
use std::fs::{self, File};
use std::io::{BufRead, BufReader, BufWriter, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

const MINIMUM_MATCHED_KEYS: usize = 100;
const MAXIMUM_BASE_COUNT_DRIFT_PERCENT: f64 = 10.0;
const MAXIMUM_NOISE_PERCENT: f64 = 20.0;
const MINIMUM_PATH_OVERLAP_PERCENT: f64 = 50.0;
const TRACE_SCHEMA: &str = "realdiff.trace/1";

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
struct Key {
    test: u32,
    method: u32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum CompactDigest {
    Sha256([u8; 32]),
    Text(u32),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct Locator {
    file: u16,
    offset: u64,
    length: u32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct Signature {
    ordinal: i32,
    args: Option<CompactDigest>,
    result: Option<CompactDigest>,
    exception: Option<u32>,
    markers: u8,
    path: u32,
    locator: Locator,
}

#[derive(Debug)]
struct Calls {
    is_harness: bool,
    signatures: Vec<Signature>,
}

#[derive(Clone, Copy, Debug)]
struct CallNode {
    call_id: i64,
    parent_call_id: i64,
    test: u32,
    method: u32,
    path: u32,
    process: u32,
    ordinal: i32,
    line: i32,
    flags: u8,
}

#[derive(Default)]
struct RunData {
    name: String,
    root: String,
    schema: String,
    language: String,
    files: Vec<PathBuf>,
    calls: HashMap<Key, Calls>,
    graph: Vec<CallNode>,
    members: Vec<MemberEntry>,
    assemblies: Vec<AssemblyEntry>,
    events: u64,
    subject_events: u64,
    harness_events: u64,
}

#[derive(Default)]
struct Interner {
    ids: HashMap<Arc<str>, u32>,
    values: Vec<Arc<str>>,
    profile: InternerProfile,
}

impl Interner {
    fn intern(&mut self, value: &str) -> Result<u32, String> {
        let started = self.profile.enabled.then(Instant::now);
        if let Some(id) = self.ids.get(value) {
            if let Some(started) = started {
                self.profile.hits += 1;
                self.profile.elapsed += started.elapsed();
            }
            return Ok(*id);
        }
        if self.profile.enabled {
            self.profile.misses += 1;
        }
        let id = u32::try_from(self.values.len())
            .map_err(|_| "streaming interner exceeded u32 identifiers".to_owned())?;
        let stored: Arc<str> = Arc::from(value);
        self.values.push(stored.clone());
        self.ids.insert(stored, id);
        if let Some(started) = started {
            self.profile.elapsed += started.elapsed();
        }
        Ok(id)
    }

    fn resolve(&self, id: u32) -> &str {
        &self.values[id as usize]
    }

    fn optional(&self, id: u32) -> Option<&str> {
        (id != u32::MAX).then(|| self.resolve(id))
    }
}

#[derive(Default)]
struct InternerProfile {
    enabled: bool,
    hits: u64,
    misses: u64,
    elapsed: Duration,
}

#[derive(Default)]
struct Profile {
    enabled: bool,
    total: Duration,
    load_runs: Vec<(String, Duration)>,
    trace_events: u64,
    trace_bytes: u64,
    stream_positions: u64,
    stream_position: Duration,
    line_reads: Duration,
    event_parse: Duration,
    digest_compaction: Duration,
    event_store: Duration,
    manifest_read: Duration,
    ordinal_sort: Duration,
    manifest_compare: Duration,
    trace_compare: Duration,
    artifact_runs_counts: Duration,
    artifact_matched: Duration,
    artifact_divergences: Duration,
    artifact_noise: Duration,
    artifact_coverage: Duration,
    artifact_call_tree: Duration,
    artifact_pr_call_tree: Duration,
    evidence_reads: u64,
    evidence_bytes: u64,
    evidence_read: Duration,
    artifact_total: Duration,
}

impl Profile {
    fn new() -> Self {
        Self {
            enabled: std::env::var_os("REALDIFF_RUST_PROFILE").is_some(),
            ..Self::default()
        }
    }

    fn start(&self) -> Option<Instant> {
        self.enabled.then(Instant::now)
    }

    fn report(&self, strings: &Interner) {
        if !self.enabled {
            return;
        }
        let milliseconds = |duration: Duration| duration.as_secs_f64() * 1000.0;
        let runs = self
            .load_runs
            .iter()
            .map(|(name, elapsed)| json!({ "name": name, "milliseconds": milliseconds(*elapsed) }))
            .collect::<Vec<_>>();
        eprintln!(
            "REALDIFF_RUST_PROFILE {}",
            serde_json::to_string(&json!({
                "totalMilliseconds": milliseconds(self.total),
                "runs": runs,
                "traceEvents": self.trace_events,
                "traceBytes": self.trace_bytes,
                "streamPosition": { "calls": self.stream_positions, "milliseconds": milliseconds(self.stream_position) },
                "lineReadMilliseconds": milliseconds(self.line_reads),
                "borrowedEventParseMilliseconds": milliseconds(self.event_parse),
                "interner": {
                    "hits": strings.profile.hits,
                    "misses": strings.profile.misses,
                    "values": strings.values.len(),
                    "milliseconds": milliseconds(strings.profile.elapsed),
                },
                "digestCompactionMilliseconds": milliseconds(self.digest_compaction),
                "eventStoreMilliseconds": milliseconds(self.event_store),
                "manifestReadMilliseconds": milliseconds(self.manifest_read),
                "ordinalSortMilliseconds": milliseconds(self.ordinal_sort),
                "manifestCompareMilliseconds": milliseconds(self.manifest_compare),
                "traceCompareMilliseconds": milliseconds(self.trace_compare),
                "artifact": {
                    "totalMilliseconds": milliseconds(self.artifact_total),
                    "runsAndCountsMilliseconds": milliseconds(self.artifact_runs_counts),
                    "matchedMilliseconds": milliseconds(self.artifact_matched),
                    "divergencesMilliseconds": milliseconds(self.artifact_divergences),
                    "noiseMilliseconds": milliseconds(self.artifact_noise),
                    "coverageMilliseconds": milliseconds(self.artifact_coverage),
                    "callTreeMilliseconds": milliseconds(self.artifact_call_tree),
                    "prCallTreeMilliseconds": milliseconds(self.artifact_pr_call_tree),
                    "evidenceReadBacks": self.evidence_reads,
                    "evidenceBytes": self.evidence_bytes,
                    "evidenceReadMilliseconds": milliseconds(self.evidence_read),
                },
            }))
            .expect("profile JSON must serialize")
        );
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct BorrowedEvent<'a> {
    #[serde(borrow)]
    test_id: Cow<'a, str>,
    #[serde(borrow)]
    method_full_name: Cow<'a, str>,
    #[serde(default, borrow)]
    file_path: Option<Cow<'a, str>>,
    #[serde(default)]
    line: i32,
    #[serde(default)]
    parent_call_id: Option<i64>,
    call_id: i64,
    ordinal: i32,
    #[serde(default, borrow)]
    args_digest: Option<Cow<'a, str>>,
    #[serde(default, borrow)]
    args_rendered: Option<Cow<'a, str>>,
    #[serde(default, borrow)]
    return_digest: Option<Cow<'a, str>>,
    #[serde(default, borrow)]
    return_rendered: Option<Cow<'a, str>>,
    #[serde(default, borrow)]
    exception_type: Option<Cow<'a, str>>,
    #[serde(default)]
    is_harness: bool,
}

#[derive(Clone, Debug)]
struct Gap {
    scope: String,
    assembly: String,
    method: Option<String>,
    base_state: String,
    pr_state: String,
    reason: String,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Confidence {
    Exact,
    Partial,
}

impl Confidence {
    fn name(self) -> &'static str {
        match self {
            Self::Exact => "Exact",
            Self::Partial => "Partial",
        }
    }
}

#[derive(Clone, Debug)]
struct Matched {
    key: Key,
    base_calls: usize,
    pr_calls: usize,
    confidence: Confidence,
    markers: u8,
    path: u32,
}

#[derive(Clone, Debug)]
struct Divergence {
    key: Key,
    ordinal: i32,
    kind: &'static str,
    detail: String,
    confidence: Confidence,
    markers: u8,
    base: Option<Locator>,
    pr: Option<Locator>,
    path: u32,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ManifestHeader<'a> {
    #[serde(borrow)]
    kind: Cow<'a, str>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RunManifest<'a> {
    #[serde(borrow)]
    schema: Cow<'a, str>,
    #[serde(borrow)]
    language: Cow<'a, str>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct AssemblyAccounting {
    assembly: String,
    discovered_members: i64,
    patched_members: i64,
    skipped_members: i64,
    patch_failed_members: i64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct WriterAccounting {
    enqueued: i64,
    written: i64,
    dropped: i64,
}

#[derive(Default)]
struct ManifestData {
    schema: String,
    language: String,
    members: Vec<MemberEntry>,
    assemblies: Vec<AssemblyEntry>,
    writer: Option<WriterAccounting>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct EvidenceEvent {
    args_digest: Option<String>,
    args_rendered: Option<String>,
    return_digest: Option<String>,
    return_rendered: Option<String>,
    exception_type: Option<String>,
}

pub(crate) fn run(options: &DiffOptions) -> Result<i32, String> {
    let total_started = Instant::now();
    let mut profile = Profile::new();
    let mut strings = Interner::default();
    strings.profile.enabled = profile.enabled;
    let base1 = load_run(
        "base_run1",
        &options.base1,
        options.base_root.as_deref(),
        true,
        &mut strings,
        &mut profile,
    )?;
    let base2 = load_run(
        "base_run2",
        &options.base2,
        options.base_root.as_deref(),
        false,
        &mut strings,
        &mut profile,
    )?;
    let base3 = if options.base3.is_empty() {
        None
    } else {
        Some(load_run(
            "base_run3",
            &options.base3,
            options.base_root.as_deref(),
            false,
            &mut strings,
            &mut profile,
        )?)
    };
    let pr = load_run(
        "pr_run",
        &options.pr,
        options.pr_root.as_deref(),
        true,
        &mut strings,
        &mut profile,
    )?;
    let bases: Vec<_> = [Some(&base1), Some(&base2), base3.as_ref()]
        .into_iter()
        .flatten()
        .collect();
    let mut refusals = Vec::new();

    let languages: BTreeSet<_> = bases
        .iter()
        .copied()
        .chain(std::iter::once(&pr))
        .map(|run| run.language.as_str())
        .collect();
    if languages.len() != 1 {
        refusals.push(format!(
            "STEP 0: traces from different languages cannot be compared ({}). Digests are only comparable within one language.",
            languages.into_iter().collect::<Vec<_>>().join(" vs ")
        ));
    }
    let base_paths = relative_paths(&base1);
    let pr_paths = relative_paths(&pr);
    let overlap = if base_paths.is_empty() {
        0.0
    } else {
        base_paths.intersection(&pr_paths).count() as f64 * 100.0 / base_paths.len() as f64
    };
    if overlap < MINIMUM_PATH_OVERLAP_PERCENT {
        refusals.push(format!(
            "STEP 0: base and PR relative path sets overlap only {overlap:.1}%. Normalization is wrong, so every comparison below it is garbage."
        ));
    }

    let manifest_compare_started = profile.start();
    let mut manifest_noise_signatures = BTreeSet::new();
    let mut manifest_noise = Vec::new();
    for left in 0..bases.len() {
        for right in left + 1..bases.len() {
            for gap in manifest_diff(bases[left], bases[right]) {
                if manifest_noise_signatures.insert(gap_signature(&gap)) {
                    manifest_noise.push(gap);
                }
            }
        }
    }
    let initial_gaps: Vec<_> = manifest_diff(&base1, &pr)
        .into_iter()
        .filter(|gap| !manifest_noise_signatures.contains(&gap_signature(gap)))
        .collect();
    if let Some(started) = manifest_compare_started {
        profile.manifest_compare += started.elapsed();
    }
    let changed_files = load_changed_files(&options.changed_files)?;
    let method_files = method_files(&base1, &pr, &strings);
    let mut lifecycle = Vec::new();
    let mut gaps = Vec::new();
    for gap in initial_gaps {
        let file = gap
            .method
            .as_ref()
            .and_then(|method| method_files.get(method));
        if is_method_lifecycle(&gap) && file.is_some_and(|path| changed_files.contains(path)) {
            lifecycle.push(gap);
        } else {
            gaps.push(gap);
        }
    }
    let gap_methods: BTreeSet<_> = gaps
        .iter()
        .chain(&lifecycle)
        .filter_map(|gap| gap.method.clone())
        .collect();

    let trace_compare_started = profile.start();
    let mut noise_keys = HashSet::new();
    for left in 0..bases.len() {
        for right in left + 1..bases.len() {
            let (different, _, _) = compare(bases[left], bases[right], &strings);
            noise_keys.extend(different.into_iter().map(|item| item.key));
        }
    }
    let (raw, matched, harness_divergences) = compare(&base1, &pr, &strings);
    if let Some(started) = trace_compare_started {
        profile.trace_compare += started.elapsed();
    }
    let mut remaining: Vec<_> = raw
        .iter()
        .filter(|item| {
            !noise_keys.contains(&item.key)
                && !gap_methods.contains(strings.resolve(item.key.method))
        })
        .cloned()
        .collect();
    append_lifecycle(
        &mut remaining,
        &lifecycle,
        &base1,
        &pr,
        &method_files,
        &mut strings,
    )?;

    check_volume(
        &mut refusals,
        &base1,
        &base2,
        &pr,
        matched.len(),
        noise_keys.len(),
    );
    if !refusals.is_empty() {
        eprintln!(
            "REFUSED to emit a DivergenceSet. An empty or degenerate comparison compares equal,"
        );
        eprintln!(
            "and a clean report produced from no data is indistinguishable from a clean result."
        );
        for refusal in refusals {
            eprintln!("  - {refusal}");
        }
        return Ok(4);
    }

    if let Some(parent) = Path::new(&options.output)
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
    {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    let output = File::create(&options.output).map_err(|error| error.to_string())?;
    let mut output = BufWriter::new(output);
    let base_readers = open_files(&base1.files)?;
    let pr_readers = open_files(&pr.files)?;
    let profile = RefCell::new(profile);
    let artifact = Artifact {
        strings: &strings,
        base1: &base1,
        base2: &base2,
        pr: &pr,
        gaps: &gaps,
        manifest_noise: &manifest_noise,
        noise_keys: &noise_keys,
        matched: &matched,
        raw: &raw,
        remaining: &remaining,
        harness_divergences: &harness_divergences,
        base_readers: &base_readers,
        pr_readers: &pr_readers,
        profile: &profile,
    };
    let artifact_started = profile.borrow().start();
    let mut serializer = serde_json::Serializer::new(&mut output);
    artifact
        .serialize(&mut serializer)
        .map_err(|error| error.to_string())?;
    output.flush().map_err(|error| error.to_string())?;
    if let Some(started) = artifact_started {
        profile.borrow_mut().artifact_total += started.elapsed();
    }
    profile.borrow_mut().total = total_started.elapsed();
    profile.borrow().report(&strings);
    Ok(0)
}

fn compare(
    left: &RunData,
    right: &RunData,
    strings: &Interner,
) -> (Vec<Divergence>, Vec<Matched>, Vec<Divergence>) {
    let mut keys: Vec<_> = left
        .calls
        .keys()
        .chain(right.calls.keys())
        .copied()
        .collect();
    keys.sort_by(|a, b| compare_keys(*a, *b, strings));
    keys.dedup();
    let mut divergences = Vec::new();
    let mut matched = Vec::new();
    let mut harness = Vec::new();
    for key in keys {
        let left_calls = left.calls.get(&key);
        let right_calls = right.calls.get(&key);
        let present = left_calls.or(right_calls).unwrap();
        let path = present.signatures[0].path;
        if left_calls.is_none() || right_calls.is_none() {
            let first = present.signatures[0];
            let divergence = Divergence {
                key,
                ordinal: -1,
                kind: if left_calls.is_none() {
                    "MissingInBase"
                } else {
                    "MissingInPr"
                },
                detail: if left_calls.is_none() {
                    format!(
                        "key absent from base, {} call(s) in PR",
                        present.signatures.len()
                    )
                } else {
                    format!(
                        "key absent from PR, {} call(s) in base",
                        present.signatures.len()
                    )
                },
                confidence: confidence(first.markers),
                markers: first.markers,
                base: left_calls.map(|calls| calls.signatures[0].locator),
                pr: right_calls.map(|calls| calls.signatures[0].locator),
                path,
            };
            if present.is_harness {
                harness.push(divergence)
            } else {
                divergences.push(divergence)
            }
            continue;
        }
        let left_calls = left_calls.unwrap();
        let right_calls = right_calls.unwrap();
        let first_markers = left_calls.signatures[0].markers | right_calls.signatures[0].markers;
        if !present.is_harness {
            matched.push(Matched {
                key,
                base_calls: left_calls.signatures.len(),
                pr_calls: right_calls.signatures.len(),
                confidence: confidence(first_markers),
                markers: first_markers,
                path,
            });
        }
        if left_calls.signatures.len() != right_calls.signatures.len() {
            let item = Divergence {
                key,
                ordinal: -1,
                kind: "CallCountChange",
                detail: format!(
                    "called {} time(s) in base, {} in PR",
                    left_calls.signatures.len(),
                    right_calls.signatures.len()
                ),
                confidence: confidence(first_markers),
                markers: first_markers,
                base: Some(left_calls.signatures[0].locator),
                pr: Some(right_calls.signatures[0].locator),
                path,
            };
            if present.is_harness {
                harness.push(item)
            } else {
                divergences.push(item)
            }
        }
        for ordinal in 0..left_calls
            .signatures
            .len()
            .min(right_calls.signatures.len())
        {
            let base = left_calls.signatures[ordinal];
            let pr = right_calls.signatures[ordinal];
            let detail = if base.args != pr.args {
                Some("argsDigest")
            } else if base.result != pr.result {
                Some("returnDigest")
            } else if base.exception != pr.exception {
                Some("exceptionType")
            } else {
                None
            };
            if let Some(detail) = detail {
                let markers = base.markers | pr.markers;
                let item = Divergence {
                    key,
                    ordinal: ordinal as i32,
                    kind: "DigestDiff",
                    detail: detail.to_owned(),
                    confidence: confidence(markers),
                    markers,
                    base: Some(base.locator),
                    pr: Some(pr.locator),
                    path: base.path,
                };
                if present.is_harness {
                    harness.push(item)
                } else {
                    divergences.push(item)
                }
            }
        }
    }
    (divergences, matched, harness)
}

fn load_run(
    name: &str,
    directory: &str,
    root: Option<&str>,
    retain_graph: bool,
    strings: &mut Interner,
    profile: &mut Profile,
) -> Result<RunData, String> {
    let run_started = profile.start();
    let mut trace_files = files_with_suffix(directory, ".ndjson")?;
    trace_files.retain(|path| !path.to_string_lossy().ends_with(".manifest.ndjson"));
    let mut run = RunData {
        name: name.to_owned(),
        root: root.unwrap_or_default().to_owned(),
        files: trace_files.clone(),
        ..RunData::default()
    };
    let mut member_indexes = HashMap::new();
    let mut assembly_indexes = HashMap::new();
    let mut ordinal_error = None;
    for (file_index, trace_path) in trace_files.iter().enumerate() {
        let process_name = trace_path
            .file_stem()
            .and_then(|value| value.to_str())
            .unwrap_or_default();
        let process = strings.intern(process_name)?;
        let mut process_ordinals: HashMap<Key, Vec<i32>> = HashMap::new();
        let mut process_keys = Vec::new();
        let file = File::open(trace_path).map_err(|error| error.to_string())?;
        let mut reader = BufReader::with_capacity(256 * 1024, file);
        let mut line = String::new();
        let mut physical_records = 0_i64;
        loop {
            line.clear();
            let position_started = profile.start();
            let offset = reader
                .stream_position()
                .map_err(|error| error.to_string())?;
            if let Some(started) = position_started {
                profile.stream_positions += 1;
                profile.stream_position += started.elapsed();
            }
            let read_started = profile.start();
            let bytes = reader
                .read_line(&mut line)
                .map_err(|error| error.to_string())?;
            if let Some(started) = read_started {
                profile.line_reads += started.elapsed();
            }
            if bytes == 0 {
                break;
            }
            let record = line.trim_end_matches(['\r', '\n']);
            if record.is_empty() {
                continue;
            }
            physical_records += 1;
            if profile.enabled {
                profile.trace_events += 1;
                profile.trace_bytes += bytes as u64;
            }
            let parse_started = profile.start();
            let event: BorrowedEvent<'_> = serde_json::from_str(record).map_err(|error| {
                format!("{}({physical_records}): {error}", trace_path.display())
            })?;
            if let Some(started) = parse_started {
                profile.event_parse += started.elapsed();
            }
            if event.test_id.is_empty() || event.method_full_name.is_empty() || event.ordinal < 0 {
                return Err(format!(
                    "{}({physical_records}): invalid event identity",
                    trace_path.display()
                ));
            }
            let test = strings.intern(&event.test_id)?;
            let method = strings.intern(&event.method_full_name)?;
            let key = Key { test, method };
            let ordinals = process_ordinals.entry(key).or_default();
            if ordinals.is_empty() {
                process_keys.push(key);
            }
            ordinals.push(event.ordinal);
            let path = match event.file_path.as_deref() {
                Some(value) => strings.intern(&normalize_path(value, root))?,
                None => u32::MAX,
            };
            let locator = Locator {
                file: u16::try_from(file_index).map_err(|_| "too many trace files".to_owned())?,
                offset,
                length: u32::try_from(bytes).map_err(|_| "trace line exceeds u32".to_owned())?,
            };
            let markers = marker_bits(event.args_rendered.as_deref())
                | marker_bits(event.return_rendered.as_deref());
            let digest_started = profile.start();
            let args = compact_digest(event.args_digest.as_deref(), strings)?;
            let result = compact_digest(event.return_digest.as_deref(), strings)?;
            if let Some(started) = digest_started {
                profile.digest_compaction += started.elapsed();
            }
            let store_started = profile.start();
            let signature = Signature {
                ordinal: event.ordinal,
                args,
                result,
                exception: event
                    .exception_type
                    .as_deref()
                    .map(|value| strings.intern(value))
                    .transpose()?,
                markers,
                path,
                locator,
            };
            run.events += 1;
            if event.is_harness {
                run.harness_events += 1
            } else {
                run.subject_events += 1
            }
            run.calls
                .entry(key)
                .or_insert_with(|| Calls {
                    is_harness: event.is_harness,
                    signatures: Vec::new(),
                })
                .signatures
                .push(signature);
            if retain_graph {
                run.graph.push(CallNode {
                    call_id: event.call_id,
                    parent_call_id: event.parent_call_id.unwrap_or(-1),
                    test,
                    method,
                    path,
                    process,
                    ordinal: event.ordinal,
                    line: event.line,
                    flags: u8::from(event.is_harness),
                });
            }
            if let Some(started) = store_started {
                profile.event_store += started.elapsed();
            }
        }
        let manifest_started = profile.start();
        let manifest = read_manifest(trace_path, physical_records)?;
        if let Some(started) = manifest_started {
            profile.manifest_read += started.elapsed();
        }
        if !run.schema.is_empty()
            && (run.schema != manifest.schema || run.language != manifest.language)
        {
            return Err(format!("Process manifests disagree in run '{name}'."));
        }
        run.schema = manifest.schema.clone();
        run.language = manifest.language.clone();
        merge_manifest(
            &mut run,
            manifest,
            &mut member_indexes,
            &mut assembly_indexes,
        );
        if ordinal_error.is_none() {
            let ordinal_started = profile.start();
            for key in process_keys {
                let ordinals = process_ordinals
                    .get_mut(&key)
                    .expect("first-seen ordinal key must exist");
                ordinals.sort_unstable();
                if ordinals
                    .iter()
                    .enumerate()
                    .any(|(expected, ordinal)| *ordinal != expected as i32)
                {
                    ordinal_error = Some(format!(
                        "Run '{name}' has a duplicate or non-contiguous call ordinal for {process_name}|{}|{}.",
                        strings.resolve(key.test),
                        strings.resolve(key.method)
                    ));
                    break;
                }
            }
            if let Some(started) = ordinal_started {
                profile.ordinal_sort += started.elapsed();
            }
        }
    }
    if let Some(error) = ordinal_error {
        return Err(error);
    }
    for calls in run.calls.values_mut() {
        calls.signatures.sort_by_key(|signature| signature.ordinal);
    }
    if let Some(started) = run_started {
        profile.load_runs.push((name.to_owned(), started.elapsed()));
    }
    Ok(run)
}

fn read_manifest(trace_path: &Path, physical_records: i64) -> Result<ManifestData, String> {
    let name = trace_path
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or_default();
    let process = name.strip_suffix(".ndjson").unwrap_or(name);
    let path = trace_path.with_file_name(format!("{process}.manifest.ndjson"));
    let reader = BufReader::new(File::open(&path).map_err(|error| error.to_string())?);
    let mut result = ManifestData::default();
    let mut accounting = Vec::new();
    let mut member_counts: HashMap<String, i64> = HashMap::new();
    for (index, line) in reader.lines().enumerate() {
        let line = line.map_err(|error| error.to_string())?;
        if line.is_empty() {
            continue;
        }
        let header: ManifestHeader<'_> = serde_json::from_str(&line)
            .map_err(|error| format!("{}({}): {error}", path.display(), index + 1))?;
        match header.kind.as_ref() {
            "run" => {
                let run: RunManifest<'_> =
                    serde_json::from_str(&line).map_err(|error| error.to_string())?;
                if run.schema != TRACE_SCHEMA {
                    return Err(format!("Unsupported schema '{}'", run.schema));
                }
                result.schema = run.schema.into_owned();
                result.language = run.language.into_owned();
            }
            "member" => {
                let member: MemberEntry =
                    serde_json::from_str(&line).map_err(|error| error.to_string())?;
                *member_counts.entry(member.assembly.clone()).or_default() += 1;
                result.members.push(member);
            }
            "assembly" => {
                accounting.push(
                    serde_json::from_str::<AssemblyAccounting>(&line)
                        .map_err(|error| error.to_string())?,
                );
                result.assemblies.push(
                    serde_json::from_str::<AssemblyEntry>(&line)
                        .map_err(|error| error.to_string())?,
                );
            }
            "writer" => {
                result.writer =
                    Some(serde_json::from_str(&line).map_err(|error| error.to_string())?)
            }
            "digest" | "unruled" => {}
            other => {
                return Err(format!(
                    "{}({}): unrecognised kind '{other}'",
                    path.display(),
                    index + 1
                ))
            }
        }
    }
    for item in accounting {
        let records = member_counts
            .get(&item.assembly)
            .copied()
            .unwrap_or_default();
        if item.patch_failed_members != 0
            || item.discovered_members != item.patched_members + item.skipped_members
            || item.discovered_members != records
        {
            return Err(format!(
                "Manifest member accounting does not reconcile for module '{}'.",
                item.assembly
            ));
        }
    }
    let writer = result
        .writer
        .as_ref()
        .ok_or_else(|| format!("Manifest has no writer accounting: {}", path.display()))?;
    if writer.dropped != 0
        || writer.enqueued != writer.written
        || writer.written != physical_records
    {
        return Err(format!(
            "Trace writer accounting does not reconcile for '{process}': enqueued={} written={} records={physical_records} dropped={}.",
            writer.enqueued, writer.written, writer.dropped
        ));
    }
    Ok(result)
}

fn merge_manifest(
    run: &mut RunData,
    manifest: ManifestData,
    member_indexes: &mut HashMap<String, usize>,
    assembly_indexes: &mut HashMap<String, usize>,
) {
    for member in manifest.members {
        let Some(method) = member.method_full_name.clone() else {
            continue;
        };
        if let Some(index) = member_indexes.get(&method).copied() {
            if run.members[index].status != "Patched" && member.status == "Patched" {
                run.members[index] = member;
            }
        } else {
            member_indexes.insert(method, run.members.len());
            run.members.push(member);
        }
    }
    for assembly in manifest.assemblies {
        if let Some(index) = assembly_indexes.get(&assembly.assembly).copied() {
            if !run.assemblies[index].instrumented && assembly.instrumented {
                run.assemblies[index] = assembly;
            }
        } else {
            assembly_indexes.insert(assembly.assembly.clone(), run.assemblies.len());
            run.assemblies.push(assembly);
        }
    }
}

fn append_lifecycle(
    remaining: &mut Vec<Divergence>,
    lifecycle: &[Gap],
    base: &RunData,
    pr: &RunData,
    method_files: &BTreeMap<String, String>,
    strings: &mut Interner,
) -> Result<(), String> {
    for gap in lifecycle {
        let added = gap.pr_state == "Patched";
        let run = if added { pr } else { base };
        let method_name = gap.method.as_deref().unwrap();
        let method = strings.intern(method_name)?;
        let mut tests = Vec::new();
        let mut seen = HashSet::new();
        for node in run.graph.iter().filter(|node| node.method == method) {
            if seen.insert(node.test) {
                tests.push(node.test);
            }
        }
        let path = method_files
            .get(method_name)
            .map(|value| strings.intern(value))
            .transpose()?
            .unwrap_or(u32::MAX);
        for test in tests {
            remaining.push(Divergence {
                key: Key { test, method },
                ordinal: -1,
                kind: if added {
                    "MethodAdded"
                } else {
                    "MethodRemoved"
                },
                detail: if added {
                    "method is absent from the base manifest and instrumented in the PR's"
                        .to_owned()
                } else {
                    "method is instrumented in the base manifest and absent from the PR's"
                        .to_owned()
                },
                confidence: Confidence::Exact,
                markers: 0,
                base: None,
                pr: None,
                path,
            });
        }
    }
    Ok(())
}

struct Artifact<'a> {
    strings: &'a Interner,
    base1: &'a RunData,
    base2: &'a RunData,
    pr: &'a RunData,
    gaps: &'a [Gap],
    manifest_noise: &'a [Gap],
    noise_keys: &'a HashSet<Key>,
    matched: &'a [Matched],
    raw: &'a [Divergence],
    remaining: &'a [Divergence],
    harness_divergences: &'a [Divergence],
    base_readers: &'a [RefCell<File>],
    pr_readers: &'a [RefCell<File>],
    profile: &'a RefCell<Profile>,
}

impl Serialize for Artifact<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut state = serializer.serialize_struct("DivergenceSet", 14)?;
        let runs_counts_started = self.profile.borrow().start();
        state.serialize_field("schema", "realdiff.divergenceset/2")?;
        state.serialize_field(
            "generatedUtc",
            &OffsetDateTime::now_utc().format(&Rfc3339).unwrap(),
        )?;
        state.serialize_field(
            "runs",
            &RunsView {
                base1: describe_run(self.base1),
                base2: describe_run(self.base2),
                pr: describe_run(self.pr),
            },
        )?;
        state.serialize_field(
            "counts",
            &CountsView {
                matched_keys: self.matched.len(),
                raw_differences: self.raw.len(),
                noise_excluded_keys: self.noise_keys.len(),
                noise_excluded_differences: self
                    .raw
                    .iter()
                    .filter(|item| self.noise_keys.contains(&item.key))
                    .count(),
                tooling_gaps: self.gaps.len(),
                remaining_divergences: self.remaining.len(),
                matched_keys_partial: self
                    .matched
                    .iter()
                    .filter(|item| item.confidence == Confidence::Partial)
                    .count(),
            },
        )?;
        if let Some(started) = runs_counts_started {
            self.profile.borrow_mut().artifact_runs_counts += started.elapsed();
        }
        let matched_started = self.profile.borrow().start();
        state.serialize_field(
            "matchedKeys",
            &MatchedView {
                values: self.matched,
                strings: self.strings,
            },
        )?;
        if let Some(started) = matched_started {
            self.profile.borrow_mut().artifact_matched += started.elapsed();
        }
        let divergences_started = self.profile.borrow().start();
        state.serialize_field(
            "divergences",
            &DivergenceView {
                values: self.remaining,
                strings: self.strings,
                base: self.base1,
                pr: self.pr,
                base_readers: self.base_readers,
                pr_readers: self.pr_readers,
                profile: self.profile,
            },
        )?;
        if let Some(started) = divergences_started {
            self.profile.borrow_mut().artifact_divergences += started.elapsed();
        }
        let noise_started = self.profile.borrow().start();
        state.serialize_field(
            "noiseExclusions",
            &NoiseView {
                keys: self.noise_keys,
                strings: self.strings,
            },
        )?;
        state.serialize_field("toolingGaps", &GapView { values: self.gaps })?;
        state.serialize_field(
            "manifestNoise",
            &ManifestNoiseView {
                values: self.manifest_noise,
            },
        )?;
        state.serialize_field(
            "harnessDivergences",
            &HarnessDivergenceView {
                values: self.harness_divergences,
                strings: self.strings,
                base: self.base1,
            },
        )?;
        if let Some(started) = noise_started {
            self.profile.borrow_mut().artifact_noise += started.elapsed();
        }
        let coverage_started = self.profile.borrow().start();
        state.serialize_field(
            "coverage",
            &CoverageView {
                members: &self.base1.members,
                assemblies: &self.base1.assemblies,
            },
        )?;
        state.serialize_field(
            "prCoverage",
            &CoverageView {
                members: &self.pr.members,
                assemblies: &self.pr.assemblies,
            },
        )?;
        if let Some(started) = coverage_started {
            self.profile.borrow_mut().artifact_coverage += started.elapsed();
        }
        let call_tree_started = self.profile.borrow().start();
        state.serialize_field(
            "callTree",
            &CallTreeView {
                nodes: &self.base1.graph,
                strings: self.strings,
            },
        )?;
        if let Some(started) = call_tree_started {
            self.profile.borrow_mut().artifact_call_tree += started.elapsed();
        }
        let pr_call_tree_started = self.profile.borrow().start();
        state.serialize_field(
            "prCallTree",
            &CallTreeView {
                nodes: &self.pr.graph,
                strings: self.strings,
            },
        )?;
        if let Some(started) = pr_call_tree_started {
            self.profile.borrow_mut().artifact_pr_call_tree += started.elapsed();
        }
        state.end()
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct RunView<'a> {
    name: &'a str,
    schema: &'a str,
    language: &'a str,
    root: &'a str,
    trace_files: usize,
    events: u64,
    subject_events: u64,
    harness_events: u64,
}

#[derive(Serialize)]
struct RunsView<'a> {
    base1: RunView<'a>,
    base2: RunView<'a>,
    pr: RunView<'a>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CountsView {
    matched_keys: usize,
    raw_differences: usize,
    noise_excluded_keys: usize,
    noise_excluded_differences: usize,
    tooling_gaps: usize,
    remaining_divergences: usize,
    matched_keys_partial: usize,
}

#[derive(Clone, Copy)]
struct MarkerNames(u8);

impl Serialize for MarkerNames {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let values = [(2, "depth"), (4, "error"), (1, "skipped"), (8, "truncated")];
        let mut sequence = serializer.serialize_seq(Some(
            values.iter().filter(|(bit, _)| self.0 & bit != 0).count(),
        ))?;
        for (_, name) in values.into_iter().filter(|(bit, _)| self.0 & bit != 0) {
            sequence.serialize_element(name)?;
        }
        sequence.end()
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct MatchedItem<'a> {
    test_id: &'a str,
    method_full_name: &'a str,
    file_path: Option<&'a str>,
    base_calls: usize,
    pr_calls: usize,
    digest_confidence: &'static str,
    partial_markers: MarkerNames,
}

struct MatchedView<'a> {
    values: &'a [Matched],
    strings: &'a Interner,
}
impl Serialize for MatchedView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut sequence = serializer.serialize_seq(Some(self.values.len()))?;
        for item in self.values {
            sequence.serialize_element(&MatchedItem {
                test_id: self.strings.resolve(item.key.test),
                method_full_name: self.strings.resolve(item.key.method),
                file_path: self.strings.optional(item.path),
                base_calls: item.base_calls,
                pr_calls: item.pr_calls,
                digest_confidence: item.confidence.name(),
                partial_markers: MarkerNames(item.markers),
            })?;
        }
        sequence.end()
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct NoiseItem<'a> {
    test_id: &'a str,
    method_full_name: &'a str,
    differences: u8,
}

struct NoiseView<'a> {
    keys: &'a HashSet<Key>,
    strings: &'a Interner,
}
impl Serialize for NoiseView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut keys: Vec<_> = self.keys.iter().copied().collect();
        keys.sort_by(|a, b| compare_keys(*a, *b, self.strings));
        let mut sequence = serializer.serialize_seq(Some(keys.len()))?;
        for key in keys {
            sequence.serialize_element(&NoiseItem {
                test_id: self.strings.resolve(key.test),
                method_full_name: self.strings.resolve(key.method),
                differences: 1,
            })?;
        }
        sequence.end()
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DivergenceItem<'a> {
    test_id: &'a str,
    method_full_name: &'a str,
    file_path: Option<&'a str>,
    ordinal: i32,
    kind: &'static str,
    detail: &'a str,
    digest_confidence: &'static str,
    partial_markers: MarkerNames,
    base_args_digest: Option<&'a str>,
    pr_args_digest: Option<&'a str>,
    base_args_rendered: Option<&'a str>,
    pr_args_rendered: Option<&'a str>,
    base_return_digest: Option<&'a str>,
    pr_return_digest: Option<&'a str>,
    base_return_rendered: Option<&'a str>,
    pr_return_rendered: Option<&'a str>,
    base_exception_type: Option<&'a str>,
    pr_exception_type: Option<&'a str>,
}

struct DivergenceView<'a> {
    values: &'a [Divergence],
    strings: &'a Interner,
    base: &'a RunData,
    pr: &'a RunData,
    base_readers: &'a [RefCell<File>],
    pr_readers: &'a [RefCell<File>],
    profile: &'a RefCell<Profile>,
}
impl Serialize for DivergenceView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut sequence = serializer.serialize_seq(Some(self.values.len()))?;
        for item in self.values {
            let base = item
                .base
                .map(|locator| read_evidence(self.base, self.base_readers, locator, self.profile))
                .transpose()
                .map_err(serde::ser::Error::custom)?;
            let pr = item
                .pr
                .map(|locator| read_evidence(self.pr, self.pr_readers, locator, self.profile))
                .transpose()
                .map_err(serde::ser::Error::custom)?;
            sequence.serialize_element(&DivergenceItem {
                test_id: self.strings.resolve(item.key.test),
                method_full_name: self.strings.resolve(item.key.method),
                file_path: self.strings.optional(item.path),
                ordinal: item.ordinal,
                kind: item.kind,
                detail: &item.detail,
                digest_confidence: item.confidence.name(),
                partial_markers: MarkerNames(item.markers),
                base_args_digest: base.as_ref().and_then(|event| event.args_digest.as_deref()),
                pr_args_digest: pr.as_ref().and_then(|event| event.args_digest.as_deref()),
                base_args_rendered: base
                    .as_ref()
                    .and_then(|event| event.args_rendered.as_deref()),
                pr_args_rendered: pr.as_ref().and_then(|event| event.args_rendered.as_deref()),
                base_return_digest: base
                    .as_ref()
                    .and_then(|event| event.return_digest.as_deref()),
                pr_return_digest: pr.as_ref().and_then(|event| event.return_digest.as_deref()),
                base_return_rendered: base
                    .as_ref()
                    .and_then(|event| event.return_rendered.as_deref()),
                pr_return_rendered: pr
                    .as_ref()
                    .and_then(|event| event.return_rendered.as_deref()),
                base_exception_type: base
                    .as_ref()
                    .and_then(|event| event.exception_type.as_deref()),
                pr_exception_type: pr
                    .as_ref()
                    .and_then(|event| event.exception_type.as_deref()),
            })?;
        }
        sequence.end()
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CallTreeItem<'a> {
    call_id: i64,
    parent_call_id: Option<i64>,
    test_id: &'a str,
    method_full_name: &'a str,
    ordinal: i32,
    is_harness: bool,
    file_path: Option<&'a str>,
    line: i32,
    process: &'a str,
}

struct CallTreeView<'a> {
    nodes: &'a [CallNode],
    strings: &'a Interner,
}
impl Serialize for CallTreeView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut sequence = serializer.serialize_seq(Some(self.nodes.len()))?;
        for node in self.nodes {
            sequence.serialize_element(&CallTreeItem {
                call_id: node.call_id,
                parent_call_id: (node.parent_call_id >= 0).then_some(node.parent_call_id),
                test_id: self.strings.resolve(node.test),
                method_full_name: self.strings.resolve(node.method),
                ordinal: node.ordinal,
                is_harness: node.flags & 1 != 0,
                file_path: self.strings.optional(node.path),
                line: node.line,
                process: self.strings.resolve(node.process),
            })?;
        }
        sequence.end()
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct GapItem<'a> {
    scope: &'a str,
    assembly: &'a str,
    method_full_name: Option<&'a str>,
    base_state: &'a str,
    pr_state: &'a str,
    reason: &'a str,
}

struct GapView<'a> {
    values: &'a [Gap],
}

impl Serialize for GapView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut sequence = serializer.serialize_seq(Some(self.values.len()))?;
        for gap in self.values {
            sequence.serialize_element(&GapItem {
                scope: &gap.scope,
                assembly: &gap.assembly,
                method_full_name: gap.method.as_deref(),
                base_state: &gap.base_state,
                pr_state: &gap.pr_state,
                reason: &gap.reason,
            })?;
        }
        sequence.end()
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ManifestNoiseItem<'a> {
    scope: &'a str,
    assembly: &'a str,
    method_full_name: Option<&'a str>,
    run1_state: &'a str,
    run2_state: &'a str,
    reason: &'static str,
}

struct ManifestNoiseView<'a> {
    values: &'a [Gap],
}

impl Serialize for ManifestNoiseView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut sequence = serializer.serialize_seq(Some(self.values.len()))?;
        for gap in self.values {
            sequence.serialize_element(&ManifestNoiseItem {
                scope: &gap.scope,
                assembly: &gap.assembly,
                method_full_name: gap.method.as_deref(),
                run1_state: &gap.base_state,
                run2_state: &gap.pr_state,
                reason:
                    "nondeterministic tracer coverage: differs between two runs of the same build",
            })?;
        }
        sequence.end()
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct HarnessDivergenceItem<'a> {
    test_id: &'a str,
    method_full_name: &'a str,
    kind: &'static str,
    detail: &'a str,
    is_test_root: Option<bool>,
}

struct HarnessDivergenceView<'a> {
    values: &'a [Divergence],
    strings: &'a Interner,
    base: &'a RunData,
}

impl Serialize for HarnessDivergenceView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut sequence = serializer.serialize_seq(Some(self.values.len()))?;
        for item in self.values {
            let method_full_name = self.strings.resolve(item.key.method);
            sequence.serialize_element(&HarnessDivergenceItem {
                test_id: self.strings.resolve(item.key.test),
                method_full_name,
                kind: item.kind,
                detail: &item.detail,
                is_test_root: member(self.base, method_full_name).map(|entry| entry.is_test_root),
            })?;
        }
        sequence.end()
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct MemberItem<'a> {
    method_full_name: Option<&'a str>,
    assembly: &'a str,
    file_path: Option<&'a str>,
    line: Option<i32>,
    status: &'a str,
    skip_reason: Option<&'a str>,
    detail: Option<&'a str>,
    source_resolution: Option<&'a str>,
    is_test_root: bool,
}

struct MemberView<'a> {
    values: &'a [MemberEntry],
}

impl Serialize for MemberView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut sequence = serializer.serialize_seq(Some(self.values.len()))?;
        for item in self.values {
            sequence.serialize_element(&MemberItem {
                method_full_name: item.method_full_name.as_deref(),
                assembly: &item.assembly,
                file_path: item.file_path.as_deref(),
                line: item.line,
                status: &item.status,
                skip_reason: item.skip_reason.as_deref(),
                detail: item.detail.as_deref(),
                source_resolution: item.source_resolution.as_deref(),
                is_test_root: item.is_test_root,
            })?;
        }
        sequence.end()
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct AssemblyItem<'a> {
    assembly: &'a str,
    instrumented: bool,
    source_partial: bool,
    source_unavailable: bool,
}

struct AssemblyView<'a> {
    values: &'a [AssemblyEntry],
}

impl Serialize for AssemblyView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut sequence = serializer.serialize_seq(Some(self.values.len()))?;
        for item in self.values {
            sequence.serialize_element(&AssemblyItem {
                assembly: &item.assembly,
                instrumented: item.instrumented,
                source_partial: item.source_partial,
                source_unavailable: item.source_unavailable,
            })?;
        }
        sequence.end()
    }
}

struct CoverageView<'a> {
    members: &'a [MemberEntry],
    assemblies: &'a [AssemblyEntry],
}

impl Serialize for CoverageView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut state = serializer.serialize_struct("Coverage", 2)?;
        state.serialize_field(
            "members",
            &MemberView {
                values: self.members,
            },
        )?;
        state.serialize_field(
            "assemblies",
            &AssemblyView {
                values: self.assemblies,
            },
        )?;
        state.end()
    }
}

fn open_files(paths: &[PathBuf]) -> Result<Vec<RefCell<File>>, String> {
    paths
        .iter()
        .map(|path| {
            File::open(path)
                .map(RefCell::new)
                .map_err(|error| error.to_string())
        })
        .collect()
}

fn read_evidence(
    run: &RunData,
    readers: &[RefCell<File>],
    locator: Locator,
    profile: &RefCell<Profile>,
) -> Result<EvidenceEvent, String> {
    let started = profile.borrow().start();
    let _ = run
        .files
        .get(locator.file as usize)
        .ok_or_else(|| "invalid event locator".to_owned())?;
    let mut file = readers
        .get(locator.file as usize)
        .ok_or_else(|| "invalid event reader".to_owned())?
        .borrow_mut();
    file.seek(SeekFrom::Start(locator.offset))
        .map_err(|error| error.to_string())?;
    let mut bytes = vec![0_u8; locator.length as usize];
    file.read_exact(&mut bytes)
        .map_err(|error| error.to_string())?;
    let result = serde_json::from_slice(&bytes).map_err(|error| error.to_string());
    if let Some(started) = started {
        let mut profile = profile.borrow_mut();
        profile.evidence_reads += 1;
        profile.evidence_bytes += locator.length as u64;
        profile.evidence_read += started.elapsed();
    }
    result
}

fn compare_keys(left: Key, right: Key, strings: &Interner) -> Ordering {
    strings
        .resolve(left.test)
        .bytes()
        .chain(std::iter::once(b'|'))
        .chain(strings.resolve(left.method).bytes())
        .cmp(
            strings
                .resolve(right.test)
                .bytes()
                .chain(std::iter::once(b'|'))
                .chain(strings.resolve(right.method).bytes()),
        )
}

fn confidence(markers: u8) -> Confidence {
    if markers == 0 {
        Confidence::Exact
    } else {
        Confidence::Partial
    }
}
fn marker_bits(value: Option<&str>) -> u8 {
    let Some(value) = value else {
        return 0;
    };
    u8::from(value.contains("<skipped:"))
        | (u8::from(value.contains("<depth:")) << 1)
        | (u8::from(value.contains("<error:")) << 2)
        | (u8::from(value.contains("<truncated>")) << 3)
}
#[cfg(test)]
fn marker_names(markers: u8) -> Vec<&'static str> {
    [(2, "depth"), (4, "error"), (1, "skipped"), (8, "truncated")]
        .into_iter()
        .filter_map(|(bit, name)| (markers & bit != 0).then_some(name))
        .collect()
}
fn compact_digest(
    value: Option<&str>,
    strings: &mut Interner,
) -> Result<Option<CompactDigest>, String> {
    let Some(value) = value else {
        return Ok(None);
    };
    if let Some(hex) = value.strip_prefix("sha256:").filter(|hex| hex.len() == 64) {
        let mut bytes = [0_u8; 32];
        for (index, byte) in bytes.iter_mut().enumerate() {
            *byte = u8::from_str_radix(&hex[index * 2..index * 2 + 2], 16)
                .map_err(|_| format!("Invalid SHA-256 digest: {value}"))?;
        }
        Ok(Some(CompactDigest::Sha256(bytes)))
    } else {
        Ok(Some(CompactDigest::Text(strings.intern(value)?)))
    }
}
fn normalize_path(value: &str, root: Option<&str>) -> String {
    let replaced = value.replace('\\', "/");
    let Some(root) = root else {
        return replaced;
    };
    let normalized_root = root.replace('\\', "/");
    replaced
        .strip_prefix(&normalized_root)
        .map(|relative| relative.trim_start_matches('/').to_owned())
        .unwrap_or(replaced)
}
fn files_with_suffix(directory: &str, suffix: &str) -> Result<Vec<PathBuf>, String> {
    let mut files = Vec::new();
    for entry in fs::read_dir(directory).map_err(|error| error.to_string())? {
        let path = entry.map_err(|error| error.to_string())?.path();
        if path
            .file_name()
            .and_then(|value| value.to_str())
            .is_some_and(|name| name.ends_with(suffix))
        {
            files.push(path);
        }
    }
    files.sort();
    Ok(files)
}
fn relative_paths(run: &RunData) -> BTreeSet<String> {
    run.calls
        .iter()
        .filter(|(_, calls)| !calls.is_harness)
        .filter_map(|(_, calls)| calls.signatures.first())
        .filter(|signature| signature.path != u32::MAX)
        .map(|signature| signature.path.to_string())
        .collect()
}
fn method_files(base: &RunData, pr: &RunData, strings: &Interner) -> BTreeMap<String, String> {
    let mut result = BTreeMap::new();
    for run in [base, pr] {
        for (key, calls) in &run.calls {
            let path = calls.signatures[0].path;
            if path != u32::MAX {
                result
                    .entry(strings.resolve(key.method).to_owned())
                    .or_insert_with(|| strings.resolve(path).to_owned());
            }
        }
    }
    result
}
fn check_volume(
    refusals: &mut Vec<String>,
    base1: &RunData,
    base2: &RunData,
    pr: &RunData,
    matched: usize,
    noise: usize,
) {
    for (name, count) in [
        ("base_run1", base1.subject_events),
        ("base_run2", base2.subject_events),
        ("pr_run", pr.subject_events),
    ] {
        if count == 0 {
            refusals.push(format!("VOLUME: run '{name}' has zero subject events."));
        }
    }
    let drift = if base1.subject_events == 0 {
        100.0
    } else {
        base1.subject_events.abs_diff(base2.subject_events) as f64 * 100.0
            / base1.subject_events as f64
    };
    if drift > MAXIMUM_BASE_COUNT_DRIFT_PERCENT {
        refusals.push(format!("VOLUME: base_run1 and base_run2 subject event counts differ by {drift:.2}%, above the 10% limit. The two base runs are not comparable, so the noise baseline is not a baseline."));
    }
    if matched < MINIMUM_MATCHED_KEYS {
        refusals.push(format!("VOLUME: only {matched} matched key(s), below the minimum of 100. Too little was compared for a clean result to mean anything."));
    }
    let noise_percent = if matched == 0 {
        100.0
    } else {
        noise as f64 * 100.0 / matched as f64
    };
    if noise_percent > MAXIMUM_NOISE_PERCENT {
        refusals.push(format!("VOLUME: the noise exclusion set covers {noise_percent:.2}% of keys, above the 20% limit. Excluding that much would hide real changes rather than cancel noise."));
    }
}
fn load_changed_files(path: &str) -> Result<BTreeSet<String>, String> {
    if path.is_empty() {
        return Ok(BTreeSet::new());
    }
    Ok(fs::read_to_string(path)
        .map_err(|error| error.to_string())?
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .map(|line| line.replace('\\', "/"))
        .collect())
}
fn manifest_diff(base: &RunData, pr: &RunData) -> Vec<Gap> {
    let base_members: BTreeMap<_, _> = base
        .members
        .iter()
        .filter_map(|m| m.method_full_name.as_ref().map(|k| (k, m)))
        .collect();
    let pr_members: BTreeMap<_, _> = pr
        .members
        .iter()
        .filter_map(|m| m.method_full_name.as_ref().map(|k| (k, m)))
        .collect();
    let methods: BTreeSet<_> = base_members
        .keys()
        .chain(pr_members.keys())
        .copied()
        .collect();
    let mut gaps = Vec::new();
    for method in methods {
        let left = base_members.get(method).copied();
        let right = pr_members.get(method).copied();
        let base_state = left.map_or("absent", |entry| entry.status.as_str());
        let pr_state = right.map_or("absent", |entry| entry.status.as_str());
        if base_state == pr_state {
            continue;
        }
        let present = left.or(right).unwrap();
        if (left.is_none() || right.is_none())
            && present.status == "Skipped"
            && present.skip_reason.as_deref() == Some("ExcludedByScope")
        {
            continue;
        }
        gaps.push(Gap {
            scope: "member".to_owned(),
            assembly: present.assembly.clone(),
            method: Some(method.clone()),
            base_state: base_state.to_owned(),
            pr_state: pr_state.to_owned(),
            reason: format!("observability differs between runs: {base_state} -> {pr_state}"),
        });
    }
    let base_assemblies: BTreeMap<_, _> =
        base.assemblies.iter().map(|a| (&a.assembly, a)).collect();
    let pr_assemblies: BTreeMap<_, _> = pr.assemblies.iter().map(|a| (&a.assembly, a)).collect();
    for assembly in base_assemblies
        .keys()
        .chain(pr_assemblies.keys())
        .copied()
        .collect::<BTreeSet<_>>()
    {
        let left = base_assemblies.get(assembly).copied();
        let right = pr_assemblies.get(assembly).copied();
        compare_flag(
            &mut gaps,
            assembly,
            "instrumented",
            left.map(|a| a.instrumented),
            right.map(|a| a.instrumented),
        );
        compare_flag(
            &mut gaps,
            assembly,
            "sourceUnavailable",
            left.map(|a| a.source_unavailable),
            right.map(|a| a.source_unavailable),
        );
        compare_flag(
            &mut gaps,
            assembly,
            "sourcePartial",
            left.map(|a| a.source_partial),
            right.map(|a| a.source_partial),
        );
    }
    gaps
}
fn compare_flag(
    gaps: &mut Vec<Gap>,
    assembly: &str,
    flag: &str,
    left: Option<bool>,
    right: Option<bool>,
) {
    if left == right {
        return;
    }
    let state = |value: Option<bool>| value.map_or("absent", |v| if v { "true" } else { "false" });
    gaps.push(Gap {
        scope: format!("assembly:{flag}"),
        assembly: assembly.to_owned(),
        method: None,
        base_state: state(left).to_owned(),
        pr_state: state(right).to_owned(),
        reason: format!("tracer coverage flag '{flag}' differs between runs"),
    });
}
fn gap_signature(gap: &Gap) -> String {
    format!(
        "{}|{}|{}",
        gap.scope,
        gap.assembly,
        gap.method.as_deref().unwrap_or_default()
    )
}
fn is_method_lifecycle(gap: &Gap) -> bool {
    gap.scope == "member"
        && ((gap.base_state == "absent" && gap.pr_state == "Patched")
            || (gap.base_state == "Patched" && gap.pr_state == "absent"))
}
fn describe_run(run: &RunData) -> RunView<'_> {
    RunView {
        name: &run.name,
        schema: &run.schema,
        language: &run.language,
        root: &run.root,
        trace_files: run.files.len(),
        events: run.events,
        subject_events: run.subject_events,
        harness_events: run.harness_events,
    }
}
fn member<'a>(run: &'a RunData, method: &str) -> Option<&'a MemberEntry> {
    run.members
        .iter()
        .find(|entry| entry.method_full_name.as_deref() == Some(method))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn markers_are_reported_in_dotnet_order() {
        assert_eq!(marker_names(15), ["depth", "error", "skipped", "truncated"]);
    }

    #[test]
    fn allocation_free_key_order_matches_combined_strings() {
        let mut strings = Interner::default();
        let method = strings.intern("method").unwrap();
        let key_10 = Key {
            test: strings.intern("volume#10").unwrap(),
            method,
        };
        let key_100 = Key {
            test: strings.intern("volume#100").unwrap(),
            method,
        };
        assert_eq!(compare_keys(key_100, key_10, &strings), Ordering::Less);
    }
}
