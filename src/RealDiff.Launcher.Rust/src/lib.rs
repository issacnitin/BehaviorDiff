use serde::{Deserialize, Serialize};
use serde_yaml::Value;
use std::collections::BTreeSet;
use std::ffi::{OsStr, OsString};
use std::fs;
use std::fs::OpenOptions;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

const INVALID: i32 = 3;
const FAILED: i32 = 4;
const LANGUAGES: [&str; 5] = ["dotnet", "java", "node", "go", "rust"];

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(deny_unknown_fields, rename_all(serialize = "PascalCase"))]
struct RepositoryConfig {
    language: Option<String>,
    build: Option<String>,
    test: Option<String>,
    workdir: Option<String>,
    #[serde(default)]
    test_projects: Vec<String>,
    #[serde(default)]
    include_namespaces: Vec<String>,
    #[serde(default)]
    exclude_namespaces: Vec<String>,
    #[serde(default)]
    redaction: RedactionConfig,
    baseline: Option<Value>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(deny_unknown_fields, rename_all(serialize = "PascalCase"))]
struct RedactionConfig {
    #[serde(default)]
    names: Vec<String>,
    #[serde(default)]
    types: Vec<String>,
    #[serde(default)]
    paths: Vec<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct ConfigEnvelope<'a> {
    config_path: &'a Path,
    config: &'a RepositoryConfig,
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
enum Language {
    Dotnet,
    Java,
    Node,
    Go,
    Rust,
}

#[derive(Clone, Debug)]
struct Candidate {
    language: Language,
    path: PathBuf,
}

#[derive(Clone, Debug)]
struct Detection {
    language: Language,
    workdir: String,
    entry_point: String,
    build: String,
    test: String,
    test_projects: Vec<String>,
    include_namespaces: Vec<String>,
    exclude_namespaces: Vec<String>,
    configured: bool,
}

struct LoadedConfig {
    root: PathBuf,
    path: PathBuf,
    value: RepositoryConfig,
    exists: bool,
}

enum Route {
    Detect(PathBuf),
    Managed(Option<PathBuf>),
    Help,
}

pub fn run(args: Vec<OsString>) -> i32 {
    let route = match route(&args) {
        Ok(route) => route,
        Err(message) => {
            eprintln!("{message}");
            return INVALID;
        }
    };

    match route {
        Route::Help => spawn_managed(&args, None),
        Route::Detect(repository) => match detect(&repository) {
            Ok(detection) => {
                println!("{}", render(&detection));
                0
            }
            Err(message) => {
                eprintln!("{message}");
                INVALID
            }
        },
        Route::Managed(repository) => {
            let handoff = match repository {
                Some(repository) => match load_config(&repository) {
                    Ok(config) => match write_handoff(&config) {
                        Ok(path) => Some(path),
                        Err(message) => {
                            eprintln!("{message}");
                            return INVALID;
                        }
                    },
                    Err(message) => {
                        eprintln!("{message}");
                        return INVALID;
                    }
                },
                None => None,
            };
            let exit = spawn_managed(&args, handoff.as_deref());
            if let Some(path) = handoff {
                let _ = fs::remove_file(path);
            }
            exit
        }
    }
}

fn route(args: &[OsString]) -> Result<Route, String> {
    if args.is_empty() || args.iter().any(|arg| arg == "--help" || arg == "-h") {
        return Ok(Route::Help);
    }
    let first = text(&args[0]);
    if first == "detect" || first == "detect-language" {
        return (args.len() == 2)
            .then(|| Route::Detect(PathBuf::from(&args[1])))
            .ok_or_else(|| "usage: realdiff detect <repo>".to_owned());
    }
    if first == "post" || first == "baseline" {
        return Ok(Route::Managed(None));
    }

    let start = usize::from(first == "warm");
    let value_options = [
        "--base",
        "--pr",
        "--target",
        "--ci",
        "--work",
        "--findings",
        "--baseline",
        "--cache-dir",
        "--cache-retention",
        "--keep-traces",
    ];
    let flag_options = ["--no-baseline", "--no-cache", "--keep", "--strict"];
    let mut repository = None;
    let mut ci = None;
    let mut index = start;
    while index < args.len() {
        let argument = text(&args[index]);
        if argument == "--engine" || argument.starts_with("--engine=") {
            return Err("--engine was removed; RealDiff uses the Rust engine.".to_owned());
        }
        if value_options.contains(&argument.as_str()) {
            index += 1;
            if index >= args.len() {
                return Err(format!("{argument} requires a value"));
            }
            if argument == "--ci" {
                ci = Some(text(&args[index]));
            }
        } else if let Some(value) = argument.strip_prefix("--ci=") {
            ci = Some(value.to_owned());
        } else if flag_options.contains(&argument.as_str()) {
        } else if argument.starts_with('-') {
            return Err(format!("Unknown option: {argument}"));
        } else if repository.replace(PathBuf::from(&args[index])).is_some() {
            return Err(format!("Unexpected positional argument: {argument}"));
        }
        index += 1;
    }

    if repository.is_none() {
        repository = match ci.as_deref() {
            Some("github") => std::env::var_os("GITHUB_WORKSPACE").map(PathBuf::from),
            Some("azuredevops") => std::env::var_os("BUILD_SOURCESDIRECTORY")
                .or_else(|| std::env::var_os("SYSTEM_DEFAULTWORKINGDIRECTORY"))
                .map(PathBuf::from),
            _ => None,
        };
    }
    Ok(Route::Managed(repository))
}

fn spawn_managed(args: &[OsString], handoff: Option<&Path>) -> i32 {
    let managed = match managed_path() {
        Ok(path) => path,
        Err(message) => {
            eprintln!("{message}");
            return FAILED;
        }
    };
    let mut command = Command::new(managed);
    command.args(args);
    if let Some(path) = handoff {
        command.env("REALDIFF_LAUNCHER_CONFIG", path);
    }
    match command.status() {
        Ok(status) => status.code().unwrap_or(FAILED),
        Err(error) => {
            eprintln!("RealDiff managed component could not start: {error}");
            FAILED
        }
    }
}

fn managed_path() -> Result<PathBuf, String> {
    if let Some(configured) = std::env::var_os("REALDIFF_MANAGED_CLI") {
        let path = PathBuf::from(configured);
        return path.is_file().then_some(path).ok_or_else(|| {
            "REALDIFF_MANAGED_CLI does not name an existing executable.".to_owned()
        });
    }
    let directory = std::env::current_exe()
        .map_err(|error| format!("Could not locate the RealDiff launcher: {error}"))?
        .parent()
        .ok_or_else(|| "RealDiff launcher has no parent directory.".to_owned())?
        .to_owned();
    let name = if cfg!(windows) {
        "realdiff-managed.exe"
    } else {
        "realdiff-managed"
    };
    let path = directory.join(name);
    path.is_file().then_some(path.clone()).ok_or_else(|| {
        format!(
            "RealDiff managed component was not found: {}",
            path.display()
        )
    })
}

fn write_handoff(config: &LoadedConfig) -> Result<PathBuf, String> {
    let envelope = ConfigEnvelope {
        config_path: &config.path,
        config: &config.value,
    };
    let json = serde_json::to_vec(&envelope)
        .map_err(|error| format!("Could not serialize RealDiff config: {error}"))?;
    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|error| format!("Could not timestamp RealDiff config handoff: {error}"))?
        .as_nanos();
    for attempt in 0..10 {
        let path = std::env::temp_dir().join(format!(
            "realdiff-launcher-config-{}-{nonce}-{attempt}.json",
            std::process::id()
        ));
        let mut options = OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.mode(0o600);
        }
        match options.open(&path) {
            Ok(mut file) => {
                file.write_all(&json).map_err(|error| {
                    format!("Could not write RealDiff config handoff: {error}")
                })?;
                return Ok(path);
            }
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(error) => {
                return Err(format!(
                    "Could not create RealDiff config handoff: {error}"
                ));
            }
        }
    }
    Err("Could not allocate a unique RealDiff config handoff file.".to_owned())
}

