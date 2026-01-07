using System.CommandLine;
using EtabExtension.CLI.Shared.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.GenerateE2K;

public static class GenerateE2KCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "generate-e2k",
            "Convert ETABS .edb file to .e2k text format"
        );

        var inputFileOption = new Option<string>(name: "--file")
        {
            Description = "Path to input ETABS file (.edb)",
            Required = true
        };
        inputFileOption.Aliases.Add("-f");

        var outputFileOption = new Option<string?>(name: "--output")
        {
            Description = "Path for output .e2k file (optional, defaults to same directory as input)",
            Required = false
        };
        outputFileOption.Aliases.Add("-o");

        var overwriteOption = new Option<bool>(name: "--overwrite")
        {
            Description = "Overwrite output file if it already exists",
            Required = false
        };
        overwriteOption.Aliases.Add("--force");

        command.Options.Add(inputFileOption);
        command.Options.Add(outputFileOption);
        command.Options.Add(overwriteOption);

        command.SetAction(async parseResult =>
        {
            var inputFile = parseResult.GetValue(inputFileOption);
            var outputFile = parseResult.GetValue(outputFileOption);
            var overwrite = parseResult.GetValue(overwriteOption);

            if (string.IsNullOrEmpty(inputFile))
            {
                Console.WriteLine("Error: --file option is required");
                Environment.Exit(1);
                return;
            }

            var service = services.GetRequiredService<IGenerateE2KService>();
            var result = await service.GenerateE2KAsync(inputFile, outputFile, overwrite);

            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}