use quote::ToTokens;
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use walkdir::{DirEntry, WalkDir};

const CACHE_VERSION: &str = "behaviordiff.rust-rewrite-cache/1";
const ORIGIN_MANIFEST: &str = ".behaviordiff-rust-origin.json";

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RewriteReport {
    pub schema: &'static str,
    pub cache_status: &'static str,
    pub cache_key: String,
    pub source_hash: String,
    pub source_files: usize,
    pub rust_files: usize,
    pub output: PathBuf,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct OriginManifest {
    schema: &'static str,
    source_hash: String,
    source_files: usize,
    rust_files: Vec<OriginFile>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct OriginFile {
    path: String,
    sha256: String,
}

pub fn rewrite(source: &Path, cache_root: &Path) -> Result<RewriteReport, String> {
    let source = fs::canonicalize(source).map_err(|error| error.to_string())?;
    if !source.join("Cargo.toml").is_file() {
        return Err(format!(
            "Rust source root has no Cargo.toml: {}",
            source.display()
        ));
    }

    let cache_root = absolute_path(cache_root).map_err(|error| error.to_string())?;
    if cache_root.starts_with(&source) {
        return Err("Rust rewrite cache must be outside the source tree".to_owned());
    }

    let files = project_files(&source)?;
    if files.is_empty() {
        return Err("Rust source root has no project files".to_owned());
    }
    let source_hash = hash_files(&source, &files)?;
    let cache_key = cache_key(&source_hash);
    let output = cache_root.join(&cache_key);
    if valid_cache_entry(&output, &source_hash) {
        return Ok(report(
            "hit",
            cache_key,
            source_hash,
            files.len(),
            rust_file_count(&files),
            output,
        ));
    }

    fs::create_dir_all(&cache_root).map_err(|error| error.to_string())?;
    let staging = cache_root.join(format!(
        ".staging-{}-{}",
        std::process::id(),
        unique_suffix()
    ));
    if staging.exists() {
        fs::remove_dir_all(&staging).map_err(|error| error.to_string())?;
    }

    let result = build_cache_entry(&source, &files, &source_hash, &staging);
    if let Err(error) = result {
        let _ = fs::remove_dir_all(&staging);
        return Err(error);
    }

    if output.exists() {
        fs::remove_dir_all(&output).map_err(|error| error.to_string())?;
    }
    match fs::rename(&staging, &output) {
        Ok(()) => {}
        Err(error) if valid_cache_entry(&output, &source_hash) => {
            let _ = fs::remove_dir_all(&staging);
            let _ = error;
            return Ok(report(
                "hit",
                cache_key,
                source_hash,
                files.len(),
                rust_file_count(&files),
                output,
            ));
        }
        Err(error) => {
            let _ = fs::remove_dir_all(&staging);
            return Err(error.to_string());
        }
    }

    Ok(report(
        "miss",
        cache_key,
        source_hash,
        files.len(),
        rust_file_count(&files),
        output,
    ))
}

fn build_cache_entry(
    source: &Path,
    files: &[PathBuf],
    source_hash: &str,
    staging: &Path,
) -> Result<(), String> {
    let mut origins = Vec::new();
    for relative in files {
        let input = source.join(relative);
        let output = staging.join(relative);
        if let Some(parent) = output.parent() {
            fs::create_dir_all(parent).map_err(|error| error.to_string())?;
        }
        if is_rust_source(relative) {
            let text = fs::read_to_string(&input).map_err(|error| error.to_string())?;
            let syntax =
                syn::parse_file(&text).map_err(|error| format!("{}: {error}", input.display()))?;
            let tokens = syntax.to_token_stream();
            let reparsed =
                syn::parse2(tokens).map_err(|error| format!("{}: {error}", input.display()))?;
            fs::write(&output, prettyplease::unparse(&reparsed))
                .map_err(|error| error.to_string())?;
            origins.push(OriginFile {
                path: slash(relative),
                sha256: hash_bytes(&fs::read(&input).map_err(|error| error.to_string())?),
            });
        } else {
            fs::copy(&input, &output).map_err(|error| error.to_string())?;
        }
    }

    let manifest = OriginManifest {
        schema: CACHE_VERSION,
        source_hash: source_hash.to_owned(),
        source_files: files.len(),
        rust_files: origins,
    };
    let manifest_text = serde_json::to_vec_pretty(&manifest).map_err(|error| error.to_string())?;
    fs::write(staging.join(ORIGIN_MANIFEST), manifest_text).map_err(|error| error.to_string())?;
    Ok(())
}

fn project_files(source: &Path) -> Result<Vec<PathBuf>, String> {
    let mut files = WalkDir::new(source)
        .follow_links(false)
        .into_iter()
        .filter_entry(included_entry)
        .filter_map(Result::ok)
        .filter(|entry| entry.file_type().is_file())
        .map(|entry| entry.path().strip_prefix(source).unwrap().to_path_buf())
        .collect::<Vec<_>>();
    files.sort();
    Ok(files)
}

fn included_entry(entry: &DirEntry) -> bool {
    if entry.depth() == 0 {
        return true;
    }
    !matches!(entry.file_name().to_str(), Some("target" | ".git"))
}

fn hash_files(source: &Path, files: &[PathBuf]) -> Result<String, String> {
    let mut hash = Sha256::new();
    for relative in files {
        let bytes = fs::read(source.join(relative)).map_err(|error| error.to_string())?;
        let path = slash(relative);
        hash.update((path.len() as u64).to_le_bytes());
        hash.update(path.as_bytes());
        hash.update((bytes.len() as u64).to_le_bytes());
        hash.update(bytes);
    }
    Ok(hex::encode(hash.finalize()))
}

fn cache_key(source_hash: &str) -> String {
    let mut hash = Sha256::new();
    hash.update(CACHE_VERSION.as_bytes());
    hash.update(source_hash.as_bytes());
    hex::encode(hash.finalize())
}

fn valid_cache_entry(path: &Path, source_hash: &str) -> bool {
    let Ok(bytes) = fs::read(path.join(ORIGIN_MANIFEST)) else {
        return false;
    };
    let Ok(value) = serde_json::from_slice::<serde_json::Value>(&bytes) else {
        return false;
    };
    value.get("schema").and_then(|item| item.as_str()) == Some(CACHE_VERSION)
        && value.get("sourceHash").and_then(|item| item.as_str()) == Some(source_hash)
}

fn report(
    cache_status: &'static str,
    cache_key: String,
    source_hash: String,
    source_files: usize,
    rust_files: usize,
    output: PathBuf,
) -> RewriteReport {
    RewriteReport {
        schema: CACHE_VERSION,
        cache_status,
        cache_key,
        source_hash,
        source_files,
        rust_files,
        output: user_path(&output),
    }
}

fn user_path(path: &Path) -> PathBuf {
    let text = path.to_string_lossy();
    if let Some(value) = text.strip_prefix(r"\\?\UNC\") {
        return PathBuf::from(format!(r"\\{value}"));
    }
    if let Some(value) = text.strip_prefix(r"\\?\") {
        return PathBuf::from(value);
    }
    path.to_path_buf()
}

fn rust_file_count(files: &[PathBuf]) -> usize {
    files
        .iter()
        .filter(|relative| is_rust_source(relative))
        .count()
}

fn is_rust_source(path: &Path) -> bool {
    path.extension().is_some_and(|extension| extension == "rs")
}

fn hash_bytes(bytes: &[u8]) -> String {
    hex::encode(Sha256::digest(bytes))
}

fn slash(path: &Path) -> String {
    path.to_string_lossy().replace('\\', "/")
}

fn absolute_path(path: &Path) -> io::Result<PathBuf> {
    let absolute = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()?.join(path)
    };
    let mut existing = absolute.as_path();
    let mut missing = Vec::new();
    while !existing.exists() {
        let name = existing.file_name().ok_or_else(|| {
            io::Error::new(io::ErrorKind::InvalidInput, "path has no existing ancestor")
        })?;
        missing.push(name.to_os_string());
        existing = existing.parent().ok_or_else(|| {
            io::Error::new(io::ErrorKind::InvalidInput, "path has no existing ancestor")
        })?;
    }

    let mut normalized = fs::canonicalize(existing)?;
    for name in missing.into_iter().rev() {
        normalized.push(name);
    }
    Ok(normalized)
}

fn unique_suffix() -> u128 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rewrite_is_external_content_addressed_and_repeatable() {
        let root = std::env::temp_dir().join(format!(
            "behaviordiff-rust-rewrite-test-{}-{}",
            std::process::id(),
            unique_suffix()
        ));
        let source = root.join("source");
        let cache = root.join("cache");
        fs::create_dir_all(source.join("src")).unwrap();
        fs::write(
            source.join("Cargo.toml"),
            "[package]\nname='rewrite-fixture'\nversion='0.1.0'\nedition='2021'\n",
        )
        .unwrap();
        fs::write(
            source.join("src/lib.rs"),
            "pub fn answer( ) -> i32 { 42 }\n",
        )
        .unwrap();

        let before = fs::read(source.join("src/lib.rs")).unwrap();
        let first = rewrite(&source, &cache).unwrap();
        let second = rewrite(&source, &cache).unwrap();

        assert_eq!(first.cache_status, "miss");
        assert_eq!(second.cache_status, "hit");
        assert_eq!(first.cache_key, second.cache_key);
        assert_eq!(first.source_files, 2);
        assert_eq!(first.rust_files, 1);
        assert_eq!(fs::read(source.join("src/lib.rs")).unwrap(), before);
        assert!(first
            .output
            .join(".behaviordiff-rust-origin.json")
            .is_file());
        assert!(first.output.join("src/lib.rs").is_file());

        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn cache_inside_source_is_rejected() {
        let root = std::env::temp_dir().join(format!(
            "behaviordiff-rust-rewrite-nested-test-{}-{}",
            std::process::id(),
            unique_suffix()
        ));
        fs::create_dir_all(&root).unwrap();
        fs::write(root.join("Cargo.toml"), "[workspace]\n").unwrap();
        let error = rewrite(&root, &root.join("cache")).unwrap_err();
        assert!(error.contains("outside the source tree"));
        fs::remove_dir_all(root).unwrap();
    }
}
