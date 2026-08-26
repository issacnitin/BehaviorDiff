use regex::Regex;
use serde::Deserialize;
use serde_json::{Map, Value};
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};
use time::format_description;
use time::{Date, OffsetDateTime};

#[derive(Default)]
pub(crate) struct BaselineOptions {
    pub(crate) findings: String,
    pub(crate) baseline: String,
}

#[derive(Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct BaselineDocument {
    schema: String,
    acknowledgements: Vec<BaselineAcknowledgement>,
    ignore_paths: Vec<BaselineIgnore>,
    ignore_members: Vec<BaselineIgnore>,
}

impl Default for BaselineDocument {
    fn default() -> Self {
        Self {
            schema: "realdiff.baseline/2".to_owned(),
            acknowledgements: Vec::new(),
            ignore_paths: Vec::new(),
            ignore_members: Vec::new(),
        }
    }
}

#[derive(Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct BaselineAcknowledgement {
    id: String,
    member: String,
    path: Option<String>,
    base_digest: String,
    pr_digest: String,
    reason: String,
    expires: Option<String>,
}

#[derive(Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct BaselineIgnore {
    id: String,
    pattern: String,
    reason: String,
    expires: Option<String>,
}

enum Matcher {
    Acknowledgement { member: String, path: String },
    Glob { pattern: Regex, path: bool },
}

struct RuleState {
    id: String,
    kind: String,
    reason: String,
    expires: Option<String>,
    expired: bool,
    matcher: Matcher,
    base_digest: Option<String>,
    pr_digest: Option<String>,
    match_count: usize,
    scope_match_count: usize,
}

impl RuleState {
    fn matches_scope(&self, member: &Value) -> bool {
        match &self.matcher {
            Matcher::Acknowledgement {
                member: expected_member,
                path: expected_path,
            } => {
                text(member, "memberName") == *expected_member
                    && text(member, "filePath").replace('\\', "/") == *expected_path
            }
            Matcher::Glob { pattern, path } => {
                let target = if *path {
                    text(member, "filePath").replace('\\', "/")
                } else {
                    text(member, "memberName")
                };
                pattern.is_match(&target)
            }
        }
    }

    fn matches_pair(&self, pair: &(String, String)) -> bool {
        self.base_digest.as_deref() == Some(&pair.0) && self.pr_digest.as_deref() == Some(&pair.1)
    }
}

pub(crate) fn run(options: &BaselineOptions) -> Result<i32, String> {
    apply(options, OffsetDateTime::now_utc().date())
}

pub(crate) fn validate_file(path: &str) -> Result<i32, String> {
    read_baseline(path)?;
    Ok(0)
}

