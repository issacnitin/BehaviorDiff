use serde_json::{Map, Value};
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::Path;
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

const MAXIMUM_EVIDENCE_PER_MEMBER: usize = 20;

#[derive(Default)]
pub(crate) struct FindingsOptions {
    pub(crate) divergences: String,
    pub(crate) frontier: String,
    pub(crate) output: String,
    pub(crate) exit_code: i32,
    pub(crate) base_sha: String,
    pub(crate) pr_sha: String,
    pub(crate) merge_base: String,
    pub(crate) cache_status: String,
    pub(crate) cache_key: String,
    pub(crate) cache_backend: String,
    pub(crate) cache_saved_wall_clock_milliseconds: i64,
    pub(crate) build_milliseconds: i64,
    pub(crate) weave_milliseconds: i64,
    pub(crate) instrumented_run_milliseconds: i64,
    pub(crate) cache_restore_milliseconds: i64,
    pub(crate) cache_store_milliseconds: i64,
    pub(crate) diff_milliseconds: i64,
    pub(crate) frontier_milliseconds: i64,
    pub(crate) strict: bool,
}

#[derive(Default)]
pub(crate) struct InvalidFindingsOptions {
    pub(crate) output: String,
    pub(crate) status: String,
    pub(crate) reason: String,
    pub(crate) exit_code: i32,
    pub(crate) base_sha: Option<String>,
    pub(crate) pr_sha: Option<String>,
    pub(crate) merge_base: Option<String>,
}

struct TreeIndex<'a> {
    by_identity: HashMap<String, &'a Value>,
    by_occurrence: HashMap<(String, String, i32), Vec<&'a Value>>,
}

impl<'a> TreeIndex<'a> {
    fn new(nodes: &'a [Value]) -> Self {
        let mut by_identity = HashMap::new();
        let mut by_occurrence = HashMap::<(String, String, i32), Vec<&Value>>::new();
        for node in nodes {
            by_identity.insert(call_identity(node), node);
            if let Some(ordinal) = nullable_i32(node, "ordinal") {
                by_occurrence
                    .entry((text(node, "testId"), text(node, "methodFullName"), ordinal))
                    .or_default()
                    .push(node);
            }
        }
        Self {
            by_identity,
            by_occurrence,
        }
    }

    fn occurrences(&self, test: &str, method: &str, ordinal: i32) -> &[&'a Value] {
        self.by_occurrence
            .get(&(test.to_owned(), method.to_owned(), ordinal))
            .map(Vec::as_slice)
            .unwrap_or_default()
    }

    fn ancestors(&self, node: &Value) -> HashSet<String> {
        let mut ancestors = HashSet::new();
        let mut current = node;
        while let Some(parent) = nullable_i64(current, "parentCallId") {
            let identity = scoped_identity(current, parent);
            if !ancestors.insert(identity.clone()) {
                break;
            }
            let Some(next) = self.by_identity.get(&identity) else {
                break;
            };
            current = next;
        }
        ancestors
    }

    fn path(&self, test: &str, method: &str, ordinal: i32) -> Vec<Value> {
        if ordinal < 0 {
            return Vec::new();
        }
        let Some(target) = self.occurrences(test, method, ordinal).first().copied() else {
            return Vec::new();
        };
        let mut path = Vec::new();
        let mut visited = HashSet::new();
        let mut current = target;
        loop {
            let identity = call_identity(current);
            if !visited.insert(identity) {
                return Vec::new();
            }
            path.push(object([
                ("memberName", Value::String(text(current, "methodFullName"))),
                ("filePath", nullable_string_value(current, "filePath")),
                ("line", nullable_number_value(current, "line")),
                ("isHarness", Value::Bool(boolean(current, "isHarness"))),
            ]));
            let Some(parent) = nullable_i64(current, "parentCallId") else {
                break;
            };
            let identity = scoped_identity(current, parent);
            let Some(next) = self.by_identity.get(&identity) else {
                return Vec::new();
            };
            current = next;
        }
        path.reverse();
        path
    }
}

