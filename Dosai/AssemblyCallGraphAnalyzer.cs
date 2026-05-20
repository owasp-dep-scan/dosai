using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Depscan;

internal static class AssemblyCallGraphAnalyzer
{
    private static readonly Dictionary<short, OpCode> SingleByteOpCodes = typeof(OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.GetValue(null) is OpCode opCode && opCode.Size == 1)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((short)(ushort)opCode.Value));

    private static readonly Dictionary<short, OpCode> MultiByteOpCodes = typeof(OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.GetValue(null) is OpCode opCode && opCode.Size == 2)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((short)(ushort)opCode.Value));

    public static (List<MethodCalls> Calls, CallGraph Graph) Analyze(string path, IReadOnlyList<Method> knownMethods)
    {
        var assemblyPaths = GetAssemblyFiles(path);
        var methodLookup = knownMethods
            .Where(method => method.MetadataToken != 0 && !string.IsNullOrWhiteSpace(method.AssemblySignature))
            .GroupBy(method => (Path.GetFullPath(method.Path ?? string.Empty), method.MetadataToken))
            .ToDictionary(group => group.Key, group => group.First());
        var calls = new List<MethodCalls>();
        var nodes = new Dictionary<string, MethodNode>(StringComparer.Ordinal);
        var edges = new List<MethodCallEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in knownMethods.Where(method => !string.IsNullOrWhiteSpace(method.AssemblySignature)))
        {
            AddNode(nodes, method.AssemblySignature!, method.Name ?? method.AssemblySignature!, method.ClassName, method.Namespace, method.FileName, method.Assembly, method.Module, method.Name == ".ctor" ? "Constructor" : "Method", method.LineNumber, method.ColumnNumber, isExternal: false);
        }

        foreach (var assemblyPath in assemblyPaths.Where(IsManagedAssembly))
        {
            try
            {
                using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata) continue;
                var reader = peReader.GetMetadataReader();
                var sourceMap = LoadPortablePdbSourceMap(assemblyPath);
                var assemblyFullPath = Path.GetFullPath(assemblyPath);
                var instantiatedTypes = CollectInstantiatedTypes(peReader, reader, assemblyPath, sourceMap);
                var candidateMethods = knownMethods
                    .Where(method => string.Equals(Path.GetFullPath(method.Path ?? string.Empty), assemblyFullPath, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(method.AssemblySignature))
                    .ToList();
                foreach (var methodHandle in reader.MethodDefinitions)
                {
                    var methodDefinition = reader.GetMethodDefinition(methodHandle);
                    if (methodDefinition.RelativeVirtualAddress == 0) continue;
                    var sourceMethod = methodLookup.TryGetValue((assemblyFullPath, MetadataTokens.GetToken(methodHandle)), out var knownMethod)
                        ? knownMethod
                        : CreateFallbackMethod(reader, methodHandle, methodDefinition, assemblyPath, sourceMap);
                    var sourceId = sourceMethod.AssemblySignature ?? BuildMethodSymbol(reader, methodHandle, methodDefinition, assemblyPath).Symbol;
                    AddNode(nodes, sourceId, sourceMethod.Name ?? sourceId, sourceMethod.ClassName, sourceMethod.Namespace, sourceMethod.FileName, sourceMethod.Assembly, sourceMethod.Module, sourceMethod.Name == ".ctor" ? "Constructor" : "Method", sourceMethod.LineNumber, sourceMethod.ColumnNumber, isExternal: false);
                    var body = peReader.GetMethodBody(methodDefinition.RelativeVirtualAddress);
                    foreach (var instruction in DecodeInstructions(body.GetILReader()))
                    {
                        if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Newobj && instruction.OpCode != OpCodes.Ldftn && instruction.OpCode != OpCodes.Ldvirtftn)
                        {
                            continue;
                        }

                        if (instruction.Operand is not int token || ResolveMember(reader, token, assemblyPath, sourceMap) is not { } target)
                        {
                            continue;
                        }

                        var callType = instruction.OpCode == OpCodes.Newobj
                            ? CallType.ConstructorCall
                            : instruction.OpCode == OpCodes.Ldftn || instruction.OpCode == OpCodes.Ldvirtftn
                                ? CallType.DelegateInvoke
                                : CallType.MethodCall;
                        var targetId = ResolveInternalTargetId(assemblyFullPath, target.MetadataToken, target.Symbol, methodLookup);
                        AddNode(nodes, targetId, target.Name, target.ClassName, target.Namespace, Path.GetFileName(target.FilePath), target.AssemblyName, Path.GetFileName(target.FilePath), callType == CallType.ConstructorCall ? "Constructor" : "Method", target.LineNumber, target.ColumnNumber, isExternal: !target.IsInternal);
                        var location = sourceMap.Resolve(MetadataTokens.GetToken(methodHandle), instruction.Offset + 1, assemblyPath);
                        var call = new MethodCalls
                        {
                            Path = location.FilePath,
                            FileName = Path.GetFileName(location.FilePath),
                            Assembly = target.AssemblyName,
                            Module = Path.GetFileName(target.FilePath),
                            Namespace = target.Namespace,
                            ClassName = target.ClassName,
                            CalledMethod = target.Name,
                            LineNumber = location.LineNumber,
                            ColumnNumber = location.ColumnNumber,
                            Arguments = Enumerable.Repeat("?", Math.Max(0, target.ParameterCount)).ToList(),
                            ArgumentExpressions = Enumerable.Repeat("?", Math.Max(0, target.ParameterCount)).ToList(),
                            CallType = callType,
                            SourceId = sourceId,
                            TargetId = targetId,
                            CallerMethod = sourceMethod.Name,
                            CallerNamespace = sourceMethod.Namespace,
                            CallerClass = sourceMethod.ClassName,
                            IsInternal = target.IsInternal
                        };
                        calls.Add(call);
                        var edgeKey = $"{sourceId}\u001f{targetId}\u001f{location.FilePath}\u001f{location.LineNumber}\u001f{location.ColumnNumber}\u001f{callType}";
                        if (edgeKeys.Add(edgeKey))
                        {
                            edges.Add(new MethodCallEdge
                            {
                                SourceId = sourceId,
                                TargetId = targetId,
                                CallLocation = new CallLocation { FileName = Path.GetFileName(location.FilePath), LineNumber = location.LineNumber, ColumnNumber = location.ColumnNumber },
                                FileName = Path.GetFileName(location.FilePath),
                                IsInternal = target.IsInternal,
                                CalledMethodName = target.Name,
                                SourceName = sourceMethod.Name,
                                TargetName = target.Name,
                                Arguments = call.Arguments,
                                ArgumentExpressions = call.ArgumentExpressions,
                                CallType = callType
                            });
                        }

                        if (instruction.OpCode == OpCodes.Callvirt)
                        {
                            foreach (var candidate in candidateMethods.Where(method => method.Name == target.Name && instantiatedTypes.Contains(method.ClassName ?? string.Empty)))
                            {
                                var candidateId = candidate.AssemblySignature!;
                                AddNode(nodes, candidateId, candidate.Name ?? candidateId, candidate.ClassName, candidate.Namespace, candidate.FileName, candidate.Assembly, candidate.Module, "Method", candidate.LineNumber, candidate.ColumnNumber, isExternal: false);
                                var candidateKey = $"{sourceId}\u001f{candidateId}\u001f{location.FilePath}\u001f{location.LineNumber}\u001f{location.ColumnNumber}\u001fVirtualCandidate";
                                if (edgeKeys.Add(candidateKey))
                                {
                                    calls.Add(new MethodCalls
                                    {
                                        Path = location.FilePath,
                                        FileName = Path.GetFileName(location.FilePath),
                                        Assembly = candidate.Assembly,
                                        Module = candidate.Module,
                                        Namespace = candidate.Namespace,
                                        ClassName = candidate.ClassName,
                                        CalledMethod = candidate.Name,
                                        LineNumber = location.LineNumber,
                                        ColumnNumber = location.ColumnNumber,
                                        Arguments = call.Arguments,
                                        ArgumentExpressions = ["virtual-candidate"],
                                        CallType = CallType.MethodCall,
                                        SourceId = sourceId,
                                        TargetId = candidateId,
                                        CallerMethod = sourceMethod.Name,
                                        CallerNamespace = sourceMethod.Namespace,
                                        CallerClass = sourceMethod.ClassName,
                                        IsInternal = true
                                    });
                                    edges.Add(new MethodCallEdge
                                    {
                                        SourceId = sourceId,
                                        TargetId = candidateId,
                                        CallLocation = new CallLocation { FileName = Path.GetFileName(location.FilePath), LineNumber = location.LineNumber, ColumnNumber = location.ColumnNumber },
                                        FileName = Path.GetFileName(location.FilePath),
                                        IsInternal = true,
                                        CalledMethodName = candidate.Name,
                                        SourceName = sourceMethod.Name,
                                        TargetName = candidate.Name,
                                        Arguments = call.Arguments,
                                        ArgumentExpressions = ["virtual-candidate"],
                                        CallType = CallType.MethodCall
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Methods already best-effort skips unloadable assemblies; keep call graph extraction equally non-fatal.
            }
        }

        var orderedEdges = edges
            .OrderBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ThenBy(edge => edge.CallLocation.FileName, StringComparer.Ordinal)
            .ThenBy(edge => edge.CallLocation.LineNumber)
            .ThenBy(edge => edge.CallLocation.ColumnNumber)
            .Select((edge, index) =>
            {
                edge.Id = $"ae{index + 1}";
                return edge;
            })
            .ToList();
        return (calls, new CallGraph { Nodes = nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(), Edges = orderedEdges });
    }

    private static string ResolveInternalTargetId(string assemblyPath, int metadataToken, string fallbackSymbol, IReadOnlyDictionary<(string Path, int Token), Method> methodLookup) =>
        metadataToken != 0 && methodLookup.TryGetValue((assemblyPath, metadataToken), out var method) && !string.IsNullOrWhiteSpace(method.AssemblySignature)
            ? method.AssemblySignature!
            : fallbackSymbol;

    private static HashSet<string> CollectInstantiatedTypes(PEReader peReader, MetadataReader reader, string assemblyPath, AssemblyCallSourceMap sourceMap)
    {
        var instantiatedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue;
            try
            {
                foreach (var instruction in DecodeInstructions(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILReader()))
                {
                    if (instruction.OpCode == OpCodes.Newobj && instruction.Operand is int token && ResolveMember(reader, token, assemblyPath, sourceMap) is { } member)
                    {
                        instantiatedTypes.Add(member.ClassName);
                    }
                }
            }
            catch
            {
                // Best-effort type collection only.
            }
        }
        return instantiatedTypes;
    }

    private static Method CreateFallbackMethod(MetadataReader reader, MethodDefinitionHandle methodHandle, MethodDefinition methodDefinition, string assemblyPath, AssemblyCallSourceMap sourceMap)
    {
        var symbol = BuildMethodSymbol(reader, methodHandle, methodDefinition, assemblyPath);
        var location = sourceMap.Resolve(MetadataTokens.GetToken(methodHandle), 1, assemblyPath);
        return new Method
        {
            Path = location.FilePath,
            FileName = Path.GetFileName(location.FilePath),
            Assembly = Path.GetFileNameWithoutExtension(assemblyPath),
            Module = Path.GetFileName(assemblyPath),
            Namespace = symbol.Namespace,
            ClassName = symbol.ClassName,
            Name = symbol.Name,
            ReturnType = symbol.ReturnType,
            LineNumber = location.LineNumber,
            ColumnNumber = location.ColumnNumber,
            AssemblySignature = symbol.Symbol,
            MetadataToken = MetadataTokens.GetToken(methodHandle)
        };
    }

    private static void AddNode(Dictionary<string, MethodNode> nodes, string id, string name, string? className, string? namespaceName, string? fileName, string? assembly, string? module, string kind, int lineNumber, int columnNumber, bool isExternal)
    {
        nodes.TryAdd(id, new MethodNode
        {
            Id = id,
            Name = name,
            Label = string.IsNullOrWhiteSpace(className) ? name : $"{className}.{name}",
            ClassName = className ?? string.Empty,
            Namespace = namespaceName ?? string.Empty,
            FileName = fileName ?? string.Empty,
            Assembly = assembly,
            Module = module,
            Kind = kind,
            LineNumber = lineNumber,
            ColumnNumber = columnNumber,
            IsExternal = isExternal
        });
    }

    private static AssemblyCallMember? ResolveMember(MetadataReader reader, int metadataToken, string assemblyPath, AssemblyCallSourceMap sourceMap)
    {
        var handle = MetadataTokens.EntityHandle(metadataToken);
        return handle.Kind switch
        {
            HandleKind.MemberReference => ResolveMemberReference(reader, (MemberReferenceHandle)handle, assemblyPath),
            HandleKind.MethodDefinition => ResolveMethodDefinition(reader, (MethodDefinitionHandle)handle, assemblyPath, sourceMap),
            HandleKind.MethodSpecification => ResolveMethodSpecification(reader, (MethodSpecificationHandle)handle, assemblyPath, sourceMap),
            _ => null
        };
    }

    private static AssemblyCallMember ResolveMemberReference(MetadataReader reader, MemberReferenceHandle handle, string assemblyPath)
    {
        var member = reader.GetMemberReference(handle);
        var name = reader.GetString(member.Name);
        var containingType = ResolveMemberParent(reader, member.Parent);
        var signature = ReadSignatureInfo(reader, member.Signature, null);
        var symbol = FormatSymbol(containingType.FullName, name, signature);
        return new AssemblyCallMember(symbol, name, containingType.Name, containingType.Namespace, containingType.AssemblyName, assemblyPath, signature.ParameterCount, 0, false, signature.ReturnType, 0, 0);
    }

    private static AssemblyCallMember ResolveMethodDefinition(MetadataReader reader, MethodDefinitionHandle handle, string assemblyPath, AssemblyCallSourceMap sourceMap)
    {
        var method = reader.GetMethodDefinition(handle);
        var symbol = BuildMethodSymbol(reader, handle, method, assemblyPath);
        var location = sourceMap.Resolve(MetadataTokens.GetToken(handle), 1, assemblyPath);
        return new AssemblyCallMember(symbol.Symbol, symbol.Name, symbol.ClassName, symbol.Namespace, symbol.AssemblyName, assemblyPath, symbol.ParameterCount, MetadataTokens.GetToken(handle), true, symbol.ReturnType, location.LineNumber, location.ColumnNumber);
    }

    private static AssemblyCallMember? ResolveMethodSpecification(MetadataReader reader, MethodSpecificationHandle handle, string assemblyPath, AssemblyCallSourceMap sourceMap)
    {
        var specification = reader.GetMethodSpecification(handle);
        return specification.Method.Kind is HandleKind.MemberReference or HandleKind.MethodDefinition
            ? ResolveMember(reader, MetadataTokens.GetToken(specification.Method), assemblyPath, sourceMap)
            : null;
    }

    private static AssemblyCallSymbol BuildMethodSymbol(MetadataReader reader, MethodDefinitionHandle handle, MethodDefinition method, string assemblyPath)
    {
        var declaringType = reader.GetTypeDefinition(method.GetDeclaringType());
        var type = GetFullTypeName(reader, declaringType);
        var name = reader.GetString(method.Name);
        var signature = ReadSignatureInfo(reader, method.Signature, method.Attributes);
        var symbol = FormatSymbol(type.FullName, name, signature);
        return new AssemblyCallSymbol(symbol, name, type.Name, type.Namespace, Path.GetFileNameWithoutExtension(assemblyPath), signature.ParameterCount, signature.ReturnType);
    }

    private static string FormatSymbol(string containingType, string name, AssemblyCallSignature signature)
    {
        var symbol = $"{containingType}.{name}({string.Join(',', signature.ParameterTypes)})";
        if (!signature.ReturnsVoid && name is not ".ctor" and not ".cctor") symbol += $":{signature.ReturnType}";
        return symbol;
    }

    private static AssemblyCallType ResolveMemberParent(MetadataReader reader, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => GetFullTypeName(reader, reader.GetTypeReference((TypeReferenceHandle)parent)),
        HandleKind.TypeDefinition => GetFullTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)parent)),
        HandleKind.TypeSpecification => new AssemblyCallType("<type-spec>", string.Empty, "<type-spec>", string.Empty),
        _ => new AssemblyCallType(string.Empty, string.Empty, string.Empty, string.Empty)
    };

    private static AssemblyCallType GetFullTypeName(MetadataReader reader, TypeReference type)
    {
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name).Replace('/', '.');
        var fullName = string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
        return new AssemblyCallType(fullName, name, ns, ResolveTypeReferenceAssemblyName(reader, type.ResolutionScope));
    }

    private static AssemblyCallType GetFullTypeName(MetadataReader reader, TypeDefinition type)
    {
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name).Replace('/', '.');
        var fullName = string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
        return new AssemblyCallType(fullName, name, ns, string.Empty);
    }

    private static string ResolveTypeReferenceAssemblyName(MetadataReader reader, EntityHandle scope) => scope.Kind switch
    {
        HandleKind.AssemblyReference => reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
        HandleKind.TypeReference => ResolveTypeReferenceAssemblyName(reader, reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope),
        _ => string.Empty
    };

    private static AssemblyCallSignature ReadSignatureInfo(MetadataReader reader, BlobHandle signatureHandle, System.Reflection.MethodAttributes? attributes)
    {
        try
        {
            var blob = reader.GetBlobReader(signatureHandle);
            if (blob.Length == 0) return new AssemblyCallSignature([], "void", true, false, 0);
            var header = blob.ReadByte();
            if ((header & 0x10) != 0 && blob.RemainingBytes > 0) _ = blob.ReadCompressedInteger();
            var parameterCount = blob.RemainingBytes > 0 ? blob.ReadCompressedInteger() : 0;
            var returnType = blob.RemainingBytes > 0 ? ReadSignatureType(reader, ref blob) : "void";
            var parameterTypes = new List<string>();
            for (var i = 0; i < parameterCount && blob.RemainingBytes > 0; i++) parameterTypes.Add(ReadSignatureType(reader, ref blob));
            var hasThis = attributes.HasValue ? (attributes.Value & System.Reflection.MethodAttributes.Static) == 0 : (header & 0x20) != 0;
            return new AssemblyCallSignature(parameterTypes, returnType, string.Equals(returnType, "void", StringComparison.OrdinalIgnoreCase), hasThis, parameterCount);
        }
        catch
        {
            return new AssemblyCallSignature(Enumerable.Repeat("?", 0).ToList(), string.Empty, false, false, 0);
        }
    }

    private static string ReadSignatureType(MetadataReader reader, ref BlobReader blob)
    {
        if (blob.RemainingBytes <= 0) return string.Empty;
        var raw = blob.ReadByte();
        if (raw is 0x11 or 0x12)
        {
            var coded = blob.ReadCompressedInteger();
            return ResolveTypeDefOrRef(reader, coded);
        }
        if (raw == 0x1d) return ReadSignatureType(reader, ref blob) + "[]";
        if (raw == 0x10) return ReadSignatureType(reader, ref blob) + "&";
        if (raw == 0x0f) return ReadSignatureType(reader, ref blob) + "*";
        if (raw == 0x13) return "GenericMethodParameter";
        if (raw == 0x12) return "GenericTypeParameter";
        return ((SignatureTypeCode)raw) switch
        {
            SignatureTypeCode.Void => "void",
            SignatureTypeCode.Boolean => "bool",
            SignatureTypeCode.Char => "char",
            SignatureTypeCode.SByte => "sbyte",
            SignatureTypeCode.Byte => "byte",
            SignatureTypeCode.Int16 => "short",
            SignatureTypeCode.UInt16 => "ushort",
            SignatureTypeCode.Int32 => "int",
            SignatureTypeCode.UInt32 => "uint",
            SignatureTypeCode.Int64 => "long",
            SignatureTypeCode.UInt64 => "ulong",
            SignatureTypeCode.Single => "float",
            SignatureTypeCode.Double => "double",
            SignatureTypeCode.String => "string",
            SignatureTypeCode.Object => "object",
            _ => "?"
        };
    }

    private static string ResolveTypeDefOrRef(MetadataReader reader, int codedIndex)
    {
        var tag = codedIndex & 0x3;
        var row = codedIndex >> 2;
        try
        {
            return tag switch
            {
                0 => GetFullTypeName(reader, reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(row))).FullName,
                1 => GetFullTypeName(reader, reader.GetTypeReference(MetadataTokens.TypeReferenceHandle(row))).FullName,
                _ => "object"
            };
        }
        catch
        {
            return "object";
        }
    }

    private static IEnumerable<AssemblyCallInstruction> DecodeInstructions(BlobReader ilReader)
    {
        while (ilReader.RemainingBytes > 0)
        {
            var offset = ilReader.Offset;
            var first = ilReader.ReadByte();
            OpCode opCode;
            if (first == 0xfe)
            {
                var second = ilReader.ReadByte();
                MultiByteOpCodes.TryGetValue(unchecked((short)(0xfe00 | second)), out opCode);
            }
            else
            {
                SingleByteOpCodes.TryGetValue(first, out opCode);
            }

            object? operand = opCode.OperandType switch
            {
                OperandType.InlineNone => null,
                OperandType.ShortInlineI => opCode == OpCodes.Ldc_I4_S ? ilReader.ReadSByte() : ilReader.ReadByte(),
                OperandType.InlineI => ilReader.ReadInt32(),
                OperandType.InlineI8 => ilReader.ReadInt64(),
                OperandType.ShortInlineR => ilReader.ReadSingle(),
                OperandType.InlineR => ilReader.ReadDouble(),
                OperandType.ShortInlineBrTarget => ilReader.ReadSByte(),
                OperandType.InlineBrTarget => ilReader.ReadInt32(),
                OperandType.ShortInlineVar => ilReader.ReadByte(),
                OperandType.InlineVar => ilReader.ReadUInt16(),
                OperandType.InlineSwitch => ReadSwitchOperand(ref ilReader),
                OperandType.InlineString or OperandType.InlineSig or OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok => ilReader.ReadInt32(),
                _ => null
            };
            yield return new AssemblyCallInstruction(offset, opCode, operand);
        }
    }

    private static int[] ReadSwitchOperand(ref BlobReader reader)
    {
        var count = reader.ReadInt32();
        var targets = new int[count];
        for (var i = 0; i < count; i++) targets[i] = reader.ReadInt32();
        return targets;
    }

    private static AssemblyCallSourceMap LoadPortablePdbSourceMap(string assemblyPath)
    {
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(pdbPath)) return AssemblyCallSourceMap.Empty;
        try
        {
            using var pdbStream = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            var reader = provider.GetMetadataReader();
            var locations = new Dictionary<int, List<AssemblyCallSequencePoint>>();
            foreach (var methodDebugHandle in reader.MethodDebugInformation)
            {
                var rowNumber = MetadataTokens.GetRowNumber(methodDebugHandle);
                var methodDebugInfo = reader.GetMethodDebugInformation(methodDebugHandle);
                var points = new List<AssemblyCallSequencePoint>();
                foreach (var sequencePoint in methodDebugInfo.GetSequencePoints())
                {
                    if (sequencePoint.IsHidden || sequencePoint.Document.IsNil) continue;
                    var document = reader.GetDocument(sequencePoint.Document);
                    points.Add(new AssemblyCallSequencePoint(sequencePoint.Offset, reader.GetString(document.Name), sequencePoint.StartLine, sequencePoint.StartColumn));
                }
                if (points.Count > 0) locations[MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(rowNumber))] = points.OrderBy(point => point.Offset).ToList();
            }
            return new AssemblyCallSourceMap(locations);
        }
        catch
        {
            return AssemblyCallSourceMap.Empty;
        }
    }

    private static List<string> GetAssemblyFiles(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return [];
        if (!File.GetAttributes(path).HasFlag(FileAttributes.Directory)) return Path.GetExtension(path) is ".dll" or ".exe" ? [path] : [];
        return new DirectoryInfo(path)
            .EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(file => file.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) || !Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories).Any())
            .Select(file => file.FullName)
            .ToList();
    }

    private static bool IsManagedAssembly(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream);
            return peReader.HasMetadata && peReader.PEHeaders.CorHeader is not null;
        }
        catch
        {
            return false;
        }
    }

    private sealed record AssemblyCallInstruction(int Offset, OpCode OpCode, object? Operand);
    private sealed record AssemblyCallSignature(List<string> ParameterTypes, string ReturnType, bool ReturnsVoid, bool HasThis, int ParameterCount);
    private sealed record AssemblyCallType(string FullName, string Name, string Namespace, string AssemblyName);
    private sealed record AssemblyCallSymbol(string Symbol, string Name, string ClassName, string Namespace, string AssemblyName, int ParameterCount, string ReturnType);
    private sealed record AssemblyCallMember(string Symbol, string Name, string ClassName, string Namespace, string AssemblyName, string FilePath, int ParameterCount, int MetadataToken, bool IsInternal, string ReturnType, int LineNumber, int ColumnNumber);
    private sealed record AssemblyCallSequencePoint(int Offset, string FilePath, int LineNumber, int ColumnNumber);
    private sealed record AssemblyCallSourceLocation(string FilePath, int LineNumber, int ColumnNumber);
    private sealed class AssemblyCallSourceMap(Dictionary<int, List<AssemblyCallSequencePoint>> locationsByToken)
    {
        public static AssemblyCallSourceMap Empty { get; } = new([]);
        public AssemblyCallSourceLocation Resolve(int methodToken, int ilOffset, string assemblyPath)
        {
            if (!locationsByToken.TryGetValue(methodToken, out var locations)) return new AssemblyCallSourceLocation(assemblyPath, Math.Max(1, ilOffset), 1);
            var point = locations.Where(candidate => candidate.Offset <= ilOffset).OrderByDescending(candidate => candidate.Offset).FirstOrDefault();
            return point is null ? new AssemblyCallSourceLocation(assemblyPath, Math.Max(1, ilOffset), 1) : new AssemblyCallSourceLocation(point.FilePath, Math.Max(1, point.LineNumber), Math.Max(1, point.ColumnNumber));
        }
    }
}
