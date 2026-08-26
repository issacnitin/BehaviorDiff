use std::env;

mod baseline;
mod dotnet_json;
mod engine;
mod findings;
mod frontier;
mod loader;
mod matcher;
mod model;
mod stream_diff;
mod stream_probe;
mod trace_io;

const EXIT_OK: i32 = 0;
const EXIT_USAGE: i32 = 2;

#[derive(Debug, Default, PartialEq, Eq)]
pub(crate) struct DiffOptions {
    pub(crate) base1: String,
    pub(crate) base2: String,
    pub(crate) base3: String,
    pub(crate) changed_files: String,
    pub(crate) pr: String,
    pub(crate) base_root: Option<String>,
    pub(crate) pr_root: Option<String>,
    pub(crate) output: String,
}

fn main() {
    std::process::exit(run(env::args().skip(1).collect()));
}

fn run(args: Vec<String>) -> i32 {
    let Some(command) = args.first() else {
        print_usage();
        return EXIT_USAGE;
    };

    match command.as_str() {
        "--help" | "-h" => {
            print_usage();
            EXIT_OK
        }
        "diff" => match parse_diff_options(&args[1..]) {
            Ok(options) => match engine::run_diff(&options) {
                Ok(exit_code) => exit_code,
                Err(message) => {
                    eprintln!("Input error: {message}");
                    EXIT_USAGE
                }
            },
            Err(message) => {
                eprintln!("{message}");
                EXIT_USAGE
            }
        },
        "stream-probe" => match parse_diff_options(&args[1..]) {
            Ok(options) if !options.base3.is_empty() => match stream_probe::run(&options) {
                Ok(()) => EXIT_OK,
                Err(message) => {
                    eprintln!("Input error: {message}");
                    EXIT_USAGE
                }
            },
            Ok(_) => {
                eprintln!("stream-probe requires --base3");
                EXIT_USAGE
            }
            Err(message) => {
                eprintln!("{message}");
                EXIT_USAGE
            }
        },
        "stream-diff" => match parse_diff_options(&args[1..]) {
            Ok(options) => match stream_diff::run(&options) {
                Ok(exit_code) => exit_code,
                Err(message) => {
                    eprintln!("Input error: {message}");
                    EXIT_USAGE
                }
            },
            Err(message) => {
                eprintln!("{message}");
                EXIT_USAGE
            }
        },
        "frontier" => match parse_frontier_options(&args[1..]) {
            Ok(options) => match frontier::run(&options) {
                Ok(exit_code) => exit_code,
                Err(message) => {
                    eprintln!("Input error: {message}");
                    EXIT_USAGE
                }
            },
            Err(message) => {
                eprintln!("{message}");
                EXIT_USAGE
            }
        },
        "findings" => match parse_findings_options(&args[1..]) {
            Ok(options) => match findings::run(&options) {
                Ok(exit_code) => exit_code,
                Err(message) => {
                    eprintln!("Input error: {message}");
                    EXIT_USAGE
                }
            },
            Err(message) => {
                eprintln!("{message}");
                EXIT_USAGE
            }
        },
        "findings-invalid" => match parse_invalid_findings_options(&args[1..]) {
            Ok(options) => match findings::write_invalid(&options) {
                Ok(exit_code) => exit_code,
                Err(message) => {
                    eprintln!("Input error: {message}");
                    EXIT_USAGE
                }
            },
            Err(message) => {
                eprintln!("{message}");
                EXIT_USAGE
            }
        },
        "baseline" => match parse_baseline_options(&args[1..]) {
            Ok(options) => match baseline::run(&options) {
                Ok(exit_code) => exit_code,
                Err(message) => {
                    eprintln!("Input error: {message}");
                    EXIT_USAGE
                }
            },
            Err(message) => {
                eprintln!("{message}");
                EXIT_USAGE
            }
        },
        "baseline-validate" => match parse_baseline_validate_options(&args[1..]) {
            Ok(path) => match baseline::validate_file(&path) {
                Ok(exit_code) => exit_code,
                Err(message) => {
                    eprintln!("Input error: {message}");
                    EXIT_USAGE
                }
            },
            Err(message) => {
                eprintln!("{message}");
                EXIT_USAGE
            }
        },
        "read" => match args.as_slice() {
            [_, path] => match trace_io::read(path) {
                Ok(exit_code) => exit_code,
                Err(message) => {
                    eprintln!("Input error: {message}");
                    EXIT_USAGE
                }
            },
            _ => {
                eprintln!("usage: read <trace.ndjson>");
                EXIT_USAGE
            }
        },
        "normalize" => match parse_normalize_options(&args[1..]) {
            Ok((input, output, force)) => match trace_io::normalize(&input, &output, force) {
                Ok(exit_code) => exit_code,
                Err(message) => {
                    eprintln!("Input error: {message}");
                    EXIT_USAGE
                }
            },
            Err(message) => {
                eprintln!("{message}");
                EXIT_USAGE
            }
        },
        _ => {
            eprintln!("Unknown command '{command}'.");
            print_usage();
            EXIT_USAGE
        }
    }
}

