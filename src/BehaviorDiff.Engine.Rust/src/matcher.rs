use crate::model::{LoadedEvent, RunData, TraceEvent};
use std::collections::{BTreeMap, BTreeSet};

#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub(crate) struct CallKey {
    pub(crate) test_id: String,
    pub(crate) method_full_name: String,
}

impl CallKey {
    fn new(event: &TraceEvent) -> Self {
        Self {
            test_id: event.test_id.clone(),
            method_full_name: event.method_full_name.clone(),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum DigestConfidence {
    Exact,
    Partial,
}

impl DigestConfidence {
    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::Exact => "Exact",
            Self::Partial => "Partial",
        }
    }
}

#[derive(Clone, Debug)]
pub(crate) struct CallRecord {
    pub(crate) loaded: LoadedEvent,
    pub(crate) ordinal: i32,
}

#[derive(Clone, Debug)]
pub(crate) struct MatchedKey {
    pub(crate) key: CallKey,
    pub(crate) base_calls: usize,
    pub(crate) pr_calls: usize,
    pub(crate) confidence: DigestConfidence,
    pub(crate) partial_markers: Vec<String>,
    pub(crate) relative_path: Option<String>,
}

#[derive(Clone, Debug)]
pub(crate) struct Divergence {
    pub(crate) key: CallKey,
    pub(crate) ordinal: i32,
    pub(crate) kind: &'static str,
    pub(crate) detail: String,
    pub(crate) confidence: DigestConfidence,
    pub(crate) partial_markers: Vec<String>,
    pub(crate) base_event: Option<TraceEvent>,
    pub(crate) pr_event: Option<TraceEvent>,
    pub(crate) relative_path: Option<String>,
}

pub(crate) type Index = BTreeMap<CallKey, Vec<CallRecord>>;

pub(crate) fn index(run: &RunData) -> Index {
    let mut ordered = run.events.clone();
    ordered.sort_by(|left, right| {
        left.process_key
            .cmp(&right.process_key)
            .then(left.line_number.cmp(&right.line_number))
    });
    let mut result: Index = BTreeMap::new();
    for loaded in ordered {
        let key = CallKey::new(&loaded.event);
        let ordinal = loaded.event.ordinal;
        result
            .entry(key)
            .or_default()
            .push(CallRecord { loaded, ordinal });
    }
    for calls in result.values_mut() {
        calls.sort_by_key(|call| call.ordinal);
    }
    result
}

pub(crate) fn compare(
    base_index: &Index,
    pr_index: &Index,
) -> (Vec<Divergence>, Vec<MatchedKey>, Vec<Divergence>) {
    let keys: BTreeSet<_> = base_index.keys().chain(pr_index.keys()).cloned().collect();
    let mut divergences = Vec::new();
    let mut matched = Vec::new();
    let mut harness_divergences = Vec::new();

    for key in keys {
        let base_calls = base_index.get(&key);
        let pr_calls = pr_index.get(&key);
        let present = base_calls.or(pr_calls).unwrap();
        let is_harness = present[0].loaded.event.is_harness;
        let relative_path = present[0].loaded.relative_path.clone();

        if base_calls.is_none() || pr_calls.is_none() {
            let (confidence, partial_markers) = classify(&[&present[0].loaded.event]);
            let divergence = Divergence {
                key,
                ordinal: -1,
                kind: if base_calls.is_none() {
                    "MissingInBase"
                } else {
                    "MissingInPr"
                },
                detail: if base_calls.is_none() {
                    format!("key absent from base, {} call(s) in PR", present.len())
                } else {
                    format!("key absent from PR, {} call(s) in base", present.len())
                },
                confidence,
                partial_markers,
                base_event: base_calls.map(|calls| calls[0].loaded.event.clone()),
                pr_event: pr_calls.map(|calls| calls[0].loaded.event.clone()),
                relative_path,
            };
            sink(is_harness, &mut divergences, &mut harness_divergences).push(divergence);
            continue;
        }

        let base_calls = base_calls.unwrap();
        let pr_calls = pr_calls.unwrap();
        let (key_confidence, key_markers) =
            classify(&[&base_calls[0].loaded.event, &pr_calls[0].loaded.event]);
        if !is_harness {
            matched.push(MatchedKey {
                key: key.clone(),
                base_calls: base_calls.len(),
                pr_calls: pr_calls.len(),
                confidence: key_confidence,
                partial_markers: key_markers.clone(),
                relative_path: relative_path.clone(),
            });
        }

        if base_calls.len() != pr_calls.len() {
            sink(is_harness, &mut divergences, &mut harness_divergences).push(Divergence {
                key: key.clone(),
                ordinal: -1,
                kind: "CallCountChange",
                detail: format!(
                    "called {} time(s) in base, {} in PR",
                    base_calls.len(),
                    pr_calls.len()
                ),
                confidence: key_confidence,
                partial_markers: key_markers,
                base_event: Some(base_calls[0].loaded.event.clone()),
                pr_event: Some(pr_calls[0].loaded.event.clone()),
                relative_path: relative_path.clone(),
            });
        }

        for ordinal in 0..base_calls.len().min(pr_calls.len()) {
            let base_event = &base_calls[ordinal].loaded.event;
            let pr_event = &pr_calls[ordinal].loaded.event;
            let Some(detail) = first_difference(base_event, pr_event) else {
                continue;
            };
            let (confidence, partial_markers) = classify(&[base_event, pr_event]);
            sink(is_harness, &mut divergences, &mut harness_divergences).push(Divergence {
                key: key.clone(),
                ordinal: ordinal as i32,
                kind: "DigestDiff",
                detail: detail.to_owned(),
                confidence,
                partial_markers,
                base_event: Some(base_event.clone()),
                pr_event: Some(pr_event.clone()),
                relative_path: base_calls[ordinal].loaded.relative_path.clone(),
            });
        }
    }
    (divergences, matched, harness_divergences)
}

fn sink<'a>(
    is_harness: bool,
    divergences: &'a mut Vec<Divergence>,
    harness_divergences: &'a mut Vec<Divergence>,
) -> &'a mut Vec<Divergence> {
    if is_harness {
        harness_divergences
    } else {
        divergences
    }
}

