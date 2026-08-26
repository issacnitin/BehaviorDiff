use sha2::{Digest, Sha256};
use std::env;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use syn::visit_mut::{self, VisitMut};
use syn::{parse_quote, Block, ImplItemFn, ItemFn};
use walkdir::WalkDir;

const CACHE_VERSION: &str = "behaviordiff-rust-rewriter-prototype/1";

const RUNTIME: &str = r#"use std::fs::OpenOptions;
use std::io::Write;

pub struct Guard {
    method: &'static str,
    file: &'static str,
    line: u32,
    completed: bool,
}

pub fn enter(method: &'static str, file: &'static str, line: u32) -> Guard {
    Guard { method, file, line, completed: false }
}

impl Guard {
    pub fn complete(mut self) {
        self.completed = true;
        emit(self.method, self.file, self.line, "normal");
    }
}

impl Drop for Guard {
    fn drop(&mut self) {
        if !self.completed {
            emit(self.method, self.file, self.line, if std::thread::panicking() { "panic" } else { "cancelled" });
        }
    }
}

fn emit(method: &str, file: &str, line: u32, outcome: &str) {
    let Ok(path) = std::env::var("BEHAVIORDIFF_RUST_TRACE") else { return; };
    let Ok(mut stream) = OpenOptions::new().create(true).append(true).open(path) else { return; };
    let method = escape(method);
    let file = escape(file);
    let _ = writeln!(stream, "{{\"method\":\"{method}\",\"file\":\"{file}\",\"line\":{line},\"outcome\":\"{outcome}\"}}");
}

fn escape(value: &str) -> String {
    value.replace('\\', "\\\\").replace('\"', "\\\"").replace('\n', "\\n").replace('\r', "\\r")
}
"#;

struct Instrumenter {
    relative_path: String,
}

impl Instrumenter {
    fn instrument(&self, name: &syn::Ident, is_async: bool, block: &mut Block) {
        let method = format!("{}::{}", self.relative_path, name);
        let original = block.clone();
        *block = if is_async {
            parse_quote!({
                let __behaviordiff_guard = crate::__behaviordiff_runtime::enter(#method, file!(), line!());
                let __behaviordiff_result = (async move #original).await;
                __behaviordiff_guard.complete();
                __behaviordiff_result
            })
        } else {
            parse_quote!({
                let __behaviordiff_guard = crate::__behaviordiff_runtime::enter(#method, file!(), line!());
                let __behaviordiff_result = (|| #original)();
                __behaviordiff_guard.complete();
                __behaviordiff_result
            })
        };
    }
}

impl VisitMut for Instrumenter {
    fn visit_item_fn_mut(&mut self, function: &mut ItemFn) {
        visit_mut::visit_item_fn_mut(self, function);
        if function.sig.constness.is_none() && function.sig.abi.is_none() {
            self.instrument(
                &function.sig.ident,
                function.sig.asyncness.is_some(),
                &mut function.block,
            );
        }
    }

    fn visit_impl_item_fn_mut(&mut self, function: &mut ImplItemFn) {
        visit_mut::visit_impl_item_fn_mut(self, function);
        if function.sig.constness.is_none() && function.sig.abi.is_none() {
            self.instrument(
                &function.sig.ident,
                function.sig.asyncness.is_some(),
                &mut function.block,
            );
        }
    }
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let arguments = env::args().collect::<Vec<_>>();
    if arguments.len() != 3 {
        return Err("usage: behaviordiff-rust-rewriter-prototype <source> <cache-root>".into());
    }

    let source = fs::canonicalize(&arguments[1])?;
    let cache_root = PathBuf::from(&arguments[2]);
    let key = cache_key(&source)?;
    let destination = cache_root.join(key);
    if destination.exists() {
        println!("cacheStatus=hit");
        println!("{}", destination.display());
        return Ok(());
    }

    let staging = cache_root.join(format!(".staging-{}", std::process::id()));
    if staging.exists() {
        fs::remove_dir_all(&staging)?;
    }
    copy_project(&source, &staging)?;
    instrument_project(&staging)?;
    fs::create_dir_all(&cache_root)?;
    fs::rename(&staging, &destination)?;
    println!("cacheStatus=miss");
    println!("{}", destination.display());
    Ok(())
}

fn cache_key(source: &Path) -> io::Result<String> {
    let mut files = WalkDir::new(source)
        .into_iter()
        .filter_map(Result::ok)
        .filter(|entry| entry.file_type().is_file())
        .filter(|entry| {
            !entry
                .path()
                .components()
                .any(|part| part.as_os_str() == "target")
        })
        .map(|entry| entry.into_path())
        .collect::<Vec<_>>();
    files.sort();
    let mut hash = Sha256::new();
    hash.update(CACHE_VERSION.as_bytes());
    for file in files {
        hash.update(
            file.strip_prefix(source)
                .unwrap()
                .to_string_lossy()
                .as_bytes(),
        );
        hash.update(fs::read(file)?);
    }
    Ok(hex::encode(hash.finalize()))
}

fn copy_project(source: &Path, destination: &Path) -> io::Result<()> {
    for entry in WalkDir::new(source).into_iter().filter_map(Result::ok) {
        let relative = entry.path().strip_prefix(source).unwrap();
        if relative
            .components()
            .any(|part| part.as_os_str() == "target")
        {
            continue;
        }
        let target = destination.join(relative);
        if entry.file_type().is_dir() {
            fs::create_dir_all(target)?;
        } else {
            fs::copy(entry.path(), target)?;
        }
    }
    Ok(())
}

fn instrument_project(project: &Path) -> Result<(), Box<dyn std::error::Error>> {
    let source_root = project.join("src");
    let mut rust_files = WalkDir::new(&source_root)
        .into_iter()
        .filter_map(Result::ok)
        .filter(|entry| {
            entry.file_type().is_file() && entry.path().extension().is_some_and(|ext| ext == "rs")
        })
        .map(|entry| entry.into_path())
        .collect::<Vec<_>>();
    rust_files.sort();
    for path in rust_files {
        let relative_path = path
            .strip_prefix(project)?
            .to_string_lossy()
            .replace('\\', "/");
        let content = fs::read_to_string(&path)?;
        let mut syntax = syn::parse_file(&content)?;
        Instrumenter { relative_path }.visit_file_mut(&mut syntax);
        if path == source_root.join("main.rs") || path == source_root.join("lib.rs") {
            syntax.items.insert(
                0,
                parse_quote!(
                    mod __behaviordiff_runtime;
                ),
            );
        }
        fs::write(path, prettyplease::unparse(&syntax))?;
    }
    fs::write(source_root.join("__behaviordiff_runtime.rs"), RUNTIME)?;
    Ok(())
}
