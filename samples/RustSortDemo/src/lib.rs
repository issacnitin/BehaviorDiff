mod config;
mod coverage;
mod service;

#[cfg(test)]
mod tests {
    use super::service::select_priority;
    use super::coverage::exercise_coverage;

    #[test]
    fn priority_observation() {
        assert!(exercise_coverage() > 0);
        let _ = select_priority(10);
        let _ = select_priority(10);
        let _ = select_priority(10);
    }
}