fn classify(events: &[&TraceEvent]) -> (DigestConfidence, Vec<String>) {
    let mut markers = BTreeSet::new();
    for event in events {
        for rendered in [
            event.args_rendered.as_deref(),
            event.return_rendered.as_deref(),
        ]
        .into_iter()
        .flatten()
        {
            for (prefix, marker) in [
                ("<skipped:", "skipped"),
                ("<depth:", "depth"),
                ("<error:", "error"),
                ("<truncated>", "truncated"),
            ] {
                if rendered.contains(prefix) {
                    markers.insert(marker.to_owned());
                }
            }
        }
    }
    if markers.is_empty() {
        (DigestConfidence::Exact, Vec::new())
    } else {
        (DigestConfidence::Partial, markers.into_iter().collect())
    }
}

fn first_difference(base: &TraceEvent, pr: &TraceEvent) -> Option<&'static str> {
    if base.args_digest != pr.args_digest {
        Some("argsDigest")
    } else if base.return_digest != pr.return_digest {
        Some("returnDigest")
    } else if base.exception_type != pr.exception_type {
        Some("exceptionType")
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn partial_markers_are_distinct_and_sorted() {
        let event = TraceEvent {
            test_id: "test".to_owned(),
            method_full_name: "method".to_owned(),
            file_path: None,
            line: 0,
            parent_call_id: None,
            call_id: 1,
            ordinal: 0,
            args_digest: None,
            args_rendered: Some("<truncated><depth:4><truncated>".to_owned()),
            return_digest: None,
            return_rendered: None,
            exception_type: None,
            is_harness: false,
        };

        let (confidence, markers) = classify(&[&event]);

        assert_eq!(confidence, DigestConfidence::Partial);
        assert_eq!(markers, ["depth", "truncated"]);
    }
}
