mod canonical;

pub use canonical::{
    capture, capture_arguments, capture_skipped, write_field, CanonicalContext, Canonicalize,
    CapturedValue,
};

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};

static NEXT_CALL_ID: AtomicU64 = AtomicU64::new(1);
static WRITER: OnceLock<Mutex<Option<File>>> = OnceLock::new();

pub struct Guard {
    call_id: u64,
    method: &'static str,
    file: &'static str,
    line: u32,
    args: Option<CapturedValue>,
    completed: bool,
}

pub fn enter(
    method: &'static str,
    file: &'static str,
    line: u32,
    args: Option<CapturedValue>,
) -> Guard {
    Guard {
        call_id: NEXT_CALL_ID.fetch_add(1, Ordering::Relaxed),
        method,
        file,
        line,
        args,
        completed: false,
    }
}

impl Guard {
    pub fn complete(mut self, result: Option<CapturedValue>) {
        self.completed = true;
        emit(
            self.call_id,
            self.method,
            self.file,
            self.line,
            "normal",
            self.args.as_ref(),
            result.as_ref(),
        );
    }
}

impl Drop for Guard {
    fn drop(&mut self) {
        if self.completed {
            return;
        }
        let outcome = if std::thread::panicking() {
            "panic"
        } else {
            "cancelled"
        };
        emit(
            self.call_id,
            self.method,
            self.file,
            self.line,
            outcome,
            self.args.as_ref(),
            None,
        );
    }
}

fn emit(
    call_id: u64,
    method: &str,
    file: &str,
    line: u32,
    outcome: &str,
    args: Option<&CapturedValue>,
    result: Option<&CapturedValue>,
) {
    let writer = WRITER.get_or_init(|| Mutex::new(open_trace()));
    let Ok(mut writer) = writer.lock() else {
        return;
    };
    let Some(stream) = writer.as_mut() else {
        return;
    };
    let method = escape(method);
    let file = escape(file);
    let exception = if outcome == "panic" {
        ",\"exceptionType\":\"RustPanic\""
    } else if outcome == "cancelled" {
        ",\"exceptionType\":\"RustFutureCancelled\""
    } else {
        ""
    };
    let args = capture_fields("args", args);
    let result = capture_fields("return", result);
    let _ = writeln!(
        stream,
        "{{\"schema\":\"behaviordiff.rust-exit-hook/2\",\"callId\":{call_id},\"method\":\"{method}\",\"file\":\"{file}\",\"line\":{line},\"outcome\":\"{outcome}\"{args}{result}{exception}}}"
    );
}

fn capture_fields(prefix: &str, value: Option<&CapturedValue>) -> String {
    let Some(value) = value else {
        return String::new();
    };
    format!(
        ",\"{prefix}Digest\":\"{}\",\"{prefix}Rendered\":\"{}\",\"{prefix}Partial\":{},\"{prefix}ValuesDigested\":{},\"{prefix}DepthLimited\":{},\"{prefix}Blocklisted\":{},\"{prefix}RenderedTruncated\":{}",
        escape(&value.digest),
        escape(&value.rendered),
        value.partial,
        value.values_digested,
        value.depth_limited,
        value.blocklisted,
        value.rendered_truncated,
    )
}

fn open_trace() -> Option<File> {
    let path = std::env::var_os("BEHAVIORDIFF_RUST_EXIT_TRACE")?;
    OpenOptions::new().create(true).append(true).open(path).ok()
}

fn escape(value: &str) -> String {
    value
        .replace('\\', "\\\\")
        .replace('"', "\\\"")
        .replace('\n', "\\n")
        .replace('\r', "\\r")
}