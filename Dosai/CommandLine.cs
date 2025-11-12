using System.CommandLine;

namespace Depscan;

public class CommandLine
{
    private const string DefaultOutputFile = "dosai.json";

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Dotnet Source and Assembly Inspector (Dosai) is a tool to list details about the namespaces and methods from sources and assemblies.");

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
                string result;
                if (Path.GetExtension(path)!.Equals(".nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    result = Dosai.GetMethodsFromNupkg(path!);
                }
                else
                {
                    result = Dosai.GetMethods(path!);
                }
                File.WriteAllText(outputFile!, result);
            },
            pathOption, outputFileOption);

        return await rootCommand.InvokeAsync(args);
    }
}