fn apply(options: &BaselineOptions, today: Date) -> Result<i32, String> {
    let baseline = read_baseline(&options.baseline)?;
    let mut rules = build_rules(&baseline, today)?;
    let mut root: Value =
        serde_json::from_slice(&fs::read(&options.findings).map_err(|error| error.to_string())?)
            .map_err(|error| error.to_string())?;
    if text(&root, "schema") != "realdiff.findings/1" || text(&root, "status") != "analyzed" {
        return Err(
            "A baseline can only be applied to analyzed realdiff.findings/1 artifacts.".to_owned(),
        );
    }

    let members = root
        .get_mut("members")
        .and_then(Value::as_array_mut)
        .ok_or_else(|| "Analyzed findings have no members array.".to_owned())?;
    let mut actionable_members = 0_i64;
    let mut actionable_call_sites = 0_i64;
    let mut suppressed_members = 0_i64;
    let mut suppressed_call_sites = 0_i64;
    for member in members.iter_mut() {
        if text(member, "attribution") != "unexpected" {
            continue;
        }
        member
            .as_object_mut()
            .ok_or_else(|| "Findings member is not an object.".to_owned())?
            .remove("suppression");
        let call_sites = number(member, "callSiteCount");
        let broad_match = rules.iter().position(|rule| {
            !rule.expired && rule.kind != "acknowledgement" && rule.matches_scope(member)
        });
        if let Some(index) = broad_match {
            rules[index].match_count += 1;
            rules[index].scope_match_count += 1;
            suppressed_members += 1;
            suppressed_call_sites += call_sites;
            insert(member, "suppression", suppression(&rules[index]))?;
            continue;
        }

        let pairs = behavior_pairs(member);
        let candidates = rules
            .iter()
            .enumerate()
            .filter(|(_, rule)| {
                !rule.expired && rule.kind == "acknowledgement" && rule.matches_scope(member)
            })
            .map(|(index, _)| index)
            .collect::<Vec<_>>();
        for index in &candidates {
            rules[*index].scope_match_count += 1;
        }
        let matched = candidates
            .into_iter()
            .filter(|index| pairs.iter().any(|pair| rules[*index].matches_pair(pair)))
            .collect::<Vec<_>>();
        for index in &matched {
            rules[*index].match_count += 1;
        }
        let fully_acknowledged = !pairs.is_empty()
            && pairs
                .iter()
                .all(|pair| matched.iter().any(|index| rules[*index].matches_pair(pair)));
        if fully_acknowledged {
            suppressed_members += 1;
            suppressed_call_sites += call_sites;
            insert(member, "suppression", suppression(&rules[matched[0]]))?;
        } else {
            actionable_members += 1;
            actionable_call_sites += call_sites;
        }
    }

    let stale = rules
        .iter()
        .filter(|rule| !rule.expired && rule.scope_match_count == 0)
        .map(rule_summary)
        .collect::<Vec<_>>();
    let digest_mismatches = rules
        .iter()
        .filter(|rule| {
            !rule.expired
                && rule.kind == "acknowledgement"
                && rule.scope_match_count > 0
                && rule.match_count == 0
        })
        .map(|rule| {
            object([
                ("ruleId", Value::String(rule.id.clone())),
                ("kind", Value::String(rule.kind.clone())),
                ("reason", Value::String(rule.reason.clone())),
                ("baseDigest", option_string(rule.base_digest.as_deref())),
                ("prDigest", option_string(rule.pr_digest.as_deref())),
            ])
        })
        .collect::<Vec<_>>();
    let expired = rules
        .iter()
        .filter(|rule| rule.expired)
        .map(|rule| {
            object([
                ("ruleId", Value::String(rule.id.clone())),
                ("kind", Value::String(rule.kind.clone())),
                ("reason", Value::String(rule.reason.clone())),
                ("expires", option_string(rule.expires.as_deref())),
            ])
        })
        .collect::<Vec<_>>();

    let summary = root
        .get_mut("summary")
        .and_then(Value::as_object_mut)
        .ok_or_else(|| "Analyzed findings have no summary object.".to_owned())?;
    summary.insert(
        "actionableUnexpectedMembers".to_owned(),
        Value::from(actionable_members),
    );
    summary.insert(
        "actionableUnexpectedCallSites".to_owned(),
        Value::from(actionable_call_sites),
    );
    summary.insert(
        "suppressedMembers".to_owned(),
        Value::from(suppressed_members),
    );
    summary.insert(
        "suppressedCallSites".to_owned(),
        Value::from(suppressed_call_sites),
    );

    let strict_comment_policy = root
        .get("commentPolicy")
        .and_then(Value::as_object)
        .map(|comment_policy| comment_policy.get("mode").and_then(Value::as_str) == Some("strict"));
    if let Some(strict) = strict_comment_policy {
        let (eligible_members, eligible_call_sites) = {
            let members = root
                .get("members")
                .and_then(Value::as_array)
                .expect("members remained an array");
            let eligible = members
                .iter()
                .filter(|member| {
                    text(member, "attribution") == "unexpected"
                        && member.get("suppression").is_none()
                        && (strict
                            || member
                                .get("defaultCommentEligible")
                                .and_then(Value::as_bool)
                                != Some(false))
                })
                .collect::<Vec<_>>();
            (
                eligible.len(),
                eligible
                    .iter()
                    .map(|member| number(member, "callSiteCount"))
                    .sum::<i64>(),
            )
        };
        let comment_policy = root
            .get_mut("commentPolicy")
            .and_then(Value::as_object_mut)
            .expect("commentPolicy remained an object");
        comment_policy.insert(
            "eligibleUnexpectedMembers".to_owned(),
            Value::from(eligible_members),
        );
        comment_policy.insert(
            "eligibleUnexpectedCallSites".to_owned(),
            Value::from(eligible_call_sites),
        );
        comment_policy.insert(
            "suppressedUnexpectedMembers".to_owned(),
            Value::from(actionable_members + suppressed_members - eligible_members as i64),
        );
        comment_policy.insert(
            "suppressedUnexpectedCallSites".to_owned(),
            Value::from(actionable_call_sites + suppressed_call_sites - eligible_call_sites),
        );
    }

    let root_object = root
        .as_object_mut()
        .ok_or_else(|| "Findings root is not an object.".to_owned())?;
    root_object.insert(
        "baseline".to_owned(),
        object([
            ("schema", Value::String(baseline.schema)),
            ("path", Value::String(display_path(&options.baseline)?)),
            ("suppressedMembers", Value::from(suppressed_members)),
            ("suppressedCallSites", Value::from(suppressed_call_sites)),
            (
                "actionableUnexpectedMembers",
                Value::from(actionable_members),
            ),
            (
                "actionableUnexpectedCallSites",
                Value::from(actionable_call_sites),
            ),
            ("staleEntries", Value::Array(stale)),
            ("digestMismatchEntries", Value::Array(digest_mismatches)),
            ("expiredEntries", Value::Array(expired)),
        ]),
    );
    let clean = actionable_members == 0;
    root_object.insert(
        "policyVerdict".to_owned(),
        Value::String(if clean { "clean" } else { "findings" }.to_owned()),
    );
    root_object.insert(
        "policyExitCode".to_owned(),
        Value::from(if clean { 0 } else { 1 }),
    );
    write(&options.findings, &root)?;
    Ok(if clean { 0 } else { 1 })
}

