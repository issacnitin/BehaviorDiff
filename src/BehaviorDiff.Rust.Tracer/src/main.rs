use behaviordiff_rust_tracer::{finalize, rewrite};
use std::path::PathBuf;

fn main() {
    match run() {
        Ok(()) => {}
        Err(error) => {
            eprintln!("Rust rewrite failed: {error}");
            std::process::exit(2);
        }
    }
}

fn run() -> Result<(), String> {
    let arguments = std::env::args().skip(1).collect::<Vec<_>>();
    if arguments.first().is_some_and(|value| value == "finalize") {
        return finalize_command(&arguments[1..]);
    }
    let mut source = None;
    let mut cache_root = None;
    let mut arguments = arguments.into_iter();
    while let Some(argument) = arguments.next() {
        let value = arguments
            .next()
            .ok_or_else(|| format!("missing value for {argument}"))?;
        match argument.as_str() {
            "--source" => source = Some(PathBuf::from(value)),
            "--cache-root" => cache_root = Some(PathBuf::from(value)),
            _ => return Err(format!("unknown option {argument}")),
        }
    }

    let source = source.ok_or_else(|| "--source is required".to_owned())?;
    let cache_root = cache_root.ok_or_else(|| "--cache-root is required".to_owned())?;
    let report = rewrite(&source, &cache_root)?;
    println!(
        "{}",
        serde_json::to_string(&report).map_err(|error| error.to_string())?
    );
    Ok(())
}

fn finalize_command(arguments: &[String]) -> Result<(), String> {
    let mut origin = None;
    let mut trace = None;
    let mut output = None;
    let mut arguments = arguments.iter();
    while let Some(argument) = arguments.next() {
        let value = arguments
            .next()
            .ok_or_else(|| format!("missing value for {argument}"))?;
        match argument.as_str() {
            "--origin" => origin = Some(PathBuf::from(value)),
            "--trace" => trace = Some(PathBuf::from(value)),
            "--out" => output = Some(PathBuf::from(value)),
            _ => return Err(format!("unknown finalize option {argument}")),
        }
    }
    let report = finalize(
        &origin.ok_or_else(|| "--origin is required".to_owned())?,
        &trace.ok_or_else(|| "--trace is required".to_owned())?,
        &output.ok_or_else(|| "--out is required".to_owned())?,
    )?;
    println!(
        "{}",
        serde_json::to_string(&report).map_err(|error| error.to_string())?
    );
    Ok(())
}
