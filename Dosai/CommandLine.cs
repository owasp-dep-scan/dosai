using System.CommandLine;

namespace Depscan;

public class CommandLine
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Dosai");

        var pathOption = new Option<string?>(
            name: "--path",
            description: "The file or directory to inspect")
            { IsRequired = true };
        
        rootCommand.AddGlobalOption(pathOption);

        var namespaceCommand = new Command("namespaces", "Retrieve the namespaces details")
        {
            pathOption
        };

        var methodCommand = new Command("methods", "Retrieve the methods details")
        {
            pathOption
        };

        rootCommand.AddCommand(namespaceCommand);
        rootCommand.AddCommand(methodCommand);

        namespaceCommand.SetHandler((path) =>
        {
            var result = Dosai.GetNamespaces(path!);
            Console.WriteLine(result);
        },
        pathOption);

        methodCommand.SetHandler((path) =>
        {
            var result = Dosai.GetMethods(path!);
            Console.WriteLine(result);
        },
        pathOption);

        return await rootCommand.InvokeAsync(args);
    }
}