# Snappier Dependency Remediation Design

## Status

Approved by the product owner on 2026-08-09 for EtabExtension.CLI issue #10.

## Problem

`Parquet.Net` 5.5.0 resolves `Snappier` 1.3.0 transitively. NuGet reports
`NU1903` because that version is covered by high-severity advisory
`GHSA-pggp-6c3x-2xmx`. Warnings are errors in this repository, so restore and
the downstream Alpha sidecar build fail. The sidecar is self-contained, which
also means the vulnerable dependency can become part of the shipped artifact.

## Decision

Keep `Parquet.Net` at 5.5.0 for this release-unblocking slice and override its
transitive dependency with a direct, centrally versioned `Snappier` 1.3.1
reference in all three independent restore roots: the production, test, and
visual-test projects.

- Add `Snappier` 1.3.1 to `Directory.Packages.props`.
- Add an unversioned `PackageReference` to the sidecar executable project.
- Add the same unversioned reference to the test project, which references
  `Parquet.Net` independently and compiles the production source files.
- Add the same unversioned reference to the visual-test project, which also
  references `Parquet.Net` independently and compiles the production source
  files.
- Do not suppress `NU1903`.
- Do not upgrade `Parquet.Net` across a major-version boundary in this slice.

Direct references are preferred over enabling central transitive pinning for
the whole solution: they make the security override visible at each affected
restore root without changing global dependency-resolution semantics.

## Verification

The change is complete only when all of the following are true:

1. A pre-change restore reproduces `NU1903` for `Snappier` 1.3.0.
2. Post-change solution restore completes without `NU1903`.
3. Production, test, and visual-test package graphs resolve `Snappier` 1.3.1
   and do not resolve 1.3.0.
4. `ParquetServiceTests` passes, proving that write/read behavior and custom
   metadata remain compatible.
5. The full test project and solution build pass.
6. A Release `win-x64` self-contained publish succeeds, and the publish input
   dependency manifest contains `Snappier/1.3.1` with no `Snappier/1.3.0`.

No live ETABS instance is required or permitted for this dependency-only work.

## Deferred Alternatives

Upgrading `Parquet.Net` to 6.x may remove the Snappier dependency entirely, but
it is a separate migration with a larger API and artifact-compatibility risk.
It should be evaluated after the Alpha release pipeline is restored. Warning
suppression is rejected because a patched compatible package is available.