fn parse_normalize_options(args: &[String]) -> Result<(String, String, bool), String> {
    let Some(input) = args.first() else {
        return Err("usage: normalize <trace.ndjson> -o <output.ndjson> [--force]".to_owned());
    };
    let mut output = String::new();
    let mut force = false;
    let mut index = 1;
    while index < args.len() {
        match args[index].as_str() {
            "--force" => {
                force = true;
                index += 1;
            }
            "-o" | "--out" => {
                output = args
                    .get(index + 1)
                    .ok_or_else(|| format!("Missing value for {}", args[index]))?
                    .clone();
                index += 2;
            }
            option => return Err(format!("Unknown option {option}")),
        }
    }
    if output.is_empty() {
        return Err("usage: normalize <trace.ndjson> -o <output.ndjson> [--force]".to_owned());
    }
    Ok((input.clone(), output, force))
}

fn parse_baseline_validate_options(args: &[String]) -> Result<String, String> {
    match args {
        [option, path] if option == "--baseline" => Ok(path.clone()),
        _ => Err("usage: baseline-validate --baseline <baseline.yml>".to_owned()),
    }
}

fn parse_invalid_findings_options(
    args: &[String],
) -> Result<findings::InvalidFindingsOptions, String> {
    let mut options = findings::InvalidFindingsOptions::default();
    let mut index = 0;
    while index < args.len() {
        let option = &args[index];
        let Some(value) = args.get(index + 1) else {
            return Err(format!("Missing value for {option}"));
        };
        match option.as_str() {
            "--out" => options.output = value.clone(),
            "--status" => options.status = value.clone(),
            "--reason" => options.reason = value.clone(),
            "--exit-code" => options.exit_code = parse_number(option, value)?,
            "--base-sha" => options.base_sha = Some(value.clone()),
            "--pr-sha" => options.pr_sha = Some(value.clone()),
            "--merge-base" => options.merge_base = Some(value.clone()),
            _ => return Err(format!("Unknown option {option}")),
        }
        index += 2;
    }
    if options.output.is_empty() || options.status.is_empty() || options.reason.is_empty() {
        return Err("usage: findings-invalid --status <refused|failed> --exit-code <code> --reason <text> --out <findings.json>".to_owned());
    }
    Ok(options)
}

fn parse_baseline_options(args: &[String]) -> Result<baseline::BaselineOptions, String> {
    let mut options = baseline::BaselineOptions::default();
    let mut index = 0;
    while index < args.len() {
        let option = &args[index];
        let Some(value) = args.get(index + 1) else {
            return Err(format!("Missing value for {option}"));
        };
        match option.as_str() {
            "--findings" => options.findings = value.clone(),
            "--baseline" => options.baseline = value.clone(),
            _ => return Err(format!("Unknown option {option}")),
        }
        index += 2;
    }
    if options.findings.is_empty() || options.baseline.is_empty() {
        return Err(
            "usage: baseline --findings <findings.json> --baseline <baseline.yml>".to_owned(),
        );
    }
    Ok(options)
}

