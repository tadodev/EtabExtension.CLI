# ETABSv1 Runtime Resolution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve the proprietary ETABS API assembly lazily from the customer's supported ETABS 23 installation while keeping `serve` handshake/shutdown ETABS-free and excluding the DLL from installer inputs.

**Architecture:** A pure locator validates an authoritative override or the default ETABS 23 Program Files directory through an injectable environment. An ETABS-free module initializer registers one lazy `AssemblyLoadContext.Default.Resolving` handler, which ignores unrelated assemblies and loads only the validated installed `ETABSv1.dll` path.

**Tech Stack:** C# 14, .NET 10, `System.Runtime.Loader`, `System.Diagnostics.FileVersionInfo`, xUnit v3, self-contained single-file publish.

---

## File Structure

- Create `src/EtabExtension.CLI/Bootstrap/EtabsApiAssemblyLocator.cs`: candidate selection, file/assembly identity validation, stable exception, and injectable production environment.
- Create `src/EtabExtension.CLI/Bootstrap/EtabsApiAssemblyBootstrap.cs`: module initializer, idempotent registration, ETABS-name filtering, and installed-path loading.
- Create `EtabExtension.CLI.Tests/EtabsApiAssemblyLocatorTests.cs`: fake-environment search-order and compatibility tests.
- Create `EtabExtension.CLI.Tests/EtabsApiAssemblyBootstrapTests.cs`: lazy filtering, load path, failure, and production registration tests.
- Modify `README.md`: supported-runtime discovery and installer exclusion contract.

### Task 1: Pure ETABS 23 Locator and Validation

**Files:**
- Create: `EtabExtension.CLI.Tests/EtabsApiAssemblyLocatorTests.cs`
- Create: `src/EtabExtension.CLI/Bootstrap/EtabsApiAssemblyLocator.cs`

- [ ] **Step 1: Write the failing locator tests**

Use a fake `IEtabsApiAssemblyEnvironment` with configurable environment variables, Program Files directory, file metadata, managed identities, and recorded access:

```csharp
[Fact]
public void Override_is_authoritative_and_wins_over_default_install()
{
    var environment = ValidEnvironment();
    environment.EnvironmentVariables["ETABS_INSTALL_DIR"] = @"D:\Custom\ETABS 23";
    environment.AddValidEtabs23(@"D:\Custom\ETABS 23");

    var path = new EtabsApiAssemblyLocator(environment).Locate();

    Assert.Equal(Path.GetFullPath(@"D:\Custom\ETABS 23\ETABSv1.dll"), path);
    Assert.DoesNotContain(environment.FileExistenceChecks,
        item => item.Contains(@"C:\Program Files", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public void Invalid_override_does_not_fall_back()
{
    var environment = ValidEnvironment();
    environment.EnvironmentVariables["ETABS_INSTALL_DIR"] = @"D:\Missing";

    var error = Assert.Throws<EtabsApiAssemblyResolutionException>(
        () => new EtabsApiAssemblyLocator(environment).Locate());

    Assert.StartsWith("ETABS_API_ASSEMBLY_UNAVAILABLE:", error.Message);
    Assert.Contains("ETABS_INSTALL_DIR", error.Message, StringComparison.Ordinal);
    Assert.DoesNotContain(environment.FileExistenceChecks,
        item => item.Contains(@"C:\Program Files", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public void Default_candidate_is_etabs_23_under_program_files()
{
    var environment = ValidEnvironment();
    environment.AddValidEtabs23(@"C:\Program Files\Computers and Structures\ETABS 23");

    Assert.Equal(
        Path.GetFullPath(@"C:\Program Files\Computers and Structures\ETABS 23\ETABSv1.dll"),
        new EtabsApiAssemblyLocator(environment).Locate());
}
```

Add distinct facts for missing `ETABS.exe`, missing `ETABSv1.dll`, ETABS product major other than 23, API file major other than 2, wrong managed name, wrong assembly version, and wrong public key token. Every failure starts with the stable code and includes the install/override action.

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~EtabsApiAssemblyLocatorTests --no-restore --tl:off
```

Expected: compilation fails because the locator contracts do not exist.

- [ ] **Step 3: Implement the minimal locator**

Create these contracts:

```csharp
internal sealed record EtabsFileVersion(
    int FileMajor, int ProductMajor, string? FileVersion, string? ProductVersion);

