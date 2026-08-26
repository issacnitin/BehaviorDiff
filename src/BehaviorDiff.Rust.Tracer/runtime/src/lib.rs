mod canonical;

pub use canonical::{
    capture, capture_arguments, capture_skipped, write_field, CanonicalContext, Canonicalize,
    CapturedValue,
};

use std::cell::RefCell;
use std::collections::HashMap;
use std::fs::{File, OpenOptions};
use std::future::Future;
use std::io::Write;
use std::pin::Pin;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::task::{Context, Poll};

static NEXT_CALL_ID: AtomicU64 = AtomicU64::new(1);
static NEXT_THREAD_ID: AtomicU64 = AtomicU64::new(1);
static WRITER: OnceLock<Mutex<Option<File>>> = OnceLock::new();

thread_local! {
    static CONTEXT: RefCell<TraceContext> = RefCell::new(TraceContext::new());
    static THREAD_ID: u64 = NEXT_THREAD_ID.fetch_add(1, Ordering::Relaxed);
}

struct TraceContext {
    stack: Vec<u64>,
    test_id: Option<String>,
    ordinals: HashMap<(String, &'static str), i32>,
}

impl TraceContext {
    fn new() -> Self {
        Self {
            stack: Vec::new(),
            test_id: None,
            ordinals: HashMap::new(),
        }
    }
}

pub struct Guard {
    call_id: u64,
    method: &'static str,
    file: &'static str,
    line: u32,
    args: Option<CapturedValue>,
    test_id: String,
    parent_call_id: Option<u64>,
    call_depth: i32,
    ordinal: i32,
    thread_id: u64,
    is_test_root: bool,
    active: bool,
    completed: bool,
}

pub fn enter(
    method: &'static str,
    file: &'static str,
    line: u32,
    args: Option<CapturedValue>,
    is_test_root: bool,
) -> Guard {
    let call_id = NEXT_CALL_ID.fetch_add(1, Ordering::Relaxed);
    let (test_id, parent_call_id, call_depth, ordinal) = CONTEXT.with(|context| {
        let mut context = context.borrow_mut();
        if is_test_root {
            context.test_id = Some(method.to_owned());
        }
        let test_id = context
            .test_id
            .clone()
            .unwrap_or_else(|| "(no-test)".to_owned());
        let parent_call_id = context.stack.last().copied();
        let call_depth = context.stack.len() as i32;
        let ordinal = context
            .ordinals
            .entry((test_id.clone(), method))
            .or_insert(0);
        let current = *ordinal;
        *ordinal += 1;
        context.stack.push(call_id);
        (test_id, parent_call_id, call_depth, current)
    });
    let thread_id = THREAD_ID.with(|value| *value);
    Guard {
        call_id,
        method,
        file,
        line,
        args,
        test_id,
        parent_call_id,
        call_depth,
        ordinal,
        thread_id,
        is_test_root,
        active: true,
        completed: false,
    }
}

impl Guard {
    pub fn complete(mut self, result: Option<CapturedValue>) {
        self.completed = true;
        emit(&self, "normal", result.as_ref());
        self.suspend();
    }

    fn activate(&mut self) {
        if self.active {
            return;
        }
        CONTEXT.with(|context| {
            let mut context = context.borrow_mut();
            if self.is_test_root {
                context.test_id = Some(self.test_id.clone());
            }
            context.stack.push(self.call_id);
        });
        self.active = true;
    }

    fn suspend(&mut self) {
        if !self.active {
            return;
        }
        CONTEXT.with(|context| {
            let mut context = context.borrow_mut();
            if context.stack.last() == Some(&self.call_id) {
                context.stack.pop();
            } else if let Some(index) = context
                .stack
                .iter()
                .rposition(|value| *value == self.call_id)
            {
                context.stack.remove(index);
            }
            if self.is_test_root {
                context.test_id = None;
            }
        });
        self.active = false;
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
        emit(self, outcome, None);
        self.suspend();
    }
}

pub struct TraceFuture<F: Future> {
    guard: Option<Guard>,
    future: Pin<Box<F>>,
}

pub fn trace_future<F: Future>(guard: Guard, future: F) -> TraceFuture<F> {
    TraceFuture {
        guard: Some(guard),
        future: Box::pin(future),
    }
}

impl<F: Future> Future for TraceFuture<F> {
    type Output = (Guard, F::Output);

    fn poll(mut self: Pin<&mut Self>, context: &mut Context<'_>) -> Poll<Self::Output> {
        let this = self.as_mut().get_mut();
        let guard = this
            .guard
            .as_mut()
            .expect("trace future polled after completion");
        guard.activate();
        match this.future.as_mut().poll(context) {
            Poll::Ready(value) => Poll::Ready((this.guard.take().unwrap(), value)),
            Poll::Pending => {
                guard.suspend();
                Poll::Pending
            }
        }
    }
}

impl<F: Future> Drop for TraceFuture<F> {
    fn drop(&mut self) {
        if let Some(guard) = &mut self.guard {
            guard.activate();
        }
    }
}

fn emit(guard: &Guard, outcome: &str, result: Option<&CapturedValue>) {
    let writer = WRITER.get_or_init(|| Mutex::new(open_trace()));
    let Ok(mut writer) = writer.lock() else {
        return;
    };
    let Some(stream) = writer.as_mut() else {
        return;
    };
    let method = escape(guard.method);
    let file = escape(guard.file);
    let test_id = escape(&guard.test_id);
    let exception = if outcome == "panic" {
        ",\"exceptionType\":\"RustPanic\""
    } else if outcome == "cancelled" {
        ",\"exceptionType\":\"RustFutureCancelled\""
    } else {
        ""
    };
    let args = capture_fields("args", guard.args.as_ref());
    let result = capture_fields("return", result);
    let parent = guard
        .parent_call_id
        .map_or_else(String::new, |value| format!(",\"parentCallId\":{value}"));
    let call_id = guard.call_id;
    let line = guard.line;
    let call_depth = guard.call_depth;
    let ordinal = guard.ordinal;
    let thread_id = guard.thread_id;
    let is_test_root = guard.is_test_root;
    let _ = writeln!(
        stream,
        "{{\"schema\":\"behaviordiff.trace/1\",\"testId\":\"{test_id}\",\"methodFullName\":\"{method}\",\"filePath\":\"{file}\",\"filePathResolution\":\"debugInfo\",\"line\":{line},\"callDepth\":{call_depth}{parent},\"callId\":{call_id},\"ordinal\":{ordinal},\"threadId\":{thread_id},\"isHarness\":{is_test_root},\"outcome\":\"{outcome}\"{args}{result}{exception}}}"
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::task::Waker;

    struct AlwaysPending;

    impl Future for AlwaysPending {
        type Output = ();

        fn poll(self: Pin<&mut Self>, _context: &mut Context<'_>) -> Poll<Self::Output> {
            Poll::Pending
        }
    }

    #[test]
    fn pending_future_suspends_logical_context_before_drop() {
        let guard = enter("async_test", "src/lib.rs", 1, None, false);
        let mut future = Box::pin(trace_future(guard, AlwaysPending));
        let waker = Waker::noop();
        let mut context = Context::from_waker(waker);
        assert!(future.as_mut().poll(&mut context).is_pending());
        CONTEXT.with(|trace| assert!(trace.borrow().stack.is_empty()));
        drop(future);
        CONTEXT.with(|trace| assert!(trace.borrow().stack.is_empty()));
    }
}
