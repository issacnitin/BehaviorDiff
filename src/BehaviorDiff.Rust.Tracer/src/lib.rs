use quote::ToTokens;
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use syn::visit_mut::{self, VisitMut};
use syn::{
    parse_quote, Block, Fields, GenericParam, ImplItemFn, Item, ItemEnum, ItemFn, ItemImpl,
    ItemStruct, ItemTrait, TraitItemFn,
};
use toml_edit::{DocumentMut, InlineTable, Item as TomlItem, Value};
use walkdir::{DirEntry, WalkDir};

const CACHE_VERSION: &str = "behaviordiff.rust-rewrite-cache/2";
const ORIGIN_MANIFEST: &str = ".behaviordiff-rust-origin.json";
const RUNTIME_CARGO: &str = include_str!("../runtime/Cargo.toml");
const RUNTIME_SOURCE: &str = include_str!("../runtime/src/lib.rs");
const RUNTIME_CANONICAL: &str = include_str!("../runtime/src/canonical.rs");

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
    instrumented_functions: usize,
    skipped_functions: usize,
    generated_readers: usize,
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
    let mut instrumented_functions = 0;
    let mut skipped_functions = 0;
    let mut generated_readers = 0;
    for relative in files {
        let input = source.join(relative);
        let output = staging.join(relative);
        if let Some(parent) = output.parent() {
            fs::create_dir_all(parent).map_err(|error| error.to_string())?;
        }
        if is_rust_source(relative) {
            let text = fs::read_to_string(&input).map_err(|error| error.to_string())?;
            let mut syntax =
                syn::parse_file(&text).map_err(|error| format!("{}: {error}", input.display()))?;
            if should_instrument(relative) {
                let mut instrumenter = ExitHookInstrumenter::new(slash(relative));
                instrumenter.visit_file_mut(&mut syntax);
                instrumented_functions += instrumenter.instrumented;
                skipped_functions += instrumenter.skipped;
                generated_readers += generate_readers(&mut syntax.items)?;
            }
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

    write_runtime(staging)?;
    inject_runtime_dependencies(staging, files)?;

    let manifest = OriginManifest {
        schema: CACHE_VERSION,
        source_hash: source_hash.to_owned(),
        source_files: files.len(),
        rust_files: origins,
        instrumented_functions,
        skipped_functions,
        generated_readers,
    };
    let manifest_text = serde_json::to_vec_pretty(&manifest).map_err(|error| error.to_string())?;
    fs::write(staging.join(ORIGIN_MANIFEST), manifest_text).map_err(|error| error.to_string())?;
    Ok(())
}

fn generate_readers(items: &mut Vec<Item>) -> Result<usize, String> {
    let mut generated = 0;
    for item in items.iter_mut() {
        if let Item::Mod(module) = item {
            if let Some((_, nested)) = &mut module.content {
                generated += generate_readers(nested)?;
            }
        }
    }

    let mut output = Vec::with_capacity(items.len() * 2);
    for item in std::mem::take(items) {
        let reader = match &item {
            Item::Struct(structure) => Some(struct_reader(structure)?),
            Item::Enum(enumeration) => Some(enum_reader(enumeration)?),
            _ => None,
        };
        output.push(item);
        if let Some(reader) = reader {
            output.push(Item::Impl(reader));
            generated += 1;
        }
    }
    *items = output;
    Ok(generated)
}

fn struct_reader(item: &ItemStruct) -> Result<ItemImpl, String> {
    let ident = &item.ident;
    let mut generics = item.generics.clone();
    add_canonical_bounds(&mut generics);
    let (impl_generics, type_generics, where_clause) = generics.split_for_impl();
    let fields = match &item.fields {
        Fields::Named(fields) => fields
            .named
            .iter()
            .map(|field| {
                let name = field.ident.as_ref().unwrap();
                let label = name.to_string();
                quote::quote! {
                    ::behaviordiff_rust_runtime::write_field(context, output, #label, &self.#name);
                }
            })
            .collect::<Vec<_>>(),
        Fields::Unnamed(fields) => fields
            .unnamed
            .iter()
            .enumerate()
            .map(|(index, _)| {
                let member = syn::Index::from(index);
                let label = index.to_string();
                quote::quote! {
                    ::behaviordiff_rust_runtime::write_field(context, output, #label, &self.#member);
                }
            })
            .collect::<Vec<_>>(),
        Fields::Unit => Vec::new(),
    };
    syn::parse2(quote::quote! {
        impl #impl_generics ::behaviordiff_rust_runtime::Canonicalize for #ident #type_generics #where_clause {
            fn write_canonical(
                &self,
                context: &mut ::behaviordiff_rust_runtime::CanonicalContext,
                output: &mut String,
            ) {
                context.write_value(stringify!(#ident), output, |context, output| {
                    output.push_str(stringify!(#ident));
                    output.push('{');
                    #(#fields)*
                    output.push('}');
                });
            }
        }
    })
    .map_err(|error| error.to_string())
}

fn enum_reader(item: &ItemEnum) -> Result<ItemImpl, String> {
    let ident = &item.ident;
    let mut generics = item.generics.clone();
    add_canonical_bounds(&mut generics);
    let (impl_generics, type_generics, where_clause) = generics.split_for_impl();
    let arms = item
        .variants
        .iter()
        .map(|variant| {
            let variant_ident = &variant.ident;
            let variant_name = variant_ident.to_string();
            match &variant.fields {
                Fields::Named(fields) => {
                    let names = fields
                        .named
                        .iter()
                        .map(|field| field.ident.as_ref().unwrap())
                        .collect::<Vec<_>>();
                    let writes = names.iter().map(|name| {
                        let label = name.to_string();
                        quote::quote! {
                            ::behaviordiff_rust_runtime::write_field(context, output, #label, #name);
                        }
                    });
                    quote::quote! {
                        Self::#variant_ident { #(#names),* } => {
                            output.push_str(#variant_name);
                            output.push('{');
                            #(#writes)*
                            output.push('}');
                        }
                    }
                }
                Fields::Unnamed(fields) => {
                    let names = (0..fields.unnamed.len())
                        .map(|index| quote::format_ident!("field_{index}"))
                        .collect::<Vec<_>>();
                    let writes = names.iter().enumerate().map(|(index, name)| {
                        let label = index.to_string();
                        quote::quote! {
                            ::behaviordiff_rust_runtime::write_field(context, output, #label, #name);
                        }
                    });
                    quote::quote! {
                        Self::#variant_ident(#(#names),*) => {
                            output.push_str(#variant_name);
                            output.push('{');
                            #(#writes)*
                            output.push('}');
                        }
                    }
                }
                Fields::Unit => quote::quote! {
                    Self::#variant_ident => output.push_str(#variant_name)
                },
            }
        })
        .collect::<Vec<_>>();
    syn::parse2(quote::quote! {
        impl #impl_generics ::behaviordiff_rust_runtime::Canonicalize for #ident #type_generics #where_clause {
            fn write_canonical(
                &self,
                context: &mut ::behaviordiff_rust_runtime::CanonicalContext,
                output: &mut String,
            ) {
                context.write_value(stringify!(#ident), output, |context, output| {
                    output.push_str(stringify!(#ident));
                    output.push(':');
                    match self { #(#arms),* }
                });
            }
        }
    })
    .map_err(|error| error.to_string())
}

fn add_canonical_bounds(generics: &mut syn::Generics) {
    for parameter in &mut generics.params {
        if let GenericParam::Type(parameter) = parameter {
            parameter
                .bounds
                .push(parse_quote!(::behaviordiff_rust_runtime::Canonicalize));
        }
    }
}

struct ExitHookInstrumenter {
    relative_path: String,
    context: Option<String>,
    instrumented: usize,
    skipped: usize,
}

impl ExitHookInstrumenter {
    fn new(relative_path: String) -> Self {
        Self {
            relative_path,
            context: None,
            instrumented: 0,
            skipped: 0,
        }
    }

    fn instrument(&mut self, signature: &syn::Signature, block: &mut Block) {
        if signature.constness.is_some() || signature.abi.is_some() {
            self.skipped += 1;
            return;
        }
        let method = match &self.context {
            Some(context) => format!("{}::{context}::{}", self.relative_path, signature.ident),
            None => format!("{}::{}", self.relative_path, signature.ident),
        };
        let file = self.relative_path.clone();
        let line = signature.ident.span().start().line as u32;
        let original = block.clone();
        *block = if signature.asyncness.is_some() {
            parse_quote!({
                let __behaviordiff_guard = ::behaviordiff_rust_runtime::enter(#method, #file, #line);
                let __behaviordiff_result = (async move #original).await;
                __behaviordiff_guard.complete();
                __behaviordiff_result
            })
        } else {
            parse_quote!({
                let __behaviordiff_guard = ::behaviordiff_rust_runtime::enter(#method, #file, #line);
                let __behaviordiff_result = (|| #original)();
                __behaviordiff_guard.complete();
                __behaviordiff_result
            })
        };
        self.instrumented += 1;
    }
}

impl VisitMut for ExitHookInstrumenter {
    fn visit_item_fn_mut(&mut self, function: &mut ItemFn) {
        visit_mut::visit_item_fn_mut(self, function);
        self.instrument(&function.sig, &mut function.block);
    }

    fn visit_impl_item_fn_mut(&mut self, function: &mut ImplItemFn) {
        visit_mut::visit_impl_item_fn_mut(self, function);
        self.instrument(&function.sig, &mut function.block);
    }

    fn visit_trait_item_fn_mut(&mut self, function: &mut TraitItemFn) {
        visit_mut::visit_trait_item_fn_mut(self, function);
        if let Some(block) = &mut function.default {
            self.instrument(&function.sig, block);
        }
    }

    fn visit_item_impl_mut(&mut self, item: &mut ItemImpl) {
        let previous = self.context.take();
        let self_type = item.self_ty.to_token_stream().to_string();
        self.context = Some(match &item.trait_ {
            Some((_, path, _)) => format!("{} for {self_type}", path.to_token_stream()),
            None => self_type,
        });
        visit_mut::visit_item_impl_mut(self, item);
        self.context = previous;
    }

    fn visit_item_trait_mut(&mut self, item: &mut ItemTrait) {
        let previous = self.context.take();
        self.context = Some(format!("trait {}", item.ident));
        visit_mut::visit_item_trait_mut(self, item);
        self.context = previous;
    }
}

fn write_runtime(staging: &Path) -> Result<(), String> {
    let runtime = staging.join(".behaviordiff/runtime");
    fs::create_dir_all(runtime.join("src")).map_err(|error| error.to_string())?;
    fs::write(runtime.join("Cargo.toml"), RUNTIME_CARGO).map_err(|error| error.to_string())?;
    fs::write(runtime.join("src/lib.rs"), RUNTIME_SOURCE).map_err(|error| error.to_string())?;
    fs::write(runtime.join("src/canonical.rs"), RUNTIME_CANONICAL)
        .map_err(|error| error.to_string())?;
    Ok(())
}

fn inject_runtime_dependencies(staging: &Path, files: &[PathBuf]) -> Result<(), String> {
    let runtime = staging.join(".behaviordiff/runtime");
    for relative in files
        .iter()
        .filter(|path| path.file_name().is_some_and(|name| name == "Cargo.toml"))
    {
        let manifest = staging.join(relative);
        let text = fs::read_to_string(&manifest).map_err(|error| error.to_string())?;
        let mut document = text
            .parse::<DocumentMut>()
            .map_err(|error| format!("{}: {error}", manifest.display()))?;
        if !document.contains_key("package") {
            continue;
        }
        let package_directory = manifest.parent().unwrap();
        let dependency_path = pathdiff::diff_paths(&runtime, package_directory)
            .ok_or_else(|| format!("cannot address runtime from {}", manifest.display()))?;
        let mut dependency = InlineTable::new();
        dependency.insert("path", Value::from(slash(&dependency_path)));
        document["dependencies"]["behaviordiff-rust-runtime"] =
            TomlItem::Value(Value::InlineTable(dependency));
        fs::write(&manifest, document.to_string()).map_err(|error| error.to_string())?;
    }
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

fn should_instrument(path: &Path) -> bool {
    path.file_name().is_none_or(|name| name != "build.rs")
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
            "struct Answer { value: i32 }\npub fn answer( ) -> i32 { Answer { value: 42 }.value }\n",
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
        assert!(first
            .output
            .join(".behaviordiff/runtime/src/lib.rs")
            .is_file());
        let rewritten = fs::read_to_string(first.output.join("src/lib.rs")).unwrap();
        assert!(rewritten.contains("behaviordiff_rust_runtime::enter"));
        assert!(rewritten.contains("impl ::behaviordiff_rust_runtime::Canonicalize"));

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
