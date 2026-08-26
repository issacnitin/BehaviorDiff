use quote::ToTokens;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use syn::visit_mut::{self, VisitMut};
use syn::{
    parse_quote, Block, Fields, FnArg, GenericArgument, ImplItemFn, Item, ItemEnum, ItemFn,
    ItemImpl, ItemStruct, ItemTrait, ItemUnion, Pat, PathArguments, ReturnType, TraitItemFn, Type,
};
use toml_edit::{DocumentMut, InlineTable, Item as TomlItem, Value};
use walkdir::{DirEntry, WalkDir};

const CACHE_VERSION: &str = "behaviordiff.rust-rewrite-cache/5";
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

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct OriginManifest {
    schema: String,
    package: String,
    source_hash: String,
    source_files: usize,
    rust_files: Vec<OriginFile>,
    instrumented_functions: usize,
    skipped_functions: usize,
    generated_readers: usize,
    members: Vec<OriginMember>,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct OriginFile {
    path: String,
    sha256: String,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct OriginMember {
    method: String,
    file_path: String,
    line: u32,
    status: String,
    skip_reason: Option<String>,
    detail: Option<String>,
    return_kind: String,
    is_test_root: bool,
    generic_template: bool,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FinalizeReport {
    pub events: usize,
    pub discovered_members: usize,
    pub patched_members: usize,
    pub skipped_members: usize,
    pub values_digested: u64,
    pub blocklisted: u64,
    pub manifest: PathBuf,
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

    let package = package_name(&source)?;
    let result = build_cache_entry(&source, &files, &package, &source_hash, &staging);
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
    package: &str,
    source_hash: &str,
    staging: &Path,
) -> Result<(), String> {
    let mut origins = Vec::new();
    let mut instrumented_functions = 0;
    let mut skipped_functions = 0;
    let mut generated_readers = 0;
    let mut members = Vec::new();
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
                let local_types = local_type_names(&syntax.items);
                let mut instrumenter =
                    ExitHookInstrumenter::new(slash(relative), local_types.clone());
                instrumenter.visit_file_mut(&mut syntax);
                if instrumenter.members.is_empty() {
                    let stem = relative
                        .file_stem()
                        .and_then(|value| value.to_str())
                        .unwrap_or("source");
                    instrumenter.members.push(OriginMember {
                        method: format!("{stem}.__file_boundary()"),
                        file_path: slash(relative),
                        line: 1,
                        status: "Skipped".to_owned(),
                        skip_reason: Some("Unobservable".to_owned()),
                        detail: Some("Rust: NoCallableSourceFile".to_owned()),
                        return_kind: "void".to_owned(),
                        is_test_root: false,
                        generic_template: false,
                    });
                    instrumenter.skipped += 1;
                }
                instrumented_functions += instrumenter.instrumented;
                skipped_functions += instrumenter.skipped;
                members.extend(instrumenter.members);
                generated_readers += generate_readers(&mut syntax.items, &local_types)?;
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
        schema: CACHE_VERSION.to_owned(),
        package: package.to_owned(),
        source_hash: source_hash.to_owned(),
        source_files: files.len(),
        rust_files: origins,
        instrumented_functions,
        skipped_functions,
        generated_readers,
        members,
    };
    let manifest_text = serde_json::to_vec_pretty(&manifest).map_err(|error| error.to_string())?;
    fs::write(staging.join(ORIGIN_MANIFEST), manifest_text).map_err(|error| error.to_string())?;
    Ok(())
}

pub fn finalize(origin: &Path, trace: &Path, output: &Path) -> Result<FinalizeReport, String> {
    let origin: OriginManifest =
        serde_json::from_slice(&fs::read(origin).map_err(|error| error.to_string())?)
            .map_err(|error| error.to_string())?;
    if origin.schema != CACHE_VERSION || origin.members.is_empty() {
        return Err(format!(
            "Rust origin inventory is invalid or empty: schema={} members={}",
            origin.schema,
            origin.members.len()
        ));
    }
    let trace_text = fs::read_to_string(trace).map_err(|error| error.to_string())?;
    let events = trace_text
        .lines()
        .filter(|line| !line.trim().is_empty())
        .map(|line| {
            serde_json::from_str::<serde_json::Value>(line).map_err(|error| error.to_string())
        })
        .collect::<Result<Vec<_>, _>>()?;
    if events.is_empty() {
        return Err(format!("Rust trace is empty: {}", trace.display()));
    }

    let observed_methods = events
        .iter()
        .filter_map(|event| {
            event
                .get("methodFullName")
                .and_then(serde_json::Value::as_str)
        })
        .collect::<HashSet<_>>();
    let mut manifest_members = Vec::new();
    for member in &origin.members {
        if member.generic_template && member.status == "Patched" {
            let prefix = format!("{}<", member.method);
            let concrete = observed_methods
                .iter()
                .filter(|method| method.starts_with(&prefix))
                .copied()
                .collect::<Vec<_>>();
            let mut template = member.clone();
            template.status = "Skipped".to_owned();
            template.skip_reason = Some("Unobservable".to_owned());
            template.detail = Some("Rust: GenericTemplate".to_owned());
            manifest_members.push(template);
            for method in concrete {
                let mut concrete_member = member.clone();
                concrete_member.method = method.to_owned();
                concrete_member.generic_template = false;
                manifest_members.push(concrete_member);
            }
        } else {
            manifest_members.push(member.clone());
        }
    }
    let patched_members = manifest_members
        .iter()
        .filter(|item| item.status == "Patched")
        .count();
    let skipped_members = manifest_members.len() - patched_members;
    let mut values_digested = 0_u64;
    let mut depth_limited = 0_u64;
    let mut blocklisted = 0_u64;
    let mut rendered_truncated = 0_u64;
    for event in &events {
        for prefix in ["args", "return"] {
            values_digested += event_counter(event, &format!("{prefix}ValuesDigested"));
            depth_limited += event_counter(event, &format!("{prefix}DepthLimited"));
            blocklisted += event_counter(event, &format!("{prefix}Blocklisted"));
            rendered_truncated += event_counter(event, &format!("{prefix}RenderedTruncated"));
        }
    }

    let mut lines = Vec::new();
    lines.push(serde_json::json!({
        "kind": "run", "schema": "behaviordiff.trace/1", "language": "rust"
    }));
    lines.push(serde_json::json!({
        "kind": "assembly", "assembly": origin.package, "discovery": "RustAstRewrite",
        "scanned": true, "instrumented": patched_members > 0,
        "patchedMembers": patched_members, "discoveredMembers": manifest_members.len(),
        "skippedMembers": skipped_members, "patchFailedMembers": 0,
        "queuedAtMs": 0, "patchedAtMs": 0, "tracedCalls": events.len(),
        "membersWithExactSource": patched_members, "exactSourcePercent": 100,
        "sourceRule": "ratio", "sourceUnavailable": false, "sourcePartial": false,
        "isTestAssembly": false, "detail": "Rust: stable cached AST rewrite"
    }));
    for member in &manifest_members {
        lines.push(serde_json::json!({
            "kind": "member", "assembly": origin.package, "method": member.method,
            "status": member.status, "skipReason": member.skip_reason,
            "detail": member.detail, "returnKind": member.return_kind,
            "isTestRoot": member.is_test_root, "sourceResolution": "debugInfo",
            "filePath": member.file_path, "line": member.line
        }));
    }
    lines.push(serde_json::json!({
        "kind": "digest", "valuesDigested": values_digested,
        "depthLimited": depth_limited, "blocklisted": blocklisted,
        "errored": 0, "renderedTruncated": rendered_truncated,
        "unreadableFields": 0, "ambiguousMapEntries": 0
    }));
    lines.push(serde_json::json!({
        "kind": "writer", "enqueued": events.len(), "written": events.len(),
        "dropped": 0, "capacity": 65536
    }));
    if let Some(parent) = output.parent() {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    let text = lines
        .iter()
        .map(|line| serde_json::to_string(line).map_err(|error| error.to_string()))
        .collect::<Result<Vec<_>, _>>()?
        .join("\n")
        + "\n";
    fs::write(output, text).map_err(|error| error.to_string())?;
    Ok(FinalizeReport {
        events: events.len(),
        discovered_members: manifest_members.len(),
        patched_members,
        skipped_members,
        values_digested,
        blocklisted,
        manifest: user_path(output),
    })
}

fn event_counter(event: &serde_json::Value, name: &str) -> u64 {
    event
        .get(name)
        .and_then(serde_json::Value::as_u64)
        .unwrap_or(0)
}

fn package_name(source: &Path) -> Result<String, String> {
    let text = fs::read_to_string(source.join("Cargo.toml")).map_err(|error| error.to_string())?;
    let document = text
        .parse::<DocumentMut>()
        .map_err(|error| error.to_string())?;
    document["package"]["name"]
        .as_str()
        .filter(|name| !name.is_empty())
        .map(str::to_owned)
        .ok_or_else(|| "Rust source Cargo.toml has no package.name".to_owned())
}

fn generate_readers(items: &mut Vec<Item>, local_types: &HashSet<String>) -> Result<usize, String> {
    let mut generated = 0;
    for item in items.iter_mut() {
        if let Item::Mod(module) = item {
            if let Some((_, nested)) = &mut module.content {
                generated += generate_readers(nested, local_types)?;
            }
        }
    }

    let mut output = Vec::with_capacity(items.len() * 2);
    for item in std::mem::take(items) {
        let reader = match &item {
            Item::Struct(structure) => Some(struct_reader(structure, local_types)?),
            Item::Enum(enumeration) => Some(enum_reader(enumeration, local_types)?),
            Item::Union(union) => Some(union_reader(union)?),
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

fn struct_reader(item: &ItemStruct, local_types: &HashSet<String>) -> Result<ItemImpl, String> {
    let ident = &item.ident;
    let generics = item.generics.clone();
    let generic_names = generic_type_names(&generics);
    let (impl_generics, type_generics, where_clause) = generics.split_for_impl();
    let fields = match &item.fields {
        Fields::Named(fields) => fields
            .named
            .iter()
            .map(|field| {
                let name = field.ident.as_ref().unwrap();
                let label = name.to_string();
                field_write(
                    &label,
                    quote::quote!(&self.#name),
                    &field.ty,
                    local_types,
                    &generic_names,
                )
            })
            .collect::<Vec<_>>(),
        Fields::Unnamed(fields) => fields
            .unnamed
            .iter()
            .enumerate()
            .map(|(index, field)| {
                let member = syn::Index::from(index);
                let label = index.to_string();
                field_write(
                    &label,
                    quote::quote!(&self.#member),
                    &field.ty,
                    local_types,
                    &generic_names,
                )
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

fn enum_reader(item: &ItemEnum, local_types: &HashSet<String>) -> Result<ItemImpl, String> {
    let ident = &item.ident;
    let generics = item.generics.clone();
    let generic_names = generic_type_names(&generics);
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
                    let writes = names.iter().zip(fields.named.iter()).map(|(name, field)| {
                        let label = name.to_string();
                        field_write(
                            &label,
                            quote::quote!(#name),
                            &field.ty,
                            local_types,
                            &generic_names,
                        )
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
                    let writes = names.iter().zip(fields.unnamed.iter()).enumerate().map(
                        |(index, (name, field))| {
                            let label = index.to_string();
                            field_write(
                                &label,
                                quote::quote!(#name),
                                &field.ty,
                                local_types,
                                &generic_names,
                            )
                        },
                    );
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

fn union_reader(item: &ItemUnion) -> Result<ItemImpl, String> {
    let ident = &item.ident;
    let generics = item.generics.clone();
    let (impl_generics, type_generics, where_clause) = generics.split_for_impl();
    syn::parse2(quote::quote! {
        impl #impl_generics ::behaviordiff_rust_runtime::Canonicalize for #ident #type_generics #where_clause {
            fn write_canonical(
                &self,
                context: &mut ::behaviordiff_rust_runtime::CanonicalContext,
                output: &mut String,
            ) {
                context.write_skipped(output, concat!("union:", stringify!(#ident)));
            }
        }
    })
    .map_err(|error| error.to_string())
}

fn field_write(
    label: &str,
    value: proc_macro2::TokenStream,
    ty: &Type,
    local_types: &HashSet<String>,
    generic_names: &HashSet<String>,
) -> proc_macro2::TokenStream {
    if exact_type(ty, local_types, generic_names) {
        quote::quote! {
            ::behaviordiff_rust_runtime::write_field(context, output, #label, #value);
        }
    } else {
        let detail = skipped_type_detail(ty, generic_names);
        quote::quote! {
            output.push_str(#label);
            output.push('=');
            context.write_skipped(output, #detail);
            output.push(';');
        }
    }
}

fn local_type_names(items: &[Item]) -> HashSet<String> {
    let mut names = HashSet::new();
    for item in items {
        match item {
            Item::Struct(item) => {
                names.insert(item.ident.to_string());
            }
            Item::Enum(item) => {
                names.insert(item.ident.to_string());
            }
            Item::Union(item) => {
                names.insert(item.ident.to_string());
            }
            Item::Mod(module) => {
                if let Some((_, nested)) = &module.content {
                    names.extend(local_type_names(nested));
                }
            }
            _ => {}
        }
    }
    names
}

fn generic_type_names(generics: &syn::Generics) -> HashSet<String> {
    generics
        .type_params()
        .map(|item| item.ident.to_string())
        .collect()
}

fn exact_type(ty: &Type, local_types: &HashSet<String>, generic_names: &HashSet<String>) -> bool {
    match ty {
        Type::Reference(reference) => exact_type(&reference.elem, local_types, generic_names),
        Type::Slice(slice) => exact_type(&slice.elem, local_types, generic_names),
        Type::Array(array) => exact_type(&array.elem, local_types, generic_names),
        Type::Tuple(tuple) => tuple
            .elems
            .iter()
            .all(|item| exact_type(item, local_types, generic_names)),
        Type::Never(_) => false,
        Type::Path(path) if path.qself.is_none() => {
            let Some(segment) = path.path.segments.last() else {
                return false;
            };
            let name = segment.ident.to_string();
            if generic_names.contains(&name) {
                return false;
            }
            let scalar = matches!(
                name.as_str(),
                "bool"
                    | "char"
                    | "str"
                    | "String"
                    | "i8"
                    | "i16"
                    | "i32"
                    | "i64"
                    | "i128"
                    | "isize"
                    | "u8"
                    | "u16"
                    | "u32"
                    | "u64"
                    | "u128"
                    | "usize"
                    | "f32"
                    | "f64"
            );
            if scalar {
                return true;
            }
            let supported = matches!(
                name.as_str(),
                "Option"
                    | "Result"
                    | "Vec"
                    | "VecDeque"
                    | "HashMap"
                    | "HashSet"
                    | "BTreeMap"
                    | "BTreeSet"
                    | "Box"
                    | "Rc"
                    | "Arc"
                    | "Weak"
                    | "SystemTime"
                    | "Instant"
            );
            if local_types.contains(&name) {
                return !matches!(name.as_str(), "union");
            }
            if !supported {
                return false;
            }
            match &segment.arguments {
                PathArguments::AngleBracketed(arguments) => {
                    arguments.args.iter().all(|argument| match argument {
                        GenericArgument::Type(ty) => exact_type(ty, local_types, generic_names),
                        GenericArgument::Lifetime(_) | GenericArgument::Const(_) => true,
                        _ => false,
                    })
                }
                PathArguments::None => false,
                PathArguments::Parenthesized(_) => false,
            }
        }
        _ => false,
    }
}

fn skipped_type_detail(ty: &Type, generic_names: &HashSet<String>) -> String {
    let text = ty.to_token_stream().to_string();
    if matches!(ty, Type::TraitObject(_)) {
        format!("trait-object:{text}")
    } else if generic_names.iter().any(|name| {
        text.split(|c: char| !c.is_alphanumeric() && c != '_')
            .any(|part| part == name)
    }) {
        format!("generic:{text}")
    } else {
        format!("external:{text}")
    }
}

struct ExitHookInstrumenter {
    relative_path: String,
    context: Option<String>,
    context_generics: HashSet<String>,
    local_types: HashSet<String>,
    instrumented: usize,
    skipped: usize,
    members: Vec<OriginMember>,
}

impl ExitHookInstrumenter {
    fn new(relative_path: String, local_types: HashSet<String>) -> Self {
        Self {
            relative_path,
            context: None,
            context_generics: HashSet::new(),
            local_types,
            instrumented: 0,
            skipped: 0,
            members: Vec::new(),
        }
    }

    fn instrument(&mut self, signature: &syn::Signature, block: &mut Block, is_test_root: bool) {
        let method = match &self.context {
            Some(context) => format!("{}::{context}::{}", self.relative_path, signature.ident),
            None => format!("{}::{}", self.relative_path, signature.ident),
        };
        let file = self.relative_path.clone();
        let line = signature.ident.span().start().line as u32;
        let mut generic_names = self.context_generics.clone();
        generic_names.extend(generic_type_names(&signature.generics));
        if signature.constness.is_some() || signature.abi.is_some() {
            self.skipped += 1;
            self.members.push(OriginMember {
                method,
                file_path: file,
                line,
                status: "Skipped".to_owned(),
                skip_reason: Some("UnsupportedShape".to_owned()),
                detail: Some("Rust: ConstOrExternFunction".to_owned()),
                return_kind: return_kind(&signature.output),
                is_test_root,
                generic_template: false,
            });
            return;
        }
        self.members.push(OriginMember {
            method: method.clone(),
            file_path: file.clone(),
            line,
            status: "Patched".to_owned(),
            skip_reason: None,
            detail: None,
            return_kind: return_kind(&signature.output),
            is_test_root,
            generic_template: !generic_names.is_empty(),
        });
        let method_expression = method_expression(&method, &generic_names);
        let args = argument_capture(signature, &self.local_types, &generic_names);
        let result = return_capture(&signature.output, &self.local_types, &generic_names);
        let original = block.clone();
        *block = if signature.asyncness.is_some() {
            parse_quote!({
                let __behaviordiff_args = ::behaviordiff_rust_runtime::capture_arguments(vec![#(#args),*]);
                let __behaviordiff_guard = ::behaviordiff_rust_runtime::enter(#method_expression, #file, #line, __behaviordiff_args, #is_test_root);
                let (__behaviordiff_guard, __behaviordiff_result) =
                    ::behaviordiff_rust_runtime::trace_future(
                        __behaviordiff_guard,
                        async move #original,
                    ).await;
                let __behaviordiff_return = #result;
                __behaviordiff_guard.complete(__behaviordiff_return);
                __behaviordiff_result
            })
        } else {
            parse_quote!({
                let __behaviordiff_args = ::behaviordiff_rust_runtime::capture_arguments(vec![#(#args),*]);
                let __behaviordiff_guard = ::behaviordiff_rust_runtime::enter(#method_expression, #file, #line, __behaviordiff_args, #is_test_root);
                let __behaviordiff_result = (|| #original)();
                let __behaviordiff_return = #result;
                __behaviordiff_guard.complete(__behaviordiff_return);
                __behaviordiff_result
            })
        };
        self.instrumented += 1;
    }
}

fn return_kind(output: &ReturnType) -> String {
    match output {
        ReturnType::Default => "void".to_owned(),
        ReturnType::Type(_, ty) => ty.to_token_stream().to_string(),
    }
}

fn method_expression(method: &str, generic_names: &HashSet<String>) -> proc_macro2::TokenStream {
    if generic_names.is_empty() {
        quote::quote!(#method.to_owned())
    } else {
        let mut names = generic_names.iter().cloned().collect::<Vec<_>>();
        names.sort();
        let identifiers = names
            .iter()
            .map(|name| quote::format_ident!("{name}"))
            .collect::<Vec<_>>();
        quote::quote!(format!(
            "{}<{}>",
            #method,
            [#(::std::any::type_name::<#identifiers>()),*].join(",")
        ))
    }
}

impl VisitMut for ExitHookInstrumenter {
    fn visit_item_fn_mut(&mut self, function: &mut ItemFn) {
        visit_mut::visit_item_fn_mut(self, function);
        self.instrument(
            &function.sig,
            &mut function.block,
            has_test_attribute(&function.attrs),
        );
    }

    fn visit_impl_item_fn_mut(&mut self, function: &mut ImplItemFn) {
        visit_mut::visit_impl_item_fn_mut(self, function);
        self.instrument(&function.sig, &mut function.block, false);
    }

    fn visit_trait_item_fn_mut(&mut self, function: &mut TraitItemFn) {
        visit_mut::visit_trait_item_fn_mut(self, function);
        if let Some(block) = &mut function.default {
            self.instrument(&function.sig, block, false);
        }
    }

    fn visit_item_impl_mut(&mut self, item: &mut ItemImpl) {
        let previous = self.context.take();
        let previous_generics = std::mem::take(&mut self.context_generics);
        let self_type = item.self_ty.to_token_stream().to_string();
        self.context = Some(match &item.trait_ {
            Some((_, path, _)) => format!("{} for {self_type}", path.to_token_stream()),
            None => self_type,
        });
        self.context_generics = generic_type_names(&item.generics);
        visit_mut::visit_item_impl_mut(self, item);
        self.context = previous;
        self.context_generics = previous_generics;
    }

    fn visit_item_trait_mut(&mut self, item: &mut ItemTrait) {
        let previous = self.context.take();
        self.context = Some(format!("trait {}", item.ident));
        visit_mut::visit_item_trait_mut(self, item);
        self.context = previous;
    }
}

fn has_test_attribute(attributes: &[syn::Attribute]) -> bool {
    attributes
        .iter()
        .any(|attribute| attribute.path().is_ident("test"))
}

fn argument_capture(
    signature: &syn::Signature,
    local_types: &HashSet<String>,
    generic_names: &HashSet<String>,
) -> Vec<proc_macro2::TokenStream> {
    signature
        .inputs
        .iter()
        .map(|argument| match argument {
            FnArg::Receiver(_) => {
                let detail = "receiver";
                quote::quote!(("self", ::behaviordiff_rust_runtime::capture_skipped(#detail)))
            }
            FnArg::Typed(argument) => {
                let Pat::Ident(pattern) = argument.pat.as_ref() else {
                    return quote::quote!((
                        "pattern",
                        ::behaviordiff_rust_runtime::capture_skipped("pattern")
                    ));
                };
                let ident = &pattern.ident;
                let name = ident.to_string();
                if exact_type(&argument.ty, local_types, generic_names) {
                    quote::quote!((#name, ::behaviordiff_rust_runtime::capture(&#ident)))
                } else {
                    let detail = skipped_type_detail(&argument.ty, generic_names);
                    quote::quote!((#name, ::behaviordiff_rust_runtime::capture_skipped(#detail)))
                }
            }
        })
        .collect()
}

fn return_capture(
    output: &ReturnType,
    local_types: &HashSet<String>,
    generic_names: &HashSet<String>,
) -> proc_macro2::TokenStream {
    match output {
        ReturnType::Default => quote::quote!(None),
        ReturnType::Type(_, ty) if exact_type(ty, local_types, generic_names) => {
            quote::quote!(Some(::behaviordiff_rust_runtime::capture(
                &__behaviordiff_result
            )))
        }
        ReturnType::Type(_, ty) => {
            let detail = skipped_type_detail(ty, generic_names);
            quote::quote!(Some(::behaviordiff_rust_runtime::capture_skipped(#detail)))
        }
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