fn load_config(repository: &Path) -> Result<LoadedConfig, String> {
    let root = repository.canonicalize().map_err(|error| {
        format!(
            "Repository does not exist: {} ({error})",
            repository.display()
        )
    })?;
    if !root.is_dir() {
        return Err(format!("Repository is not a directory: {}", root.display()));
    }
    let path = root.join(".realdiff").join("config.yml");
    let exists = path.is_file();
    let value = if exists {
        let content = fs::read_to_string(&path)
            .map_err(|error| format!("Could not read RealDiff config: {error}"))?;
        if content.trim().is_empty() {
            return Err(format!("RealDiff config is empty: {}", path.display()));
        }
        serde_yaml::from_str(&content)
            .map_err(|error| format!("RealDiff config YAML is malformed: {error}"))?
    } else {
        RepositoryConfig::default()
    };
    validate_config(&value, &path)?;
    Ok(LoadedConfig {
        root,
        path,
        value,
        exists,
    })
}

fn validate_config(config: &RepositoryConfig, path: &Path) -> Result<(), String> {
    if let Some(language) = config.language.as_deref() {
        if !LANGUAGES.contains(&language.trim().to_ascii_lowercase().as_str()) {
            return Err(format!(
                "Unsupported language '{language}' in {}. Expected dotnet, java, node, go, or rust.",
                path.display()
            ));
        }
    }
    for (name, values) in [
        ("test_projects", &config.test_projects),
        ("include_namespaces", &config.include_namespaces),
        ("exclude_namespaces", &config.exclude_namespaces),
        ("redaction.names", &config.redaction.names),
        ("redaction.types", &config.redaction.types),
        ("redaction.paths", &config.redaction.paths),
    ] {
        if values.iter().any(|value| value.trim().is_empty()) {
            return Err(format!(
                "RealDiff config {name} contains an empty value."
            ));
        }
    }
    Ok(())
}

