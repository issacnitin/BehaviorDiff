use crate::model::{AssemblyEntry, LoadedEvent, MemberEntry, RunData, TraceEvent};
use serde_json::Value;
use std::collections::{BTreeMap, HashMap};
use std::fs;
use std::path::{Path, PathBuf};

const TRACE_SCHEMA: &str = "behaviordiff.trace/1";

#[derive(Default)]
pub(crate) struct LoadReport {
    pub(crate) malformed_lines: usize,
    pub(crate) absolute_paths_remaining: usize,
    pub(crate) paths_normalized: usize,
    pub(crate) paths_already_relative: usize,
    pub(crate) paths_missing: usize,
    pub(crate) root_was_inferred: bool,
}

pub(crate) fn load_run(
    name: &str,
    directory: &str,
    explicit_root: Option<&str>,
    report: &mut LoadReport,
) -> Result<RunData, String> {
    let directory_path = Path::new(directory);
    if !directory_path.is_dir() {
        return Err(format!("Run '{name}': directory not found: {directory}"));
    }

    let mut manifest_files = Vec::new();
    let mut trace_files = Vec::new();
    for entry in fs::read_dir(directory_path).map_err(|error| error.to_string())? {
        let path = entry.map_err(|error| error.to_string())?.path();
        let Some(file_name) = path.file_name().and_then(|value| value.to_str()) else {
            continue;
        };
        if file_name.ends_with(".manifest.ndjson") {
            manifest_files.push(path);
        } else if file_name.ends_with(".ndjson") {
            trace_files.push(path);
        }
    }
    manifest_files.sort();
    trace_files.sort();

    if trace_files.is_empty() {
        return Err(format!(
            "Run '{name}': no trace files (*.ndjson) in {directory}"
        ));
    }

    let mut raw_events = Vec::new();
    let mut record_counts = HashMap::new();
    for path in &trace_files {
        let process_key = path
            .file_stem()
            .and_then(|value| value.to_str())
            .unwrap_or_default()
            .to_owned();
        for (line_index, line) in read_lines(path)?.into_iter().enumerate() {
            if line.is_empty() {
                continue;
            }
            *record_counts.entry(process_key.clone()).or_insert(0_i64) += 1;
            match serde_json::from_str::<TraceEvent>(&line) {
                Ok(event)
                    if !event.test_id.is_empty()
                        && !event.method_full_name.is_empty()
                        && event.ordinal >= 0 =>
                {
                    raw_events.push((event, process_key.clone(), line_index + 1));
                }
                _ => report.malformed_lines += 1,
            }
        }
    }

    let root = explicit_root.map(str::to_owned).unwrap_or_else(|| {
        infer_root(
            raw_events
                .iter()
                .map(|(event, _, _)| event.file_path.as_deref()),
        )
    });
    report.root_was_inferred = explicit_root.is_none();

    let events = raw_events
        .iter()
        .map(|(event, process_key, line_number)| LoadedEvent {
            event: event.clone(),
            process_key: process_key.clone(),
            line_number: *line_number,
            relative_path: normalize_path(event.file_path.as_deref(), &root, report),
        })
        .collect();

    let mut members: Vec<MemberEntry> = Vec::new();
    let mut member_indexes: HashMap<String, usize> = HashMap::new();
    let mut assemblies: Vec<AssemblyEntry> = Vec::new();
    let mut assembly_indexes: HashMap<String, usize> = HashMap::new();
    let mut schema = None;
    let mut language = None;

    for path in &manifest_files {
        let records = read_manifest(path)?;
        validate_manifest(path, &records, &record_counts)?;

        let metadata = records
            .iter()
            .find(|record| record["kind"] == "run")
            .ok_or_else(|| format!("Manifest has no run metadata: {}", path.display()))?;
        let process_schema = required_string(metadata, "schema", path)?;
        let process_language = required_string(metadata, "language", path)?;
        if process_schema != TRACE_SCHEMA {
            return Err(format!(
                "Manifest schema '{process_schema}' is unsupported; expected '{TRACE_SCHEMA}': {}",
                path.display()
            ));
        }
        if schema
            .as_deref()
            .is_some_and(|value| value != process_schema)
            || language
                .as_deref()
                .is_some_and(|value| value != process_language)
        {
            return Err(format!(
                "Process manifests disagree on schema or language in run '{name}'."
            ));
        }
        schema = Some(process_schema.to_owned());
        language = Some(process_language.to_owned());

        for record in records.iter().filter(|record| record["kind"] == "member") {
            let member: MemberEntry = serde_json::from_value(record.clone())
                .map_err(|error| format!("{}: {error}", path.display()))?;
            validate_member(&member, path)?;
            let Some(method) = member.method_full_name.clone() else {
                continue;
            };
            if let Some(index) = member_indexes.get(&method).copied() {
                if members[index].status != "Patched" && member.status == "Patched" {
                    members[index] = member;
                }
            } else {
                member_indexes.insert(method, members.len());
                members.push(member);
            }
        }

        for record in records.iter().filter(|record| record["kind"] == "assembly") {
            let assembly: AssemblyEntry = serde_json::from_value(record.clone())
                .map_err(|error| format!("{}: {error}", path.display()))?;
            validate_assembly(&assembly, path)?;
            if let Some(index) = assembly_indexes.get(&assembly.assembly).copied() {
                if !assemblies[index].instrumented && assembly.instrumented {
                    assemblies[index] = assembly;
                }
            } else {
                assembly_indexes.insert(assembly.assembly.clone(), assemblies.len());
                assemblies.push(assembly);
            }
        }
    }

    if manifest_files.is_empty() || schema.is_none() || language.is_none() {
        return Err(format!(
            "Run '{name}': no versioned coverage manifests in {directory}"
        ));
    }

    validate_ordinals(name, &raw_events)?;
    Ok(RunData {
        name: name.to_owned(),
        root,
        events,
        members,
        assemblies,
        trace_file_count: trace_files.len(),
        schema: schema.unwrap(),
        language: language.unwrap(),
    })
}

