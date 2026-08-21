using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The identity test that lets the model-open confirmation tell a re-spelled path
/// from a different model of the same name. Anything it cannot prove identical must
/// answer <see cref="FileIdentityMatch.Unprovable"/> rather than guessing either way.
/// </summary>
public sealed class WindowsFileIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-file-identity-tests", Guid.NewGuid().ToString("N"));

    public WindowsFileIdentityTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string CreateFile(string relativePath, string content)
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AZeroFileIndexIsNoAnswerRatherThanAnIdentity()
    {
        // Guards a FAIL-OPEN hazard, not a degradation: a redirector that reports a zero
        // index would otherwise make every file on the volume compare equal, so a
        // genuinely different model — even one with a different name — would be accepted
        // as the requested one with only a warning.
        Assert.Null(WindowsFileIdentity.IdentityFrom(volume: 0x1234, indexHigh: 0, indexLow: 0));

        Assert.NotNull(WindowsFileIdentity.IdentityFrom(volume: 0x1234, indexHigh: 0, indexLow: 1));
        Assert.NotNull(WindowsFileIdentity.IdentityFrom(volume: 0x1234, indexHigh: 1, indexLow: 0));
    }

    [Fact]
    public void DistinctFilesOnOneVolumeKeepDistinctIdentities()
    {
        var left = WindowsFileIdentity.IdentityFrom(volume: 7, indexHigh: 0, indexLow: 11);
        var right = WindowsFileIdentity.IdentityFrom(volume: 7, indexHigh: 0, indexLow: 12);

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void AnUnavailableIndexDescribesItselfInsteadOfReportingErrorZero()
    {
        var result = FileIdentityResult.Unprovable(WindowsFileIdentity.FileIndexUnavailable);

        Assert.Equal("the filesystem reported no file index", result.DescribeFailure());
        Assert.DoesNotContain("win32Error", result.DescribeFailure(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameFileIsIdenticalToItself()
    {
        var path = CreateFile("model.edb", "edb");

        Assert.Equal(FileIdentityMatch.Same, WindowsFileIdentity.Compare(path, path).Match);
    }

    [Fact]
    public void ADifferentlySpelledPathToTheSameFileIsIdentical()
    {
        var path = CreateFile("nested/model.edb", "edb");
        var indirect = Path.Combine(_directory, "nested", "..", "nested", "model.edb");

        Assert.Equal(FileIdentityMatch.Same, WindowsFileIdentity.Compare(path, indirect).Match);
    }

    [Fact]
    public void ByteIdenticalCopiesInDifferentFoldersAreNotIdentical()
    {
        // The D:\Work\test\sample_v2.EDB case: same name, same bytes, different file.
        var left = CreateFile("a/sample_v2.EDB", "identical bytes");
        var right = CreateFile("b/sample_v2.EDB", "identical bytes");

        Assert.Equal(FileIdentityMatch.Different, WindowsFileIdentity.Compare(left, right).Match);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnusablePathsAreNeverProven(string? candidate)
    {
        var path = CreateFile("model.edb", "edb");

        Assert.Equal(
            FileIdentityMatch.Unprovable,
            WindowsFileIdentity.Compare(path, candidate).Match);
        Assert.Equal(
            FileIdentityMatch.Unprovable,
            WindowsFileIdentity.Compare(candidate, path).Match);
    }

    [Fact]
    public void AMissingFileIsUnprovableAndSaysWhy()
    {
        var path = CreateFile("model.edb", "edb");

        var result = WindowsFileIdentity.Compare(path, Path.Combine(_directory, "missing.edb"));

        // Unprovable, not Different: nothing about the requested file was disproved.
        Assert.Equal(FileIdentityMatch.Unprovable, result.Match);
        Assert.Equal(2, result.Win32Error); // ERROR_FILE_NOT_FOUND
    }

    [Fact]
    public void AFolderIsUnprovable()
    {
        // FILE_FLAG_BACKUP_SEMANTICS is deliberately not set, so a directory handle
        // cannot be opened and the answer is "cannot tell", not "different".
        var path = CreateFile("model.edb", "edb");

        Assert.Equal(
            FileIdentityMatch.Unprovable,
            WindowsFileIdentity.Compare(path, _directory).Match);
    }

    [Fact]
    public void AFileHeldExclusivelyElsewhereIsStillIdentifiable()
    {
        // ETABS keeps the .edb open while the model is loaded. FileShare.None is the
        // strict form: it denies every conflicting open, so this passes only because
        // FILE_READ_ATTRIBUTES is exempt from share-access checking. Requesting
        // GENERIC_READ here would fail — which is the whole reason for that choice.
        var path = CreateFile("model.edb", "edb");
        using var exclusiveHolder = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(FileIdentityMatch.Same, WindowsFileIdentity.Compare(path, path).Match);
    }
}
