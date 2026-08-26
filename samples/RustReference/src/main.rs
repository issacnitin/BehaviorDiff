use std::collections::{BTreeMap, HashMap, HashSet};
use std::future::Future;
use std::pin::Pin;
use std::task::{Context, Poll, Waker};

#[derive(Clone)]
struct PrivateRecord {
    secret: i32,
}

enum PrivateChoice {
    Value(i32),
    Empty,
}

trait Score {
    fn score(&self) -> i32;
}

impl Score for PrivateRecord {
    fn score(&self) -> i32 {
        self.secret
    }
}

fn private_struct_and_enum() -> i32 {
    let record = PrivateRecord { secret: 7 };
    let choice = PrivateChoice::Value(record.secret);
    let _unused = PrivateChoice::Empty;
    match choice {
        PrivateChoice::Value(value) => value,
        PrivateChoice::Empty => 0,
    }
}

fn generic_identity<T: Clone>(value: T) -> T {
    value.clone()
}

fn trait_object(value: &dyn Score) -> i32 {
    value.score()
}

async fn async_completion() -> i32 {
    std::future::ready(11).await
}

async fn async_cancellation() {
    std::future::pending::<()>().await
}

fn maybe_value(present: bool) -> Result<i32, &'static str> {
    if present {
        Ok(13)
    } else {
        Err("missing")
    }
}

fn question_mark_return(present: bool) -> Result<i32, &'static str> {
    let value = maybe_value(present)?;
    Ok(value + 1)
}

fn panic_unwinding() {
    panic!("prototype panic");
}

fn standard_collections() -> usize {
    let vector = vec![1, 2, 3];
    let mut hash_map = HashMap::new();
    hash_map.insert("one", 1);
    let mut tree_map = BTreeMap::new();
    tree_map.insert("two", 2);
    let hash_set = HashSet::from([3, 4]);
    vector.len() + hash_map.len() + tree_map.len() + hash_set.len()
}

fn block_on<F: Future>(future: F) -> F::Output {
    let waker = Waker::noop();
    let mut context = Context::from_waker(waker);
    let mut future = Box::pin(future);
    loop {
        match Pin::as_mut(&mut future).poll(&mut context) {
            Poll::Ready(value) => return value,
            Poll::Pending => std::thread::yield_now(),
        }
    }
}

fn cancel_after_first_poll<F: Future>(future: F) {
    let waker = Waker::noop();
    let mut context = Context::from_waker(waker);
    let mut future = Box::pin(future);
    assert!(Pin::as_mut(&mut future).poll(&mut context).is_pending());
}

fn main() {
    let record = PrivateRecord { secret: 17 };
    assert_eq!(private_struct_and_enum(), 7);
    assert_eq!(generic_identity(String::from("generic")), "generic");
    assert_eq!(trait_object(&record), 17);
    assert_eq!(block_on(async_completion()), 11);
    cancel_after_first_poll(async_cancellation());
    assert_eq!(question_mark_return(true), Ok(14));
    assert_eq!(question_mark_return(false), Err("missing"));
    assert!(std::panic::catch_unwind(panic_unwinding).is_err());
    assert_eq!(standard_collections(), 7);
    println!("RUST_REFERENCE_OK");
}
