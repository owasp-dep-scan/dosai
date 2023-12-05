using System.CommandLine;

namespace Depscan;

public class CommandLine
{
    private const string DefaultOutputFile = "dosai.json";

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Dosai");

        var pathOption = new Option<string?>(
            name: "--path",
            description: "The file or directory to inspect")
            { IsRequired = true };
        
        var outputFileOption = new Option<string?>(
            name: "--o",
            description: $"The output file location and name, default value when option not provided is '{DefaultOutputFile}'");
        outputFileOption.SetDefaultValue(DefaultOutputFile);
        
        rootCommand.AddGlobalOption(pathOption);

        var namespaceCommand = new Command("namespaces", "Retrieve the namespaces details")
        {
            pathOption,
            outputFileOption
        };

        var methodCommand = new Command("methods", "Retrieve the methods details")
        {
            pathOption,
            outputFileOption
        };

        rootCommand.AddCommand(namespaceCommand);
        rootCommand.AddCommand(methodCommand);

        namespaceCommand.SetHandler((path, outputFile) =>
        {
            var result = Dosai.GetNamespaces(path!);
            File.WriteAllText(outputFile!, result);
        },
        pathOption, outputFileOption);

        methodCommand.SetHandler((path, outputFile) =>
        {
            var result = Dosai.GetMethods(path!);
            File.WriteAllText(outputFile!, result);
        },
        pathOption, outputFileOption);

        return await rootCommand.InvokeAsync(args);
    }
}