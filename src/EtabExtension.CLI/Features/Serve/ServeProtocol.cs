using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// The two terminal outcomes of serve startup. Exactly one startup frame is
/// written to stdout before anything else: <c>ready</c> (the identity/capability
/// handshake) or <c>refused</c> (a typed recovery refusal). A consumer never has
/// to infer startup state from process exit alone.
/// </summary>
public static class ServeStartupOutcomes
{
    public const string Ready = "ready";
    public const string Refused = "refused";
}

/// <summary>
/// A single request line on the serve daemon's stdin:
/// <c>{"id":123,"command":"analyze-and-extract","request":{...}}</c>.
/// The <c>request</c> payload is the existing per-command request JSON, unchanged.
/// </summary>
public sealed class ServeRequest
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("command")] public string Command { get; init; } = string.Empty;

    /// <summary>
    /// The caller's declared intent for a COLD ETABS start, e.g.
    /// <c>"visible-start-consented"</c>.
    ///
    /// <para>It lives on the ENVELOPE rather than in any command's payload because it
    /// describes the request, not the work. Putting it in each payload would duplicate it
    /// across every command and make each new command a fresh chance to forget it.</para>
    ///
    /// <para>Absent or unrecognised means no consent, and a cold start is refused before
    /// any process is created. Requests that reuse an existing session ignore it.</para>
    /// </summary>
    [JsonPropertyName("startIntent")] public string? StartIntent { get; init; }

    [JsonPropertyName("request")] public JsonElement? Request { get; init; }
}

/// <summary>
/// A request the dispatcher has ACCEPTED but cannot answer yet.
///
/// <para>It exists so one command can take minutes without the protocol reader taking
/// minutes with it. A handler that returns this has finished everything that needs the
/// request scope - its declared intent, its captured <c>EtabsWorkContext</c> - and hands
/// back the task that will produce the real answer. <see cref="ServeLoop"/> goes straight
/// back to stdin and writes that answer, on the same request id and in the same envelope
/// shape as any other response, whenever it arrives.</para>
///
/// <para>This is never serialised: it is a signal between the dispatcher and the loop, and
/// the caller only ever sees the result it eventually carries.</para>
///
/// <para><b>Wire consequence.</b> Because the reader keeps going, a response to a LATER id
/// can now be written before the deferred one. Clients that keep a single in-flight request
/// - which the Rust <c>SharedClient</c> does, by holding one mutex across send and receive -
/// can never observe this. A client that wants to poll during a long run needs a stdout
/// reader that routes responses by <c>id</c> first; one that assumes the next id-bearing
/// line is its own answer must not send anything until it has been answered.</para>
/// </summary>
public sealed class DeferredServeResponse(Task<object> completion)
{
    /// <summary>The eventual response object, in the shape the command normally returns.</summary>
    public Task<object> Completion { get; } = completion;
}

public sealed record ServeHandshake(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("buildId")] string BuildId,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("exePath")] string ExePath,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities)
{
    /// <summary>
    /// Always <c>ready</c> — a handshake is only written once recovery succeeded.
    /// The refusal frame carries <c>refused</c>, so one field discriminates the
    /// startup outcome without inspecting the other members.
    /// </summary>
    [JsonPropertyName("startup")]
    public string Startup { get; init; } = ServeStartupOutcomes.Ready;

    internal static Assembly MetadataAssembly => typeof(ServeHandshake).Assembly;

    public static ServeHandshake Current(IReadOnlyList<string> capabilities)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Process executable path is unavailable");

        return FromAssembly(
            MetadataAssembly,
            Environment.ProcessId,
            Path.GetFullPath(exePath),
            capabilities);
    }

    internal static ServeHandshake FromAssembly(
        Assembly assembly,
        int pid,
        string exePath,
        IReadOnlyList<string> capabilities)
    {
        return new(
            ServeProtocolIdentity.Name,
            ServeProtocolIdentity.Version,
            ServeProtocolIdentity.RequiredMetadata(assembly, "SidecarVersion"),
            ServeProtocolIdentity.RequiredMetadata(assembly, "SidecarBuildId"),
            pid,
            Path.GetFullPath(exePath),
            capabilities);
    }
}

/// <summary>
/// Protocol name/version and the build identity read from assembly metadata.
/// Shared by the <c>ready</c> handshake and the <c>refused</c> startup frame so
/// both describe the same sidecar artifact.
/// </summary>
internal static class ServeProtocolIdentity
{
    public const string Name = "etab-cli-serve";
    public const int Version = 1;

