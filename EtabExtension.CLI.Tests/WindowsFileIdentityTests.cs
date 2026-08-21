using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The identity test that lets the model-open confirmation tell a re-spelled path
/// from a different model of the same name. Anything it cannot prove identical must
/// answer false, so an unprovable match never passes as a proven one.
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
    public void TheSameFileIsIdenticalToItself()
    {
        var path = CreateFile("model.edb", "edb");

        Assert.True(WindowsFileIdentity.SameFile(path, path));
    }

    [Fact]
    public void ADifferentlySpelledPathToTheSameFileIsIdentical()
    {
        var path = CreateFile("nested/model.edb", "edb");
        var indirect = Path.Combine(_directory, "nested", "..", "nested", "model.edb");

        Assert.True(WindowsFileIdentity.SameFile(path, indirect));
    }

    [Fact]
    public void ByteIdenticalCopiesInDifferentFoldersAreNotIdentical()
    {
        // The D:\Work\test\sample_v2.EDB case: same name, same bytes, different file.
        var left = CreateFile("a/sample_v2.EDB", "identical bytes");
        var right = CreateFile("b/sample_v2.EDB", "identical bytes");

        Assert.False(WindowsFileIdentity.SameFile(left, right));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnusablePathsAreNeverIdentical(string? candidate)
    {
        var path = CreateFile("model.edb", "edb");

        Assert.False(WindowsFileIdentity.SameFile(path, candidate));
        Assert.False(WindowsFileIdentity.SameFile(candidate, path));
    }

    [Fact]
    public void AMissingFileIsNeverIdentical()
    {
        var path = CreateFile("model.edb", "edb");

        Assert.False(WindowsFileIdentity.SameFile(
            path,
            Path.Combine(_directory, "missing.edb")));
    }

    [Fact]
    public void AFolderIsNeverIdenticalToAFile()
    {
        var path = CreateFile("model.edb", "edb");

        Assert.False(WindowsFileIdentity.SameFile(path, _directory));
    }

    [Fact]
    public void AFileHeldOpenElsewhereIsStillIdentifiable()
    {
        // ETABS keeps the .edb open while the model is loaded; the identity read must
        // not contend with it.
        var path = CreateFile("model.edb", "edb");
        using var holder = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        Assert.True(WindowsFileIdentity.SameFile(path, path));
    }
}
