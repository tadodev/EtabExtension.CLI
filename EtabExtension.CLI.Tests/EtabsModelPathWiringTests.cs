// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using EtabExtension.CLI.Features.CloseModel;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Features.UnlockModel;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The revert guard for #21.
///
/// <para>Every behavioral test in this repo reaches the ETABS boundary through an
/// injected seam, because <c>ETABSApplication</c> and <c>ETABSModel</c> are sealed and
/// cannot be faked. That is exactly where this bug lived: the seams were right and the
/// wiring — the one line that picks which CSI call to make — was wrong, so swapping the
/// call back left every test green.</para>
///
/// <para>These tests therefore assert on compiled IL, and they scan the WHOLE production
/// assembly rather than a list of known files. A hand-enumerated list rots toward a false
/// pass: it certifies a tree in which some other command still calls the folder-returning
/// API. Instead every production type is walked, and only an explicitly named allow-list
/// may make the call at all — a new caller fails on the day it is written. The test
/// project compiles the production sources directly, so the assembly inspected here is
/// the same code the sidecar ships.</para>
/// </summary>
public sealed class EtabsModelPathWiringTests
{
    private const string FolderReturningCall = "GetModelFilepath";
    private const string FileReturningCall = "GetModelFilename";

    private static readonly string CanonicalRead =
        $"{typeof(EtabsCurrentModelPath).FullName}.{nameof(EtabsCurrentModelPath.Read)}";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ProductionCalls =
        ILCalls.ByProductionType(typeof(EtabsCurrentModelPath).Assembly);

    /// <summary>
    /// The complete set of production types permitted to ask ETABS which model is open.
    /// Deliberately a set rather than a claim of uniqueness: the Mode A commands and the
    /// model-open path are separate readers. Anything not listed must go through one of
    /// them.
    /// </summary>
    private static readonly string[] ModelPathReaders =
    [
        // The Mode A reader (#21): get-status, unlock-model, close-model.
        "EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsCurrentModelPath",

        // The serve inspection wrapper. Already correct before #21 — the in-repo
        // precedent that the right call was known all along.
        "EtabExtension.CLI.Features.Serve.Inspection.EtabsInspectionApi",

        // The model-open path. Today that is OpenModelService reading directly; on
        // codex/alpha-19-snapshot-export it becomes EtabsModelFileApi behind
        // IEtabsModelFileApi. Both are named so this list is true before AND after those
        // two branches compose. Folding either onto EtabsCurrentModelPath.Read is
        // follow-up work, not something this guard should pre-empt.
        "EtabExtension.CLI.Features.OpenModel.OpenModelService",
        "EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsModelFileApi"
    ];

    /// <summary>
    /// Types permitted to call the folder-returning API. CLI #19 repaired
    /// <c>OpenModelService</c>, so this list has reached its intended terminal state:
    /// empty.
    ///
    /// <para>Empty is the strong state, not a disabled one —
    /// <see cref="NothingOutsideThePendingRepairsCallsTheFolderReturningApi"/> subtracts
    /// this list from a whole-assembly scan, so with nothing to subtract it now bans the
    /// call everywhere. Do not add an entry without a tracked defect and a reason;
    /// <see cref="ThePendingFolderApiRepairListHasNoStaleEntries"/> forces any entry back
    /// out once its type is repaired.</para>
    /// </summary>
    private static readonly string[] PendingFolderApiRepairs = [];

    /// <summary>
    /// The whole-assembly rule. A fifth command that reads the current model itself,
    /// rather than through a declared reader, fails here the day it is written.
    /// </summary>
    [Fact]
    public void OnlyTheDeclaredReadersAskEtabsForTheCurrentModelPath()
    {
        var offenders = ProductionCalls
            .Where(type => type.Value.Any(IsCsiModelPathCall))
            .Select(type => type.Key)
            .Where(name => !ModelPathReaders.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These types ask ETABS for the current model path directly: {string.Join(", ", offenders)}. " +
            $"Read it through {typeof(EtabsCurrentModelPath).FullName}.Read instead, or — if this is a " +
            "genuinely new reader — add it to ModelPathReaders with the reason.");
    }

    /// <summary>
    /// The folder-returning call is banned outright, everywhere, save for the named
    /// holdouts another branch is repairing.
    /// </summary>
    [Fact]
    public void NothingOutsideThePendingRepairsCallsTheFolderReturningApi()
    {
        var offenders = ProductionCalls
            .Where(type => type.Value.Any(call => IsCall(call, FolderReturningCall)))
            .Select(type => type.Key)
            .Where(name => !PendingFolderApiRepairs.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These types call {FolderReturningCall}(): {string.Join(", ", offenders)}. It returns the " +
            "model's FOLDER, not its file path — it cannot be saved back, cannot confirm which model is " +
            $"open, and can never path-match a .edb. Use {FileReturningCall}(includePath: true).");
    }