pub(crate) fn run(options: &FindingsOptions) -> Result<i32, String> {
    let divergence: Value =
        serde_json::from_slice(&fs::read(&options.divergences).map_err(|error| error.to_string())?)
            .map_err(|error| error.to_string())?;
    let frontier: Value =
        serde_json::from_slice(&fs::read(&options.frontier).map_err(|error| error.to_string())?)
            .map_err(|error| error.to_string())?;
    let frontier_nodes = array(&frontier, "frontier")?;
    let observations = array(&divergence, "divergences")?;
    let base_nodes = array(&divergence, "callTree")?;
    let pr_nodes = array(&divergence, "prCallTree")?;
    if base_nodes.is_empty() || pr_nodes.is_empty() {
        return Err(format!(
            "Findings call-tree input is empty: base={} pr={}",
            base_nodes.len(),
            pr_nodes.len()
        ));
    }
    let coverage = frontier
        .get("changedFileCoverage")
        .cloned()
        .ok_or_else(|| "Frontier report has no changedFileCoverage".to_owned())?;
    let coverage_summary = coverage
        .get("summary")
        .ok_or_else(|| "Frontier changedFileCoverage has no summary".to_owned())?;
    let changed_files = frontier
        .pointer("/attributionInputs/changedFiles")
        .and_then(Value::as_array)
        .ok_or_else(|| "Frontier report has no changed files".to_owned())?
        .iter()
        .filter_map(Value::as_str)
        .map(str::to_owned)
        .collect::<HashSet<_>>();
    let noise_methods = methods(&divergence, "noiseExclusions");
    let manifest_noise_methods = methods(&divergence, "manifestNoise");
    let base_index = TreeIndex::new(base_nodes);
    let pr_index = TreeIndex::new(pr_nodes);

    let mut groups = Vec::<(String, Vec<&Value>)>::new();
    let mut group_index = HashMap::<String, usize>::new();
    for node in frontier_nodes {
        let method = text(node, "methodFullName");
        let index = *group_index.entry(method.clone()).or_insert_with(|| {
            groups.push((method.clone(), Vec::new()));
            groups.len() - 1
        });
        groups[index].1.push(node);
    }
    groups.sort_by(|left, right| {
        let left_unexpected = text(left.1[0], "attribution") == "UNEXPECTED";
        let right_unexpected = text(right.1[0], "attribution") == "UNEXPECTED";
        right_unexpected
            .cmp(&left_unexpected)
            .then_with(|| right.1.len().cmp(&left.1.len()))
    });

    let mut members = Vec::new();
    for (method, group) in groups {
        members.push(describe_member(
            &method,
            &group,
            observations,
            base_nodes,
            pr_nodes,
            &base_index,
            &pr_index,
            &changed_files,
            &noise_methods,
            &manifest_noise_methods,
        )?);
    }
    let unexpected = members
        .iter()
        .filter(|member| text(member, "attribution") == "unexpected")
        .collect::<Vec<_>>();
    let expected = members
        .iter()
        .filter(|member| text(member, "attribution") == "expected")
        .collect::<Vec<_>>();
    let unexpected_call_sites = unexpected
        .iter()
        .map(|member| integer(member, "callSiteCount"))
        .sum::<i64>();
    let expected_call_sites = expected
        .iter()
        .map(|member| integer(member, "callSiteCount"))
        .sum::<i64>();
    let default_eligible = unexpected
        .iter()
        .filter(|member| boolean(member, "defaultCommentEligible"))
        .copied()
        .collect::<Vec<_>>();
    let default_eligible_call_sites = default_eligible
        .iter()
        .map(|member| integer(member, "callSiteCount"))
        .sum::<i64>();
    let eligible_members = if options.strict {
        unexpected.len()
    } else {
        default_eligible.len()
    };
    let eligible_call_sites = if options.strict {
        unexpected_call_sites
    } else {
        default_eligible_call_sites
    };
    let measured_total = options.build_milliseconds
        + options.weave_milliseconds
        + options.instrumented_run_milliseconds
        + options.cache_restore_milliseconds
        + options.cache_store_milliseconds
        + options.diff_milliseconds
        + options.frontier_milliseconds;
    let artifact = object([
        (
            "schema",
            Value::String("behaviordiff.findings/1".to_owned()),
        ),
        (
            "generatedUtc",
            Value::String(OffsetDateTime::now_utc().format(&Rfc3339).unwrap()),
        ),
        ("status", Value::String("analyzed".to_owned())),
        (
            "verdict",
            Value::String(
                if unexpected.is_empty() {
                    "clean"
                } else {
                    "findings"
                }
                .to_owned(),
            ),
        ),
        ("isCleanResult", Value::Bool(unexpected.is_empty())),
        ("exitCode", Value::from(options.exit_code)),
        (
            "exitReason",
            Value::String(
                if unexpected.is_empty() {
                    "analyzed_no_unexpected"
                } else {
                    "unexpected_findings"
                }
                .to_owned(),
            ),
        ),
        (
            "refs",
            object([
                ("baseSha", Value::String(options.base_sha.clone())),
                ("prSha", Value::String(options.pr_sha.clone())),
                ("mergeBaseSha", Value::String(options.merge_base.clone())),
            ]),
        ),
        (
            "commentPolicy",
            object([
                (
                    "mode",
                    Value::String(
                        if options.strict {
                            "strict"
                        } else {
                            "high-confidence"
                        }
                        .to_owned(),
                    ),
                ),
                ("eligibleUnexpectedMembers", Value::from(eligible_members)),
                (
                    "eligibleUnexpectedCallSites",
                    Value::from(eligible_call_sites),
                ),
                (
                    "suppressedUnexpectedMembers",
                    Value::from(unexpected.len() - eligible_members),
                ),
                (
                    "suppressedUnexpectedCallSites",
                    Value::from(unexpected_call_sites - eligible_call_sites),
                ),
            ]),
        ),
        (
            "baseTraceCache",
            object([
                ("status", Value::String(cache_status(options))),
                ("key", Value::String(options.cache_key.clone())),
                ("backend", Value::String(cache_backend(options))),
                (
                    "savedWallClockMilliseconds",
                    Value::from(options.cache_saved_wall_clock_milliseconds),
                ),
            ]),
        ),
        (
            "timings",
            object([
                ("buildMilliseconds", Value::from(options.build_milliseconds)),
                ("weaveMilliseconds", Value::from(options.weave_milliseconds)),
                (
                    "instrumentedRunMilliseconds",
                    Value::from(options.instrumented_run_milliseconds),
                ),
                (
                    "cacheRestoreMilliseconds",
                    Value::from(options.cache_restore_milliseconds),
                ),
                (
                    "cacheStoreMilliseconds",
                    Value::from(options.cache_store_milliseconds),
                ),
                ("diffMilliseconds", Value::from(options.diff_milliseconds)),
                (
                    "frontierMilliseconds",
                    Value::from(options.frontier_milliseconds),
                ),
                ("measuredTotalMilliseconds", Value::from(measured_total)),
            ]),
        ),
        (
            "summary",
            object([
                ("unexpectedMembers", Value::from(unexpected.len())),
                ("unexpectedCallSites", Value::from(unexpected_call_sites)),
                (
                    "highConfidenceUnexpectedMembers",
                    Value::from(default_eligible.len()),
                ),
                (
                    "highConfidenceUnexpectedCallSites",
                    Value::from(default_eligible_call_sites),
                ),
                (
                    "nondeterministicUnexpectedMembers",
                    Value::from(
                        unexpected
                            .iter()
                            .filter(|member| {
                                member
                                    .pointer("/nondeterminism/classification")
                                    .and_then(Value::as_str)
                                    != Some("none")
                            })
                            .count(),
                    ),
                ),
                (
                    "defaultCommentSuppressedMembers",
                    Value::from(unexpected.len() - default_eligible.len()),
                ),
                (
                    "defaultCommentSuppressedCallSites",
                    Value::from(unexpected_call_sites - default_eligible_call_sites),
                ),
                ("expectedMembers", Value::from(expected.len())),
                ("expectedCallSites", Value::from(expected_call_sites)),
                (
                    "untestedMembers",
                    Value::from(
                        members
                            .iter()
                            .filter(|member| integer(member, "untestedCallSiteCount") > 0)
                            .count(),
                    ),
                ),
                (
                    "editedFiles",
                    Value::from(integer(coverage_summary, "editedFiles")),
                ),
                (
                    "exercisedEditedFiles",
                    Value::from(integer(coverage_summary, "exercisedEditedFiles")),
                ),
                (
                    "tracedMembers",
                    Value::from(integer(coverage_summary, "tracedMembers")),
                ),
                (
                    "observedCallSites",
                    Value::from(integer(coverage_summary, "observedCallSites")),
                ),
                (
                    "totalCallCount",
                    Value::from(integer(coverage_summary, "totalCallCount")),
                ),
            ]),
        ),
        ("coverage", coverage),
        ("members", Value::Array(members)),
    ]);
    write(&options.output, &artifact)?;
    Ok(0)
}

