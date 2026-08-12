using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class EtabsApiDiagnosticFormatterTests
{
    [Fact]
    public void Exception_formats_fixed_hresult_normalizes_controls_and_ignores_deeper_inner()
    {
        var deeper = new InvalidOperationException("must-not-appear");
        var inner = new TestException("inner\r\n\t\u0001message", unchecked((int)0x80070005), deeper);
        var outer = new TestException("outer\r\n\t\u0002message", unchecked((int)0x80004005), inner);

        var diagnostic = EtabsApiDiagnosticFormatter.Exception("cFile.OpenFile\r\n", outer);

        Assert.StartsWith("ETABS_COM_OPERATION_FAILED", diagnostic, StringComparison.Ordinal);
        Assert.Contains("hresult=0x80004005", diagnostic, StringComparison.Ordinal);
        Assert.Contains("innerHResult=0x80070005", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-appear", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostic, char.IsControl);
        Assert.True(diagnostic.Length <= EtabsApiDiagnosticFormatter.TotalLimit);
    }

    [Fact]
    public void Exception_enforces_operation_type_message_and_total_limits()
    {
        var operation = new string('o', EtabsApiDiagnosticFormatter.OperationLimit + 20);
        var longType = CreateExceptionWithTypeName(
            new string('T', EtabsApiDiagnosticFormatter.ExceptionTypeLimit + 20),
            new string('m', EtabsApiDiagnosticFormatter.MessageLimit + 20));
        var diagnostic = EtabsApiDiagnosticFormatter.Exception(operation, longType);
        var fields = ParseFields(diagnostic);

        Assert.Equal(EtabsApiDiagnosticFormatter.OperationLimit, fields["operation"].Length);
        Assert.EndsWith("…", fields["operation"], StringComparison.Ordinal);
        Assert.Equal(EtabsApiDiagnosticFormatter.ExceptionTypeLimit, fields["exceptionType"].Length);
        Assert.EndsWith("…", fields["exceptionType"], StringComparison.Ordinal);
        Assert.Equal(EtabsApiDiagnosticFormatter.MessageLimit, fields["message"].Length);
        Assert.EndsWith("…", fields["message"], StringComparison.Ordinal);
        Assert.True(diagnostic.Length <= EtabsApiDiagnosticFormatter.TotalLimit);
    }

    [Fact]
    public void Exception_caps_inner_components_independently()
    {
        var inner = CreateExceptionWithTypeName(
            new string('I', EtabsApiDiagnosticFormatter.ExceptionTypeLimit + 40),
            new string('n', EtabsApiDiagnosticFormatter.MessageLimit + 40));
        var outer = new COMException(
            new string('m', EtabsApiDiagnosticFormatter.MessageLimit + 40),
            unchecked((int)0x80004005));
        var wrapper = new TestException(outer.Message, outer.HResult, inner);
        var diagnostic = EtabsApiDiagnosticFormatter.Exception("cSapModel.Test", wrapper);
        var fields = ParseFields(diagnostic);

        Assert.Equal(EtabsApiDiagnosticFormatter.MessageLimit, fields["message"].Length);
        Assert.Equal(EtabsApiDiagnosticFormatter.ExceptionTypeLimit, fields["innerExceptionType"].Length);
        Assert.Equal(EtabsApiDiagnosticFormatter.MessageLimit, fields["innerMessage"].Length);
        Assert.All(
            new[] { fields["message"], fields["innerExceptionType"], fields["innerMessage"] },
            value => Assert.EndsWith("…", value, StringComparison.Ordinal));
        Assert.True(diagnostic.Length <= EtabsApiDiagnosticFormatter.TotalLimit);
    }

    [Fact]
    public void Api_return_preserves_exact_signed_code_and_bounds_operation()
    {
        var diagnostic = EtabsApiDiagnosticFormatter.ApiReturn(
            new string('o', EtabsApiDiagnosticFormatter.OperationLimit + 1),
            -7);
        var fields = ParseFields(diagnostic);

        Assert.StartsWith("ETABS_API_CALL_FAILED", diagnostic, StringComparison.Ordinal);
        Assert.Equal("-7", fields["returnCode"]);
        Assert.Equal(EtabsApiDiagnosticFormatter.OperationLimit, fields["operation"].Length);
        Assert.EndsWith("…", fields["operation"], StringComparison.Ordinal);
        Assert.True(diagnostic.Length <= EtabsApiDiagnosticFormatter.TotalLimit);
    }

    private static Dictionary<string, string> ParseFields(string diagnostic) => diagnostic
        .Split("; ", StringSplitOptions.None)
        .Skip(1)
        .Select(field => field.Split('=', 2))
        .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

    private static Exception CreateExceptionWithTypeName(string typeName, string message)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"DiagnosticFormatterTests{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("main");
        var type = module.DefineType(typeName, TypeAttributes.Public, typeof(Exception));
        var baseConstructor = typeof(Exception).GetConstructor([typeof(string)])!;
        var constructor = type.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(string)]);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, baseConstructor);
        il.Emit(OpCodes.Ret);
        return (Exception)Activator.CreateInstance(type.CreateType()!, message)!;
    }

    private sealed class TestException : Exception
    {
        public TestException(string message, int hresult, Exception? inner = null)
            : base(message, inner)
        {
            HResult = hresult;
        }
    }
}
