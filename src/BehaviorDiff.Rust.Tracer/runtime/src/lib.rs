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
    completed: bool,
}

pub fn enter(method: &'static str, file: &'static str, line: u32) -> Guard {
    Guard {
        call_id: NEXT_CALL_ID.fetch_add(1, Ordering::Relaxed),
        method,
        file,
        line,
        completed: false,
    }
}

impl Guard {
    pub fn complete(mut self) {
        self.completed = true;
        emit(self.call_id, self.method, self.file, self.line, "normal");
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
        emit(self.call_id, self.method, self.file, self.line, outcome);
    }
}

fn emit(call_id: u64, method: &str, file: &str, line: u32, outcome: &str) {
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
    let _ = writeln!(
        stream,
        "{{\"schema\":\"behaviordiff.rust-exit-hook/1\",\"callId\":{call_id},\"method\":\"{method}\",\"file\":\"{file}\",\"line\":{line},\"outcome\":\"{outcome}\"{exception}}}"
    );
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