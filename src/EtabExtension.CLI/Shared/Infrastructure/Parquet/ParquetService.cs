// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Common;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Shared.Infrastructure.Parquet;

/// <summary>
/// Implementation of IParquetService.
/// Logic mirrors the demo script WriteTableToParquetAsync exactly —
/// all-string columns, dynamic schema from field keys, MakeUniqueFieldNames deduplication.
/// 
/// Note: uses `await using` for writer and row group per parquet-dotnet docs.
/// </summary>
public class ParquetService : IParquetService
{
    internal const string ColumnMappingMetadataKey = "etabextension.cli.parquet.columnMapping.v1";

    private static readonly JsonSerializerOptions ColumnMappingJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ParquetWriteResult> WriteAsync(
        string outputPath,
        List<string> fieldNames,
        List<string> flatData)
    {
        var pathError = PathSafe.GetErrorIfInvalidPath(outputPath, "OutputPath");
        if (pathError is not null)
            return new ParquetWriteResult(false, 0, outputPath, pathError);

        if (fieldNames.Count == 0)
            return new ParquetWriteResult(false, 0, outputPath, "Table has no fields");

        int columnCount = fieldNames.Count;

        if (flatData.Count % columnCount != 0)
            return new ParquetWriteResult(false, 0, outputPath,
                $"Invalid shape: {flatData.Count} values is not divisible by {columnCount} columns");

        int rowCount = flatData.Count / columnCount;

        // Sanitize and deduplicate field names — same as demo script
        var uniqueNames = MakeUniqueFieldNames(fieldNames);
        var columnMappings = BuildColumnMappings(fieldNames, uniqueNames);
        var schemaFields = uniqueNames
            .Select(name => new DataField(name, typeof(string)))
            .ToArray();
        var schema = new ParquetSchema(schemaFields);

        // Pivot row-major flat list into column arrays — same as demo script
        var columns = new List<DataColumn>(columnCount);
        for (int c = 0; c < columnCount; c++)
        {
            var values = new string[rowCount];
            for (int r = 0; r < rowCount; r++)
                values[r] = flatData[r * columnCount + c] ?? string.Empty;

            columns.Add(new DataColumn(schemaFields[c], values));
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // await using matches parquet-dotnet docs pattern
        await using var fs = File.Create(outputPath);
        await using var writer = await ParquetWriter.CreateAsync(schema, fs);
        writer.CustomMetadata = new Dictionary<string, string>
        {
            [ColumnMappingMetadataKey] = SerializeColumnMapping(columnMappings)
        };
        using var rowGroup = writer.CreateRowGroup();

        foreach (var col in columns)
            await rowGroup.WriteColumnAsync(col);

        return new ParquetWriteResult(true, rowCount, outputPath, Columns: columnMappings);
    }

    // ── Helpers — copied verbatim from demo script ────────────────────────────

    private static List<string> MakeUniqueFieldNames(List<string> rawFields)
    {
        var result = new List<string>(rawFields.Count);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in rawFields)
        {
            var baseName = SanitizeFieldName(raw);
            if (!seen.TryGetValue(baseName, out var count))
            {
                seen[baseName] = 1;
                result.Add(baseName);
                continue;
            }

            count++;
            seen[baseName] = count;
            result.Add($"{baseName}_{count}");
        }

        return result;
    }

    private static string SanitizeFieldName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Column";

        var chars = raw.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray();
        var cleaned = new string(chars).Trim('_');

        return string.IsNullOrWhiteSpace(cleaned) ? "Column" : cleaned;
    }

    private static List<ParquetColumnMapping> BuildColumnMappings(
        List<string> rawFields,
        List<string> parquetNames)
    {
        var mappings = new List<ParquetColumnMapping>(rawFields.Count);

        for (var i = 0; i < rawFields.Count; i++)
        {
            mappings.Add(new ParquetColumnMapping(i, parquetNames[i], rawFields[i]));
        }

        return mappings;
    }

    private static string SerializeColumnMapping(IReadOnlyList<ParquetColumnMapping> columns)
    {
        var metadata = new ParquetColumnMappingMetadata(1, columns);
        return JsonSerializer.Serialize(metadata, ColumnMappingJsonOptions);
    }

    private sealed record ParquetColumnMappingMetadata(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("columns")] IReadOnlyList<ParquetColumnMapping> Columns);
}
