using System.CommandLine;

namespace Dosai;

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


        var methodsCommand = new Command("methods", "Retrieve details about the methods")
        {
            pathOption,
            outputFileOption
        };

        rootCommand.AddCommand(methodsCommand);

        methodsCommand.SetHandler((path, outputFile) =>
        {
            var result = Dosai.GetMethods(path!);
            File.WriteAllText(outputFile!, result);
        },
        pathOption, outputFileOption);

        return await rootCommand.InvokeAsync(args);
    }
}