internal interface IEtabsApiAssemblyEnvironment
{
    string? GetEnvironmentVariable(string name);
    string GetProgramFilesDirectory();
    bool FileExists(string path);
    EtabsFileVersion GetFileVersion(string path);
    AssemblyName GetAssemblyName(string path);
}

internal interface IEtabsApiAssemblyLocator
{
    string Locate();
}

internal sealed class EtabsApiAssemblyResolutionException : InvalidOperationException
{
    internal const string Code = "ETABS_API_ASSEMBLY_UNAVAILABLE";

    internal EtabsApiAssemblyResolutionException(string detail, Exception? inner = null)
        : base($"{Code}: {detail} Install supported ETABS 23 or set ETABS_INSTALL_DIR " +
               "to its installation directory.", inner) { }
}
```

`EtabsApiAssemblyLocator` implements `IEtabsApiAssemblyLocator`. Its `Locate()` method uses a non-blank override exclusively, otherwise `ProgramFiles\Computers and Structures\ETABS 23`. It requires both files, ETABS product major 23, API file major 2, managed name `ETABSv1`, version `1.0.0.0`, and token `453d728ef24c6f5e`, then returns the full DLL path. The production environment uses `Environment`, `File`, `FileVersionInfo`, and `AssemblyName.GetAssemblyName`. Inspection failures are wrapped with the stable exception.

- [ ] **Step 4: Run focused tests and confirm GREEN**

Run the Step 2 command. Expected: all locator tests pass without launching ETABS.

- [ ] **Step 5: Commit the locator slice**

```powershell
git add src/EtabExtension.CLI/Bootstrap/EtabsApiAssemblyLocator.cs EtabExtension.CLI.Tests/EtabsApiAssemblyLocatorTests.cs
git commit -m "validate installed etabs api assembly"
```

### Task 2: Earliest Lazy Assembly Bootstrap

**Files:**
- Create: `EtabExtension.CLI.Tests/EtabsApiAssemblyBootstrapTests.cs`
- Create: `src/EtabExtension.CLI/Bootstrap/EtabsApiAssemblyBootstrap.cs`

- [ ] **Step 1: Write failing resolver and registration tests**

```csharp
[Fact]
public void Unrelated_assembly_request_is_ignored_without_discovery()
{
    var locator = new StubLocator(@"C:\ETABS 23\ETABSv1.dll");
    var resolver = new EtabsApiAssemblyResolver(locator, _ => typeof(string).Assembly);

    Assert.Null(resolver.Resolve(AssemblyLoadContext.Default, new AssemblyName("Unrelated")));
    Assert.Equal(0, locator.CallCount);
}

[Fact]
public void Etabsv1_request_loads_only_the_validated_installed_path()
{
    var expectedPath = Path.GetFullPath(@"C:\ETABS 23\ETABSv1.dll");
    string? loadedPath = null;
    var resolver = new EtabsApiAssemblyResolver(new StubLocator(expectedPath), path =>
    {
        loadedPath = path;
        return typeof(string).Assembly;
    });

    Assert.Same(typeof(string).Assembly,
        resolver.Resolve(AssemblyLoadContext.Default, new AssemblyName("ETABSv1")));
    Assert.Equal(expectedPath, loadedPath);
}

[Fact]
public void Production_module_initializer_is_registered_once_on_default_context()
{
    Assert.Same(AssemblyLoadContext.Default, EtabsApiAssemblyBootstrap.RegisteredContext);
    Assert.Equal(1, EtabsApiAssemblyBootstrap.RegistrationCount);
    EtabsApiAssemblyBootstrap.Register();
    Assert.Equal(1, EtabsApiAssemblyBootstrap.RegistrationCount);
}
```

Also assert case-insensitive ETABS name matching and stable wrapping of load failures.

- [ ] **Step 2: Run focused bootstrap tests and confirm RED**

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~EtabsApiAssemblyBootstrapTests --no-restore --tl:off
```

Expected: compilation fails because bootstrap/resolver types do not exist.

- [ ] **Step 3: Implement the resolver and module initializer**

