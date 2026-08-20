namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

public static class EtabsApiErrorCodes
{
    public const string ComOperationFailed = "ETABS_COM_OPERATION_FAILED";
    public const string ApiCallFailed = "ETABS_API_CALL_FAILED";
    public const string InfrastructureOperationFailed = "ETABS_INFRASTRUCTURE_OPERATION_FAILED";

    /// <summary>
    /// A CSI call reported success but the resulting model state contradicts it —
    /// e.g. <c>cFile.OpenFile</c> returned zero while ETABS still reports a blank or
    /// foreign current model.
    /// </summary>
    public const string ModelOpenNotConfirmed = "ETABS_MODEL_OPEN_NOT_CONFIRMED";
}

public static class EtabsApiDiagnosticFormatter
{
    public const int OperationLimit = 128;
    public const int ExceptionTypeLimit = 256;
    public const int MessageLimit = 512;
    public const int TotalLimit = 2048;

    public static string ApiReturn(string operation, int returnCode)
    {
        var diagnostic = string.Join(
            "; ",
            EtabsApiErrorCodes.ApiCallFailed,
            $"operation={Component(operation, OperationLimit)}",
            $"returnCode={returnCode}");
        return Component(diagnostic, TotalLimit);
    }

    public static string Exception(string operation, Exception exception)
        => ExceptionCore(EtabsApiErrorCodes.ComOperationFailed, operation, exception);

    public static string InfrastructureException(string operation, Exception exception)
        => ExceptionCore(EtabsApiErrorCodes.InfrastructureOperationFailed, operation, exception);

    /// <summary>
    /// Normalizes control characters and caps a diagnostic at <see cref="TotalLimit"/>
    /// so protocol fields never carry raw unbounded text.
    /// </summary>
    public static string Bounded(string? diagnostic) => Component(diagnostic, TotalLimit);

    public static string AppendTerminalFacts(string diagnostic, string terminalFacts)
    {
        var suffix = Component(terminalFacts, TotalLimit);
        var prefixLimit = TotalLimit - suffix.Length - 2;
        return prefixLimit <= 0
            ? suffix
            : $"{Component(diagnostic, prefixLimit)}; {suffix}";
    }

    private static string ExceptionCore(
        string errorCode,
        string operation,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var fields = new List<string>
        {
            errorCode,
            $"operation={Component(operation, OperationLimit)}",
            $"exceptionType={Component(ExceptionType(exception), ExceptionTypeLimit)}",
            $"hresult=0x{unchecked((uint)exception.HResult):X8}",
            $"message={Component(exception.Message, MessageLimit)}"
        };

        if (exception.InnerException is { } inner)
        {
            fields.Add($"innerExceptionType={Component(ExceptionType(inner), ExceptionTypeLimit)}");
            fields.Add($"innerHResult=0x{unchecked((uint)inner.HResult):X8}");
            fields.Add($"innerMessage={Component(inner.Message, MessageLimit)}");
        }

        return Component(string.Join("; ", fields), TotalLimit);
    }

    private static string ExceptionType(Exception exception) =>
        exception.GetType().FullName ?? exception.GetType().Name;

    private static string Component(string? value, int limit)
    {
        var normalized = string.Concat((value ?? string.Empty)
            .Select(character => char.IsControl(character) ? ' ' : character));
        return normalized.Length <= limit
            ? normalized
            : string.Concat(normalized.AsSpan(0, limit - 1), "…");
    }
}