fn detect(repository: &Path) -> Result<Detection, String> {
    let config = load_config(repository)?;
    let workdir = resolve_workdir(&config)?;
    let mut candidates = scan_candidates(&workdir, false)?;
    if candidates.is_empty() {
        candidates = scan_candidates(&workdir, true)?;
    }
    let configured_language = config
        .value
        .language
        .as_deref()
        .map(parse_language)
        .transpose()?;
    if let Some(language) = configured_language {
        candidates.retain(|candidate| candidate.language == language);
    }
    if candidates.is_empty() && configured_language.is_none() {
        return Err(detection_failure("Could not detect a supported repository language. Expected a solution/project, pom.xml, package.json, go.mod, or Cargo.toml."));
    }

    let (language, marker) = if let Some(language) = configured_language {
        if candidates.len() == 1 {
            (language, Some(candidates.remove(0)))
        } else if candidates.is_empty()
            && config.value.build.as_deref().is_some_and(nonempty)
            && config.value.test.as_deref().is_some_and(nonempty)
        {
            (language, None)
        } else {
            let detail = if candidates.is_empty() {
                format!(
                    "The configured language has no conventional build entry point in workdir '{}'.",
                    relative(&config.root, &workdir)
                )
            } else {
                format!(
                    "The configured workdir contains multiple {} build entry points: {}.",
                    language_name(language),
                    evidence_list(&workdir, &candidates)
                )
            };
            return Err(detection_failure(&detail));
        }
    } else {
        let languages: BTreeSet<_> = candidates.iter().map(|item| item.language).collect();
        if languages.len() != 1 {
            return Err(detection_failure(&format!(
                "Repository language is ambiguous: {}.",
                evidence_list(&workdir, &candidates)
            )));
        }
        if candidates.len() != 1 {
            return Err(detection_failure(&format!(
                "Multiple {} build entry points were found: {}.",
                language_name(candidates[0].language),
                evidence_list(&workdir, &candidates)
            )));
        }
        let marker = candidates.remove(0);
        (marker.language, Some(marker))
    };

    let entry_point = marker
        .as_ref()
        .map(|item| relative(&workdir, &item.path))
        .unwrap_or_else(|| relative(&config.root, &workdir));
    let inferred_tests = if language == Language::Dotnet {
        infer_dotnet_tests(&workdir)
    } else {
        Vec::new()
    };
    let test_projects = configured_or(&config.value.test_projects, inferred_tests);
    let include_namespaces = configured_or(
        &config.value.include_namespaces,
        infer_scope(language, &config.root, &workdir),
    );
    let build = config
        .value
        .build
        .clone()
        .filter(|value| nonempty(value))
        .unwrap_or_else(|| default_build(language, &entry_point));
    let test = config
        .value
        .test
        .clone()
        .filter(|value| nonempty(value))
        .unwrap_or_else(|| default_test(language, &test_projects));
    Ok(Detection {
        language,
        workdir: relative(&config.root, &workdir),
        entry_point,
        build,
        test,
        test_projects,
        include_namespaces,
        exclude_namespaces: config.value.exclude_namespaces,
        configured: config.exists,
    })
}

