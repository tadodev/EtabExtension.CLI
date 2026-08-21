// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The revert guard for CLI #22.
///
/// <para>Every behavioral test above reaches ETABS through an injected seam, because
/// <c>cOAPI</c> and <c>ETABSApplication</c> cannot be faked. That is exactly where this
/// bug lived: the seams were fine and the WIRING was missing — the daemon's launcher
/// never issued a hide at all, so a background Commit put a blank <c>(Untitled)</c>
/// window on screen while every test stayed green.</para>
///
/// <para>These tests therefore assert on compiled IL, per METHOD rather than per type,
/// and they scan the WHOLE production assembly rather than a list of known files. Per
/// method matters here: <c>ServeDispatcher</c> hosts both intents, so a type-level rule
/// could not tell "open-model reveals ETABS" from "snapshot-export reveals ETABS" —
/// which is the one distinction the issue is about. The test project compiles the
/// production sources directly, so the assembly inspected here is the code the sidecar
/// ships.</para>
/// </summary>
public sealed class EtabsVisibilityWiringTests
{
    /// <summary>The CSI application-visibility calls, by method name.</summary>
    private static readonly string[] CsiVisibilityMethods = ["Hide", "Unhide", "Visible"];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ProductionCalls =
        VisibilityCallGraph.ByProductionMethod(typeof(IEtabsRawApi).Assembly);

    /// <summary>
    /// The complete set of production types allowed to issue a CSI visibility call at all.
    ///
    /// <list type="bullet">
    /// <item><c>EtabsRawApi</c> — the managed daemon's adapter onto <c>cOAPI</c>, added by
    /// CLI #22. Everything the daemon does goes through it.</item>
    /// <item><c>EtabsSessionHelpers</c> — the pre-existing one-shot Mode B owner
    /// (<c>HideIfVisible</c>), used by <c>generate-e2k</c>, <c>run-analysis</c>,
    /// <c>extract-*</c> and the standalone <c>snapshot-export</c>/<c>analyze-and-extract</c>
    /// paths that create and destroy their own ETABS. That path was always correct; it is
    /// the daemon that lost the hide, which is what CLI #22 restores.</item>
    /// </list>
    /// </summary>
    private static readonly string[] CsiVisibilityOwners =
    [
        "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.EtabsRawApi",
        "EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsSessionHelpers"
    ];

    /// <summary>
    /// The complete set of production methods allowed to ask the shared session to show
    /// ETABS. One entry, and it is the explicit-open path.
    /// </summary>
    private static readonly string[] RevealCallers =
    [
        "EtabExtension.CLI.Features.Serve.ServeDispatcher.RevealAfterConfirmedOpen"
    ];

