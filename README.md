# Dotnet Source and Assembly Inspector (Dosai)

List details about the namespaces and methods from a C# .NET source file or assembly

## Usage
`Dosai [command] [options]`

### Options:
`--path [path]` (REQUIRED)  The file or directory to inspect  
`--version`                 Show version information  
`-?`, `-h`, `--help`        Show help and usage information  

### Commands:
`namespaces`  Retrieve the namespaces details
`methods`     Retrieve the methods details

---

## Developers

### Running code directly from the code repository
1. `dotnet build ./Dosai`
2. Run a command such as:
   * `dotnet run --project ./Dosai/ namespaces --path ./Dosai/bin/x64/Debug/net8.0/Dosai.dll`
   * `dotnet run --project ./Dosai/ methods --path ./Dosai/Dosai.cs`

### Generating a self-contained executable for a system
* Windows: `dotnet publish -r win-x64 --self-contained`
* Linux: `dotnet publish -r linux-x64 --self-contained`

### Invoking the self-contained executable
* Windows: `Dosai.exe namespaces --path ./Dosai/bin/x64/Debug/net8.0/Dosai.dll`
* Linux: `Dosai methods --path ./Dosai/Dosai.cs`

### Run unit tests
`dotnet test`