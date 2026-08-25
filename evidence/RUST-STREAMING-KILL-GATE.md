# Rust streaming prototype kill-gate

The streaming prototype is authorized only as a bounded probe against the retained FluentValidation #2136 corpus.

Before frontier implementation, one direct `stream-probe` process must satisfy every condition below:

- peak resident working set is strictly less than 300 MiB (`314,572,800` bytes);
- exactly `423,974` trace events are consumed across base1, base2, base3, and PR;
- exactly `53,245` subject behavioral keys match between base1 and PR;
- exactly `2,649` subject behavioral keys enter the three-run noise exclusion set;
- compact base1 and PR call graphs are constructed while consuming the same events.

The RSS condition is a hard stop. A result equal to or above 300 MiB ends streaming-engine work regardless of proximity. The threshold will not be raised and frontier work will not begin. Rust then remains frozen as the qualified alternative implementation and work moves to the Rust tracer prototypes.

If the pre-frontier gate passes, the completed streaming diff/frontier path must remain below 500 MiB and preserve byte-identical final `findings.json` modulo only `generatedUtc` before it can replace the existing Rust implementation behind `--engine=rust`. C# remains the default throughout. The old Rust implementation remains selected until the new implementation passes the full existing corpus gate.

Run the hard gate with:

```powershell
pwsh -File tools/verify-rust-streaming-kill-gate.ps1 `
  -WorkDirectory $env:TEMP\behaviordiff-fluentvalidation-cli\rust-corpus
```