use crate::model::{AssemblyEntry, MemberEntry};
use crate::DiffOptions;
use serde::ser::{SerializeSeq, SerializeStruct};
use serde::{Deserialize, Serialize, Serializer};
use serde_json::{json, Value};
use std::borrow::Cow;
use std::cell::RefCell;
use std::cmp::Ordering;
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};
use std::fs::{self, File};
use std::io::{BufRead, BufReader, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

const MINIMUM_MATCHED_KEYS: usize = 100;
const MAXIMUM_BASE_COUNT_DRIFT_PERCENT: f64 = 10.0;
const MAXIMUM_NOISE_PERCENT: f64 = 20.0;
const MINIMUM_PATH_OVERLAP_PERCENT: f64 = 50.0;
const TRACE_SCHEMA: &str = "behaviordiff.trace/1";

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
}

impl Interner {
    fn intern(&mut self, value: &str) -> Result<u32, String> {
        if let Some(id) = self.ids.get(value) {
            return Ok(*id);
        }
        let id = u32::try_from(self.values.len())
            .map_err(|_| "streaming interner exceeded u32 identifiers".to_owned())?;
        let stored: Arc<str> = Arc::from(value);
        self.values.push(stored.clone());
        self.ids.insert(stored, id);
        Ok(id)
    }

    fn resolve(&self, id: u32) -> &str {
        &self.values[id as usize]
    }

    fn optional(&self, id: u32) -> Option<&str> {
        (id != u32::MAX).then(|| self.resolve(id))
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
    let mut strings = Interner::default();
    let base1 = load_run(
        "base_run1",
        &options.base1,
        options.base_root.as_deref(),
        true,
        &mut strings,
    )?;
    let base2 = load_run(
        "base_run2",
        &options.base2,
        options.base_root.as_deref(),
        false,
        &mut strings,
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
        )?)
    };
    let pr = load_run(
        "pr_run",
        &options.pr,
        options.pr_root.as_deref(),
        true,
        &mut strings,
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

    let mut noise_keys = HashSet::new();
    for left in 0..bases.len() {
        for right in left + 1..bases.len() {
            let (different, _, _) = compare(bases[left], bases[right], &strings);
            noise_keys.extend(different.into_iter().map(|item| item.key));
        }
    }
    let (raw, matched, harness_divergences) = compare(&base1, &pr, &strings);
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
    let base_readers = open_files(&base1.files)?;
    let pr_readers = open_files(&pr.files)?;
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
    };
    let mut serializer = serde_json::Serializer::pretty(output);
    artifact
        .serialize(&mut serializer)
        .map_err(|error| error.to_string())?;
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
) -> Result<RunData, String> {
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
    for (file_index, trace_path) in trace_files.iter().enumerate() {
        let process_name = trace_path
            .file_stem()
            .and_then(|value| value.to_str())
            .unwrap_or_default();
        let process = strings.intern(process_name)?;
        let file = File::open(trace_path).map_err(|error| error.to_string())?;
        let mut reader = BufReader::with_capacity(256 * 1024, file);
        let mut line = String::new();
        let mut physical_records = 0_i64;
        loop {
            line.clear();
            let offset = reader
                .stream_position()
                .map_err(|error| error.to_string())?;
            let bytes = reader
                .read_line(&mut line)
                .map_err(|error| error.to_string())?;
            if bytes == 0 {
                break;
            }
            let record = line.trim_end_matches(['\r', '\n']);
            if record.is_empty() {
                continue;
            }
            physical_records += 1;
            let event: BorrowedEvent<'_> = serde_json::from_str(record).map_err(|error| {
                format!("{}({physical_records}): {error}", trace_path.display())
            })?;
            if event.test_id.is_empty() || event.method_full_name.is_empty() || event.ordinal < 0 {
                return Err(format!(
                    "{}({physical_records}): invalid event identity",
                    trace_path.display()
                ));
            }
            let test = strings.intern(&event.test_id)?;
            let method = strings.intern(&event.method_full_name)?;
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
            let signature = Signature {
                ordinal: event.ordinal,
                args: compact_digest(event.args_digest.as_deref(), strings)?,
                result: compact_digest(event.return_digest.as_deref(), strings)?,
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
                .entry(Key { test, method })
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
        }
        let manifest = read_manifest(trace_path, physical_records)?;
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
    }
    for calls in run.calls.values_mut() {
        calls.signatures.sort_by_key(|signature| signature.ordinal);
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
}

