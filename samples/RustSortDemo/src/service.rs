use crate::config::PRIORITY_BIAS;

pub fn select_priority(input: i32) -> i32 {
    apply_priority_bias(input)
}

fn apply_priority_bias(input: i32) -> i32 {
    biased_priority(input)
}

fn biased_priority(input: i32) -> i32 {
    input + PRIORITY_BIAS
}