fn read_baseline(path: &str) -> Result<BaselineDocument, String> {
    if !Path::new(path).exists() {
        return Err(format!("Baseline file does not exist: {path}"));
    }
    let content = fs::read_to_string(path).map_err(|error| error.to_string())?;
    if content.trim().is_empty() {
        return Err(format!("Baseline file is empty: {path}"));
    }
    let baseline: BaselineDocument = serde_yaml::from_str(&content)
        .map_err(|error| format!("Baseline YAML is malformed: {error}"))?;
    validate(&baseline)?;
    Ok(baseline)
}

fn validate(baseline: &BaselineDocument) -> Result<(), String> {
    if baseline.schema != "realdiff.baseline/2" {
        return Err(format!(
            "Unsupported baseline schema '{}'.",
            baseline.schema
        ));
    }
    let mut ids = HashSet::new();
    for rule in &baseline.acknowledgements {
        required(&rule.id, "acknowledgement id")?;
        required(&rule.member, "acknowledgement member")?;
        required(
            rule.path.as_deref().unwrap_or_default(),
            "acknowledgement path",
        )?;
        required(&rule.base_digest, "acknowledgement baseDigest")?;
        required(&rule.pr_digest, "acknowledgement prDigest")?;
        required(&rule.reason, "acknowledgement reason")?;
        unique(&mut ids, &rule.id)?;
        parse_expiry(rule.expires.as_deref(), &rule.id)?;
    }
    for (rule, kind) in baseline
        .ignore_paths
        .iter()
        .map(|rule| (rule, "path"))
        .chain(baseline.ignore_members.iter().map(|rule| (rule, "member")))
    {
        required(&rule.id, &format!("{kind} ignore id"))?;
        required(&rule.pattern, &format!("{kind} ignore pattern"))?;
        required(&rule.reason, &format!("{kind} ignore reason"))?;
        unique(&mut ids, &rule.id)?;
        parse_expiry(rule.expires.as_deref(), &rule.id)?;
    }
    Ok(())
}

fn build_rules(baseline: &BaselineDocument, today: Date) -> Result<Vec<RuleState>, String> {
    let mut rules = Vec::new();
    for rule in &baseline.acknowledgements {
        rules.push(RuleState {
            id: rule.id.clone(),
            kind: "acknowledgement".to_owned(),
            reason: rule.reason.clone(),
            expires: rule.expires.clone(),
            expired: is_expired(rule.expires.as_deref(), &rule.id, today)?,
            matcher: Matcher::Acknowledgement {
                member: rule.member.clone(),
                path: rule.path.as_deref().unwrap_or_default().replace('\\', "/"),
            },
            base_digest: Some(rule.base_digest.clone()),
            pr_digest: Some(rule.pr_digest.clone()),
            match_count: 0,
            scope_match_count: 0,
        });
    }
    for (source, kind) in baseline
        .ignore_paths
        .iter()
        .map(|rule| (rule, "path"))
        .chain(baseline.ignore_members.iter().map(|rule| (rule, "member")))
    {
        rules.push(RuleState {
            id: source.id.clone(),
            kind: format!("{kind}Ignore"),
            reason: source.reason.clone(),
            expires: source.expires.clone(),
            expired: is_expired(source.expires.as_deref(), &source.id, today)?,
            matcher: Matcher::Glob {
                pattern: glob(&source.pattern, kind == "path")?,
                path: kind == "path",
            },
            base_digest: None,
            pr_digest: None,
            match_count: 0,
            scope_match_count: 0,
        });
    }
    Ok(rules)
}

fn glob(value: &str, path: bool) -> Result<Regex, String> {
    let chars = value.chars().collect::<Vec<_>>();
    let mut expression = String::from("^");
    let mut index = 0;
    while index < chars.len() {
        match chars[index] {
            '*' => {
                let recursive = chars.get(index + 1) == Some(&'*');
                if recursive {
                    index += 1;
                    if path && chars.get(index + 1) == Some(&'/') {
                        index += 1;
                        expression.push_str("(?:.*/)?");
                    } else {
                        expression.push_str(".*");
                    }
                } else if path {
                    expression.push_str("[^/]*");
                } else {
                    expression.push_str(".*");
                }
            }
            '?' if path => expression.push_str("[^/]"),
            '?' => expression.push('.'),
            current => expression.push_str(&regex::escape(&current.to_string())),
        }
        index += 1;
    }
    expression.push('$');
    Regex::new(&expression).map_err(|error| error.to_string())
}