pub(crate) fn write_invalid(options: &InvalidFindingsOptions) -> Result<i32, String> {
    if options.status != "refused" && options.status != "failed" {
        return Err(format!("Invalid findings status '{}'.", options.status));
    }
    let artifact = object([
        (
            "schema",
            Value::String("behaviordiff.findings/1".to_owned()),
        ),
        (
            "generatedUtc",
            Value::String(OffsetDateTime::now_utc().format(&Rfc3339).unwrap()),
        ),
        ("status", Value::String(options.status.clone())),
        ("verdict", Value::String("could_not_analyze".to_owned())),
        ("isCleanResult", Value::Bool(false)),
        ("exitCode", Value::from(options.exit_code)),
        (
            "exitReason",
            Value::String(
                if options.status == "refused" {
                    "analysis_refused"
                } else {
                    "analysis_failed"
                }
                .to_owned(),
            ),
        ),
        (
            "refs",
            object([
                ("baseSha", option_string(options.base_sha.clone())),
                ("prSha", option_string(options.pr_sha.clone())),
                ("mergeBaseSha", option_string(options.merge_base.clone())),
            ]),
        ),
        (
            "refusal",
            object([("reason", Value::String(options.reason.clone()))]),
        ),
    ]);
    write(&options.output, &artifact)?;
    Ok(0)
}