fn parse_findings_options(args: &[String]) -> Result<findings::FindingsOptions, String> {
    let mut options = findings::FindingsOptions::default();
    let mut index = 0;
    while index < args.len() {
        let option = &args[index];
        if option == "--strict" {
            options.strict = true;
            index += 1;
            continue;
        }
        let Some(value) = args.get(index + 1) else {
            return Err(format!("Missing value for {option}"));
        };
        match option.as_str() {
            "--divergences" => options.divergences = value.clone(),
            "--frontier" => options.frontier = value.clone(),
            "--out" => options.output = value.clone(),
            "--exit-code" => options.exit_code = parse_number(option, value)?,
            "--base-sha" => options.base_sha = value.clone(),
            "--pr-sha" => options.pr_sha = value.clone(),
            "--merge-base" => options.merge_base = value.clone(),
            "--cache-status" => options.cache_status = value.clone(),
            "--cache-key" => options.cache_key = value.clone(),
            "--cache-backend" => options.cache_backend = value.clone(),
            "--cache-saved-ms" => {
                options.cache_saved_wall_clock_milliseconds = parse_number(option, value)?
            }
            "--build-ms" => options.build_milliseconds = parse_number(option, value)?,
            "--weave-ms" => options.weave_milliseconds = parse_number(option, value)?,
            "--run-ms" => options.instrumented_run_milliseconds = parse_number(option, value)?,
            "--cache-restore-ms" => {
                options.cache_restore_milliseconds = parse_number(option, value)?
            }
            "--cache-store-ms" => options.cache_store_milliseconds = parse_number(option, value)?,
            "--diff-ms" => options.diff_milliseconds = parse_number(option, value)?,
            "--frontier-ms" => options.frontier_milliseconds = parse_number(option, value)?,
            _ => return Err(format!("Unknown option {option}")),
        }
        index += 2;
    }
    if options.divergences.is_empty() || options.frontier.is_empty() || options.output.is_empty() {
        return Err(findings_usage().to_owned());
    }
    Ok(options)
}

fn parse_number<T: std::str::FromStr>(option: &str, value: &str) -> Result<T, String> {
    value
        .parse()
        .map_err(|_| format!("Invalid numeric value for {option}: {value}"))
}

fn parse_frontier_options(args: &[String]) -> Result<frontier::FrontierOptions, String> {
    let mut options = frontier::FrontierOptions::default();
    let mut index = 0;
    while index < args.len() {
        let option = &args[index];
        let Some(value) = args.get(index + 1) else {
            return Err(format!("Missing value for {option}"));
        };
        match option.as_str() {
            "--in" => options.input = value.clone(),
            "--changed-files" => options.changed_files = value.clone(),
            "--out" => options.output = value.clone(),
            _ => return Err(format!("Unknown option {option}")),
        }
        index += 2;
    }
    if options.input.is_empty() || options.output.is_empty() {
        return Err(
            "usage: frontier --in <set.json> --changed-files <paths.txt> --out <report.json>"
                .to_owned(),
        );
    }
    Ok(options)
}

fn parse_diff_options(args: &[String]) -> Result<DiffOptions, String> {
    let mut options = DiffOptions::default();
    let mut index = 0;

    while index < args.len() {
        let option = &args[index];
        let Some(value) = args.get(index + 1) else {
            return Err(format!("Missing value for {option}"));
        };

        match option.as_str() {
            "--base1" => options.base1 = value.clone(),
            "--base2" => options.base2 = value.clone(),
            "--base3" => options.base3 = value.clone(),
            "--changed-files" => options.changed_files = value.clone(),
            "--pr" => options.pr = value.clone(),
            "--base-root" => options.base_root = Some(value.clone()),
            "--pr-root" => options.pr_root = Some(value.clone()),
            "--out" => options.output = value.clone(),
            _ => return Err(format!("Unknown option {option}")),
        }

        index += 2;
    }

    if options.base1.is_empty()
        || options.base2.is_empty()
        || options.pr.is_empty()
        || options.output.is_empty()
    {
        return Err(diff_usage().to_owned());
    }

    Ok(options)
}

