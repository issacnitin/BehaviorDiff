#![feature(rustc_private)]

extern crate rustc_driver;
extern crate rustc_interface;
extern crate rustc_middle;

use rustc_driver::{run_compiler, Callbacks, Compilation};
use rustc_interface::interface;
use rustc_middle::ty::TyCtxt;
use std::env;
use std::fs::OpenOptions;
use std::io::Write;

struct MirCallbacks;

impl Callbacks for MirCallbacks {
    fn after_analysis<'tcx>(
        &mut self,
        _compiler: &interface::Compiler,
        tcx: TyCtxt<'tcx>,
    ) -> Compilation {
        if let Ok(report_path) = env::var("REALDIFF_MIR_REPORT") {
            let mut methods = tcx
                .hir_body_owners()
                .map(|local_def_id| {
                    let def_id = local_def_id.to_def_id();
                    let _body = tcx.optimized_mir(def_id);
                    tcx.def_path_str(def_id)
                })
                .collect::<Vec<_>>();
            methods.sort();
            if !methods.is_empty() {
                let mut report = OpenOptions::new()
                    .create(true)
                    .append(true)
                    .open(report_path)
                    .expect("open MIR report");
                for method in methods {
                    writeln!(report, "{method}").expect("write MIR report");
                }
            }
        }

        Compilation::Continue
    }
}

fn main() {
    let mut arguments = env::args().collect::<Vec<_>>();
    if arguments.len() > 1 {
        arguments.remove(1);
    }

    let mut callbacks = MirCallbacks;
    run_compiler(&arguments, &mut callbacks);
}