#[allow(clippy::too_many_arguments)]
fn describe_member(
    method: &str,
    group: &[&Value],
    observations: &[Value],
    base_nodes: &[Value],
    pr_nodes: &[Value],
    base_index: &TreeIndex<'_>,
    pr_index: &TreeIndex<'_>,
    changed_files: &HashSet<String>,
    noise_methods: &HashSet<String>,
    manifest_noise_methods: &HashSet<String>,
) -> Result<Value, String> {
    let first = group[0];
    let file_path = nullable_text(first, "filePath");
    let source_generated = is_generated_source(file_path.as_deref());
    let mut frontier_by_test = HashMap::<String, &Value>::new();
    for node in group {
        frontier_by_test.entry(text(node, "testId")).or_insert(node);
    }
    let member_divergences = observations
        .iter()
        .filter(|item| text(item, "methodFullName") == method)
        .collect::<Vec<_>>();
    let rendered_evidence = member_divergences
        .iter()
        .take(MAXIMUM_EVIDENCE_PER_MEMBER)
        .map(|divergence| {
            describe_evidence(
                divergence,
                &frontier_by_test,
                base_index,
                pr_index,
                changed_files,
            )
        })
        .collect::<Result<Vec<_>, _>>()?;
    let mut observing_tests = member_divergences
        .iter()
        .map(|item| text(item, "testId"))
        .collect::<HashSet<_>>()
        .into_iter()
        .collect::<Vec<_>>();
    observing_tests.sort();
    let executing_test_count = base_nodes
        .iter()
        .chain(pr_nodes)
        .filter(|node| text(node, "methodFullName") == method)
        .map(|node| text(node, "testId"))
        .collect::<HashSet<_>>()
        .len();
    let tests_with_reaction = observing_tests
        .iter()
        .filter(|test| {
            frontier_by_test
                .get(*test)
                .is_some_and(|node| !boolean(node, "untested"))
        })
        .count();
    let consequences = describe_consequences(
        method,
        &rendered_evidence,
        observations,
        &frontier_by_test,
        base_index,
        pr_index,
        changed_files,
    )?;
    let mut changed_files_reaching = rendered_evidence
        .iter()
        .flat_map(|item| {
            item.get("changedFilesOnPath")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .filter_map(Value::as_str)
        })
        .map(str::to_owned)
        .collect::<HashSet<_>>()
        .into_iter()
        .collect::<Vec<_>>();
    changed_files_reaching.sort();
    let verified = text(first, "classification") == "frontier";
    let exact = !member_divergences.is_empty()
        && member_divergences
            .iter()
            .all(|item| text(item, "digestConfidence") == "Exact");
    let causally_connected = has_causal_connectivity(
        method,
        &member_divergences,
        observations,
        base_index,
        pr_index,
    );
    let mut nondeterminism_reasons = Vec::new();
    if noise_methods.contains(method) {
        nondeterminism_reasons.push("same member varied between baseline runs".to_owned());
    }
    if manifest_noise_methods.contains(method) {
        nondeterminism_reasons
            .push("same member's instrumentation state varied between baseline runs".to_owned());
    }
    let mut suppression_reasons = Vec::new();
    if !verified {
        suppression_reasons.push("unverified_frontier".to_owned());
    }
    if !exact {
        suppression_reasons.push("non_exact_digest".to_owned());
    }
    if !causally_connected {
        suppression_reasons.push("no_causal_connectivity".to_owned());
    }
    if !nondeterminism_reasons.is_empty() {
        suppression_reasons.push("baseline_nondeterminism".to_owned());
    }
    let default_eligible = suppression_reasons.is_empty();
    let confidence = if default_eligible {
        "high"
    } else if !nondeterminism_reasons.is_empty() || !exact {
        "low"
    } else {
        "medium"
    };
    let symptoms = distinct_strings(group.iter().flat_map(|node| strings(node, "symptoms")));
    let downgrade_reasons = distinct_strings(
        group
            .iter()
            .flat_map(|node| strings(node, "downgradeReasons")),
    );
    Ok(object([
        ("memberName", Value::String(method.to_owned())),
        (
            "attribution",
            Value::String(text(first, "attribution").to_lowercase()),
        ),
        ("filePath", option_string(file_path.clone())),
        ("line", nullable_number_value(first, "line")),
        ("sourceGenerated", Value::Bool(source_generated)),
        (
            "sourceGeneratedNote",
            if source_generated {
                Value::String("Generated source is not present in the git diff, so path attribution cannot classify it as edited.".to_owned())
            } else {
                Value::Null
            },
        ),
        ("callSiteCount", Value::from(group.len())),
        ("distinctTestCount", Value::from(executing_test_count)),
        (
            "observingTests",
            Value::Array(observing_tests.into_iter().map(Value::String).collect()),
        ),
        (
            "testsWithAssertionReaction",
            Value::from(tests_with_reaction),
        ),
        (
            "assertionReactionSummary",
            Value::String(assertion_reaction_summary(
                executing_test_count,
                tests_with_reaction,
            )),
        ),
        (
            "changedFilesReachingMember",
            Value::Array(
                changed_files_reaching
                    .into_iter()
                    .map(Value::String)
                    .collect(),
            ),
        ),
        ("verified", Value::Bool(verified)),
        ("confidence", Value::String(confidence.to_owned())),
        (
            "confidenceFactors",
            object([
                ("verifiedFrontier", Value::Bool(verified)),
                ("exactDigest", Value::Bool(exact)),
                ("causallyConnected", Value::Bool(causally_connected)),
            ]),
        ),
        ("defaultCommentEligible", Value::Bool(default_eligible)),
        (
            "commentSuppressionReasons",
            Value::Array(suppression_reasons.into_iter().map(Value::String).collect()),
        ),
        (
            "nondeterminism",
            object([
                (
                    "classification",
                    Value::String(
                        if nondeterminism_reasons.is_empty() {
                            "none"
                        } else {
                            "baseline-observed"
                        }
                        .to_owned(),
                    ),
                ),
                (
                    "reasons",
                    Value::Array(
                        nondeterminism_reasons
                            .into_iter()
                            .map(Value::String)
                            .collect(),
                    ),
                ),
            ]),
        ),
        (
            "symptoms",
            Value::Array(symptoms.into_iter().map(Value::String).collect()),
        ),
        (
            "downgradeReasons",
            Value::Array(downgrade_reasons.into_iter().map(Value::String).collect()),
        ),
        (
            "descendantsCompared",
            Value::from(
                group
                    .iter()
                    .map(|node| integer(node, "descendantKeysCompared"))
                    .sum::<i64>(),
            ),
        ),
        (
            "untestedCallSiteCount",
            Value::from(
                group
                    .iter()
                    .filter(|node| boolean(node, "untested"))
                    .count(),
            ),
        ),
        ("evidenceTotalCount", Value::from(member_divergences.len())),
        (
            "evidenceTruncated",
            Value::Bool(member_divergences.len() > rendered_evidence.len()),
        ),
        ("evidence", Value::Array(rendered_evidence)),
        ("consequences", Value::Array(consequences)),
    ]))
}