    /// <summary>
    /// The un-fakeable wiring: which raw CSI call each adapter actually makes. Swapping
    /// <c>Hide</c> for <c>Unhide</c> here would leave every fake-driven test green and put
    /// a window on screen for every background run.
    /// </summary>
    [Theory]
    [InlineData("Hide", "ETABSv1.cOAPI.Hide")]
    [InlineData("Unhide", "ETABSv1.cOAPI.Unhide")]
    [InlineData("Visible", "ETABSv1.cOAPI.Visible")]
    public void TheRawAdapterMakesTheCsiCallItIsNamedFor(string adapter, string expectedCall)
    {
        var calls = Calls($"EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.EtabsRawApi.{adapter}");

        Assert.Contains(expectedCall, calls, StringComparer.Ordinal);
        Assert.DoesNotContain(
            calls,
            call => IsCsiVisibilityCall(call)
                && !string.Equals(call, expectedCall, StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole-assembly rule. A command that reaches for <c>cOAPI.Hide</c> on its own —
    /// which is how a background command would end up hiding a window the user asked for —
    /// fails here the day it is written.
    /// </summary>
    [Fact]
    public void OnlyTheDeclaredOwnersIssueCsiVisibilityCalls()
    {
        var offenders = ProductionCalls
            .Where(method => method.Value.Any(IsCsiVisibilityCall))
            .Select(method => OwningType(method.Key))
            .Where(type => !CsiVisibilityOwners.Contains(type, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These types call cOAPI Hide/Unhide/Visible directly: {string.Join(", ", offenders)}. " +
            $"Managed-session visibility belongs to {nameof(ManagedEtabsVisibility)} through " +
            "IEtabsVisibilityApi, which reads the state before transitioning and confirms it " +
            "after — Cardex documents that Hide/Unhide error when the application is already " +
            "in the requested state.");
    }

    /// <summary>
    /// The hide has to be ON the start path. Proving the policy is correct is worthless if
    /// nothing calls it — that is precisely the shape of the shipped defect.
    /// </summary>
    [Fact]
    public void TheManagedLaunchHidesTheApplicationItStarted()
    {
        Assert.True(
            Reaches(
                "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsLauncher.Launch",
                $"{typeof(ManagedEtabsVisibility).FullName}." +
                nameof(ManagedEtabsVisibility.EnsureHidden)),
            "ManagedEtabsLauncher.Launch no longer hides the application it started. A managed " +
            "session exists to do background work; nothing may reach the screen between " +
            "ApplicationStart and an explicit user request.");
    }

    /// <summary>
    /// The session re-asserts hidden once the blank model exists. <c>InitializeNewModel</c>
    /// is what makes the window say <c>(Untitled)</c>, and the supervised RC1 timeline shows
    /// that title arriving 6 s after the window did — so a single hide at
    /// <c>ApplicationStart</c> is not on its own proof that nothing surfaces later.
    /// </summary>
    [Fact]
    public void TheSessionReAssertsHiddenBeforeHandingTheApplicationOut()
    {
        Assert.True(
            Reaches(
                "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.EtabsSession.GetOrStartOwned",
                $"{typeof(IManagedEtabsApplication).FullName}." +
                nameof(IManagedEtabsApplication.EnsureHiddenForBackgroundWork)),
            "EtabsSession no longer confirms the managed application hidden before handing it " +
            "to a command.");
    }

    /// <summary>
    /// The acceptance criterion — "focused tests … fail if the two intents collapse to one
    /// behavior" — stated over the compiled assembly. Any method other than the explicit
    /// open path asking for a reveal fails here, including a sibling dispatch method that
    /// looks harmless in review.
    /// </summary>
    [Fact]
    public void OnlyTheExplicitOpenPathAsksForEtabsToBeShown()
    {
        var reveal = $"{typeof(IEtabsSession).FullName}." +
            nameof(IEtabsSession.RevealForExplicitUserRequest);

        var offenders = ProductionCalls
            .Where(method => method.Value.Contains(reveal, StringComparer.Ordinal))
            .Select(method => method.Key)
            .Where(name => !RevealCallers.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These methods ask the shared session to show ETABS: {string.Join(", ", offenders)}. " +
            "Only the explicit open-model path may, and only after the requested model is " +
            "confirmed open. A background command that reveals ETABS is the CLI #22 defect.");
    }

    /// <summary>
    /// The other direction: the allow-listed caller must actually make the call, so the
    /// list cannot silently describe a path that no longer reveals anything, and the
    /// open-model handler must actually reach it.
    /// </summary>
    [Fact]
    public void TheExplicitOpenPathReallyDoesRevealAndIsReachedFromTheOpenDispatch()
    {
        var reveal = $"{typeof(IEtabsSession).FullName}." +
            nameof(IEtabsSession.RevealForExplicitUserRequest);

        Assert.All(
            RevealCallers,
            caller => Assert.Contains(reveal, Calls(caller), StringComparer.Ordinal));
        Assert.True(
            Reaches($"{typeof(ServeDispatcher).FullName}.{OpenDispatch}", reveal),
            "open-model no longer ends with ETABS visible.");
    }

    /// <summary>
    /// The acceptance criterion in its sharpest form. <c>ServeDispatcher</c> hosts every
    /// command, so this walks its dispatch handlers and requires that exactly one of them
    /// — the explicit open — can reach the reveal at all. Collapsing the two intents, in
    /// either direction, fails here.
    /// </summary>
    [Fact]
    public void ExactlyOneDispatchHandlerCanEverPutEtabsOnScreen()
    {
        var reveal = $"{typeof(IEtabsSession).FullName}." +
            nameof(IEtabsSession.RevealForExplicitUserRequest);
        var dispatchHandlers = ProductionCalls.Keys
            .Where(name => name.StartsWith($"{typeof(ServeDispatcher).FullName}.Dispatch", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(dispatchHandlers.Length > 5, "The dispatch-handler scan found almost nothing.");

        var revealing = dispatchHandlers.Where(handler => Reaches(handler, reveal)).ToArray();

        Assert.Equal([$"{typeof(ServeDispatcher).FullName}.{OpenDispatch}"], revealing);
    }

    private const string OpenDispatch = "DispatchOpenModelAsync";

    private static IReadOnlyList<string> Calls(string methodFullName)
    {
        Assert.True(
            ProductionCalls.ContainsKey(methodFullName),
            $"{methodFullName} was not found in the production assembly — was it renamed or moved?");
        return ProductionCalls[methodFullName];
    }

    /// <summary>
    /// Whether <paramref name="target"/> is callable from <paramref name="from"/> through
    /// production code. Transitive on purpose: moving a call one helper deeper is the
    /// cheapest way to slip past a direct-call assertion, and it changes nothing about what
    /// the daemon actually does.
    /// </summary>
    private static bool Reaches(string from, string target)
    {
        Assert.True(
            ProductionCalls.ContainsKey(from),
            $"{from} was not found in the production assembly — was it renamed or moved?");

        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var pending = new Queue<string>();
        pending.Enqueue(from);
        while (pending.Count > 0)
        {
            foreach (var call in ProductionCalls[pending.Dequeue()])
            {
                if (string.Equals(call, target, StringComparison.Ordinal))
                {
                    return true;
                }

                if (ProductionCalls.ContainsKey(call) && seen.Add(call))
                {
                    pending.Enqueue(call);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A CSI/EtabSharp visibility call as opposed to one of our own seams. Our
    /// <c>IEtabsVisibilityApi</c> declares methods with the SAME names, so the owning type
    /// is what discriminates: anything outside the <c>EtabExtension.CLI</c> namespace root
    /// is the real application-visibility surface.
    /// </summary>
    private static bool IsCsiVisibilityCall(string call) =>
        !call.StartsWith("EtabExtension.CLI.", StringComparison.Ordinal)
        && CsiVisibilityMethods.Any(method =>
            call.EndsWith($".{method}", StringComparison.Ordinal));

    private static string OwningType(string methodFullName) =>
        methodFullName[..methodFullName.LastIndexOf('.')];
}

/// <summary>
/// A minimal IL reader: every method each production METHOD calls, keyed
/// <c>Namespace.Type.Method</c>.
///
/// <para>Compiler-generated members are attributed back to the method that wrote them —
/// an async state machine <c>&lt;Foo&gt;d__7</c> and a lambda <c>&lt;Foo&gt;b__7_0</c>
/// both count as <c>Foo</c> — so `await`ing or capturing does not launder a call out of
/// the guard.</para>
///
/// <para>Deliberately named apart from any other IL helper in this suite: sibling
/// branches carry their own scanners, and two files declaring one type name would not
/// compose.</para>
/// </summary>
internal static class VisibilityCallGraph
{
    private const byte TwoBytePrefix = 0xFE;
    private const string ProductionNamespaceRoot = "EtabExtension.CLI";
    private const string TestNamespace = "EtabExtension.CLI.Tests";

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(opCode => opCode.Value)
        .ToDictionary(group => group.Key, group => group.First());

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ByProductionMethod(
        Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        using var stream = OpenAssembly(assembly);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var byMethod = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            if (definition.IsNested || !IsProduction(reader.GetString(definition.Namespace)))
            {
                continue;
            }

            var owner = FullName(reader, handle);
            foreach (var typeHandle in WithNestedTypes(reader, handle))
            {
                var nested = reader.GetTypeDefinition(typeHandle);
                var typeOrigin = Origin(reader.GetString(nested.Name));
                foreach (var methodHandle in nested.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    var name = reader.GetString(method.Name);
                    var key = $"{owner}.{Origin(name) ?? typeOrigin ?? name}";
                    if (!byMethod.TryGetValue(key, out var calls))
                    {
                        calls = [];
                        byMethod[key] = calls;
                    }

                    Collect(peReader, reader, method, calls);
                }
            }
        }

        return byMethod.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The originating method behind a compiler-generated name: <c>&lt;Foo&gt;d__7</c>,
    /// <c>&lt;Foo&gt;b__7_0</c> and <c>&lt;Foo&gt;g__Local|7_0</c> all yield <c>Foo</c>.
    /// Display-class names such as <c>&lt;&gt;c</c> yield null and fall through to the
    /// method's own name.
    ///
    /// <para>Unwraps repeatedly and matches angle brackets by depth, because the nesting
    /// really does stack: an async lambda inside an async method compiles to
    /// <c>&lt;&lt;Foo&gt;b__0&gt;d</c>, and stopping at the first <c>&gt;</c> would
    /// attribute its calls to a method that does not exist.</para>
    /// </summary>
    private static string? Origin(string name)
    {
        var unwrapped = false;
        while (name.Length > 0 && name[0] == '<')
        {
            unwrapped = true;
            var depth = 0;
            var end = -1;
            for (var index = 0; index < name.Length; index++)
            {
                if (name[index] == '<')
                {
                    depth++;
                }
                else if (name[index] == '>' && --depth == 0)
                {
                    end = index;
                    break;
                }
            }

            if (end <= 1)
            {
                return null;
            }

            name = name[1..end];
        }

        // A plain method name is NOT an origin. Returning it here would key an async
        // method's MoveNext under "MoveNext" and quietly drop the entire body of every
        // async method from the graph — which is most of the dispatcher.
        return unwrapped && name.Length > 0 ? name : null;
    }

    private static bool IsProduction(string? typeNamespace) =>
        typeNamespace is not null
        && typeNamespace.StartsWith(ProductionNamespaceRoot, StringComparison.Ordinal)
        && !typeNamespace.Equals(TestNamespace, StringComparison.Ordinal)
        && !typeNamespace.StartsWith($"{TestNamespace}.", StringComparison.Ordinal);

    private static FileStream OpenAssembly(Assembly assembly)
    {
        var location = assembly.Location;
        return string.IsNullOrEmpty(location)
            ? throw new InvalidOperationException(
                $"{assembly.FullName} has no file on disk to read IL from.")
            : File.OpenRead(location);
    }

    private static void Collect(
        PEReader peReader,
        MetadataReader reader,
        MethodDefinition method,
        List<string> calls)
    {
        if (method.RelativeVirtualAddress == 0)
        {
            return;
        }

        var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        if (il is null)
        {
            return;
        }

        foreach (var token in CalledTokens(il))
        {
            calls.Add(Describe(reader, token));
        }
    }

    private static List<TypeDefinitionHandle> WithNestedTypes(
        MetadataReader reader,
        TypeDefinitionHandle root)
    {
        var all = new List<TypeDefinitionHandle>();
        var pending = new Queue<TypeDefinitionHandle>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var handle = pending.Dequeue();
            all.Add(handle);
            foreach (var nested in reader.GetTypeDefinition(handle).GetNestedTypes())
            {
                pending.Enqueue(nested);
            }
        }

        return all;
    }

    private static List<int> CalledTokens(byte[] il)
    {
        var tokens = new List<int>();
        var offset = 0;
        while (offset < il.Length)
        {
            short value = il[offset];
            offset++;
            if (value == TwoBytePrefix && offset < il.Length)
            {
                value = unchecked((short)((TwoBytePrefix << 8) | il[offset]));
                offset++;
            }

            if (!OpCodesByValue.TryGetValue(value, out var opCode))
            {
                throw new InvalidOperationException(
                    $"Unknown IL opcode 0x{value:X4} at offset {offset - 1}");
            }

            if (opCode.OperandType == OperandType.InlineSwitch)
            {
                var branches = BitConverter.ToInt32(il, offset);
                offset += 4 + (4 * branches);
                continue;
            }

            if (opCode.OperandType == OperandType.InlineMethod)
            {
                tokens.Add(BitConverter.ToInt32(il, offset));
            }

            offset += OperandSize(opCode.OperandType);
        }

        return tokens;
    }

    private static int OperandSize(OperandType operandType) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 4
    };

    private static string Describe(MetadataReader reader, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                return $"{OwnerName(reader, member.Parent)}.{reader.GetString(member.Name)}";

            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return $"{FullName(reader, method.GetDeclaringType())}.{reader.GetString(method.Name)}";

            case HandleKind.MethodSpecification:
                var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return Describe(reader, MetadataTokens.GetToken(specification.Method));

            default:
                return token.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string OwnerName(MetadataReader reader, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => FullName(reader, reader.GetTypeReference((TypeReferenceHandle)parent)),
        HandleKind.TypeDefinition => FullName(reader, (TypeDefinitionHandle)parent),
        _ => "?"
    };

    private static string FullName(MetadataReader reader, TypeReference type)
    {
        var name = reader.GetString(type.Name);
        var space = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
    }

    private static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var space = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
    }
}