fn resolve_workdir(config: &LoadedConfig) -> Result<PathBuf, String> {
    let relative = config.value.workdir.as_deref().unwrap_or(".");
    let path = config
        .root
        .join(relative)
        .canonicalize()
        .map_err(|_| format!("RealDiff config workdir does not exist: {relative}"))?;
    if !path.starts_with(&config.root) {
        return Err(format!(
            "RealDiff config workdir escapes the repository root: {relative}"
        ));
    }
    path.is_dir()
        .then_some(path)
        .ok_or_else(|| format!("RealDiff config workdir does not exist: {relative}"))
}

fn scan_candidates(root: &Path, recursive: bool) -> Result<Vec<Candidate>, String> {
    let mut files = Vec::new();
    visit(root, root, recursive, &mut files)?;
    let solutions: Vec<_> = files
        .iter()
        .filter(|path| path.extension() == Some(OsStr::new("sln")))
        .cloned()
        .collect();
    let dotnet = if solutions.is_empty() {
        files
            .iter()
            .filter(|path| path.extension() == Some(OsStr::new("csproj")))
            .cloned()
            .collect()
    } else {
        solutions
    };
    let mut result = Vec::new();
    result.extend(dotnet.into_iter().map(|path| Candidate {
        language: Language::Dotnet,
        path,
    }));
    for (name, language) in [
        ("pom.xml", Language::Java),
        ("package.json", Language::Node),
        ("go.mod", Language::Go),
        ("Cargo.toml", Language::Rust),
    ] {
        result.extend(
            files
                .iter()
                .filter(|path| path.file_name() == Some(OsStr::new(name)))
                .cloned()
                .map(|path| Candidate { language, path }),
        );
    }
    result.sort_by(|left, right| left.path.cmp(&right.path));
    Ok(result)
}

fn visit(
    root: &Path,
    directory: &Path,
    recursive: bool,
    files: &mut Vec<PathBuf>,
) -> Result<(), String> {
    for entry in fs::read_dir(directory)
        .map_err(|error| format!("Could not scan repository {}: {error}", root.display()))?
    {
        let entry = entry.map_err(|error| format!("Could not scan repository: {error}"))?;
        let path = entry.path();
        if path.is_file() {
            files.push(path);
        } else if recursive && path.is_dir() && !ignored(&path) {
            visit(root, &path, true, files)?;
        }
    }
    Ok(())
}

fn ignored(path: &Path) -> bool {
    matches!(
        path.file_name().and_then(OsStr::to_str),
        Some("bin" | "obj" | "target" | "node_modules" | "dist" | ".git")
    )
}

fn infer_dotnet_tests(root: &Path) -> Vec<String> {
    let mut files = Vec::new();
    if visit(root, root, true, &mut files).is_err() {
        return Vec::new();
    }
    let mut tests: Vec<_> = files
        .into_iter()
        .filter(|path| path.extension() == Some(OsStr::new("csproj")))
        .filter(|path| fs::read_to_string(path).is_ok_and(|text| text.contains("xunit")))
        .map(|path| relative(root, &path))
        .collect();
    tests.sort();
    tests
}

fn infer_scope(language: Language, repository: &Path, workdir: &Path) -> Vec<String> {
    match language {
        Language::Node => ["src", "lib", "app", "dist"]
            .into_iter()
            .filter(|name| workdir.join(name).is_dir())
            .map(str::to_owned)
            .collect(),
        Language::Java => infer_java_packages(workdir),
        Language::Dotnet => infer_dotnet_namespaces(workdir),
        Language::Go | Language::Rust => vec![relative(repository, workdir)],
    }
}

fn infer_java_packages(root: &Path) -> Vec<String> {
    let mut packages = BTreeSet::new();
    for source_root in ["src/main/java", "src/test/java"] {
        let directory = root.join(source_root);
        let mut files = Vec::new();
        if directory.is_dir() && visit(&directory, &directory, true, &mut files).is_ok() {
            for file in files
                .into_iter()
                .filter(|path| path.extension() == Some(OsStr::new("java")))
            {
                if let Ok(text) = fs::read_to_string(file) {
                    if let Some(package) = text.lines().map(str::trim).find_map(|line| {
                        line.strip_prefix("package ")
                            .and_then(|value| value.strip_suffix(';'))
                            .map(str::trim)
                    }) {
                        packages.insert(package.to_owned());
                    }
                }
            }
        }
    }
    packages.into_iter().collect()
}