impl Serialize for Artifact<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut state = serializer.serialize_struct("DivergenceSet", 13)?;
        state.serialize_field("schema", "behaviordiff.divergenceset/2")?;
        state.serialize_field(
            "generatedUtc",
            &OffsetDateTime::now_utc().format(&Rfc3339).unwrap(),
        )?;
        state.serialize_field("runs", &json!({
            "base1": describe_run(self.base1), "base2": describe_run(self.base2), "pr": describe_run(self.pr)
        }))?;
        state.serialize_field("counts", &json!({
            "matchedKeys": self.matched.len(),
            "rawDifferences": self.raw.len(),
            "noiseExcludedKeys": self.noise_keys.len(),
            "noiseExcludedDifferences": self.raw.iter().filter(|item| self.noise_keys.contains(&item.key)).count(),
            "toolingGaps": self.gaps.len(),
            "remainingDivergences": self.remaining.len(),
            "matchedKeysPartial": self.matched.iter().filter(|item| item.confidence == Confidence::Partial).count(),
        }))?;
        state.serialize_field(
            "matchedKeys",
            &MatchedView {
                values: self.matched,
                strings: self.strings,
            },
        )?;
        state.serialize_field(
            "divergences",
            &DivergenceView {
                values: self.remaining,
                strings: self.strings,
                base: self.base1,
                pr: self.pr,
                base_readers: self.base_readers,
                pr_readers: self.pr_readers,
            },
        )?;
        state.serialize_field(
            "noiseExclusions",
            &NoiseView {
                keys: self.noise_keys,
                strings: self.strings,
            },
        )?;
        state.serialize_field(
            "toolingGaps",
            &self.gaps.iter().map(describe_gap).collect::<Vec<_>>(),
        )?;
        state.serialize_field("manifestNoise", &self.manifest_noise.iter().map(|gap| json!({
            "scope": gap.scope, "assembly": gap.assembly, "methodFullName": gap.method,
            "run1State": gap.base_state, "run2State": gap.pr_state,
            "reason": "nondeterministic tracer coverage: differs between two runs of the same build"
        })).collect::<Vec<_>>())?;
        state.serialize_field("harnessDivergences", &self.harness_divergences.iter().map(|item| json!({
            "testId": self.strings.resolve(item.key.test), "methodFullName": self.strings.resolve(item.key.method),
            "kind": item.kind, "detail": item.detail,
            "isTestRoot": member(self.base1, self.strings.resolve(item.key.method)).map(|entry| entry.is_test_root),
        })).collect::<Vec<_>>())?;
        state.serialize_field("coverage", &json!({
            "members": self.base1.members.iter().map(describe_member).collect::<Vec<_>>(),
            "assemblies": self.base1.assemblies.iter().map(describe_assembly).collect::<Vec<_>>(),
        }))?;
        state.serialize_field(
            "callTree",
            &CallTreeView {
                nodes: &self.base1.graph,
                strings: self.strings,
            },
        )?;
        state.serialize_field(
            "prCallTree",
            &CallTreeView {
                nodes: &self.pr.graph,
                strings: self.strings,
            },
        )?;
        state.end()
    }
}

struct MatchedView<'a> {
    values: &'a [Matched],
    strings: &'a Interner,
}
impl Serialize for MatchedView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut sequence = serializer.serialize_seq(Some(self.values.len()))?;
        for item in self.values {
            sequence.serialize_element(&json!({
                "testId": self.strings.resolve(item.key.test), "methodFullName": self.strings.resolve(item.key.method),
                "filePath": self.strings.optional(item.path), "baseCalls": item.base_calls, "prCalls": item.pr_calls,
                "digestConfidence": item.confidence.name(), "partialMarkers": marker_names(item.markers),
            }))?;
        }
        sequence.end()
    }
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
            sequence.serialize_element(&json!({
                "testId": self.strings.resolve(key.test), "methodFullName": self.strings.resolve(key.method), "differences": 1
            }))?;
        }
        sequence.end()
    }
}

