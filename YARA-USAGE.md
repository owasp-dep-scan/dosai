# Using Dosai with YARA for .NET Security Analysis

## Introduction

Dosai is a .NET source and assembly inspection tool that extracts detailed metadata from C# source files, compiled assemblies, and NuGet packages. When combined with YARA, it provides a powerful framework for identifying suspicious patterns in .NET applications. This document explains how security analysts and reverse engineers can leverage these tools together for malware analysis, vulnerability assessment, and security research.

## How Dosai Works

Dosai uses Microsoft.CodeAnalysis (Roslyn) API and .NET Reflection to extract metadata from both source code and compiled assemblies. It provides a unified view of code structure across different .NET compilation outputs.

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   Source Code   │    │  .NET Assembly  │    │   .nupkg File   │
│   (.cs, .vb)    │    │  (.dll, .exe)   │    │                 │
└─────────┬───────┘    └─────────┬───────┘    └─────────┬───────┘
          │                      │                      │
          │                      │                      │ (Extract)
          ▼                      ▼                      ▼
    ┌─────────────┐      ┌─────────────┐        ┌─────────────┐
    │  Roslyn     │      │  Reflection │        │  Extracted  │
    │  Analysis   │      │  Analysis   │───────▶│  Directory  │
    │             │      │             │        │             │
    └──────┬──────┘      └──────┬──────┘        └──────┬──────┘
           │                    │                      │
           │                    │                      │
           └────────────────────┼──────────────────────┘
                                │
                                ▼
                        ┌─────────────────┐
                        │  Unified JSON   │
                        │   Output Model  │
                        │ (MethodsSlice)  │
                        └─────────────────┘
```

## Dosai JSON Output Schema

The JSON output from Dosai contains the following key components:

- **Dependencies**: List of external namespaces/libraries used
- **Methods**: List of method objects detailing signatures, locations, parameters, return types
- **MethodCalls**: List of method call objects representing invocations found in source code
- **Properties, Fields, Events, Constructors**: Lists of corresponding member types
- **CallGraph**: List of method call edges defining the call graph structure
- **AssemblyInformation**: List of assembly information objects
- **SourceAssemblyMapping**: List of mappings linking source locations to assembly definitions

## Using Dosai

### Basic Usage

To analyze a .NET assembly and generate JSON output:

```bash
Dosai methods --path /path/to/assembly.dll --o analysis.json
```

To analyze source code:

```bash
Dosai methods --path /path/to/source.cs --o analysis.json
```

To analyze a NuGet package:

```bash
Dosai methods --path /path/to/package.nupkg --o analysis.json
```

## YARA Integration with Dosai

YARA can be used to scan the JSON output from Dosai to identify suspicious patterns. The following sections explain how to use the provided sample YARA rules for security analysis.

### YARA Rule Categories

1. **Suspicious API Usage**: Detects usage of potentially malicious APIs
2. **Anti-Analysis Techniques**: Identifies methods used to evade analysis
3. **Persistence Mechanisms**: Finds code that establishes persistence
4. **Data Exfiltration Patterns**: Detects potential data theft mechanisms
5. **Suspicious Assembly Metadata**: Identifies suspicious assembly properties
6. **Call Graph Anomalies**: Detects unusual method call patterns
7. **Suspicious Dependencies**: Identifies potentially malicious dependencies

### Running YARA with Dosai Output

```bash
yara -r -m contrib/yara-rules/sample-rules.yar analysis.json
```

The `-r` flag enables recursive scanning, and `-m` shows metadata.

## Security Analysis Scenarios

### 1. Malware Analysis

When analyzing a suspicious .NET executable:

```bash
# Generate Dosai output
Dosai methods --path suspicious.exe --o malware_analysis.json

