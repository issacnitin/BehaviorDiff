use crate::loader::{load_run, LoadReport};
use crate::matcher::{compare, index, CallKey, DigestConfidence, Divergence, MatchedKey};
use crate::model::{AssemblyEntry, MemberEntry, RunData};
use crate::DiffOptions;
use serde_json::{json, Value};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::Path;
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

const MINIMUM_MATCHED_KEYS: usize = 100;
const MAXIMUM_BASE_COUNT_DRIFT_PERCENT: f64 = 10.0;
const MAXIMUM_NOISE_PERCENT: f64 = 20.0;
const MINIMUM_PATH_OVERLAP_PERCENT: f64 = 50.0;

#[derive(Clone, Debug)]
struct ToolingGap {
    scope: String,
    assembly: String,
    method_full_name: Option<String>,
    base_state: String,
    pr_state: String,
    reason: String,
}

pub(crate) fn run_diff(options: &DiffOptions) -> Result<i32, String> {
    let mut base1_report = LoadReport::default();
    let mut base2_report = LoadReport::default();
    let mut base3_report = LoadReport::default();
    let mut pr_report = LoadReport::default();
    let base1 = load_run(
        "base_run1",
        &options.base1,
        options.base_root.as_deref(),
        &mut base1_report,
    )?;
    let base2 = load_run(
        "base_run2",
        &options.base2,
        options.base_root.as_deref(),
        &mut base2_report,
    )?;
    let base3 = if options.base3.is_empty() {
        None
    } else {
        Some(load_run(
            "base_run3",
            &options.base3,
            options.base_root.as_deref(),
            &mut base3_report,
        )?)
    };
    let pr = load_run(
        "pr_run",
        &options.pr,
        options.pr_root.as_deref(),
        &mut pr_report,
    )?;
    let base_runs: Vec<&RunData> = [Some(&base1), Some(&base2), base3.as_ref()]
        .into_iter()
        .flatten()
        .collect();
    let mut refusals = Vec::new();

    let languages: BTreeSet<_> = base_runs
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
    let malformed = base1_report.malformed_lines
        + base2_report.malformed_lines
        + base3_report.malformed_lines
        + pr_report.malformed_lines;
    if malformed > 0 {
        refusals.push(format!(
            "STEP 0: {malformed} malformed trace line(s) were found. Missing events can look like removed behavior."
        ));
    }
    let absolute_remaining = base1_report.absolute_paths_remaining
        + base2_report.absolute_paths_remaining
        + base3_report.absolute_paths_remaining
        + pr_report.absolute_paths_remaining;
    if absolute_remaining > 0 {
        refusals.push(format!(
            "STEP 0: {absolute_remaining} FilePath(s) are still absolute after normalization. Every subsequent comparison would treat identical files as different. Pass --base-root/--pr-root."
        ));
    }
    let base_paths = relative_paths(&base1);
    let pr_paths = relative_paths(&pr);
    let shared = base_paths.intersection(&pr_paths).count();
    let overlap = if base_paths.is_empty() {
        0.0
    } else {
        shared as f64 * 100.0 / base_paths.len() as f64
    };
    if overlap < MINIMUM_PATH_OVERLAP_PERCENT {
        refusals.push(format!(
            "STEP 0: base and PR relative path sets overlap only {overlap:.1}%. Normalization is wrong, so every comparison below it is garbage."
        ));
    }

    let mut manifest_noise_signatures = BTreeSet::new();
    let mut manifest_noise = Vec::new();
    for left in 0..base_runs.len() {
        for right in left + 1..base_runs.len() {
            for gap in manifest_diff(base_runs[left], base_runs[right]) {
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
    let mut method_files = BTreeMap::new();
    for run in [&base1, &pr] {
        for loaded in &run.events {
            if let Some(path) = &loaded.relative_path {
                method_files
                    .entry(loaded.event.method_full_name.clone())
                    .or_insert_with(|| path.clone());
            }
        }
    }
    let mut lifecycle = Vec::new();
    let mut gaps = Vec::new();
    for gap in initial_gaps {
        let file = gap
            .method_full_name
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
        .chain(lifecycle.iter())
        .filter_map(|gap| gap.method_full_name.clone())
        .collect();

    let base_indexes: Vec<_> = base_runs.iter().map(|run| index(run)).collect();
    let base1_index = &base_indexes[0];
    let mut noise_keys = BTreeSet::new();
    let mut noise_divergences = Vec::new();
    for left in 0..base_indexes.len() {
        for right in left + 1..base_indexes.len() {
            let (pair_divergences, _, _) = compare(&base_indexes[left], &base_indexes[right]);
            for divergence in pair_divergences {
                if noise_keys.insert(divergence.key.clone()) {
                    noise_divergences.push(divergence);
                }
            }
        }
    }

    let (raw, matched, harness_divergences) = compare(base1_index, &index(&pr));
    let mut remaining: Vec<_> = raw
        .iter()
        .filter(|divergence| {
            !noise_keys.contains(&divergence.key)
                && !gap_methods.contains(&divergence.key.method_full_name)
        })
        .cloned()
        .collect();
    for gap in &lifecycle {
        let added = gap.pr_state == "Patched";
        let side = if added { &pr } else { &base1 };
        let method = gap.method_full_name.as_deref().unwrap();
        let mut test_ids = BTreeSet::new();
        for loaded in side
            .events
            .iter()
            .filter(|loaded| loaded.event.method_full_name == method)
        {
            if !test_ids.insert(loaded.event.test_id.clone()) {
                continue;
            }
            remaining.push(Divergence {
                key: CallKey {
                    test_id: loaded.event.test_id.clone(),
                    method_full_name: method.to_owned(),
                },
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
                confidence: DigestConfidence::Exact,
                partial_markers: Vec::new(),
                base_event: None,
                pr_event: None,
                relative_path: method_files.get(method).cloned(),
            });
        }
    }
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

    let artifact = artifact(
        &base1,
        &base2,
        &pr,
        &gaps,
        &manifest_noise,
        &noise_divergences,
        &noise_keys,
        &matched,
        &raw,
        &remaining,
        &harness_divergences,
    );
    if let Some(parent) = Path::new(&options.output)
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
    {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    fs::write(
        &options.output,
        serde_json::to_string_pretty(&artifact).map_err(|error| error.to_string())?,
    )
    .map_err(|error| error.to_string())?;
    Ok(0)
}

fn relative_paths(run: &RunData) -> BTreeSet<String> {
    run.events
        .iter()
        .filter(|loaded| !loaded.event.is_harness)
        .filter_map(|loaded| loaded.relative_path.clone())
        .collect()
}

fn check_volume(
    refusals: &mut Vec<String>,
    base1: &RunData,
    base2: &RunData,
    pr: &RunData,
    matched: usize,
    noise: usize,
) {
    let base1_count = base1.subject_event_count();
    let base2_count = base2.subject_event_count();
    let pr_count = pr.subject_event_count();
    for (name, count) in [
        ("base_run1", base1_count),
        ("base_run2", base2_count),
        ("pr_run", pr_count),
    ] {
        if count == 0 {
            refusals.push(format!("VOLUME: run '{name}' has zero subject events."));
        }
    }
    let drift = if base1_count == 0 {
        100.0
    } else {
        base1_count.abs_diff(base2_count) as f64 * 100.0 / base1_count as f64
    };
    if drift > MAXIMUM_BASE_COUNT_DRIFT_PERCENT {
        refusals.push(format!(
            "VOLUME: base_run1 and base_run2 subject event counts differ by {drift:.2}%, above the {MAXIMUM_BASE_COUNT_DRIFT_PERCENT:.0}% limit. The two base runs are not comparable, so the noise baseline is not a baseline."
        ));
    }
    if matched < MINIMUM_MATCHED_KEYS {
        refusals.push(format!(
            "VOLUME: only {matched} matched key(s), below the minimum of {MINIMUM_MATCHED_KEYS}. Too little was compared for a clean result to mean anything."
        ));
    }
    let noise_percent = if matched == 0 {
        100.0
    } else {
        noise as f64 * 100.0 / matched as f64
    };
    if noise_percent > MAXIMUM_NOISE_PERCENT {
        refusals.push(format!(
            "VOLUME: the noise exclusion set covers {noise_percent:.2}% of keys, above the {MAXIMUM_NOISE_PERCENT:.0}% limit. Excluding that much would hide real changes rather than cancel noise."
        ));
    }
}

fn manifest_diff(base: &RunData, pr: &RunData) -> Vec<ToolingGap> {
    let base_members: BTreeMap<_, _> = base
        .members
        .iter()
        .filter_map(|member| {
            member
                .method_full_name
                .as_ref()
                .map(|method| (method, member))
        })
        .collect();
    let pr_members: BTreeMap<_, _> = pr
        .members
        .iter()
        .filter_map(|member| {
            member
                .method_full_name
                .as_ref()
                .map(|method| (method, member))
        })
        .collect();
    let methods: BTreeSet<_> = base_members
        .keys()
        .chain(pr_members.keys())
        .copied()
        .collect();
    let mut gaps = Vec::new();
    for method in methods {
        let base_entry = base_members.get(method).copied();
        let pr_entry = pr_members.get(method).copied();
        let base_state = base_entry.map_or("absent", |entry| entry.status.as_str());
        let pr_state = pr_entry.map_or("absent", |entry| entry.status.as_str());
        if base_state == pr_state {
            continue;
        }
        let present = base_entry.or(pr_entry).unwrap();
        if (base_entry.is_none() || pr_entry.is_none())
            && present.status == "Skipped"
            && present.skip_reason.as_deref() == Some("ExcludedByScope")
        {
            continue;
        }
        gaps.push(ToolingGap {
            scope: "member".to_owned(),
            assembly: present.assembly.clone(),
            method_full_name: Some(method.to_string()),
            base_state: base_state.to_owned(),
            pr_state: pr_state.to_owned(),
            reason: format!("observability differs between runs: {base_state} -> {pr_state}"),
        });
    }

    let base_assemblies: BTreeMap<_, _> = base
        .assemblies
        .iter()
        .map(|assembly| (assembly.assembly.as_str(), assembly))
        .collect();
    let pr_assemblies: BTreeMap<_, _> = pr
        .assemblies
        .iter()
        .map(|assembly| (assembly.assembly.as_str(), assembly))
        .collect();
    let names: BTreeSet<_> = base_assemblies
        .keys()
        .chain(pr_assemblies.keys())
        .copied()
        .collect();
    for name in names {
        let base_entry = base_assemblies.get(name).copied();
        let pr_entry = pr_assemblies.get(name).copied();
        if !base_entry.is_some_and(|entry| entry.instrumented)
            && !pr_entry.is_some_and(|entry| entry.instrumented)
        {
            continue;
        }
        add_flag_gap(
            &mut gaps,
            name,
            "sourceUnavailable",
            base_entry.map(|entry| entry.source_unavailable),
            pr_entry.map(|entry| entry.source_unavailable),
        );
        add_flag_gap(
            &mut gaps,
            name,
            "sourcePartial",
            base_entry.map(|entry| entry.source_partial),
            pr_entry.map(|entry| entry.source_partial),
        );
    }
    gaps
}

fn add_flag_gap(
    gaps: &mut Vec<ToolingGap>,
    assembly: &str,
    flag: &str,
    base_value: Option<bool>,
    pr_value: Option<bool>,
) {
    if base_value == pr_value {
        return;
    }
    let state = |value: Option<bool>| match value {
        Some(true) => "True",
        Some(false) => "False",
        None => "absent",
    };
    gaps.push(ToolingGap {
        scope: format!("assembly:{flag}"),
        assembly: assembly.to_owned(),
        method_full_name: None,
        base_state: state(base_value).to_owned(),
        pr_state: state(pr_value).to_owned(),
        reason: format!("tracer coverage flag '{flag}' differs between runs"),
    });
}

fn gap_signature(gap: &ToolingGap) -> String {
    format!(
        "{}|{}|{}",
        gap.scope,
        gap.assembly,
        gap.method_full_name.as_deref().unwrap_or_default()
    )
}

#[allow(clippy::too_many_arguments)]
fn artifact(
    base1: &RunData,
    base2: &RunData,
    pr: &RunData,
    gaps: &[ToolingGap],
    manifest_noise: &[ToolingGap],
    noise_divergences: &[Divergence],
    noise_keys: &BTreeSet<CallKey>,
    matched: &[MatchedKey],
    raw: &[Divergence],
    remaining: &[Divergence],
    harness_divergences: &[Divergence],
) -> Value {
    json!({
        "schema": "behaviordiff.divergenceset/2",
        "generatedUtc": OffsetDateTime::now_utc().format(&Rfc3339).unwrap(),
        "runs": {
            "base1": describe_run(base1),
            "base2": describe_run(base2),
            "pr": describe_run(pr),
        },
        "counts": {
            "matchedKeys": matched.len(),
            "rawDifferences": raw.len(),
            "noiseExcludedKeys": noise_keys.len(),
            "noiseExcludedDifferences": raw.iter().filter(|item| noise_keys.contains(&item.key)).count(),
            "toolingGaps": gaps.len(),
            "remainingDivergences": remaining.len(),
            "matchedKeysPartial": matched.iter().filter(|item| item.confidence.as_str() == "Partial").count(),
        },
        "matchedKeys": matched.iter().map(describe_matched).collect::<Vec<_>>(),
        "divergences": remaining.iter().map(describe_divergence).collect::<Vec<_>>(),
        "noiseExclusions": noise_keys.iter().map(|key| json!({
            "testId": key.test_id,
            "methodFullName": key.method_full_name,
            "differences": noise_divergences.iter().filter(|item| item.key == *key).count(),
        })).collect::<Vec<_>>(),
        "toolingGaps": gaps.iter().map(describe_gap).collect::<Vec<_>>(),
        "manifestNoise": manifest_noise.iter().map(|gap| json!({
            "scope": gap.scope,
            "assembly": gap.assembly,
            "methodFullName": gap.method_full_name,
            "run1State": gap.base_state,
            "run2State": gap.pr_state,
            "reason": "nondeterministic tracer coverage: differs between two runs of the same build",
        })).collect::<Vec<_>>(),
        "harnessDivergences": harness_divergences.iter().map(|item| json!({
            "testId": item.key.test_id,
            "methodFullName": item.key.method_full_name,
            "kind": item.kind,
            "detail": item.detail,
            "isTestRoot": member(base1, &item.key.method_full_name).map(|entry| entry.is_test_root),
        })).collect::<Vec<_>>(),
        "coverage": {
            "members": base1.members.iter().map(describe_member).collect::<Vec<_>>(),
            "assemblies": base1.assemblies.iter().map(describe_assembly).collect::<Vec<_>>(),
        },
        "callTree": describe_call_tree(base1),
        "prCallTree": describe_call_tree(pr),
    })
}

fn describe_run(run: &RunData) -> Value {
    json!({
        "name": run.name,
        "schema": run.schema,
        "language": run.language,
        "root": run.root,
        "traceFiles": run.trace_file_count,
        "events": run.events.len(),
        "subjectEvents": run.subject_event_count(),
        "harnessEvents": run.harness_event_count(),
    })
}

fn describe_matched(item: &MatchedKey) -> Value {
    json!({
        "testId": item.key.test_id,
        "methodFullName": item.key.method_full_name,
        "filePath": item.relative_path,
        "baseCalls": item.base_calls,
        "prCalls": item.pr_calls,
        "digestConfidence": item.confidence.as_str(),
        "partialMarkers": item.partial_markers,
    })
}

fn describe_divergence(item: &Divergence) -> Value {
    json!({
        "testId": item.key.test_id,
        "methodFullName": item.key.method_full_name,
        "filePath": item.relative_path,
        "ordinal": item.ordinal,
        "kind": item.kind,
        "detail": item.detail,
        "digestConfidence": item.confidence.as_str(),
        "partialMarkers": item.partial_markers,
        "baseArgsDigest": item.base_event.as_ref().and_then(|event| event.args_digest.as_ref()),
        "prArgsDigest": item.pr_event.as_ref().and_then(|event| event.args_digest.as_ref()),
        "baseArgsRendered": item.base_event.as_ref().and_then(|event| event.args_rendered.as_ref()),
        "prArgsRendered": item.pr_event.as_ref().and_then(|event| event.args_rendered.as_ref()),
        "baseReturnDigest": item.base_event.as_ref().and_then(|event| event.return_digest.as_ref()),
        "prReturnDigest": item.pr_event.as_ref().and_then(|event| event.return_digest.as_ref()),
        "baseReturnRendered": item.base_event.as_ref().and_then(|event| event.return_rendered.as_ref()),
        "prReturnRendered": item.pr_event.as_ref().and_then(|event| event.return_rendered.as_ref()),
        "baseExceptionType": item.base_event.as_ref().and_then(|event| event.exception_type.as_ref()),
        "prExceptionType": item.pr_event.as_ref().and_then(|event| event.exception_type.as_ref()),
    })
}

fn describe_gap(gap: &ToolingGap) -> Value {
    json!({
        "scope": gap.scope,
        "assembly": gap.assembly,
        "methodFullName": gap.method_full_name,
        "baseState": gap.base_state,
        "prState": gap.pr_state,
        "reason": gap.reason,
    })
}

fn describe_member(member: &MemberEntry) -> Value {
    json!({
        "methodFullName": member.method_full_name,
        "assembly": member.assembly,
        "filePath": member.file_path,
        "line": member.line,
        "status": member.status,
        "skipReason": member.skip_reason,
        "detail": member.detail,
        "sourceResolution": member.source_resolution,
        "isTestRoot": member.is_test_root,
    })
}

fn describe_assembly(assembly: &AssemblyEntry) -> Value {
    json!({
        "assembly": assembly.assembly,
        "instrumented": assembly.instrumented,
        "sourcePartial": assembly.source_partial,
        "sourceUnavailable": assembly.source_unavailable,
    })
}

fn describe_call_tree(run: &RunData) -> Vec<Value> {
    run.events
        .iter()
        .map(|loaded| {
            json!({
                "callId": loaded.event.call_id,
                "parentCallId": loaded.event.parent_call_id,
                "testId": loaded.event.test_id,
                "methodFullName": loaded.event.method_full_name,
                "ordinal": loaded.event.ordinal,
                "isHarness": loaded.event.is_harness,
                "filePath": loaded.relative_path,
                "line": loaded.event.line,
                "process": loaded.process_key,
            })
        })
        .collect()
}

fn member<'a>(run: &'a RunData, method: &str) -> Option<&'a MemberEntry> {
    run.members
        .iter()
        .find(|entry| entry.method_full_name.as_deref() == Some(method))
}

fn load_changed_files(path: &str) -> Result<BTreeSet<String>, String> {
    if path.is_empty() {
        return Ok(BTreeSet::new());
    }
    let content = fs::read_to_string(path).map_err(|error| format!("{path}: {error}"))?;
    Ok(content
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .map(|line| line.replace('\\', "/"))
        .collect())
}

fn is_method_lifecycle(gap: &ToolingGap) -> bool {
    gap.scope == "member"
        && ((gap.base_state == "absent" && gap.pr_state == "Patched")
            || (gap.base_state == "Patched" && gap.pr_state == "absent"))
}
