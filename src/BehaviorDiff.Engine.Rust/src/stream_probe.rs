use crate::DiffOptions;
use serde::{Deserialize, Serialize};
use std::borrow::Cow;
use std::collections::{HashMap, HashSet};
use std::fs::{self, File};
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};

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
struct Signature {
    ordinal: i32,
    args: Option<CompactDigest>,
    result: Option<CompactDigest>,
    exception: Option<u32>,
}

#[derive(Clone, Copy, Debug)]
#[allow(dead_code)]
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
struct RunAggregate {
    signatures: HashMap<Key, Vec<Signature>>,
    graph: Vec<CallNode>,
    events: u64,
    subject_events: u64,
    harness_events: u64,
}

#[derive(Default)]
struct Interner {
    ids: HashMap<Box<str>, u32>,
}

impl Interner {
    fn intern(&mut self, value: &str) -> Result<u32, String> {
        if let Some(id) = self.ids.get(value) {
            return Ok(*id);
        }
        let id = u32::try_from(self.ids.len())
            .map_err(|_| "streaming interner exceeded u32 identifiers".to_owned())?;
        self.ids.insert(value.into(), id);
        Ok(id)
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
    return_digest: Option<Cow<'a, str>>,
    #[serde(default, borrow)]
    exception_type: Option<Cow<'a, str>>,
    #[serde(default)]
    is_harness: bool,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ManifestRecord<'a> {
    #[serde(borrow)]
    kind: Cow<'a, str>,
    #[serde(default)]
    enqueued: i64,
    #[serde(default)]
    written: i64,
    #[serde(default)]
    dropped: i64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ProbeReport {
    schema: &'static str,
    events_consumed: u64,
    matched_keys: usize,
    noise_keys: usize,
    base_call_nodes: usize,
    pr_call_nodes: usize,
    base1_events: u64,
    base2_events: u64,
    base3_events: u64,
    pr_events: u64,
    interned_strings: usize,
    base1_aggregate_keys: usize,
    pr_aggregate_keys: usize,
}

pub(crate) fn run(options: &DiffOptions) -> Result<(), String> {
    let mut interner = Interner::default();
    let mut base1 = load_run(
        "base_run1",
        &options.base1,
        options.base_root.as_deref(),
        true,
        &mut interner,
    )?;
    let base2 = load_run(
        "base_run2",
        &options.base2,
        options.base_root.as_deref(),
        false,
        &mut interner,
    )?;
    let mut noise = differing_keys(&base1.signatures, &base2.signatures);
    drop(base2.signatures);

    let base3 = load_run(
        "base_run3",
        &options.base3,
        options.base_root.as_deref(),
        false,
        &mut interner,
    )?;
    noise.extend(differing_keys(&base1.signatures, &base3.signatures));
    drop(base3.signatures);

    let mut pr = load_run(
        "pr_run",
        &options.pr,
        options.pr_root.as_deref(),
        true,
        &mut interner,
    )?;
    let matched = base1
        .signatures
        .keys()
        .filter(|key| pr.signatures.contains_key(key))
        .count();

    let report = ProbeReport {
        schema: "behaviordiff.streaming-probe/1",
        events_consumed: base1.events + base2.events + base3.events + pr.events,
        matched_keys: matched,
        noise_keys: noise.len(),
        base_call_nodes: base1.graph.len(),
        pr_call_nodes: pr.graph.len(),
        base1_events: base1.events,
        base2_events: base2.events,
        base3_events: base3.events,
        pr_events: pr.events,
        interned_strings: interner.ids.len(),
        base1_aggregate_keys: base1.signatures.len(),
        pr_aggregate_keys: pr.signatures.len(),
    };

    if let Some(parent) = Path::new(&options.output)
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
    {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    let output = File::create(&options.output).map_err(|error| error.to_string())?;
    serde_json::to_writer_pretty(output, &report).map_err(|error| error.to_string())?;

    // Keep the measured state live until after serialization so RSS sampling includes the full probe.
    std::hint::black_box((&mut base1, &mut pr, &noise));
    Ok(())
}

fn load_run(
    name: &str,
    directory: &str,
    root: Option<&str>,
    retain_graph: bool,
    interner: &mut Interner,
) -> Result<RunAggregate, String> {
    let mut trace_files = files_with_suffix(directory, ".ndjson")?;
    trace_files.retain(|path| !path.to_string_lossy().ends_with(".manifest.ndjson"));
    if trace_files.is_empty() {
        return Err(format!("Run '{name}' has no trace files in {directory}"));
    }

    let mut run = RunAggregate::default();
    for trace_path in trace_files {
        let process_name = trace_path
            .file_stem()
            .and_then(|value| value.to_str())
            .ok_or_else(|| format!("Invalid trace file name: {}", trace_path.display()))?;
        let process = interner.intern(process_name)?;
        let file = File::open(&trace_path).map_err(|error| error.to_string())?;
        let mut reader = BufReader::with_capacity(256 * 1024, file);
        let mut line = String::new();
        let mut physical_records = 0_i64;
        loop {
            line.clear();
            if reader
                .read_line(&mut line)
                .map_err(|error| error.to_string())?
                == 0
            {
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
                    "{}({physical_records}): required event identity is invalid",
                    trace_path.display()
                ));
            }

            let test = interner.intern(&event.test_id)?;
            let method = interner.intern(&event.method_full_name)?;
            let args = compact_digest(event.args_digest.as_deref(), interner)?;
            let result = compact_digest(event.return_digest.as_deref(), interner)?;
            let exception = event
                .exception_type
                .as_deref()
                .map(|value| interner.intern(value))
                .transpose()?;
            run.events += 1;
            if event.is_harness {
                run.harness_events += 1;
            } else {
                run.subject_events += 1;
                run.signatures
                    .entry(Key { test, method })
                    .or_default()
                    .push(Signature {
                        ordinal: event.ordinal,
                        args,
                        result,
                        exception,
                    });
            }

            if retain_graph {
                let path = match event.file_path.as_deref() {
                    Some(value) => interner.intern(&normalize_path(value, root))?,
                    None => u32::MAX,
                };
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
        validate_writer(&trace_path, physical_records)?;
    }

    for signatures in run.signatures.values_mut() {
        signatures.sort_by_key(|signature| signature.ordinal);
        if signatures
            .iter()
            .enumerate()
            .any(|(expected, signature)| signature.ordinal != expected as i32)
        {
            return Err(format!(
                "Run '{name}' has a duplicate or non-contiguous subject call ordinal."
            ));
        }
    }
    Ok(run)
}

fn differing_keys(
    left: &HashMap<Key, Vec<Signature>>,
    right: &HashMap<Key, Vec<Signature>>,
) -> HashSet<Key> {
    let mut different = HashSet::new();
    for (key, left_signatures) in left {
        if right.get(key) != Some(left_signatures) {
            different.insert(*key);
        }
    }
    for key in right.keys() {
        if !left.contains_key(key) {
            different.insert(*key);
        }
    }
    different
}

fn compact_digest(
    value: Option<&str>,
    interner: &mut Interner,
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
        return Ok(Some(CompactDigest::Sha256(bytes)));
    }
    Ok(Some(CompactDigest::Text(interner.intern(value)?)))
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

fn validate_writer(trace_path: &Path, physical_records: i64) -> Result<(), String> {
    let file_name = trace_path
        .file_name()
        .and_then(|value| value.to_str())
        .ok_or_else(|| format!("Invalid trace path: {}", trace_path.display()))?;
    let process_key = file_name.strip_suffix(".ndjson").unwrap_or(file_name);
    let manifest_path = trace_path.with_file_name(format!("{process_key}.manifest.ndjson"));
    let file = File::open(&manifest_path).map_err(|error| error.to_string())?;
    let reader = BufReader::new(file);
    let mut writer = None;
    for (index, line) in reader.lines().enumerate() {
        let line = line.map_err(|error| error.to_string())?;
        if line.is_empty() {
            continue;
        }
        let record: ManifestRecord<'_> = serde_json::from_str(&line)
            .map_err(|error| format!("{}({}): {error}", manifest_path.display(), index + 1))?;
        if record.kind == "writer" {
            writer = Some((record.enqueued, record.written, record.dropped));
        }
    }
    let (enqueued, written, dropped) = writer.ok_or_else(|| {
        format!(
            "Manifest has no writer accounting: {}",
            manifest_path.display()
        )
    })?;
    if dropped != 0 || enqueued != written || written != physical_records {
        return Err(format!(
            "Trace writer accounting does not reconcile for '{process_key}': enqueued={enqueued} written={written} records={physical_records} dropped={dropped}."
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sha256_digest_is_decoded_losslessly() {
        let mut interner = Interner::default();
        let digest = compact_digest(
            Some("sha256:000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"),
            &mut interner,
        )
        .unwrap();
        assert_eq!(
            digest,
            Some(CompactDigest::Sha256([
                0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22,
                23, 24, 25, 26, 27, 28, 29, 30, 31,
            ]))
        );
    }

    #[test]
    fn differing_keys_detects_missing_and_changed_sequences() {
        let key = Key { test: 1, method: 2 };
        let signature = Signature {
            ordinal: 0,
            args: None,
            result: None,
            exception: None,
        };
        let left = HashMap::from([(key, vec![signature])]);
        let right = HashMap::new();
        assert_eq!(differing_keys(&left, &right), HashSet::from([key]));
    }
}
