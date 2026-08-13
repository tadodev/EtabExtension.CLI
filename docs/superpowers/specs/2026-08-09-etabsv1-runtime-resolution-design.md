# ETABSv1 Runtime Resolution Design

**Date:** 2026-08-09

**Status:** Approved

## Problem

`EtabExtension.CLI` compiles against the proprietary `ETABSv1.dll` installed with ETABS. EtabSharp intentionally marks that assembly `ExcludeFromSingleFile=true`, so a self-contained publish contains `etab-cli.exe` plus a loose `ETABSv1.dll`. The desktop installer bundles only the executable. Without an explicit runtime resolver, real ETABS commands can fail to load `ETABSv1` even when the customer has the supported ETABS installation.

Embedding or redistributing the build machine's `ETABSv1.dll` is not acceptable for the narrow Alpha. EtabSharp documents API-version mismatch failures when it is embedded, and the CSI license reviewed for this work does not provide an explicit redistribution grant.

## Scope

This change makes the C# sidecar resolve `ETABSv1` lazily and directly from the customer's supported ETABS 23 installation. It preserves ETABS-free `serve` handshake and `shutdown`, returns a stable actionable diagnostic when a real ETABS operation requests an unavailable or incompatible API assembly, and records the installer requirement to exclude the loose publish DLL.

It does not change ETABS commands, launch ETABS during tests, modify the Rust/Tauri repository, alter EtabSharp, support ETABS 22 or 24, or redistribute CSI binaries.

## Runtime Bootstrap

An ETABS-free bootstrap class uses `[ModuleInitializer]` to register an `AssemblyLoadContext.Default.Resolving` handler as soon as the CLI module initializes. Registration is idempotent and retains a single handler for the process lifetime.

The handler ignores every assembly request except the simple name `ETABSv1`. It performs no ETABS discovery during registration. Discovery and validation occur only if the runtime requests `ETABSv1`, which keeps protocol handshake and shutdown independent of ETABS installation state.

The resolver loads the validated assembly with `AssemblyLoadContext.LoadFromAssemblyPath`. It does not copy the DLL, probe the current directory, or synthesize a replacement assembly.

## Candidate Search Order

Candidate selection is deterministic:

1. If `ETABS_INSTALL_DIR` is non-empty, it is authoritative. Only `<override>\ETABSv1.dll` and `<override>\ETABS.exe` are considered. An invalid override fails closed and does not fall back.
2. Otherwise resolve the machine's `ProgramFiles` special folder and consider `Computers and Structures\ETABS 23` below it.

Only ETABS 23 is supported for this Alpha. The resolver does not probe `Program Files (x86)`, ETABS 22, ETABS 24, `PATH`, the working directory, or arbitrary registry locations. A custom location must use `ETABS_INSTALL_DIR`.

## Compatibility Validation

The selected directory must contain both `ETABS.exe` and `ETABSv1.dll`. Validation is read-only and requires:

- `ETABS.exe` product/file version parses with major version `23`.
- `ETABSv1.dll` managed assembly simple name is exactly `ETABSv1`.
- The managed assembly version is `1.0.0.0`.
- The public key token is `453d728ef24c6f5e`.
- The ETABS API file version parses with major version `2`.

The product and API file versions may advance within those supported major versions. This accepts ETABS 23 patch updates without accepting a different product or API generation.

## Failure Contract

Every missing or incompatible `ETABSv1` request throws the same dedicated resolution exception. Its message starts with the stable code:

`ETABS_API_ASSEMBLY_UNAVAILABLE:`

The remainder identifies the rejected location or missing requirement and tells the user to install supported ETABS 23 or set `ETABS_INSTALL_DIR` to its installation directory. Diagnostics must not recommend copying or downloading `ETABSv1.dll` separately.

Non-`ETABSv1` resolution requests return `null` so normal .NET resolution behavior remains authoritative.

## Testability

Discovery and validation are separated from process-global registration. A small environment abstraction supplies environment variables, Program Files, file existence, file-version metadata, and managed assembly identity. Unit tests use fakes and never inspect or start a real ETABS installation.

The assembly load operation is injected for focused resolver tests. Bootstrap tests assert that registration is idempotent and that production registration targets `AssemblyLoadContext.Default`. Process-level Release smoke publishes the CLI, stages only `etab-cli.exe` in an isolated directory, sets an authoritative missing override, sends `shutdown`, and proves the handshake/shutdown path does not request `ETABSv1`.

## Packaging Contract

The existing EtabSharp build/publish behavior may continue to place `ETABSv1.dll` beside `etab-cli.exe` so compilation and local development remain possible. That loose file is an input from the build machine's ETABS installation, not a distributable application resource.

The desktop installer must include `etab-cli.exe` and exclude `ETABSv1.dll`. Installer verification must fail if `ETABSv1.dll` appears in the packaged resources or installer payload. The C# publish smoke records the two-file publish output, then tests the installer-equivalent executable-only staging directory.

## Acceptance Criteria

- Module initialization registers the lazy resolver before `Program` executes.
- Registration and `serve` handshake/shutdown do not discover or load ETABS.
- `ETABS_INSTALL_DIR` wins and does not silently fall back when invalid.
- The default candidate is the supported ETABS 23 directory under Program Files.
- Missing and incompatible candidates fail with `ETABS_API_ASSEMBLY_UNAVAILABLE:`.
- Assembly name/version/token, API major, and ETABS product major are validated.
- The resolver loads only the validated installed path and ignores unrelated assembly requests.
- Unit tests, the complete ETABS-free suite, serialized solution build, Release publish, and isolated executable-only handshake/shutdown smoke pass without launching ETABS.
- No CSI binary is added to source control or installer inputs.
