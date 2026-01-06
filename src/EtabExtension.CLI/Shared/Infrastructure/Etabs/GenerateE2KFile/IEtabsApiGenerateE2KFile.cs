using System;
using System.Collections.Generic;
using System.Text;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.GenerateE2KFile;

/// <summary>
/// Defines a method for generating an E2K file from an EDB file asynchronously.
/// </summary>
public interface IEtabsApiGenerateE2KFile
{
    /// <summary>
    /// Generate .e2k file from .edb
    /// </summary>
    Task<bool> GenerateE2KFileAsync(string edbFilePath, string e2KOutputPath);
}