# Scan with YARA rules
yara -r -m contrib/yara-rules/sample-rules.yar malware_analysis.json
```

Look for matches in these categories:

- Suspicious API Usage (especially Process.Start, File operations, Registry access)
- Anti-Analysis Techniques (debugger detection, VM detection)
- Data Exfiltration Patterns (network operations, file compression)
- Obfuscated Code (unusual naming patterns)

### 2. Vulnerability Assessment

To identify potentially vulnerable code patterns:

```bash
# Analyze source code
Dosai methods --path /path/to/source --o vuln_analysis.json

# Focus on specific patterns
yara -r -m contrib/yara-rules/sample-rules.yar vuln_analysis.json | grep -E "(API_Usage|Data_Exfiltration)"
```

### 3. Supply Chain Analysis

To analyze third-party dependencies:

```bash
# Analyze NuGet package
Dosai methods --path package.nupkg --o supply_chain.json

# Check for suspicious dependencies
yara -r -m contrib/yara-rules/sample-rules.yar supply_chain.json | grep "Suspicious_Dependencies"
```

## Understanding sample YARA Rule Matches

### Suspicious API Usage

When this rule matches, it indicates the presence of potentially dangerous API calls:

- Process creation APIs: Could indicate code execution capabilities
- File operations: May be used for data theft or system modification
- Registry operations: Often used for persistence or configuration changes
- Network operations: Could indicate data exfiltration or command and control
- Cryptographic operations: May be used for data obfuscation or ransomware
- Reflection: Commonly used in obfuscation and dynamic code execution

### Anti-Analysis Techniques

Matches in this category suggest attempts to evade detection:

- Debugger detection: Code that checks if a debugger is attached
- VM detection: Code that identifies virtualized environments
- Timing checks: Techniques to detect sandbox environments
- Sandbox detection: Methods to identify analysis environments

### Persistence Mechanisms

These matches indicate code that may establish persistence:

- Registry persistence: Adding entries to Run or RunOnce keys
- Scheduled tasks: Creating tasks for periodic execution
- Startup folder: Placing executables in startup directories
- WMI event subscription: Using WMI for persistence

### Data Exfiltration Patterns

Matches here suggest potential data theft:

- Network data transmission: HTTP POST, FTP uploads, SMTP sending
- Data collection: Clipboard access, screenshots, keylogging
- File compression: Compressing data before exfiltration

### Suspicious Assembly Metadata

These matches indicate potentially deceptive assembly properties:

- Minimal metadata: Assemblies with limited identifying information
- Suspicious company names: Impersonating legitimate companies
- Suspicious assembly names: Mimicking system processes
- Strong name verification bypass: Techniques to load assemblies with invalid signatures

### Call Graph Anomalies

These matches indicate unusual method call patterns:

- Highly connected methods: Methods that call many other methods (potential dispatchers)
- Recursive calls: Methods that call themselves (potential infinite loops or obfuscation)
- Unusual call patterns: Calls between unrelated namespaces or assemblies

### Suspicious Dependencies

These matches indicate potentially malicious libraries:

- Known malicious libraries: Obfuscators or packers associated with malware
- Unusual namespace dependencies: Libraries not typically used in legitimate applications
- Network libraries: Libraries that facilitate network communication
- Cryptographic libraries: Libraries that provide encryption capabilities

## Advanced Analysis Techniques

### Customizing YARA Rules

You can extend the provided YARA rules to detect specific patterns relevant to your analysis:

```yara
rule Custom_Suspicious_Pattern
{
    meta:
        description = "Detects custom suspicious pattern"
        author = "Security Analyst"

    strings:
        $pattern1 = /"CalledMethod":\s*"Your\.Target\.Namespace\.Method"/
        $pattern2 = /"ClassName":\s*"SuspiciousClassName"/

    condition:
        any of them
}
```

### Combining Multiple Rules

Use YARA's condition syntax to create more sophisticated detection logic:

```yara
rule Complex_Malware_Indicator
{
    meta:
        description = "Detects complex malware patterns"

    condition:
        (DotNet_Suspicious_API_Usage and DotNet_Obfuscated_Code) or
        (DotNet_Anti_Analysis_Techniques and DotNet_Persistence_Mechanisms)
}
```
