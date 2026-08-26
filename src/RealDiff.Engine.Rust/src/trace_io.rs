use serde_json::Value;
use std::fs::{self, File, OpenOptions};
use std::io::{BufRead, BufReader, BufWriter, Write};
use std::path::Path;

const EXIT_MALFORMED: i32 = 1;

pub(crate) fn read(path: &str) -> Result<i32, String> {
    let (records, malformed) = visit(path, |_| Ok(()))?;
    println!("records: {records}");
    println!("malformed: {malformed}");
    Ok(if malformed == 0 { 0 } else { EXIT_MALFORMED })
}

pub(crate) fn normalize(input: &str, output: &str, force: bool) -> Result<i32, String> {
    if Path::new(output).exists() && !force {
        return Err(format!(
            "Output already exists: {output}; pass --force to replace it."
        ));
    }
    if let Some(parent) = Path::new(output).parent() {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    let file = OpenOptions::new()
        .create(true)
        .truncate(true)
        .write(true)
        .open(output)
        .map_err(|error| error.to_string())?;
    let mut writer = BufWriter::new(file);
    let (records, malformed) = visit(input, |value| {
        serde_json::to_writer(&mut writer, value).map_err(|error| error.to_string())?;
        writer.write_all(b"\n").map_err(|error| error.to_string())
    })?;
    writer.flush().map_err(|error| error.to_string())?;
    println!("records: {records}");
    println!("malformed: {malformed}");
    println!("normalized: {output}");
    Ok(if malformed == 0 { 0 } else { EXIT_MALFORMED })
}

fn visit(
    path: &str,
    mut visitor: impl FnMut(&Value) -> Result<(), String>,
) -> Result<(usize, usize), String> {
    let file = File::open(path).map_err(|error| format!("{path}: {error}"))?;
    let reader = BufReader::new(file);
    let mut records = 0;
    let mut malformed = 0;
    for line in reader.lines() {
        let line = line.map_err(|error| format!("{path}: {error}"))?;
        if line.trim().is_empty() {
            continue;
        }
        match serde_json::from_str::<Value>(&line) {
            Ok(value) if value.is_object() => {
                records += 1;
                visitor(&value)?;
            }
            _ => malformed += 1,
        }
    }
    Ok((records, malformed))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn object_records_are_required() {
        let object: Value = serde_json::from_str(r#"{"testId":"proof"}"#).unwrap();
        let array: Value = serde_json::from_str("[]").unwrap();
        assert!(object.is_object());
        assert!(!array.is_object());
    }
}
