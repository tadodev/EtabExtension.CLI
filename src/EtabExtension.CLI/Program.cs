using System.CommandLine;
using EtabExtension.CLI.Features.Validation;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

//Register shared infrastructure
builder.Services.AddEtabsInfrastructure();

//Register features
builder.Services.AddValidationFeature();

var app = builder.Build();

//Create root command
var rootCommand = new RootCommand("EtabExtension.CLI - Etabs Automation CLI")
{
    Description = "A CLI tool for automating ETABS operations, designed to be called from a Rust Tauri backend application. All commands return JSON output."
};

// Add all feature commands
rootCommand.Subcommands.Add(ValidateCommand.Create(app.Services));

return await rootCommand.Parse(args).InvokeAsync();