fn has_causal_connectivity(
    member: &str,
    member_divergences: &[&Value],
    divergences: &[Value],
    base: &TreeIndex<'_>,
    pr: &TreeIndex<'_>,
) -> bool {
    member_divergences.iter().any(|divergence| {
        let Some(ordinal) = nullable_i32(divergence, "ordinal") else {
            return false;
        };
        if ordinal < 0 {
            return false;
        }
        let test = text(divergence, "testId");
        let related = divergences
            .iter()
            .filter(|candidate| {
                text(candidate, "testId") == test
                    && text(candidate, "methodFullName") != member
                    && nullable_i32(candidate, "ordinal").is_some_and(|value| value >= 0)
            })
            .collect::<Vec<_>>();
        !related.is_empty()
            && (has_related(base, &test, member, ordinal, &related)
                || has_related(pr, &test, member, ordinal, &related))
    })
}

fn has_related(
    tree: &TreeIndex<'_>,
    test: &str,
    member: &str,
    ordinal: i32,
    related: &[&Value],
) -> bool {
    for target in tree.occurrences(test, member, ordinal) {
        let target_ancestors = tree.ancestors(target);
        let target_identity = call_identity(target);
        for divergence in related {
            let related_method = text(divergence, "methodFullName");
            let related_ordinal = nullable_i32(divergence, "ordinal").unwrap();
            for candidate in tree.occurrences(test, &related_method, related_ordinal) {
                let related_identity = call_identity(candidate);
                if target_ancestors.contains(&related_identity)
                    || tree.ancestors(candidate).contains(&target_identity)
                {
                    return true;
                }
            }
        }
    }
    false
}