fn infer_dotnet_namespaces(root: &Path) -> Vec<String> {
    let mut files = Vec::new();
    if visit(root, root, true, &mut files).is_err() {
        return Vec::new();
    }
    let mut names: Vec<_> = files
        .into_iter()
        .filter(|path| path.extension() == Some(OsStr::new("csproj")))
        .filter_map(|path| path.file_stem().and_then(OsStr::to_str).map(str::to_owned))
        .filter(|name| !name.ends_with(".Tests"))
        .collect();
    names.sort();
    names.dedup();
    names
}

fn render(detection: &Detection) -> String {
    let mut output = format!(
        "language: {}\nworkdir: {}\nentry_point: {}\nbuild: {}\ntest: {}\n",
        language_name(detection.language),
        yaml_scalar(&detection.workdir),
        yaml_scalar(&detection.entry_point),
        yaml_scalar(&detection.build),
        yaml_scalar(&detection.test)
    );
    render_list(&mut output, "test_projects", &detection.test_projects);
    render_list(
        &mut output,
        "include_namespaces",
        &detection.include_namespaces,
    );
    render_list(
        &mut output,
        "exclude_namespaces",
        &detection.exclude_namespaces,
    );
    output.push_str("source: ");
    output.push_str(if detection.configured {
        ".realdiff/config.yml + detection"
    } else {
        "auto-detection"
    });
    output
}

fn render_list(output: &mut String, name: &str, values: &[String]) {
    if values.is_empty() {
        output.push_str(&format!("{name}: []\n"));
    } else {
        output.push_str(&format!("{name}:\n"));
        for value in values {
            output.push_str(&format!("- {}\n", yaml_scalar(value)));
        }
    }
}

fn yaml_scalar(value: &str) -> String {
    if value.is_empty()
        || value.starts_with([
            '-', '?', ':', '!', '&', '*', '#', '{', '[', '|', '>', '@', '`',
        ])
    {
        format!("'{}'", value.replace('\'', "''"))
    } else {
        value.to_owned()
    }
}

fn default_build(language: Language, entry: &str) -> String {
    match language {
        Language::Dotnet => format!("dotnet build {} -c Release --nologo", quote(entry)),
        Language::Java => "mvn --batch-mode --no-transfer-progress package -DskipTests".to_owned(),
        Language::Node => "npm ci && npm run build --if-present".to_owned(),
        Language::Go => "go build ./...".to_owned(),
        Language::Rust => "cargo build".to_owned(),
    }
}

fn default_test(language: Language, projects: &[String]) -> String {
    match language {
        Language::Dotnet if !projects.is_empty() => format!(
            "dotnet test {} -c Release --no-build --nologo",
            projects
                .iter()
                .map(|value| quote(value))
                .collect::<Vec<_>>()
                .join(" ")
        ),
        Language::Dotnet => "dotnet test -c Release --no-build --nologo".to_owned(),
        Language::Java => "mvn --batch-mode --no-transfer-progress test".to_owned(),
        Language::Node => "npm test".to_owned(),
        Language::Go => "go test ./...".to_owned(),
        Language::Rust => "cargo test -- --test-threads=1".to_owned(),
    }
}

fn configured_or(configured: &[String], inferred: Vec<String>) -> Vec<String> {
    if configured.is_empty() {
        inferred
    } else {
        configured.to_vec()
    }
}

fn evidence_list(root: &Path, candidates: &[Candidate]) -> String {
    candidates
        .iter()
        .map(|candidate| relative(root, &candidate.path))
        .collect::<Vec<_>>()
        .join(", ")
}

fn relative(root: &Path, path: &Path) -> String {
    let value = path
        .strip_prefix(root)
        .unwrap_or(path)
        .to_string_lossy()
        .replace('\\', "/");
    if value.is_empty() {
        ".".to_owned()
    } else {
        value
    }
}