    public static string RequiredMetadata(Assembly assembly, string key)
    {
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == key)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required assembly metadata '{key}' is missing or blank");
        }

        return value;
    }

    public static string ProcessExePath() => Path.GetFullPath(
        Environment.ProcessPath
        ?? throw new InvalidOperationException("Process executable path is unavailable"));
}

/// <summary>
/// The terminal startup frame written when managed-ETABS orphan recovery refuses
/// to start the daemon. It carries the same build identity as the handshake, the
/// stable recovery error code, bounded terminal facts, and the location of the
/// retained recovery record.
///
/// <para>The record is retained deliberately — it is the only proof of which
/// ETABS process a previous daemon owned. Deleting it to make startup succeed
/// destroys that evidence, so the remediation says so explicitly.</para>
///
/// <para>No <c>capabilities</c> are advertised: nothing is served after a
/// refusal, and a capability list would read as a ready daemon.</para>
/// </summary>
public sealed record ServeStartupRefusal(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("startup")] string Startup,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("buildId")] string BuildId,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("exePath")] string ExePath,
    [property: JsonPropertyName("errorCode")] string ErrorCode,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("state")] ManagedEtabsShutdownState State,
    [property: JsonPropertyName("processExitConfirmed")] bool ProcessExitConfirmed,
    [property: JsonPropertyName("recordRetained")] bool RecordRetained,
    [property: JsonPropertyName("ownedPid")] int? OwnedPid,
    [property: JsonPropertyName("recordPath")] string RecordPath,
    [property: JsonPropertyName("remediation")] string Remediation)
{
    /// <summary>
    /// Deliberately never instructs a PID-only kill. Managed authority is pid +
    /// process start time + executable path together; PIDs are reused, so a live
    /// process can match the recorded pid and executable path and still be a
    /// different, foreign ETABS. That is exactly the state the sidecar refused to
    /// touch, so the human instruction must not be weaker than the code.
    /// </summary>
    internal const string RemediationText =
        "A previous managed ETABS session could not be proven cleaned up. Do not terminate " +
        "anything by pid alone. Managed identity is pid + process start time (UTC) + executable " +
        "path, and all three are recorded in the managed-session record at recordPath. Terminate " +
        "the live process named by ownedPid only if all three match that record exactly. PIDs are " +
        "reused: a different start time or executable path means this is a foreign process that " +
        "the sidecar refused to touch on purpose. If any of the three differs, or cannot be read, " +
        "do not terminate the process — leave it running, keep the record, and escalate. The " +
        "record is retained as recovery evidence; do not delete it to bypass this refusal.";

    public static ServeStartupRefusal Current(
        ManagedEtabsShutdownResult recovery,
        string recordPath) => FromAssembly(
            ServeHandshake.MetadataAssembly,
            Environment.ProcessId,
            ServeProtocolIdentity.ProcessExePath(),
            recovery,
            recordPath);

    internal static ServeStartupRefusal FromAssembly(
        Assembly assembly,
        int pid,
        string exePath,
        ManagedEtabsShutdownResult recovery,
        string recordPath)
    {
        ArgumentNullException.ThrowIfNull(recovery);

        return new(
            ServeProtocolIdentity.Name,
            ServeProtocolIdentity.Version,
            ServeStartupOutcomes.Refused,
            ServeProtocolIdentity.RequiredMetadata(assembly, "SidecarVersion"),
            ServeProtocolIdentity.RequiredMetadata(assembly, "SidecarBuildId"),
            pid,
            Path.GetFullPath(exePath),
            recovery.ErrorCode ?? ManagedEtabsShutdownErrorCodes.IdentityMismatch,
            EtabsApiDiagnosticFormatter.Bounded(
                recovery.Error ?? "Managed ETABS orphan recovery failed closed."),
            recovery.Data.State,
            recovery.Data.ProcessExitConfirmed,
            recovery.Data.RecordRetained,
            recovery.Data.OwnedPid,
            EtabsApiDiagnosticFormatter.Bounded(recordPath),
            RemediationText);
    }
}

/// <summary>
/// JSON options for the line-delimited serve protocol. Mirrors
/// <c>JsonExtensions.DefaultOptions</c> (camelCase, null-ignored, enum-as-string)
/// but <b>compact</b> (<c>WriteIndented = false</c>) so every response is exactly
/// one line — pretty-printing would break line framing.
/// </summary>
internal static class ServeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>
/// Routes one serve request to the matching feature, executed against the single
/// shared ETABS session. Returns the feature's existing <c>Result</c>/<c>Result&lt;T&gt;</c>
/// (the loop serializes it by runtime type and injects the correlation id).
/// </summary>
public interface IServeDispatcher
{
    IReadOnlyCollection<string> Capabilities { get; }
    Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct);
}
