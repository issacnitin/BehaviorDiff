use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs;
use std::path::Path;

#[derive(Default)]
pub(crate) struct FrontierOptions {
    pub(crate) input: String,
    pub(crate) changed_files: String,
    pub(crate) output: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct DivergenceSet {
    call_tree: Vec<CallNode>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct FrontierIndexReport {
    schema: &'static str,
    call_tree_nodes: usize,
    roots: usize,
    edges: usize,
    orphans: usize,
    changed_files: usize,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct CallNode {
    pub(crate) call_id: i64,
    pub(crate) parent_call_id: Option<i64>,
    pub(crate) test_id: String,
    #[serde(default)]
    pub(crate) process: String,
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

    #[cfg(test)]
    fn children_of(&self, node: &CallNode) -> &[usize] {
        self.children
            .get(&CallIdentity::new(node, node.call_id))
            .map(Vec::as_slice)
            .unwrap_or_default()
    }

    fn edge_count(&self) -> usize {
        self.children.values().map(Vec::len).sum()
    }
}

pub(crate) fn run(options: &FrontierOptions) -> Result<i32, String> {
    let bytes = fs::read(&options.input).map_err(|error| error.to_string())?;
    let set: DivergenceSet = serde_json::from_slice(&bytes).map_err(|error| error.to_string())?;
    if set.call_tree.is_empty() {
        return Err("Frontier input callTree is empty".to_owned());
    }
    let index = CallTreeIndex::build(&set.call_tree);
    let changed_files = if options.changed_files.is_empty() {
        0
    } else {
        fs::read_to_string(&options.changed_files)
            .map_err(|error| error.to_string())?
            .lines()
            .filter(|line| !line.trim().is_empty())
            .count()
    };
    let report = FrontierIndexReport {
        schema: "behaviordiff.frontier-index/1",
        call_tree_nodes: set.call_tree.len(),
        roots: index.roots,
        edges: index.edge_count(),
        orphans: index.orphans.len(),
        changed_files,
    };
    if let Some(parent) = Path::new(&options.output).parent() {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    fs::write(
        &options.output,
        serde_json::to_vec_pretty(&report).map_err(|error| error.to_string())?,
    )
    .map_err(|error| error.to_string())?;
    Ok(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn node(test: &str, process: &str, call_id: i64, parent_call_id: Option<i64>) -> CallNode {
        CallNode {
            call_id,
            parent_call_id,
            test_id: test.to_owned(),
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