    /// <summary>
    /// Keeps the holdout list honest in the other direction: an entry that no longer makes
    /// the call is a repaired type, and leaving it listed quietly re-opens the hole.
    /// </summary>
    [Fact]
    public void ThePendingFolderApiRepairListHasNoStaleEntries()
    {
        // Report every stale entry at once, not just the first: these are deleted during
        // composition with the branch that repairs them.
        var repaired = PendingFolderApiRepairs
            .Where(name => !Calls(name).Any(call => IsCall(call, FolderReturningCall)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            repaired.Length == 0,
            $"These types no longer call {FolderReturningCall}(): {string.Join(", ", repaired)}. " +
            "Delete their entries from PendingFolderApiRepairs so the whole-assembly ban covers them too.");
    }

    /// <summary>
    /// The one line the whole issue turns on. Also proves the scanner is not vacuous: it
    /// has to find a real call for this to pass.
    /// </summary>
    [Fact]
    public void TheCanonicalReaderAsksEtabsForTheModelFileNameAndNeverTheFilepath()
    {
        var calls = Calls(typeof(EtabsCurrentModelPath).FullName!);

        Assert.Contains(calls, call => IsCall(call, FileReturningCall));
        Assert.DoesNotContain(calls, call => IsCall(call, FolderReturningCall));
    }

    /// <summary>
    /// The routing rule for the three commands #21 repairs, stated positively: they must
    /// go THROUGH the canonical reader, not merely avoid the wrong call.
    /// </summary>
    [Theory]
    [InlineData(typeof(GetStatusService))]
    [InlineData(typeof(UnlockModelService))]
    [InlineData(typeof(CloseModelService))]
    public void ModeACommandsReadTheCurrentModelOnlyThroughTheCanonicalReader(Type service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var calls = Calls(service.FullName!);

        Assert.Contains(calls, call => string.Equals(call, CanonicalRead, StringComparison.Ordinal));
        Assert.DoesNotContain(calls, IsCsiModelPathCall);
    }

    private static IReadOnlyList<string> Calls(string typeFullName)
    {
        Assert.True(
            ProductionCalls.ContainsKey(typeFullName),
            $"{typeFullName} was not found in the production assembly — was it renamed or moved?");
        return ProductionCalls[typeFullName];
    }

    /// <summary>
    /// A call into the CSI/EtabSharp model surface, as opposed to one of our own seams.
    /// <c>ServeInspectionService</c> — and, after #19, <c>EtabsModelOpen</c> — call a
    /// method NAMED <c>GetModelFilename</c> on an <c>EtabExtension.CLI</c> interface.
    /// That is the seam working as intended, not a direct read, so the owning type is
    /// what discriminates.
    /// </summary>
    private static bool IsCsiModelPathCall(string call) =>
        (IsCall(call, FolderReturningCall) || IsCall(call, FileReturningCall))
        && !call.StartsWith("EtabExtension.CLI.", StringComparison.Ordinal);

    private static bool IsCall(string call, string methodName) =>
        call.EndsWith($".{methodName}", StringComparison.Ordinal);
}

/// <summary>
/// A minimal IL reader: every method a type (and its compiler-generated nested types,
/// which is where lambdas end up) calls, as <c>Namespace.Type.Method</c>.
/// </summary>
internal static class ILCalls
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

    /// <summary>
    /// Every top-level production type in <paramref name="assembly"/> mapped to the calls
    /// it makes. Nested types roll up into their declaring type, so a call made from a
    /// lambda is attributed to the method that wrote it.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ByProductionType(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        using var stream = OpenAssembly(assembly);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var byType = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            if (definition.IsNested || !IsProduction(reader.GetString(definition.Namespace)))
            {
                continue;
            }

            byType[FullName(reader, handle)] = CallsFrom(peReader, reader, handle);
        }

        return byType;
    }

    private static bool IsProduction(string? typeNamespace) =>
        typeNamespace is not null
        && typeNamespace.StartsWith(ProductionNamespaceRoot, StringComparison.Ordinal)
        && !typeNamespace.Equals(TestNamespace, StringComparison.Ordinal)
        && !typeNamespace.StartsWith($"{TestNamespace}.", StringComparison.Ordinal);

    private static FileStream OpenAssembly(Assembly assembly)
    {
        var location = assembly.Location;
        if (string.IsNullOrEmpty(location))
        {
            throw new InvalidOperationException(
                $"{assembly.FullName} has no file on disk to read IL from.");
        }

        return File.OpenRead(location);
    }

    private static List<string> CallsFrom(
        PEReader peReader,
        MetadataReader reader,
        TypeDefinitionHandle root)
    {
        var calls = new List<string>();
        foreach (var typeHandle in WithNestedTypes(reader, root))
        {
            foreach (var methodHandle in reader.GetTypeDefinition(typeHandle).GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                if (il is null)
                {
                    continue;
                }

                foreach (var token in CalledTokens(il))
                {
                    calls.Add(Describe(reader, token));
                }
            }
        }

        return calls;
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
        if (type.IsNested)
        {
            return $"{FullName(reader, type.GetDeclaringType())}+{name}";
        }

        var space = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
    }
}