fn read_lines(path: &Path) -> Result<Vec<String>, String> {
    let content =
        fs::read_to_string(path).map_err(|error| format!("{}: {error}", path.display()))?;
    Ok(content.lines().map(str::to_owned).collect())
}

fn read_manifest(path: &Path) -> Result<Vec<Value>, String> {
    let mut records = Vec::new();
    let mut metadata_count = 0;
    for (line_index, line) in read_lines(path)?.into_iter().enumerate() {
        if line.is_empty() {
            continue;
        }
        let record: Value = serde_json::from_str(&line)
            .map_err(|error| format!("{}({}): {error}", path.display(), line_index + 1))?;
        let kind = record["kind"]
            .as_str()
            .ok_or_else(|| format!("{}({}): 'kind' is required", path.display(), line_index + 1))?;
        if !matches!(
            kind,
            "run" | "assembly" | "member" | "digest" | "unruled" | "writer"
        ) {
            return Err(format!(
                "{}({}): unrecognised kind: '{kind}'",
                path.display(),
                line_index + 1
            ));
        }
        if kind == "run" {
            metadata_count += 1;
            if metadata_count > 1 {
                return Err(format!(
                    "{}({}): duplicate run metadata record",
                    path.display(),
                    line_index + 1
                ));
            }
        }
        records.push(record);
    }
    Ok(records)
}

fn validate_manifest(
    path: &Path,
    records: &[Value],
    record_counts: &HashMap<String, i64>,
) -> Result<(), String> {
    for assembly in records.iter().filter(|record| record["kind"] == "assembly") {
        let name = required_string(assembly, "assembly", path)?;
        let member_records = records
            .iter()
            .filter(|record| record["kind"] == "member" && record["assembly"] == name)
            .count() as i64;
        let discovered = number(assembly, "discoveredMembers");
        let patched = number(assembly, "patchedMembers");
        let skipped = number(assembly, "skippedMembers");
        let failed = number(assembly, "patchFailedMembers");
        if failed != 0 || discovered != patched + skipped || discovered != member_records {
            return Err(format!(
                "Manifest member accounting does not reconcile for module '{name}': discovered={discovered} instrumented={patched} skipped={skipped} failed={failed} records={member_records}."
            ));
        }
    }

    let writer = records
        .iter()
        .rev()
        .find(|record| record["kind"] == "writer")
        .ok_or_else(|| format!("Manifest has no writer accounting: {}", path.display()))?;
    let file_name = path
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or_default();
    let process_key = file_name
        .strip_suffix(".manifest.ndjson")
        .unwrap_or(file_name);
    let physical_records = record_counts.get(process_key).copied().unwrap_or_default();
    let enqueued = number(writer, "enqueued");
    let written = number(writer, "written");
    let dropped = number(writer, "dropped");
    if dropped != 0 || enqueued != written || written != physical_records {
        return Err(format!(
            "Trace writer accounting does not reconcile for '{process_key}': enqueued={enqueued} written={written} records={physical_records} dropped={dropped}."
        ));
    }
    Ok(())
}