fn parse_expiry(value: Option<&str>, id: &str) -> Result<Option<Date>, String> {
    let Some(value) = value else { return Ok(None) };
    let format = format_description::parse_borrowed::<2>("[year]-[month]-[day]").unwrap();
    Date::parse(value, &format)
        .map(Some)
        .map_err(|_| format!("Baseline rule '{id}' expiry must use YYYY-MM-DD."))
}

fn is_expired(value: Option<&str>, id: &str, today: Date) -> Result<bool, String> {
    Ok(parse_expiry(value, id)?.is_some_and(|expiry| expiry < today))
}

fn behavior_pairs(member: &Value) -> Vec<(String, String)> {
    let mut seen = HashSet::new();
    member
        .get("evidence")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|item| {
            let pair = (text(item, "baseDigest"), text(item, "prDigest"));
            (!pair.0.is_empty() && !pair.1.is_empty() && seen.insert(pair.clone())).then_some(pair)
        })
        .collect()
}

fn suppression(rule: &RuleState) -> Value {
    object([
        ("ruleId", Value::String(rule.id.clone())),
        ("kind", Value::String(rule.kind.clone())),
        ("reason", Value::String(rule.reason.clone())),
        ("expires", option_string(rule.expires.as_deref())),
    ])
}

fn rule_summary(rule: &RuleState) -> Value {
    object([
        ("ruleId", Value::String(rule.id.clone())),
        ("kind", Value::String(rule.kind.clone())),
        ("reason", Value::String(rule.reason.clone())),
    ])
}

fn display_path(path: &str) -> Result<String, String> {
    let absolute = if Path::new(path).is_absolute() {
        PathBuf::from(path)
    } else {
        std::env::current_dir()
            .map_err(|error| error.to_string())?
            .join(path)
    };
    let normalized = absolute.to_string_lossy().replace('\\', "/");
    if let Some(marker) = normalized.rfind("/.realdiff/") {
        Ok(normalized[marker + 1..].to_owned())
    } else {
        Ok(Path::new(path)
            .file_name()
            .and_then(|value| value.to_str())
            .unwrap_or(path)
            .to_owned())
    }
}

fn write(path: &str, value: &Value) -> Result<(), String> {
    let mut bytes = crate::dotnet_json::to_vec_pretty(value)?;
    if cfg!(windows) {
        bytes.extend_from_slice(b"\r\n");
    } else {
        bytes.push(b'\n');
    }
    fs::write(path, bytes).map_err(|error| error.to_string())
}

fn required(value: &str, field: &str) -> Result<(), String> {
    if value.trim().is_empty() {
        Err(format!("Baseline {field} is required."))
    } else {
        Ok(())
    }
}

fn unique(ids: &mut HashSet<String>, id: &str) -> Result<(), String> {
    if ids.insert(id.to_owned()) {
        Ok(())
    } else {
        Err(format!("Baseline rule id '{id}' is duplicated."))
    }
}

fn insert(root: &mut Value, property: &str, value: Value) -> Result<(), String> {
    root.as_object_mut()
        .ok_or_else(|| "Findings member is not an object.".to_owned())?
        .insert(property.to_owned(), value);
    Ok(())
}

fn object<const N: usize>(entries: [(&str, Value); N]) -> Value {
    let mut map = Map::new();
    for (key, value) in entries {
        map.insert(key.to_owned(), value);
    }
    Value::Object(map)
}

fn text(value: &Value, property: &str) -> String {
    value
        .get(property)
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_owned()
}

fn number(value: &Value, property: &str) -> i64 {
    value.get(property).and_then(Value::as_i64).unwrap_or(0)
}

fn option_string(value: Option<&str>) -> Value {
    value.map_or(Value::Null, |value| Value::String(value.to_owned()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn path_and_member_globs_match_the_policy_contract() {
        let path = glob("**/generated/**", true).unwrap();
        assert!(path.is_match("generated/value.cs"));
        assert!(path.is_match("src/generated/value.cs"));
        assert!(!path.is_match("src/not-generated/value.cs"));

        let member = glob("Legacy.Cache.?ead*", false).unwrap();
        assert!(member.is_match("Legacy.Cache.Read()"));
        assert!(!member.is_match("Acme.Cache.Read()"));
    }
}
