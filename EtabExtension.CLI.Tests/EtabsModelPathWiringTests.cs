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
/// <para>These tests therefore assert on the compiled IL of the wiring itself: exactly
/// one type may ask ETABS for the current model path, and it must ask with the call that
/// returns a file. The test project compiles the production sources directly, so the
/// assembly inspected here is the same code the sidecar ships.</para>
/// </summary>
public sealed class EtabsModelPathWiringTests
{
    private const string FolderReturningCall = "GetModelFilepath";
    private const string FileReturningCall = "GetModelFilename";

    private static readonly string CanonicalRead =
        $"{typeof(EtabsCurrentModelPath).FullName}.{nameof(EtabsCurrentModelPath.Read)}";

    [Fact]
    public void TheCanonicalReaderAsksEtabsForTheModelFileNameAndNeverTheFilepath()
    {
        var calls = ILCalls.MadeBy(typeof(EtabsCurrentModelPath));

        Assert.Contains(calls, call => EndsWithCall(call, FileReturningCall));
        Assert.DoesNotContain(calls, call => EndsWithCall(call, FolderReturningCall));
    }

    [Theory]
    [InlineData(typeof(GetStatusService))]
    [InlineData(typeof(UnlockModelService))]
    [InlineData(typeof(CloseModelService))]
    public void ModeACommandsReadTheCurrentModelOnlyThroughTheCanonicalReader(Type service)
    {
        var calls = ILCalls.MadeBy(service);

        Assert.Contains(calls, call => string.Equals(call, CanonicalRead, StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => EndsWithCall(call, FolderReturningCall));
        Assert.DoesNotContain(calls, call => EndsWithCall(call, FileReturningCall));
    }

    private static bool EndsWithCall(string call, string methodName) =>
        call.EndsWith($".{methodName}", StringComparison.Ordinal);
}

/// <summary>
/// A minimal IL reader: every method the given type (and its compiler-generated nested
/// types) calls, as <c>Namespace.Type.Method</c>.
/// </summary>
internal static class ILCalls
{
    private const byte TwoBytePrefix = 0xFE;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(opCode => opCode.Value)
        .ToDictionary(group => group.Key, group => group.First());

    public static IReadOnlyList<string> MadeBy(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        using var stream = File.OpenRead(type.Assembly.Location);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var calls = new List<string>();
        foreach (var typeHandle in WithNestedTypes(reader, Locate(reader, type)))
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

    private static TypeDefinitionHandle Locate(MetadataReader reader, Type type)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            if (string.Equals(reader.GetString(definition.Name), type.Name, StringComparison.Ordinal)
                && string.Equals(reader.GetString(definition.Namespace), type.Namespace, StringComparison.Ordinal))
            {
                return handle;
            }
        }

        throw new InvalidOperationException($"{type.FullName} is not defined in {type.Assembly.Location}");
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
