# Rust frontier and findings qualification

Item 1 qualified the Rust frontier, analyzed findings, invalid findings, and baseline policy before C# engine removal began. All commands used committed code, wrote artifacts under `%TEMP%`, and left the repository clean.

## Final findings equivalence

Each row used the retained Rust `DivergenceSet` as the common input to current C# and Rust frontier implementations, then current C# and Rust findings implementations. Every input had positive base and PR call-tree counts. Final `findings.json` bytes were compared after replacing exactly one `generatedUtc` value and no other field.

| Input | Input bytes | Divergences | Base / PR nodes | Changed files | C# / Rust frontier | Members (unexpected / expected) | C# / Rust findings bytes | Normalized SHA-256 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| SampleApp sort | 7,439,195 | 5 | 9,425 / 9,425 | 1 | 3 / 3 | 1 (1 / 0) | 14,818 / 14,807 | `8E9EA9234492CA8CEB119806550101F18587FC7BADD59EFF20CF1DE9B693D08D` |
| SampleApp retry | 7,451,571 | 17 | 9,425 / 9,425 | 1 | 2 / 2 | 1 (1 / 0) | 54,252 / 54,242 | `AA413009640A0BC7B46ED918FFB96EA577C78A89969347F3B456EA6B2EF6A139` |
| SampleApp config | 7,362,066 | 6 | 9,358 / 9,358 | 1 | 2 / 2 | 1 (1 / 0) | 13,570 / 13,560 | `4F180C2E670F6066E49D013C082A3A619AF9E01638D40B74FD97401904D964CD` |
| .NET conformance | 7,432,234 | 0 | 9,425 / 9,425 | 0 | 0 / 0 | 0 (0 / 0) | 1,766 / 1,756 | `76719393824F6A0CA6F573AE9F4362544B5E7010395AD94B7993A7561ABB2323` |
| Java conformance | 297,217 | 0 | 271 / 271 | 0 | 0 / 0 | 0 (0 / 0) | 1,766 / 1,756 | `76719393824F6A0CA6F573AE9F4362544B5E7010395AD94B7993A7561ABB2323` |
| Node conformance | 255,451 | 0 | 307 / 307 | 0 | 0 / 0 | 0 (0 / 0) | 1,766 / 1,756 | `76719393824F6A0CA6F573AE9F4362544B5E7010395AD94B7993A7561ABB2323` |
| Go conformance | 426,393 | 0 | 421 / 421 | 0 | 0 / 0 | 0 (0 / 0) | 1,766 / 1,756 | `76719393824F6A0CA6F573AE9F4362544B5E7010395AD94B7993A7561ABB2323` |
| JSON-java #1061 | 67,666,797 | 19 | 85,819 / 85,804 | 2 | 9 / 9 | 6 (5 / 1) | 48,762 / 48,752 | `E30CAF6FA8FD2E7EAFE48019AA64F6BD0458C8BD54659BB27594EAE5FFE6BD7C` |
| JSON-java #1062 | 67,645,634 | 0 | 85,819 / 85,818 | 1 | 0 / 0 | 0 (0 / 0) | 2,116 / 2,106 | `4FB295BE103C2B262827E4BF38E8D1C7FAFC9FF1BF0E941C71FCC326C0A8B2B8` |
| JSON-java #1065 | 67,823,385 | 42 | 85,830 / 85,888 | 3 | 30 / 30 | 25 (3 / 22) | 100,548 / 100,538 | `39C1E68801D313FF7C30B93B81C10D9CEDB9EBBC073500A95C8F761B3242047E` |

All ten normalized byte comparisons were equal. The raw size differences were confined to the permitted live `generatedUtc` representation.

## Baseline and invalid artifacts

The four-member baseline fixture produced byte-identical C# and Rust artifacts:

| Scenario | Actionable | Suppressed | Stale / changed / expired | Bytes | SHA-256 |
| --- | ---: | ---: | ---: | ---: | --- |
| Exact and broad policy | 1 | 3 | 1 / 0 / 1 | 4,176 | `96A4F6D7987F4D5C5F3614858C6D82D0C3CBCE5F589879D67CB72198B83A8126` |
| Changed digest | 2 | 2 | 1 / 1 / 1 | 4,225 | `B47132CA2408996951EF58E752C3E1132DF1836449EE2B6DCAF1C8C69C63FD80` |
| Four generated acknowledgements | 0 | 4 | 0 / 0 / 0 | 4,183 | `668D8666DB8D3491EA05EE25D6AEA461221980C11171FC8EF641E72C9BE9D2A9` |

Refused and failed findings matched modulo only `generatedUtc`, omitted `members`, and preserved nullable refs. Their normalized hashes were `90B7D1D4039BD11A48CFE25E8240D3EFFCD7A70BDBDF7977FA89458EC034D1FA` and `0451FD3CCA8B9357E5AF65D542C6557C4339DD7908F2C9F9EA0904E026C1DAC9`.

## Performance

The retained FluentValidation input contained 8 trace files totaling 602,526,499 bytes and 8 changed files. Every measured output contained 3,181 divergences, 106,007 base nodes, 105,993 PR nodes, and 107,343,031 output bytes.

Three alternating trials measured wall time. The split path was Rust stream-diff followed by C# frontier; the all-Rust path changed only frontier ownership.

| Path | Median full wall | Median diff | Median frontier |
| --- | ---: | ---: | ---: |
| Split | 5,373.399 ms | 3,442.767 ms | 1,952.080 ms |
| All Rust | 4,242.705 ms | 3,443.471 ms | 791.114 ms |

All Rust reduced median full wall time by 1,130.694 ms (21.04%) and median frontier time by 1,160.966 ms (59.47%).

Post-exit `PeakWorkingSet64` returned zero and was rejected. A corrected paired run sampled child working set every 10 ms and required positive samples:

| Path | Full wall | Full-path peak | Diff peak | Frontier peak | Positive samples (diff / frontier) |
| --- | ---: | ---: | ---: | ---: | ---: |
| Split | 5,492.428 ms | 318.320 MiB | 171.121 MiB | 318.320 MiB | 110 / 82 |
| All Rust | 4,215.229 ms | 307.348 MiB | 171.250 MiB | 307.348 MiB | 148 / 30 |

The all-Rust paired run used 10.972 MiB less peak working set and 1,277.199 ms less wall time. The pre-frontier stream-diff peak remained below the 300 MiB gate in both runs.