fn describe_consequences(
    frontier_member: &str,
    evidence: &[Value],
    divergences: &[Value],
    frontier_by_test: &HashMap<String, &Value>,
    base: &TreeIndex<'_>,
    pr: &TreeIndex<'_>,
    changed_files: &HashSet<String>,
) -> Result<Vec<Value>, String> {
    let mut candidates = Vec::new();
    let mut seen = HashSet::new();
    for item in evidence {
        let test = text(item, "testId");
        let member = outermost_product_ancestor(item.get("baseCallPath"), frontier_member)
            .or_else(|| outermost_product_ancestor(item.get("prCallPath"), frontier_member));
        if let Some(member) = member {
            if seen.insert(format!("{test}|{member}")) {
                candidates.push((test, member));
            }
        }
    }
    let mut result = Vec::new();
    for (test, member) in candidates {
        let divergence = divergences.iter().find(|item| {
            text(item, "testId") == test
                && text(item, "methodFullName") == member
                && nullable_i32(item, "ordinal").is_some_and(|value| value >= 0)
                && (nullable_text(item, "baseReturnRendered")
                    != nullable_text(item, "prReturnRendered")
                    || nullable_text(item, "baseExceptionType")
                        != nullable_text(item, "prExceptionType"))
        });
        if let Some(divergence) = divergence {
            result.push(object([
                ("memberName", Value::String(member)),
                (
                    "evidence",
                    describe_evidence(divergence, frontier_by_test, base, pr, changed_files)?,
                ),
            ]));
        }
    }
    Ok(result)
}

fn outermost_product_ancestor(path: Option<&Value>, frontier: &str) -> Option<String> {
    path.and_then(Value::as_array).and_then(|nodes| {
        nodes.iter().find_map(|node| {
            let member = text(node, "memberName");
            (!boolean(node, "isHarness") && member != frontier).then_some(member)
        })
    })
}

