use behaviordiff_rust_tracer::rewrite;
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
    let mut source = None;
    let mut cache_root = None;
    let mut arguments = std::env::args().skip(1);
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
