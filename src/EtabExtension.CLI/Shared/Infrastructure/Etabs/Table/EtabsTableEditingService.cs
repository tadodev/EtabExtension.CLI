// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;
using EtabSharp.Core;
using EtabSharp.DatabaseTables.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;

/// <inheritdoc cref="IEtabsTableEditingService"/>
public class EtabsTableEditingService : IEtabsTableEditingService
{
    private readonly ETABSApplication _app;
    private readonly ILogger<EtabsTableEditingService> _logger;

    public EtabsTableEditingService(ETABSApplication app, ILogger<EtabsTableEditingService> logger)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Generic edit ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TableEditResult> EditAsync(
        string tableKey,
        Func<TableDataArrayResult, TableDataArrayResult> modifyCallback,
        string? groupName = null)
    {
        await Task.CompletedTask;

        if (string.IsNullOrEmpty(tableKey))
            return TableEditResult.Fail(tableKey, "tableKey cannot be null or empty");
        if (modifyCallback is null)
            return TableEditResult.Fail(tableKey, "modifyCallback cannot be null");

        _logger.LogInformation("EditAsync: starting for table '{TableKey}'", tableKey);

        var applyResult = _app.Model.DatabaseTables.EditTableWorkflow(
            tableKey,
            modifyCallback,
            groupName ?? string.Empty);

        return new TableEditResult
        {
            IsSuccess = applyResult.IsSuccess,
            ErrorMessage = applyResult.ErrorMessage,
            TableKey = tableKey,
            FatalErrors = applyResult.NumFatalErrors,
            Warnings = applyResult.NumWarnMsgs,
            ImportLog = applyResult.ImportLog ?? string.Empty
        };
    }

    // ── Single-row / single-field convenience ────────────────────────────────

    /// <inheritdoc />
    public async Task<TableEditResult> SetFieldValueAsync(
        string tableKey,
        string keyField,
        string keyValue,
        string targetField,
        string newValue,
        string? groupName = null)
    {
        await Task.CompletedTask;

        var changeLog = new List<string>();
        var rowsModified = 0;

        var applyResult = _app.Model.DatabaseTables.EditTableWorkflow(
            tableKey,
            table =>
            {
                AssertTableLoaded(table, tableKey);
                var rows = table.GetStructuredData();

                var row = FindRow(rows, keyField, keyValue, tableKey);
                var oldValue = GetField(row, targetField, tableKey);

                row[targetField] = newValue;
                rowsModified = 1;
                changeLog.Add(
                    $"Table='{tableKey}' {keyField}='{keyValue}' {targetField}: '{oldValue}' → '{newValue}'");

                _logger.LogInformation("{Change}", changeLog[^1]);
                table.SetStructuredData(rows);
                return table;
            },
            groupName ?? string.Empty);

        return BuildResult(tableKey, applyResult, rowsModified, changeLog);
    }

    /// <inheritdoc />
    public async Task<TableEditResult> ScaleFieldValueAsync(
        string tableKey,
        string keyField,
        string keyValue,
        string targetField,
        double scaleFactor,
        string? groupName = null)
    {
        await Task.CompletedTask;

        var changeLog = new List<string>();
        var rowsModified = 0;

        var applyResult = _app.Model.DatabaseTables.EditTableWorkflow(
            tableKey,
            table =>
            {
                AssertTableLoaded(table, tableKey);
                var rows = table.GetStructuredData();

                var row = FindRow(rows, keyField, keyValue, tableKey);
                var oldValueStr = GetField(row, targetField, tableKey);

                if (!double.TryParse(oldValueStr, out var current))
                    throw new InvalidOperationException(
                        $"Field '{targetField}' value '{oldValueStr}' is not a valid number.");

                var scaled = current * scaleFactor;
                var newValueStr = scaled.ToString("G10");

                row[targetField] = newValueStr;
                rowsModified = 1;
                changeLog.Add(
                    $"Table='{tableKey}' {keyField}='{keyValue}' {targetField}: " +
                    $"{current:G6} × {scaleFactor:G6} = {scaled:G6}");

                _logger.LogInformation("{Change}", changeLog[^1]);
                table.SetStructuredData(rows);
                return table;
            },
            groupName ?? string.Empty);

        return BuildResult(tableKey, applyResult, rowsModified, changeLog);
    }

