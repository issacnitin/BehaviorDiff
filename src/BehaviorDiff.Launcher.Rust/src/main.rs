use behaviordiff_launcher::run;

fn main() {
    std::process::exit(run(std::env::args_os().skip(1).collect()));
}
