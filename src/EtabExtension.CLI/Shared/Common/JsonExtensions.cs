using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Shared.Common;

public static class JsonExtensions
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Outputs a result as JSON to the console
    /// </summary>
    public static void WriteJsonToConsole<T>(this Result<T> result)
    {
        var json = JsonSerializer.Serialize(result, DefaultOptions);
        Console.WriteLine(json);
    }

    /// <summary>
    /// Outputs a result as JSON to the console
    /// </summary>
    public static void WriteJsonToConsole(this Result result)
    {
        var json = JsonSerializer.Serialize(result, DefaultOptions);
        Console.WriteLine(json);
    }

    /// <summary>
    /// Exits the application with appropriate exit code based on result
    /// </summary>
    public static int ExitWithResult<T>(this Result<T> result)
    {
        result.WriteJsonToConsole();
        return result.Success ? 0 : 1;
    }

    /// <summary>
    /// Exits the application with appropriate exit code based on result
    /// </summary>
    public static int ExitWithResult(this Result result)
    {
        result.WriteJsonToConsole();
        return result.Success ? 0 : 1;
    }
}