fn describe_evidence(
    divergence: &Value,
    frontier_by_test: &HashMap<String, &Value>,
    base: &TreeIndex<'_>,
    pr: &TreeIndex<'_>,
    changed_files: &HashSet<String>,
) -> Result<Value, String> {
    let test = text(divergence, "testId");
    let member = text(divergence, "methodFullName");
    let ordinal = nullable_i32(divergence, "ordinal").unwrap_or(-1);
    let exact_occurrence = ordinal >= 0;
    let base_path = base.path(&test, &member, ordinal);
    let pr_path = pr.path(&test, &member, ordinal);
    let assertion_reacted = frontier_by_test
        .get(&test)
        .map(|node| !boolean(node, "untested"));
    let mut changed_on_path = base_path
        .iter()
        .chain(&pr_path)
        .filter_map(|node| nullable_text(node, "filePath"))
        .filter(|path| changed_files.contains(path))
        .collect::<HashSet<_>>()
        .into_iter()
        .collect::<Vec<_>>();
    changed_on_path.sort();
    let mut entries = vec![
        ("testId", Value::String(test.clone())),
        ("ordinal", Value::from(ordinal)),
        ("kind", Value::String(text(divergence, "kind"))),
        ("detail", Value::String(text(divergence, "detail"))),
        (
            "digestConfidence",
            Value::String(text(divergence, "digestConfidence")),
        ),
        (
            "baseDigest",
            Value::String(behavior_digest(divergence, "base")?),
        ),
        (
            "prDigest",
            Value::String(behavior_digest(divergence, "pr")?),
        ),
    ];
    if exact_occurrence {
        for (output, input) in [
            ("baseArgs", "baseArgsRendered"),
            ("prArgs", "prArgsRendered"),
            ("baseReturn", "baseReturnRendered"),
            ("prReturn", "prReturnRendered"),
            ("baseException", "baseExceptionType"),
            ("prException", "prExceptionType"),
        ] {
            if let Some(value) = nullable_text(divergence, input) {
                entries.push((output, Value::String(value)));
            }
        }
        entries.push(("baseCallPath", Value::Array(base_path)));
        entries.push(("prCallPath", Value::Array(pr_path)));
    }
    entries.extend([
        (
            "assertionReacted",
            assertion_reacted.map_or(Value::Null, Value::Bool),
        ),
        (
            "assertionEvidence",
            frontier_by_test.get(&test).map_or(Value::Null, |node| {
                nullable_string_value(node, "untestedEvidence")
            }),
        ),
        (
            "changedFilesOnPath",
            Value::Array(changed_on_path.into_iter().map(Value::String).collect()),
        ),
    ]);
    Ok(object_iter(entries))
}

fn behavior_digest(divergence: &Value, side: &str) -> Result<String, String> {
    let values = Value::Array(vec![
        divergence
            .get(format!("{side}ArgsDigest"))
            .cloned()
            .unwrap_or(Value::Null),
        divergence
            .get(format!("{side}ReturnDigest"))
            .cloned()
            .unwrap_or(Value::Null),
        divergence
            .get(format!("{side}ExceptionType"))
            .cloned()
            .unwrap_or(Value::Null),
    ]);
    let bytes = crate::dotnet_json::to_vec(&values)?;
    Ok(format!("sha256:{:x}", Sha256::digest(bytes)))
}

fn assertion_reaction_summary(observing: usize, reacted: usize) -> String {
    format!(
        "{} test{} executed this; {}",
        observing,
        if observing == 1 { "" } else { "s" },
        if reacted == 0 {
            "none asserted on the changed value.".to_owned()
        } else {
            format!(
                "{} test{} had an assertion react.",
                reacted,
                if reacted == 1 { "" } else { "s" }
            )
        }
    )
}

fn cache_status(options: &FindingsOptions) -> String {
    if options.cache_status.is_empty() {
        "unreported".to_owned()
    } else {
        options.cache_status.clone()
    }
}

fn cache_backend(options: &FindingsOptions) -> String {
    if options.cache_backend.is_empty() {
        "none".to_owned()
    } else {
        options.cache_backend.clone()
    }
}

fn methods(root: &Value, property: &str) -> HashSet<String> {
    root.get(property)
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .map(|item| text(item, "methodFullName"))
        .collect()
}