```csharp
internal sealed class EtabsApiAssemblyResolver
{
    private readonly IEtabsApiAssemblyLocator locator;
    private readonly Func<string, Assembly> load;

    internal EtabsApiAssemblyResolver(
        IEtabsApiAssemblyLocator locator,
        Func<string, Assembly> load)
    {
        this.locator = locator;
        this.load = load;
    }

    internal Assembly? Resolve(AssemblyLoadContext context, AssemblyName requested)
    {
        if (!string.Equals(requested.Name, "ETABSv1", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = locator.Locate();
        try { return load(path); }
        catch (EtabsApiAssemblyResolutionException) { throw; }
        catch (Exception error)
        {
            throw new EtabsApiAssemblyResolutionException(
                $"Failed to load the validated ETABS API assembly at '{path}'.", error);
        }
    }
}

internal static class EtabsApiAssemblyBootstrap
{
    private static readonly EtabsApiAssemblyResolver Resolver = new(
        new EtabsApiAssemblyLocator(new SystemEtabsApiAssemblyEnvironment()),
        path => AssemblyLoadContext.Default.LoadFromAssemblyPath(path));
    private static int registrationCount;

    internal static AssemblyLoadContext? RegisteredContext { get; private set; }
    internal static int RegistrationCount => Volatile.Read(ref registrationCount);

    [ModuleInitializer]
    internal static void Register()
    {
        if (Interlocked.CompareExchange(ref registrationCount, 1, 0) != 0) return;
        RegisteredContext = AssemblyLoadContext.Default;
        RegisteredContext.Resolving += Resolver.Resolve;
    }
}
```

Production bootstrap code must not import or mention an `ETABSv1` type; only the assembly-name string is permitted.

- [ ] **Step 4: Run both focused classes and confirm GREEN**

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --filter "FullyQualifiedName~EtabsApiAssemblyLocatorTests|FullyQualifiedName~EtabsApiAssemblyBootstrapTests" --no-restore --tl:off
```

Expected: all resolver tests pass without launching ETABS.

- [ ] **Step 5: Commit the bootstrap slice**

```powershell
git add src/EtabExtension.CLI/Bootstrap/EtabsApiAssemblyBootstrap.cs EtabExtension.CLI.Tests/EtabsApiAssemblyBootstrapTests.cs
git commit -m "resolve etabs api from installed etabs"
```

### Task 3: Record the Installer Boundary

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Add the runtime and packaging contract**

```markdown
## Runtime ETABS API Dependency

The Alpha supports ETABS 23. `etab-cli` resolves `ETABSv1.dll` directly from
the customer's ETABS 23 installation. Set `ETABS_INSTALL_DIR` only for a custom
ETABS 23 install location; when set, it is authoritative.

EtabSharp may copy `ETABSv1.dll` into local build and publish directories so
the project can compile. That proprietary CSI file is not an installer input:
desktop packaging must bundle `etab-cli.exe` and must exclude `ETABSv1.dll`.
Do not embed, commit, or redistribute the DLL.
```

- [ ] **Step 2: Check and commit documentation**

```powershell
git diff --check
git add README.md
git commit -m "document etabs api installer boundary"
```

Expected: only approved packaging guidance changes.

### Task 4: Full Verification and Installer-Equivalent Smoke

**Files:**
- No repository changes expected.

- [ ] **Step 1: Run complete ETABS-free tests**

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --no-restore --tl:off
```

Expected: all prior tests plus new resolver tests pass.

- [ ] **Step 2: Run serialized solution build**

```powershell
dotnet build EtabExtension.CLI.slnx --no-restore --tl:off -m:1
```

Expected: zero warnings and zero errors.

- [ ] **Step 3: Publish Release with immutable metadata**

```powershell
$head = git rev-parse HEAD
dotnet publish src\EtabExtension.CLI\EtabExtension.CLI.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts\publish\etabsv1-runtime --no-restore --tl:off -m:1 "-p:SidecarBuildId=0.1.0+g$head"
```

Expected: publish succeeds and contains `etab-cli.exe` plus the build-only loose `ETABSv1.dll` copied by EtabSharp.

- [ ] **Step 4: Run isolated executable-only serve smoke**

Create a temporary directory outside the repository, copy only `etab-cli.exe`, set `ETABS_INSTALL_DIR` to a definitely missing directory, start `etab-cli.exe serve`, read the handshake, send one `shutdown` request, and wait for exit. Do not issue `get-status` or any ETABS command.

Expected: handshake has protocol `etab-cli-serve`, protocol version `1`, semantic version `0.1.0`, exact HEAD-based build ID, capabilities including `shutdown`; shutdown succeeds, process exits `0`, and the isolated directory contains no `ETABSv1.dll`.

- [ ] **Step 5: Verify hygiene and self-review**

```powershell
git diff --check
git status --short --branch
git log -5 --oneline
```

Inspect every changed file for eager discovery, fallback after invalid override, mutable global test state, any `ETABSv1` type reference in bootstrap code, accidental CSI DLL additions, and any ETABS process launch.
