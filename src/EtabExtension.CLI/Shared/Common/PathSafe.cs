// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Linq;
using System.Text.RegularExpressions;

namespace EtabExtension.CLI.Shared.Common;

/// <summary>
/// Utilities for safe path handling and sanitization to prevent Path Traversal.
/// </summary>
public static class PathSafe
{
    /// <summary>
    /// Robustly sanitizes a string (e.g. a table key) into a safe filename slug.
    /// Replaces all characters that are not letters, digits, or underscores with underscores.
    /// This prevents path traversal sequences like "../" from being injected via table keys.
    /// </summary>
    public static string ToSafeSlug(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "unnamed";

        // Lowercase and replace any character that is not a letter, digit, or underscore with '_'
        var chars = input.ToLowerInvariant()
            .Select(c => (char.IsLetterOrDigit(c) || c == '_') ? c : '_')
            .ToArray();

        var slug = new string(chars);

        // Collapse multiple underscores and trim
        slug = Regex.Replace(slug, @"_+", "_").Trim('_');

        return string.IsNullOrEmpty(slug) ? "unnamed" : slug;
    }

    /// <summary>
    /// Validates a path for invalid characters and ensures it's a well-formed path.
    /// Returns null if valid, or an error message if invalid.
    /// </summary>
    public static string? GetErrorIfInvalidPath(string? path, string paramName = "Path")
    {
        if (string.IsNullOrWhiteSpace(path))
            return $"{paramName} cannot be empty.";

        try
        {
            // This will catch many invalid path formats and check for invalid characters
            _ = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return $"{paramName} is not a valid path.";
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return $"{paramName} contains invalid characters.";

        return null;
    }
}
