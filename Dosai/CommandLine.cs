using System.CommandLine;

namespace Depscan;

public class CommandLine
{
    private const string DefaultOutputFile = "dosai.json";

    public static int Main(string[] args)
    {
        var rootCommand = new RootCommand("Dotnet Source and Assembly Inspector (Dosai) is a tool to list details about the namespaces and methods from sources and assemblies.");

        var pathOption = new Option<string?>("--path")
        {
            Description = "The file or directory to inspect",
            Required = true
        };

        var outputFileOption = new Option<string?>("--o")
        {
            Description = $"The output file location and name",
            DefaultValueFactory = parseResult => DefaultOutputFile
        };

        rootCommand.Options.Add(pathOption);
        rootCommand.Options.Add(outputFileOption);

        var methodsCommand = new Command("methods", "Retrieve details about the methods")
        {
            pathOption,
            outputFileOption
        };

        rootCommand.Subcommands.Add(methodsCommand);

        methodsCommand.SetAction(parseResult =>
            {
                var path = parseResult.GetValue(pathOption);
                var outputFile = parseResult.GetValue(outputFileOption);
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

                return 0;
            });

        return rootCommand.Parse(args).Invoke();
    }
}