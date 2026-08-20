# Security Policy

## Reporting a vulnerability

Please report security issues privately through GitHub's **Report a vulnerability** feature for this repository. Do not open a public issue for vulnerabilities involving credential exposure, arbitrary code execution, or unsafe handling of untrusted pull requests.

Include a clear description, reproduction steps, affected versions, and any suggested mitigation. You should receive an acknowledgement within seven days.

## Security model

BehaviorDiff builds and executes the target repository's tests. Treat target code as untrusted:

- Run it only on an isolated CI agent or disposable environment.
- Do not expose long-lived credentials to analysis jobs.
- GitHub fork pull requests should use read-only tokens and skip comment posting.
- Keep optional model credentials in a trusted post-processing environment, not in a job that builds pull-request code.
- Review trace artifacts before sharing them; arguments and return values can contain application data.

BehaviorDiff does not provide a sandbox. Its output is evidence from executed tests, not a security verdict.