fn is_generated_source(path: Option<&str>) -> bool {
    let Some(path) = path else { return false };
    path.contains("/obj/")
        || path.starts_with("obj/")
        || path.contains("/target/generated-sources/")
        || path.starts_with("target/generated-sources/")
        || path.contains("/target/generated-test-sources/")
        || path.starts_with("target/generated-test-sources/")
        || path.contains("/generated/")
        || path.starts_with("generated/")
        || [
            ".g.cs",
            ".generated.cs",
            ".g.java",
            ".generated.java",
            ".g.js",
            ".generated.js",
            ".g.ts",
            ".generated.ts",
        ]
        .iter()
        .any(|suffix| path.ends_with(suffix))
}

fn write(path: &str, value: &Value) -> Result<(), String> {
    if let Some(parent) = Path::new(path).parent() {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    fs::write(path, crate::dotnet_json::to_vec_pretty(value)?)
        .map_err(|error| error.to_string())?;
    println!("Findings written: {}", Path::new(path).display());
    Ok(())
}

fn object<const N: usize>(entries: [(&str, Value); N]) -> Value {
    object_iter(entries)
}

fn object_iter<'a>(entries: impl IntoIterator<Item = (&'a str, Value)>) -> Value {
    let mut map = Map::new();
    for (key, value) in entries {
        map.insert(key.to_owned(), value);
    }
    Value::Object(map)
}

fn array<'a>(root: &'a Value, property: &str) -> Result<&'a [Value], String> {
    root.get(property)
        .and_then(Value::as_array)
        .map(Vec::as_slice)
        .ok_or_else(|| format!("JSON input has no {property} array"))
}

fn text(value: &Value, property: &str) -> String {
    value
        .get(property)
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_owned()
}

fn nullable_text(value: &Value, property: &str) -> Option<String> {
    value
        .get(property)
        .and_then(Value::as_str)
        .map(str::to_owned)
}

fn nullable_string_value(value: &Value, property: &str) -> Value {
    nullable_text(value, property).map_or(Value::Null, Value::String)
}

fn nullable_number_value(value: &Value, property: &str) -> Value {
    value.get(property).cloned().unwrap_or(Value::Null)
}

fn option_string(value: Option<String>) -> Value {
    value.map_or(Value::Null, Value::String)
}

fn integer(value: &Value, property: &str) -> i64 {
    value.get(property).and_then(Value::as_i64).unwrap_or(0)
}

fn nullable_i32(value: &Value, property: &str) -> Option<i32> {
    value
        .get(property)
        .and_then(Value::as_i64)
        .and_then(|number| i32::try_from(number).ok())
}

fn nullable_i64(value: &Value, property: &str) -> Option<i64> {
    value.get(property).and_then(Value::as_i64)
}

fn boolean(value: &Value, property: &str) -> bool {
    value
        .get(property)
        .and_then(Value::as_bool)
        .unwrap_or(false)
}

fn strings<'a>(value: &'a Value, property: &str) -> impl Iterator<Item = String> + 'a {
    value
        .get(property)
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .map(str::to_owned)
}

fn distinct_strings(values: impl IntoIterator<Item = String>) -> Vec<String> {
    let mut seen = HashSet::new();
    values
        .into_iter()
        .filter(|value| seen.insert(value.clone()))
        .collect()
}

fn scoped_identity(node: &Value, call_id: i64) -> String {
    format!(
        "{}|{}|{}",
        text(node, "testId"),
        text(node, "process"),
        call_id
    )
}

fn call_identity(node: &Value) -> String {
    scoped_identity(node, nullable_i64(node, "callId").unwrap_or(0))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn behavior_digest_matches_system_text_json_array_hash() {
        let divergence = object([
            ("baseArgsDigest", Value::String("sha256:aa".to_owned())),
            ("baseReturnDigest", Value::Null),
            ("baseExceptionType", Value::String("Type`1".to_owned())),
        ]);
        assert_eq!(
            behavior_digest(&divergence, "base").unwrap(),
            "sha256:e7d085b56610964d327cb1dbd02d6952980dbbb4344d050edb597a3f9c14b849"
        );
    }
}
