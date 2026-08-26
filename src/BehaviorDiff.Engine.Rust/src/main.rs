use std::env;

mod dotnet_json;
mod engine;
mod frontier;
mod loader;
mod matcher;
mod model;
mod stream_diff;
mod stream_probe;

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
        _ => {
            eprintln!("Unknown command '{command}'.");
            print_usage();
            EXIT_USAGE
        }
    }
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
}