fn parse_language(value: &str) -> Result<Language, String> {
    match value.trim().to_ascii_lowercase().as_str() {
        "dotnet" => Ok(Language::Dotnet),
        "java" => Ok(Language::Java),
        "node" => Ok(Language::Node),
        "go" => Ok(Language::Go),
        "rust" => Ok(Language::Rust),
        _ => Err(format!("Unsupported language: {value}")),
    }
}

fn language_name(language: Language) -> &'static str {
    match language {
        Language::Dotnet => "dotnet",
        Language::Java => "java",
        Language::Node => "node",
        Language::Go => "go",
        Language::Rust => "rust",
    }
}

fn detection_failure(detail: &str) -> String {
    format!(
        "{detail} Run 'realdiff detect <repo>' and write .realdiff/config.yml to resolve it."
    )
}

fn quote(value: &str) -> String {
    if value.contains(' ') {
        format!("\"{value}\"")
    } else {
        value.to_owned()
    }
}

fn nonempty(value: &str) -> bool {
    !value.trim().is_empty()
}

fn text(value: &OsString) -> String {
    value.to_string_lossy().into_owned()
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::tempdir;

    #[test]
    fn detects_all_languages() {
        for (marker, expected) in [
            ("sample.sln", "dotnet"),
            ("pom.xml", "java"),
            ("package.json", "node"),
            ("go.mod", "go"),
            ("Cargo.toml", "rust"),
        ] {
            let directory = tempdir().unwrap();
            fs::write(directory.path().join(marker), "").unwrap();
            let output = render(&detect(directory.path()).unwrap());
            assert!(
                output.contains(&format!("language: {expected}")),
                "{output}"
            );
            assert!(
                output.contains(&format!("entry_point: {marker}")),
                "{output}"
            );
        }
    }

    #[test]
    fn config_overrides_detection() {
        let directory = tempdir().unwrap();
        fs::create_dir_all(directory.path().join("services/web/.realdiff")).unwrap();
        fs::create_dir_all(directory.path().join(".realdiff")).unwrap();
        fs::write(directory.path().join("services/web/package.json"), "").unwrap();
        fs::write(
            directory.path().join(".realdiff/config.yml"),
            "language: node\nworkdir: services/web\nbuild: npm run compile\ntest: npm test\ninclude_namespaces: [src/domain]\n",
        )
        .unwrap();
        let output = render(&detect(directory.path()).unwrap());
        assert!(output.contains("workdir: services/web"));
        assert!(output.contains("build: npm run compile"));
        assert!(output.contains("- src/domain"));
        assert!(output.contains("source: .realdiff/config.yml + detection"));
    }

    #[test]
    fn refuses_ambiguous_repository() {
        let directory = tempdir().unwrap();
        fs::write(directory.path().join("pom.xml"), "").unwrap();
        fs::write(directory.path().join("package.json"), "").unwrap();
        let error = detect(directory.path()).unwrap_err();
        assert!(
            error.contains("Repository language is ambiguous"),
            "{error}"
        );
    }

    #[test]
    fn handoff_uses_managed_property_names() {
        let config = RepositoryConfig {
            test_projects: vec!["tests/**/*.csproj".to_owned()],
            include_namespaces: vec!["Acme".to_owned()],
            redaction: RedactionConfig {
                names: vec!["password".to_owned()],
                ..RedactionConfig::default()
            },
            ..RepositoryConfig::default()
        };
        let envelope = ConfigEnvelope {
            config_path: Path::new(".realdiff/config.yml"),
            config: &config,
        };
        let json = serde_json::to_string(&envelope).unwrap();
        assert!(json.contains("\"TestProjects\""), "{json}");
        assert!(json.contains("\"IncludeNamespaces\""), "{json}");
        assert!(json.contains("\"Redaction\":{\"Names\""), "{json}");
        assert!(!json.contains("test_projects"), "{json}");
    }

    #[test]
    fn routes_analysis_repository_after_options() {
        let args = ["--base", "main", "--pr", "HEAD", "repository", "--strict"]
            .into_iter()
            .map(OsString::from)
            .collect::<Vec<_>>();
        match route(&args).unwrap() {
            Route::Managed(Some(repository)) => assert_eq!(repository, Path::new("repository")),
            _ => panic!("analysis was not routed to the managed component"),
        }
    }

    #[test]
    fn refuses_missing_option_value() {
        let error = route(&[OsString::from("repository"), OsString::from("--base")])
            .err()
            .unwrap();
        assert_eq!(error, "--base requires a value");
    }
}
