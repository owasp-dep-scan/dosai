# SECURITY.md

## Reporting Security Issues

The OWASP dep-scan team and community take security bugs seriously. We appreciate your efforts to responsibly disclose your findings, and will make every effort to acknowledge your contributions.

To report a security issue, email [team@appthreat.com](mailto:team@appthreat.com) and include the word **"SECURITY"** in the subject line.

The OWASP dep-scan team will send a response indicating the next steps in handling your report. After the initial reply to your report, the security team will keep you informed of progress toward a fix and full announcement, and may ask for additional information or guidance.

Report security bugs in third-party modules to the person or team maintaining the module.

## Service Level Agreements (SLAs)

We use the following target response and resolution times for reported security issues. These SLAs are best-effort commitments, not contractual guarantees.

| Severity                                                                                       | Initial Response | Triage / Confirmation | Remediation Target | Disclosure                |
| ---------------------------------------------------------------------------------------------- | ---------------- | --------------------- | ------------------ | ------------------------- |
| **Critical** (RCE, credential exfiltration, supply-chain compromise)                           | 48 hours         | 5 business days       | 15 business days   | Coordinated with reporter |
| **High** (unsafe file writes, path traversal, command injection in Dosai tooling)              | 5 business days  | 10 business days      | 30 business days   | Coordinated with reporter |
| **Medium** (information disclosure, denial of service, misleading output with security impact) | 10 business days | 15 business days      | 60 business days   | Next scheduled release    |
| **Low** (minor hardening improvements, verbose errors without sensitive data)                  | 15 business days | 30 business days      | Best effort        | Next scheduled release    |

After remediation is available, we will publish a GitHub Security Advisory (GHSA) with CVE assignment where appropriate.

## What Counts as a Genuine Security Issue

A genuine security issue is a weakness in `dosai` itself that can be exploited to compromise confidentiality, integrity, or availability beyond expected tool behavior.

### In scope

- Arbitrary code execution caused by parsing or inspecting untrusted .NET source, assemblies, or NuGet packages.
- Unsafe file writes/path handling in JSON, GraphML, GEXF, Mermaid, package extraction, or temp-file logic.
- Vulnerabilities in Dosai command handling that allow command injection.
- Trust-boundary bypasses in PURL/package metadata resolution that load attacker-controlled data unexpectedly.
- Security control bypasses where Dosai claims/exports incorrect graph or slice results due to a clear logic flaw with demonstrable security impact.
- XML/graph output escaping issues that can affect downstream security tooling.

### Out of scope

- Denial-of-service bugs and crashes with very large repositories unless impact is high-amplification or bypasses documented resource controls. Users should run Dosai with isolation and resource limits when analyzing untrusted code.
- False positives/false negatives in data-flow or call graph heuristics unless they stem from a clear exploitable bug in Dosai.
- Feature requests, UX issues, or documentation mistakes without security impact.
- Findings about third-party software discovered by Dosai output; those belong to the analyzed target.
- Build/runtime hardening gaps in user environments outside Dosai control.
- Vulnerabilities in optional external tools not bundled or maintained here.
- Theoretical parser concerns without a reproducible proof-of-impact.

### Grey areas

- High-amplification DoS from malformed source or binaries that requires unusual hardware or massive input.
- Dependency vulnerabilities where exploitability in Dosai runtime paths is unclear.
- Output confusion issues that could mislead CI enforcement in security-sensitive pipelines.
- PURL attribution ambiguity that materially changes vulnerability triage.

When unsure, report privately with a minimal reproducer and impact narrative.

## Shared Responsibility Model

Dosai analyzes potentially untrusted code and binaries. Users are responsible for:

- running Dosai in appropriately isolated environments for hostile inputs;
- protecting generated reports that may contain source snippets, routes, URLs, and package metadata;
- validating data-flow findings before treating them as confirmed vulnerabilities;
- keeping the .NET SDK/runtime and Dosai dependencies updated.

Maintainers are responsible for:

- safe parsing and output generation;
- avoiding intentional execution of analyzed code;
- fixing reported vulnerabilities in Dosai;
- documenting security-impacting behavior and limitations.

## Security-related Output Notes

Dosai reports can contain sensitive information, including:

- source snippets;
- file paths;
- API endpoints and URLs;
- package URLs and dependency identifiers;
- source-to-sink data-flow paths.

Treat generated reports as sensitive artifacts in CI/CD systems.
