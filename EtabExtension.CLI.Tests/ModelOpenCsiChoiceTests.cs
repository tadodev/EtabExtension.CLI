using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// Guards the CSI and identity choices that no behavioral test can reach.
///
/// <para>Everything else about the model-open boundary is proven through
/// <see cref="IEtabsModelFileApi"/> with a fake. What remains on the far side of that
/// seam is a handful of production-only choices — which CSI call reads the current
/// model, and which identity check the primitive defaults to — where picking wrong is
/// invisible to every fake. One of those already shipped as a defect and had to be
/// caught by a supervised live run. These tests fail on the revert.</para>
/// </summary>
public sealed partial class ModelOpenCsiChoiceTests
{
    /// <summary>
    /// The call that returns the model's FOLDER rather than its file path (observed,
    /// ETABS 23.3.0). A folder cannot be saved back and cannot confirm which model is
    /// loaded, so nothing on the model-open path may use it.
    /// </summary>
    [GeneratedRegex(@"\.\s*GetModelFilepath\s*\(")]
    private static partial Regex FolderApiCall();

    /// <summary>
    /// The files still permitted to call the folder-returning API, each a known defect
    /// tracked by CLI #21 and being repaired on <c>codex/alpha-21-getstatus-path</c>.
    ///
    /// <para>This list is meant to reach zero. When #21 lands, each repaired file makes
    /// <see cref="TheFolderApiAllowListHasNoStaleEntries"/> fail with the path to
    /// delete — one line each, then the array is empty and the guard covers the whole
    /// tree unconditionally.</para>
    /// </summary>
    private static readonly string[] AllowedFolderApiCallers =
    [
        Path.Combine("src", "EtabExtension.CLI", "Features", "GetStatus", "GetStatusService.cs"),
        Path.Combine("src", "EtabExtension.CLI", "Features", "UnlockModel", "UnlockModelService.cs"),
        Path.Combine("src", "EtabExtension.CLI", "Features", "CloseModel", "CloseModelService.cs")
    ];

    /// <summary>
    /// Scans the whole source tree rather than a fixed file list: a hardcoded list rots
    /// toward a false pass, because adding a fifth file to the model-open path would
    /// silently stop being guarded.
    /// </summary>
    [Fact]
    public void NoSourceOutsideTheKnownExceptionsCallsTheFolderReturningApi()
    {
        var offenders = ProductionSources()
            .Where(file => FolderApiCall().IsMatch(file.Text))
            .Select(file => file.RelativePath)
            .Where(relative => !AllowedFolderApiCallers.Contains(relative, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These files call GetModelFilepath(): {string.Join(", ", offenders)}. That call " +
            "returns the model's FOLDER, not its file path — it cannot be handed to " +
            "cFile.Save and cannot confirm which model is open. Use " +
            "GetModelFilename(includePath: true) through IEtabsModelFileApi.");
    }

    /// <summary>
    /// Keeps the allow-list honest in the other direction. An entry that no longer
    /// calls the folder API is a repaired file, and leaving it listed would quietly
    /// re-open the hole for that path.
    /// </summary>
    [Fact]
    public void TheFolderApiAllowListHasNoStaleEntries()
    {
        // Every stale entry at once, not the first: these are deleted during composition
        // with the branch that repairs them, and reporting them one per run would cost a
        // rebuild per line.
        var repaired = AllowedFolderApiCallers
            .Where(relative => !FolderApiCall().IsMatch(ReadRepositoryFile(relative)))
            .ToArray();

        Assert.True(
            repaired.Length == 0,
            $"These files no longer call GetModelFilepath() and have been repaired: " +
            $"{string.Join(", ", repaired)}. Delete their lines from " +
            "AllowedFolderApiCallers so the whole-tree guard covers them too.");
    }

    /// <summary>
    /// The seam's only production implementation must read the current model with
    /// <c>GetModelFilename(includePath: true)</c>. Reverting that line is invisible to
    /// every fake-driven test, so it is asserted directly.
    /// </summary>
    [Fact]
    public void TheModelFileApiReadsTheCurrentModelWithGetModelFilename()
    {
        var implementation = Between(
            ReadRepositoryFile(ModelOpenSource),
            "public string? GetModelFilename() =>",
            ";");

        Assert.Contains("ModelInfo.GetModelFilename(", implementation, StringComparison.Ordinal);
        Assert.Contains("includePath: true", implementation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same class of hazard as the CSI choice: every test supplies the identity check
    /// explicitly, so the production default is never exercised. Swapping it for a
    /// permissive stub would disable the guard against a same-named model in another
    /// folder while leaving the suite green.
    ///
    /// <para>Asserted on source text rather than by comparing delegates —
    /// a method-group conversion allocates a fresh delegate per call, so
    /// <c>Assert.Same</c> could never hold.</para>
    /// </summary>
    [Fact]
    public void TheOpenPrimitiveDefaultsToTheRealFileIdentityCheck()
    {
        var defaulting = Between(
            ReadRepositoryFile(ModelOpenSource),
            "compareIdentity ??",
            ")");

        Assert.Contains("WindowsFileIdentity.Compare", defaulting, StringComparison.Ordinal);
    }

    private static string ModelOpenSource => Path.Combine(
        "src", "EtabExtension.CLI", "Shared", "Infrastructure", "Etabs", "EtabsModelOpen.cs");

    private static IEnumerable<(string RelativePath, string Text)> ProductionSources()
    {
        var root = RepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        Assert.True(Directory.Exists(sourceRoot), $"Expected source root not found: {sourceRoot}");

        return Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, root))
            .Select(path => (Path.GetRelativePath(root, path), File.ReadAllText(path)));
    }

    private static bool IsBuildOutput(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("obj", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("bin", StringComparer.OrdinalIgnoreCase);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Could not find '{start}' — did the seam get renamed?");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"Could not find '{end}' after '{start}'.");
        return source[from..to];
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);
        Assert.True(File.Exists(path), $"Expected source file not found: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Anchored to this file's own compile-time location, so it does not depend on the
    /// working directory or on where the test assembly was copied.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));
}