struct DivergenceView<'a> {
    values: &'a [Divergence],
    strings: &'a Interner,
    base: &'a RunData,
    pr: &'a RunData,
    base_readers: &'a [RefCell<File>],
    pr_readers: &'a [RefCell<File>],
}
impl Serialize for DivergenceView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut sequence = serializer.serialize_seq(Some(self.values.len()))?;
        for item in self.values {
            let base = item
                .base
                .map(|locator| read_evidence(self.base, self.base_readers, locator))
                .transpose()
                .map_err(serde::ser::Error::custom)?;
            let pr = item
                .pr
                .map(|locator| read_evidence(self.pr, self.pr_readers, locator))
                .transpose()
                .map_err(serde::ser::Error::custom)?;
            sequence.serialize_element(&json!({
                "testId": self.strings.resolve(item.key.test), "methodFullName": self.strings.resolve(item.key.method),
                "filePath": self.strings.optional(item.path), "ordinal": item.ordinal, "kind": item.kind, "detail": item.detail,
                "digestConfidence": item.confidence.name(), "partialMarkers": marker_names(item.markers),
                "baseArgsDigest": base.as_ref().and_then(|event| event.args_digest.as_ref()),
                "prArgsDigest": pr.as_ref().and_then(|event| event.args_digest.as_ref()),
                "baseArgsRendered": base.as_ref().and_then(|event| event.args_rendered.as_ref()),
                "prArgsRendered": pr.as_ref().and_then(|event| event.args_rendered.as_ref()),
                "baseReturnDigest": base.as_ref().and_then(|event| event.return_digest.as_ref()),
                "prReturnDigest": pr.as_ref().and_then(|event| event.return_digest.as_ref()),
                "baseReturnRendered": base.as_ref().and_then(|event| event.return_rendered.as_ref()),
                "prReturnRendered": pr.as_ref().and_then(|event| event.return_rendered.as_ref()),
                "baseExceptionType": base.as_ref().and_then(|event| event.exception_type.as_ref()),
                "prExceptionType": pr.as_ref().and_then(|event| event.exception_type.as_ref()),
            }))?;
        }
        sequence.end()
    }
}

struct CallTreeView<'a> {
    nodes: &'a [CallNode],
    strings: &'a Interner,
}
impl Serialize for CallTreeView<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut sequence = serializer.serialize_seq(Some(self.nodes.len()))?;
        for node in self.nodes {
            sequence.serialize_element(&json!({
                "callId": node.call_id,
                "parentCallId": (node.parent_call_id >= 0).then_some(node.parent_call_id),
                "testId": self.strings.resolve(node.test), "methodFullName": self.strings.resolve(node.method),
                "ordinal": node.ordinal, "isHarness": node.flags & 1 != 0,
                "filePath": self.strings.optional(node.path), "line": node.line,
                "process": self.strings.resolve(node.process),
            }))?;
        }
        sequence.end()
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
) -> Result<EvidenceEvent, String> {
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
    serde_json::from_slice(&bytes).map_err(|error| error.to_string())
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
fn describe_run(run: &RunData) -> Value {
    json!({ "name": run.name, "schema": run.schema, "language": run.language, "root": run.root, "traceFiles": run.files.len(), "events": run.events, "subjectEvents": run.subject_events, "harnessEvents": run.harness_events })
}
fn describe_gap(gap: &Gap) -> Value {
    json!({ "scope": gap.scope, "assembly": gap.assembly, "methodFullName": gap.method, "baseState": gap.base_state, "prState": gap.pr_state, "reason": gap.reason })
}
fn describe_member(member: &MemberEntry) -> Value {
    json!({ "methodFullName": member.method_full_name, "assembly": member.assembly, "status": member.status, "skipReason": member.skip_reason, "detail": member.detail, "sourceResolution": member.source_resolution, "isTestRoot": member.is_test_root })
}
fn describe_assembly(assembly: &AssemblyEntry) -> Value {
    json!({ "assembly": assembly.assembly, "instrumented": assembly.instrumented, "sourcePartial": assembly.source_partial, "sourceUnavailable": assembly.source_unavailable })
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
