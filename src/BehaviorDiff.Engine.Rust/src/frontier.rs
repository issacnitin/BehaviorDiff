use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap, HashSet};
use std::fs;
use std::path::Path;
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

#[derive(Default)]
pub(crate) struct FrontierOptions {
    pub(crate) input: String,
    pub(crate) changed_files: String,
    pub(crate) output: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct DivergenceSet {
    counts: DivergenceCounts,
    #[serde(default)]
    matched_keys: Vec<MatchedKey>,
    #[serde(default)]
    divergences: Vec<Divergence>,
    #[serde(default)]
    tooling_gaps: Vec<serde_json::Value>,
    #[serde(default)]
    manifest_noise: Vec<serde_json::Value>,
    #[serde(default)]
    harness_divergences: Vec<HarnessDivergence>,
    #[serde(default)]
    coverage: Coverage,
    call_tree: Vec<CallNode>,
    #[serde(default)]
    pr_call_tree: Vec<CallNode>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct DivergenceCounts {
    matched_keys: usize,
    remaining_divergences: usize,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct MatchedKey {
    test_id: String,
    method_full_name: String,
    digest_confidence: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Divergence {
    test_id: String,
    method_full_name: String,
    file_path: Option<String>,
    kind: String,
    detail: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct HarnessDivergence {
    test_id: String,
    is_test_root: Option<bool>,
}

#[derive(Default, Deserialize)]
struct Coverage {
    #[serde(default)]
    members: Vec<CoverageMember>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct CoverageMember {
    method_full_name: Option<String>,
    assembly: String,
    status: String,
    skip_reason: Option<String>,
    source_resolution: Option<String>,
    #[serde(default)]
    is_test_root: bool,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct CallNode {
    pub(crate) call_id: i64,
    pub(crate) parent_call_id: Option<i64>,
    pub(crate) test_id: String,
    pub(crate) method_full_name: String,
    #[serde(default)]
    pub(crate) is_harness: bool,
    pub(crate) file_path: Option<String>,
    pub(crate) line: Option<i32>,
    #[serde(default)]
    pub(crate) process: String,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct FrontierNode {
    classification: &'static str,
    attribution: &'static str,
    test_id: String,
    method_full_name: String,
    file_path: Option<String>,
    line: Option<i32>,
    symptoms: Vec<String>,
    downgrade_reasons: Vec<String>,
    descendant_keys_compared: usize,
    untested: bool,
    untested_evidence: Option<&'static str>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct FrontierReport {
    schema: &'static str,
    generated_utc: String,
    counts: FrontierCounts,
    attribution_inputs: AttributionInputs,
    changed_file_coverage: ChangedFileCoverageReport,
    frontier: Vec<FrontierNode>,
    collateral: Vec<FrontierNode>,
    untested_approximation: &'static str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct FrontierCounts {
    diverged_keys: usize,
    frontier_nodes: usize,
    frontier_verified: usize,
    frontier_unverified: usize,
    collateral_suppressed: usize,
    unexpected: usize,
    expected: usize,
    untested: usize,
    manifest_noise_cancelled: usize,
    tooling_gaps: usize,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct AttributionInputs {
    changed_files: Vec<String>,
    changed_paths_matching_a_traced_file: usize,
    changed_paths_in_trace_path_namespace: usize,
}

#[derive(Serialize)]
struct ChangedFileCoverageReport {
    summary: ChangedFileCoverageSummary,
    files: Vec<ChangedFileCoverage>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ChangedFileCoverageSummary {
    edited_files: usize,
    exercised_edited_files: usize,
    traced_members: usize,
    observed_call_sites: usize,
    base_call_count: usize,
    pr_call_count: usize,
    total_call_count: usize,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ChangedFileCoverage {
    file_path: String,
    exercised: bool,
    traced_members: usize,
    observed_call_sites: usize,
    base_call_count: usize,
    pr_call_count: usize,
    total_call_count: usize,
    interpretation: &'static str,
}

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
struct CallIdentity {
    test_id: String,
    process: String,
    call_id: i64,
}

impl CallIdentity {
    fn new(node: &CallNode, call_id: i64) -> Self {
        Self {
            test_id: node.test_id.clone(),
            process: node.process.clone(),
            call_id,
        }
    }
}

pub(crate) struct CallTreeIndex {
    children: HashMap<CallIdentity, Vec<usize>>,
    pub(crate) orphans: Vec<usize>,
    pub(crate) roots: usize,
}

impl CallTreeIndex {
    pub(crate) fn build(nodes: &[CallNode]) -> Self {
        let by_call = nodes
            .iter()
            .enumerate()
            .map(|(index, node)| (CallIdentity::new(node, node.call_id), index))
            .collect::<HashMap<_, _>>();
        let mut children = HashMap::<CallIdentity, Vec<usize>>::new();
        let mut orphans = Vec::new();
        let mut roots = 0;

        for (index, node) in nodes.iter().enumerate() {
            let Some(parent_call_id) = node.parent_call_id else {
                roots += 1;
                continue;
            };
            let parent = CallIdentity::new(node, parent_call_id);
            if by_call.contains_key(&parent) {
                children.entry(parent).or_default().push(index);
            } else {
                orphans.push(index);
            }
        }

        Self {
            children,
            orphans,
            roots,
        }
    }

    fn children_of(&self, node: &CallNode) -> &[usize] {
        self.children
            .get(&CallIdentity::new(node, node.call_id))
            .map(Vec::as_slice)
            .unwrap_or_default()
    }
}

pub(crate) fn run(options: &FrontierOptions) -> Result<i32, String> {
    let bytes = fs::read(&options.input).map_err(|error| error.to_string())?;
    let set: DivergenceSet = serde_json::from_slice(&bytes).map_err(|error| error.to_string())?;
    if set.call_tree.is_empty() {
        return Err("Frontier input callTree is empty".to_owned());
    }
    println!("=== input ===");
    println!("  matched keys             : {}", set.counts.matched_keys);
    println!(
        "  divergences (part 1)     : {}",
        set.counts.remaining_divergences
    );
    let index = CallTreeIndex::build(&set.call_tree);
    println!("  call-tree nodes          : {}", set.call_tree.len());
    println!("  roots                    : {}", index.roots);
    println!("  orphans                  : {}", index.orphans.len());
    let mut refusals = Vec::new();
    if !index.orphans.is_empty() {
        refusals.push(format!(
            "CALL TREE: {} non-root event(s) could not resolve a parent. Descendant sets would be incomplete, which makes every frontier verdict unsound.",
            index.orphans.len()
        ));
    }

    let mut diverged_keys = BTreeMap::<String, Vec<&Divergence>>::new();
    for divergence in &set.divergences {
        diverged_keys
            .entry(key(&divergence.test_id, &divergence.method_full_name))
            .or_default()
            .push(divergence);
    }
    let confidence_by_key = set
        .matched_keys
        .iter()
        .map(|item| {
            (
                key(&item.test_id, &item.method_full_name),
                item.digest_confidence.as_str(),
            )
        })
        .collect::<HashMap<_, _>>();
    let harness_methods = set
        .coverage
        .members
        .iter()
        .filter(|member| member.is_test_root)
        .filter_map(|member| member.method_full_name.as_deref())
        .collect::<HashSet<_>>();
    let mut skipped_by_type = HashMap::<String, String>::new();
    let mut member_assembly = HashMap::<&str, &str>::new();
    let mut unresolved_members = HashMap::<&str, &str>::new();
    for member in &set.coverage.members {
        let Some(method) = member.method_full_name.as_deref() else {
            continue;
        };
        member_assembly.entry(method).or_insert(&member.assembly);
        if member.status == "Skipped" && member.skip_reason.as_deref() != Some("Unobservable") {
            skipped_by_type.insert(
                declaring_type(method),
                format!(
                    "{}{}",
                    method,
                    member
                        .skip_reason
                        .as_ref()
                        .map(|reason| format!(" ({reason})"))
                        .unwrap_or_default()
                ),
            );
        }
        if let Some(resolution) = member.source_resolution.as_deref() {
            if resolution != "sequencePoints" && resolution != "stateMachine" {
                unresolved_members.insert(method, resolution);
            }
        }
    }
    let harness_diverged_tests = set
        .harness_divergences
        .iter()
        .filter(|item| item.is_test_root != Some(false))
        .map(|item| item.test_id.as_str())
        .collect::<HashSet<_>>();
    let mut line_by_key = HashMap::<String, Option<i32>>::new();
    let mut calls_by_key = HashMap::<String, Vec<usize>>::new();
    for (node_index, node) in set.call_tree.iter().enumerate() {
        let node_key = key(&node.test_id, &node.method_full_name);
        line_by_key.entry(node_key.clone()).or_insert(node.line);
        calls_by_key.entry(node_key).or_default().push(node_index);
    }

    let mut nodes = Vec::new();
    let mut collateral = Vec::new();
    for (node_key, entries) in &diverged_keys {
        let first = entries[0];
        if harness_methods.contains(first.method_full_name.as_str())
            || calls_by_key.get(node_key).is_some_and(|calls| {
                !calls.is_empty() && calls.iter().all(|call| set.call_tree[*call].is_harness)
            })
        {
            continue;
        }
        let mut node = FrontierNode {
            classification: "frontier_unverified",
            attribution: "UNEXPECTED",
            test_id: first.test_id.clone(),
            method_full_name: first.method_full_name.clone(),
            file_path: first.file_path.clone(),
            line: line_by_key.get(node_key).copied().flatten(),
            symptoms: entries
                .iter()
                .map(|item| format!("{}:{}", item.kind, item.detail))
                .collect::<Vec<_>>(),
            downgrade_reasons: Vec::new(),
            descendant_keys_compared: 0,
            untested: false,
            untested_evidence: None,
        };
        node.symptoms.sort();
        let lifecycle = entries
            .iter()
            .any(|item| item.kind == "MethodAdded" || item.kind == "MethodRemoved");
        let unalignable = lifecycle || entries.iter().any(|item| item.kind == "CallCountChange");
        let mut descendant_keys = HashSet::new();
        let mut descendant_methods = HashSet::new();
        if !unalignable {
            for call in calls_by_key.get(node_key).into_iter().flatten() {
                collect_descendants(
                    &set.call_tree[*call],
                    &set.call_tree,
                    &index,
                    &mut descendant_keys,
                    &mut descendant_methods,
                );
            }
        }
        node.descendant_keys_compared = descendant_keys.len();
        if unalignable {
            node.downgrade_reasons.push(if lifecycle {
                "MemberLifecycle: the member exists on only one side, so it has no counterpart to compare descendants against. This says the PR added or removed it, not that it caused anything.".to_owned()
            } else {
                "SubtreeUnalignable: call count differs, descendants are not alignable".to_owned()
            });
        } else if descendant_keys
            .iter()
            .any(|descendant| diverged_keys.contains_key(descendant))
        {
            collateral.push(node);
            continue;
        }
        if confidence_by_key.get(node_key).copied() == Some("Partial") {
            node.downgrade_reasons
                .push("NodePartial: this key's own digest is Partial".to_owned());
        }
        if let Some(descendant) = descendant_keys
            .iter()
            .find(|descendant| confidence_by_key.get(*descendant).copied() == Some("Partial"))
        {
            let method = descendant.split_once('|').map_or("", |(_, method)| method);
            node.downgrade_reasons.push(format!(
                "DescendantPartial: {method} has a Partial digest, so 'identical' does not establish identical behavior"
            ));
        }
        for method in descendant_methods
            .iter()
            .map(String::as_str)
            .chain(std::iter::once(first.method_full_name.as_str()))
        {
            let declared = declaring_type(method);
            if let Some(skipped) = skipped_by_type.get(&declared) {
                node.downgrade_reasons.push(format!(
                    "DescendantSkipped: {declared} also declares {skipped}, which was never instrumented, so a call to it from this subtree would be invisible"
                ));
                break;
            }
        }
        for method in &descendant_methods {
            if let Some(resolution) = unresolved_members.get(method.as_str()) {
                node.downgrade_reasons.push(format!(
                    "DescendantSourceUnresolved: {method} resolved its source as '{resolution}', so a divergence in it could not be attributed"
                ));
                break;
            }
        }
        if node.downgrade_reasons.is_empty() {
            node.classification = "frontier";
        }
        node.untested = !harness_diverged_tests.contains(node.test_id.as_str());
        node.untested_evidence = Some(if node.untested {
            "no harness event in this test diverged, so no assertion reacted to the change"
        } else {
            "the test's own trace diverged, so an assertion reacted"
        });
        nodes.push(node);
    }

    let changed = load_changed_files(&options.changed_files)?;
    let coverage = changed_file_coverage(&changed, &set.call_tree, &set.pr_call_tree);
    if changed.is_empty() && !nodes.is_empty() {
        refusals.push(format!(
            "ATTRIBUTION: the changed-file set is empty but there are {} frontier node(s). All of them would be classified UNEXPECTED, which reads as a large finding rather than as a missing input.",
            nodes.len()
        ));
    }
    let trace_paths = set
        .call_tree
        .iter()
        .filter_map(|node| node.file_path.as_deref())
        .collect::<HashSet<_>>();
    let trace_roots = trace_paths
        .iter()
        .map(|path| first_segment(path))
        .collect::<HashSet<_>>();
    let exact_matches = changed
        .iter()
        .filter(|file| trace_paths.contains(file.as_str()))
        .count();
    let namespace_matches = changed
        .iter()
        .filter(|file| trace_roots.contains(first_segment(file)))
        .count();
    if !changed.is_empty() && namespace_matches == 0 {
        refusals.push(format!(
            "ATTRIBUTION: no changed path shares a root segment with any traced path. The two sets are in different path formats, so every node would be classified UNEXPECTED and the run would look like a spectacular finding. Sample changed='{}' traced='{}'.",
            changed.first().map(String::as_str).unwrap_or("<none>"),
            trace_paths.iter().next().copied().unwrap_or("<none>")
        ));
    }
    let intentionally_untraced =
        changed_files_intentionally_untraced(&set.coverage.members, &changed);
    let unattributable =
        !changed.is_empty() && !nodes.is_empty() && exact_matches == 0 && !intentionally_untraced;
    if unattributable {
        let reasons = changed
            .iter()
            .map(|file| {
                let members = members_for_changed_file(&set.coverage.members, file);
                let mut counts = BTreeMap::<&str, usize>::new();
                for member in members
                    .iter()
                    .filter_map(|member| member.skip_reason.as_deref())
                {
                    *counts.entry(member).or_default() += 1;
                }
                let reasons = if counts.is_empty() {
                    "no member of this file reached the manifest".to_owned()
                } else {
                    counts
                        .into_iter()
                        .map(|(reason, count)| format!("{reason} x{count}"))
                        .collect::<Vec<_>>()
                        .join(", ")
                };
                format!("      {file} -> {reasons}")
            })
            .collect::<Vec<_>>()
            .join("\n");
        refusals.push(format!(
            "ATTRIBUTION: none of the {} changed file(s) contributed a single traced member, so all {} frontier node(s) would be reported as UNEXPECTED changes in files the PR never touched. The edited code was not observed, so this run cannot attribute anything.\n    changed files and why their members were not instrumented:\n{reasons}",
            changed.len(), nodes.len()
        ));
    }
    for node in &mut nodes {
        if node
            .file_path
            .as_ref()
            .is_some_and(|file| changed.contains(file))
        {
            node.attribution = "EXPECTED";
        }
    }
    if !refusals.is_empty() {
        eprintln!("REFUSED to emit a frontier report.");
        for refusal in refusals {
            eprintln!("  - {refusal}");
        }
        return Ok(if unattributable { 3 } else { 4 });
    }

    let report = FrontierReport {
        schema: "behaviordiff.frontierreport/2",
        generated_utc: OffsetDateTime::now_utc().format(&Rfc3339).unwrap(),
        counts: FrontierCounts {
            diverged_keys: diverged_keys.len(),
            frontier_nodes: nodes.len(),
            frontier_verified: nodes.iter().filter(|node| node.classification == "frontier").count(),
            frontier_unverified: nodes.iter().filter(|node| node.classification != "frontier").count(),
            collateral_suppressed: collateral.len(),
            unexpected: nodes.iter().filter(|node| node.attribution == "UNEXPECTED").count(),
            expected: nodes.iter().filter(|node| node.attribution == "EXPECTED").count(),
            untested: nodes.iter().filter(|node| node.untested).count(),
            manifest_noise_cancelled: set.manifest_noise.len(),
            tooling_gaps: set.tooling_gaps.len(),
        },
        attribution_inputs: AttributionInputs {
            changed_files: changed.to_vec(),
            changed_paths_matching_a_traced_file: exact_matches,
            changed_paths_in_trace_path_namespace: namespace_matches,
        },
        changed_file_coverage: coverage,
        frontier: nodes,
        collateral,
        untested_approximation: "A frontier node is reported untested when no harness event in its test diverged. That means no assertion reacted to the change in this run; it is not proof that the value is unasserted.",
    };
    if let Some(parent) = Path::new(&options.output).parent() {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    fs::write(
        &options.output,
        serde_json::to_vec_pretty(&report).map_err(|error| error.to_string())?,
    )
    .map_err(|error| error.to_string())?;
    println!(
        "Frontier report written: {}",
        Path::new(&options.output)
            .canonicalize()
            .unwrap_or_else(|_| Path::new(&options.output).to_path_buf())
            .display()
    );
    Ok(0)
}

fn key(test_id: &str, method: &str) -> String {
    format!("{test_id}|{method}")
}

fn collect_descendants(
    node: &CallNode,
    nodes: &[CallNode],
    index: &CallTreeIndex,
    keys: &mut HashSet<String>,
    methods: &mut HashSet<String>,
) {
    for child in index.children_of(node) {
        let child = &nodes[*child];
        keys.insert(key(&child.test_id, &child.method_full_name));
        methods.insert(child.method_full_name.clone());
        collect_descendants(child, nodes, index, keys, methods);
    }
}

fn load_changed_files(path: &str) -> Result<Vec<String>, String> {
    if path.is_empty() || !Path::new(path).is_file() {
        return Ok(Vec::new());
    }
    let mut files = fs::read_to_string(path)
        .map_err(|error| error.to_string())?
        .lines()
        .map(|line| line.trim().replace('\\', "/"))
        .filter(|line| !line.is_empty())
        .collect::<HashSet<_>>()
        .into_iter()
        .collect::<Vec<_>>();
    files.sort();
    Ok(files)
}

fn changed_file_coverage(
    changed: &[String],
    base: &[CallNode],
    pr: &[CallNode],
) -> ChangedFileCoverageReport {
    let files = changed
        .iter()
        .map(|file| {
            let base_calls = base
                .iter()
                .filter(|node| !node.is_harness && node.file_path.as_deref() == Some(file))
                .collect::<Vec<_>>();
            let pr_calls = pr
                .iter()
                .filter(|node| !node.is_harness && node.file_path.as_deref() == Some(file))
                .collect::<Vec<_>>();
            let traced_members = base_calls
                .iter()
                .chain(&pr_calls)
                .map(|node| node.method_full_name.as_str())
                .collect::<HashSet<_>>()
                .len();
            let observed_call_sites = base_calls
                .iter()
                .chain(&pr_calls)
                .map(|node| key(&node.test_id, &node.method_full_name))
                .collect::<HashSet<_>>()
                .len();
            let base_call_count = base_calls.len();
            let pr_call_count = pr_calls.len();
            let total_call_count = base_call_count + pr_call_count;
            ChangedFileCoverage {
                file_path: file.clone(),
                exercised: total_call_count > 0,
                traced_members,
                observed_call_sites,
                base_call_count,
                pr_call_count,
                total_call_count,
                interpretation: if total_call_count > 0 {
                    "executed by tests in the representative base or PR run"
                } else {
                    "not observed; zero calls are not evidence of unchanged behavior"
                },
            }
        })
        .collect::<Vec<_>>();
    ChangedFileCoverageReport {
        summary: ChangedFileCoverageSummary {
            edited_files: files.len(),
            exercised_edited_files: files.iter().filter(|file| file.exercised).count(),
            traced_members: files.iter().map(|file| file.traced_members).sum(),
            observed_call_sites: files.iter().map(|file| file.observed_call_sites).sum(),
            base_call_count: files.iter().map(|file| file.base_call_count).sum(),
            pr_call_count: files.iter().map(|file| file.pr_call_count).sum(),
            total_call_count: files.iter().map(|file| file.total_call_count).sum(),
        },
        files,
    }
}

fn changed_files_intentionally_untraced(members: &[CoverageMember], changed: &[String]) -> bool {
    !changed.is_empty()
        && changed.iter().all(|file| {
            let matches = members_for_changed_file(members, file);
            !matches.is_empty()
                && matches.iter().all(|member| {
                    member.status == "Skipped"
                        && matches!(
                            member.skip_reason.as_deref(),
                            Some("ExcludedByScope" | "Unobservable")
                        )
                })
        })
}

fn members_for_changed_file<'a>(
    members: &'a [CoverageMember],
    file: &str,
) -> Vec<&'a CoverageMember> {
    let stem = Path::new(file)
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or_default();
    members
        .iter()
        .filter(|member| {
            member.assembly == file
                || (!stem.is_empty()
                    && member
                        .method_full_name
                        .as_ref()
                        .is_some_and(|method| declaring_type_simple_name(method) == stem))
        })
        .collect()
}

fn first_segment(path: &str) -> &str {
    path.split_once('/').map_or(path, |(first, _)| first)
}

fn declaring_type(method: &str) -> String {
    let qualified = method
        .split_once('(')
        .map_or(method, |(qualified, _)| qualified);
    qualified
        .rsplit_once('.')
        .map_or(qualified, |(declaring, _)| declaring.trim_end_matches('.'))
        .to_owned()
}

fn declaring_type_simple_name(method: &str) -> &str {
    let declaring = method
        .split_once('(')
        .map_or(method, |(declaring, _)| declaring);
    let declaring = declaring
        .rsplit_once('.')
        .map_or(declaring, |(value, _)| value);
    let simple = declaring
        .rsplit_once('.')
        .map_or(declaring, |(_, value)| value);
    simple.split_once('`').map_or(simple, |(value, _)| value)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn node(test: &str, process: &str, call_id: i64, parent_call_id: Option<i64>) -> CallNode {
        CallNode {
            call_id,
            parent_call_id,
            test_id: test.to_owned(),
            method_full_name: format!("method_{call_id}"),
            is_harness: false,
            file_path: Some("src/file.rs".to_owned()),
            line: Some(1),
            process: process.to_owned(),
        }
    }

    #[test]
    fn indexes_parents_only_within_test_and_process_scope() {
        let nodes = vec![
            node("test-a", "p1", 1, None),
            node("test-a", "p1", 2, Some(1)),
            node("test-a", "p2", 1, None),
            node("test-a", "p2", 2, Some(1)),
            node("test-b", "p1", 3, Some(1)),
        ];

        let index = CallTreeIndex::build(&nodes);

        assert_eq!(index.roots, 2);
        assert_eq!(index.children_of(&nodes[0]), [1]);
        assert_eq!(index.children_of(&nodes[2]), [3]);
        assert_eq!(index.orphans, [4]);
    }
}
