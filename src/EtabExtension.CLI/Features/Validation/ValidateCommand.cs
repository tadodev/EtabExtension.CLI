using System.CommandLine;
using EtabExtension.CLI.Shared.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.Validation;

public static class ValidateCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "validate",
            "Check ETABS installation, file validity, and analysis status"
        );

        var fileOption = new Option<string?>(name: "--file")
        {
            Description = "Path to ETABS file (.edb or .e2k) to validate",
            Required = true
        };
        fileOption.Aliases.Add("-f");

        command.Options.Add(fileOption);

        command.SetAction(async parseResult =>
        {
            var file = parseResult.GetValue(fileOption);

            var service = services.GetRequiredService<IValidationService>();
            var result = await service.ValidateAsync(file);

            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}