fn print_usage() {
    println!("BehaviorDiff.Engine.Rust");
    println!(
        "usage: behaviordiff-engine diff --base1 <dir> --base2 <dir> --pr <dir> --out <file.json>"
    );
    println!(
        "       behaviordiff-engine stream-probe --base1 <dir> --base2 <dir> --base3 <dir> --pr <dir> --out <report.json>"
    );
    println!(
        "       behaviordiff-engine stream-diff --base1 <dir> --base2 <dir> --base3 <dir> --pr <dir> --out <set.json>"
    );
    println!("       behaviordiff-engine frontier --in <set.json> --changed-files <paths.txt> --out <report.json>");
    println!("       behaviordiff-engine findings --divergences <set.json> --frontier <report.json> --out <findings.json> --exit-code <code> --base-sha <sha> --pr-sha <sha> --merge-base <sha>");
    println!("       behaviordiff-engine findings-invalid --status <refused|failed> --exit-code <code> --reason <text> --out <findings.json>");
    println!(
        "       behaviordiff-engine baseline --findings <findings.json> --baseline <baseline.yml>"
    );
    println!("       behaviordiff-engine baseline-validate --baseline <baseline.yml>");
    println!("       behaviordiff-engine read <trace.ndjson>");
    println!("       behaviordiff-engine normalize <trace.ndjson> -o <output.ndjson> [--force]");
}

fn findings_usage() -> &'static str {
    "usage: findings --divergences <set.json> --frontier <report.json> --out <findings.json> --exit-code <code> --base-sha <sha> --pr-sha <sha> --merge-base <sha> [timing/cache options] [--strict]"
}

fn diff_usage() -> &'static str {
    "usage: diff --base1 <dir> --base2 <dir> --pr <dir> --out <file.json>\n            [--base3 <dir>] [--base-root <path>] [--pr-root <path>]"
}

#[cfg(test)]
mod tests {
    use super::*;

    fn strings(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| (*value).to_owned()).collect()
    }

    #[test]
    fn parses_the_dotnet_diff_contract() {
        let options = parse_diff_options(&strings(&[
            "--base1",
            "base-1",
            "--base2",
            "base-2",
            "--base3",
            "base-3",
            "--changed-files",
            "changed.txt",
            "--pr",
            "pr",
            "--base-root",
            "base-root",
            "--pr-root",
            "pr-root",
            "--out",
            "set.json",
        ]))
        .unwrap();

        assert_eq!(options.base1, "base-1");
        assert_eq!(options.base2, "base-2");
        assert_eq!(options.base3, "base-3");
        assert_eq!(options.changed_files, "changed.txt");
        assert_eq!(options.pr, "pr");
        assert_eq!(options.base_root.as_deref(), Some("base-root"));
        assert_eq!(options.pr_root.as_deref(), Some("pr-root"));
        assert_eq!(options.output, "set.json");
    }

    #[test]
    fn rejects_an_unknown_option() {
        let error = parse_diff_options(&strings(&["--unknown", "value"])).unwrap_err();

        assert_eq!(error, "Unknown option --unknown");
    }

    #[test]
    fn rejects_a_missing_required_option() {
        let error = parse_diff_options(&strings(&[
            "--base1", "base-1", "--base2", "base-2", "--pr", "pr",
        ]))
        .unwrap_err();

        assert_eq!(error, diff_usage());
    }

    #[test]
    fn findings_refs_are_optional_like_the_dotnet_contract() {
        let options = parse_findings_options(&strings(&[
            "--divergences",
            "set.json",
            "--frontier",
            "frontier.json",
            "--out",
            "findings.json",
            "--strict",
        ]))
        .unwrap();

        assert_eq!(options.divergences, "set.json");
        assert_eq!(options.frontier, "frontier.json");
        assert_eq!(options.output, "findings.json");
        assert!(options.base_sha.is_empty());
        assert!(options.strict);
    }
}