    // ── Multi-row convenience ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TableEditResult> SetFieldValuesForMultipleRowsAsync(
        string tableKey,
        string keyField,
        string targetField,
        Dictionary<string, string> updates,
        string? groupName = null)
    {
        await Task.CompletedTask;

        if (updates is null || updates.Count == 0)
            return TableEditResult.Fail(tableKey, "'updates' dictionary cannot be null or empty");

        var changeLog = new List<string>();
        var rowsModified = 0;

        var applyResult = _app.Model.DatabaseTables.EditTableWorkflow(
            tableKey,
            table =>
            {
                AssertTableLoaded(table, tableKey);
                var rows = table.GetStructuredData();
                var rowLookup = BuildRowLookup(rows, keyField);

                foreach (var (keyValue, newValue) in updates)
                {
                    if (!rowLookup.TryGetValue(keyValue, out var row))
                    {
                        _logger.LogWarning(
                            "Row with {KeyField}='{KeyValue}' not found in '{TableKey}' — skipped",
                            keyField, keyValue, tableKey);
                        continue;
                    }

                    var oldValue = row.TryGetValue(targetField, out var ov) ? ov : "(missing)";
                    row[targetField] = newValue;
                    rowsModified++;
                    changeLog.Add(
                        $"Table='{tableKey}' {keyField}='{keyValue}' {targetField}: '{oldValue}' → '{newValue}'");
                    _logger.LogInformation("{Change}", changeLog[^1]);
                }

                table.SetStructuredData(rows);
                return table;
            },
            groupName ?? string.Empty);

        return BuildResult(tableKey, applyResult, rowsModified, changeLog);
    }

    /// <inheritdoc />
    public async Task<TableEditResult> ScaleFieldValuesForMultipleRowsAsync(
        string tableKey,
        string keyField,
        string targetField,
        Dictionary<string, double> scaleFactors,
        string? groupName = null)
    {
        await Task.CompletedTask;

        if (scaleFactors is null || scaleFactors.Count == 0)
            return TableEditResult.Fail(tableKey, "'scaleFactors' dictionary cannot be null or empty");

        var changeLog = new List<string>();
        var rowsModified = 0;

        var applyResult = _app.Model.DatabaseTables.EditTableWorkflow(
            tableKey,
            table =>
            {
                AssertTableLoaded(table, tableKey);
                var rows = table.GetStructuredData();
                var rowLookup = BuildRowLookup(rows, keyField);

                foreach (var (keyValue, factor) in scaleFactors)
                {
                    if (!rowLookup.TryGetValue(keyValue, out var row))
                    {
                        _logger.LogWarning(
                            "Row with {KeyField}='{KeyValue}' not found in '{TableKey}' — skipped",
                            keyField, keyValue, tableKey);
                        continue;
                    }

                    if (!row.TryGetValue(targetField, out var oldValueStr) ||
                        !double.TryParse(oldValueStr, out var current))
                    {
                        _logger.LogWarning(
                            "Field '{TargetField}' not found or not numeric for {KeyField}='{KeyValue}' — skipped",
                            targetField, keyField, keyValue);
                        continue;
                    }

                    var scaled = current * factor;
                    var newValueStr = scaled.ToString("G10");
                    row[targetField] = newValueStr;
                    rowsModified++;
                    changeLog.Add(
                        $"Table='{tableKey}' {keyField}='{keyValue}' {targetField}: " +
                        $"{current:G6} × {factor:G6} = {scaled:G6}");
                    _logger.LogInformation("{Change}", changeLog[^1]);
                }

                table.SetStructuredData(rows);
                return table;
            },
            groupName ?? string.Empty);

        return BuildResult(tableKey, applyResult, rowsModified, changeLog);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static void AssertTableLoaded(TableDataArrayResult table, string tableKey)
    {
        if (!table.IsSuccess)
            throw new InvalidOperationException(
                $"Failed to load table '{tableKey}' for editing. " +
                $"ReturnCode={table.ReturnCode}, Error='{table.ErrorMessage}'");
    }

    internal static Dictionary<string, Dictionary<string, string>> BuildRowLookup(
        IEnumerable<Dictionary<string, string>> rows,
        string keyField)
    {
        var lookup = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (row.TryGetValue(keyField, out var keyValue)
                && keyValue is not null
                && !lookup.ContainsKey(keyValue))
            {
                lookup[keyValue] = row;
            }
        }

        return lookup;
    }

    private static Dictionary<string, string> FindRow(
        List<Dictionary<string, string>> rows,
        string keyField,
        string keyValue,
        string tableKey)
    {
        var row = rows.FirstOrDefault(r =>
            r.TryGetValue(keyField, out var v) &&
            string.Equals(v, keyValue, StringComparison.OrdinalIgnoreCase));

        if (row is null)
            throw new InvalidOperationException(
                $"No row with {keyField}='{keyValue}' found in table '{tableKey}'.");

        return row;
    }

    private static string GetField(Dictionary<string, string> row, string fieldKey, string tableKey)
    {
        if (!row.TryGetValue(fieldKey, out var value))
            throw new InvalidOperationException(
                $"Field '{fieldKey}' not found in table '{tableKey}'.");
        return value;
    }

    private static TableEditResult BuildResult(
        string tableKey,
        ApplyEditedTablesResult applyResult,
        int rowsModified,
        List<string> changeLog)
    {
        return new TableEditResult
        {
            IsSuccess = applyResult.IsSuccess,
            ErrorMessage = applyResult.ErrorMessage,
            TableKey = tableKey,
            RowsModified = rowsModified,
            ChangeLog = changeLog,
            FatalErrors = applyResult.NumFatalErrors,
            Warnings = applyResult.NumWarnMsgs,
            ImportLog = applyResult.ImportLog ?? string.Empty
        };
    }
}
