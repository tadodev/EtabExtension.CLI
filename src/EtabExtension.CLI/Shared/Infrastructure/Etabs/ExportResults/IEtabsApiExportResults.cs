using EtabExtension.CLI.Shared.Infrastructure.Etabs.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.ExportResults;

public interface IEtabsApiExportResults
{
    /// <summary>
    /// Export results from an analyzed model
    /// </summary>
    Task<ExportResultsResponse> ExportResultsAsync(string filePath, string outputPath);


}