fn validate_member(member: &MemberEntry, path: &Path) -> Result<(), String> {
    if member.assembly.is_empty()
        || !matches!(
            member.status.as_str(),
            "Patched" | "Skipped" | "PatchFailed" | "EnumerationFailed"
        )
    {
        return Err(format!("{}: invalid member record", path.display()));
    }
    Ok(())
}

fn validate_assembly(assembly: &AssemblyEntry, path: &Path) -> Result<(), String> {
    if assembly.assembly.is_empty()
        || !matches!(
            assembly.discovery.as_str(),
            "BuildTimeWeave" | "JavaAgentTransform" | "NodeAstTransform" | "GoAstRewrite"
        )
    {
        return Err(format!("{}: invalid assembly record", path.display()));
    }
    Ok(())
}

fn validate_ordinals(run_name: &str, events: &[(TraceEvent, String, usize)]) -> Result<(), String> {
    let mut groups: BTreeMap<String, Vec<i32>> = BTreeMap::new();
    for (event, process_key, _) in events {
        groups
            .entry(format!(
                "{process_key}\0{}\0{}",
                event.test_id, event.method_full_name
            ))
            .or_default()
            .push(event.ordinal);
    }
    for (key, ordinals) in &mut groups {
        ordinals.sort_unstable();
        if ordinals
            .iter()
            .enumerate()
            .any(|(expected, ordinal)| *ordinal != expected as i32)
        {
            return Err(format!(
                "Run '{run_name}' has a duplicate or non-contiguous call ordinal for {}.",
                key.replace('\0', "|")
            ));
        }
    }
    Ok(())
}

fn required_string<'a>(record: &'a Value, field: &str, path: &Path) -> Result<&'a str, String> {
    record[field]
        .as_str()
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            format!(
                "{}: '{field}' is required and must be non-empty",
                path.display()
            )
        })
}

fn number(record: &Value, field: &str) -> i64 {
    record[field].as_i64().unwrap_or_default()
}

fn infer_root<'a>(paths: impl Iterator<Item = Option<&'a str>>) -> String {
    let absolute_paths: Vec<PathBuf> = paths
        .flatten()
        .map(PathBuf::from)
        .filter(|path| is_path_rooted(path.to_string_lossy().as_ref()))
        .collect();
    let Some(first) = absolute_paths.first().and_then(|path| path.parent()) else {
        return String::new();
    };
    let mut components: Vec<_> = first.components().collect();
    for path in absolute_paths.iter().skip(1) {
        let Some(parent) = path.parent() else {
            continue;
        };
        let other: Vec<_> = parent.components().collect();
        let shared = components
            .iter()
            .zip(other.iter())
            .take_while(|(left, right)| left.as_os_str().eq_ignore_ascii_case(right.as_os_str()))
            .count();
        components.truncate(shared);
    }
    components
        .iter()
        .collect::<PathBuf>()
        .to_string_lossy()
        .into_owned()
}

fn normalize_path(path: Option<&str>, root: &str, report: &mut LoadReport) -> Option<String> {
    let Some(path) = path.filter(|value| !value.is_empty()) else {
        report.paths_missing += 1;
        return None;
    };
    if !is_path_rooted(path) {
        report.paths_already_relative += 1;
        return Some(path.replace('\\', "/"));
    }
    if let Some(relative) = path.strip_prefix("/_/") {
        report.paths_normalized += 1;
        return Some(relative.replace('\\', "/"));
    }
    if !root.is_empty() && path.len() >= root.len() && path[..root.len()].eq_ignore_ascii_case(root)
    {
        report.paths_normalized += 1;
        return Some(
            path[root.len()..]
                .trim_start_matches(['\\', '/'])
                .replace('\\', "/"),
        );
    }
    report.absolute_paths_remaining += 1;
    Some(path.replace('\\', "/"))
}

fn is_path_rooted(path: &str) -> bool {
    path.starts_with('/') || path.starts_with("\\\\") || path.as_bytes().get(1) == Some(&b':')
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_deterministic_source_paths() {
        let mut report = LoadReport::default();

        let normalized = normalize_path(Some("/_/samples/App.cs"), "ignored", &mut report);

        assert_eq!(normalized.as_deref(), Some("samples/App.cs"));
        assert_eq!(report.paths_normalized, 1);
        assert_eq!(report.absolute_paths_remaining, 0);
    }

    #[test]
    fn reports_absolute_paths_outside_the_root() {
        let mut report = LoadReport::default();

        let normalized = normalize_path(Some("C:\\other\\App.cs"), "C:\\repo", &mut report);

        assert_eq!(normalized.as_deref(), Some("C:/other/App.cs"));
        assert_eq!(report.absolute_paths_remaining, 1);
    }
}
