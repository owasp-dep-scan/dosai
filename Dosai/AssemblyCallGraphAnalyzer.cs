using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Depscan;

internal static class AssemblyCallGraphAnalyzer
{
    private const int MaxSwitchTargets = 4096;

    private static readonly Dictionary<short, OpCode> SingleByteOpCodes = typeof(OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.GetValue(null) is OpCode { Size: 1 })
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((short)(ushort)opCode.Value));

    private static readonly Dictionary<short, OpCode> MultiByteOpCodes = typeof(OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.GetValue(null) is OpCode { Size: 2 })
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
            AddNode(nodes, method.AssemblySignature!, method.Name ?? method.AssemblySignature!, method.ClassName, method.Namespace, method.FileName, method.Assembly, method.Module, method.Name == ".ctor" ? "Constructor" : "Method", method.LineNumber, method.ColumnNumber, isExternal: false, AnalysisEvidenceKind.AssemblyReflection);
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
                var stateMachineMethods = BuildStateMachineMethodMap(reader, assemblyPath, sourceMap);
                var instantiatedTypes = CollectInstantiatedTypes(peReader, reader, assemblyPath, sourceMap);
                var dispatchIndex = DispatchResolver.AssemblyIndex.Create(knownMethods.Where(method => string.Equals(Path.GetFullPath(method.Path ?? string.Empty), assemblyFullPath, StringComparison.OrdinalIgnoreCase)), instantiatedTypes);
                foreach (var methodHandle in reader.MethodDefinitions)
                {
                    var methodDefinition = reader.GetMethodDefinition(methodHandle);
                    if (methodDefinition.RelativeVirtualAddress == 0) continue;
                    var methodToken = MetadataTokens.GetToken(methodHandle);
                    var isGeneratedStateMachine = stateMachineMethods.TryGetValue(methodToken, out var stateMachineOwner);
                    var sourceMethod = isGeneratedStateMachine && stateMachineOwner is not null
                        ? stateMachineOwner.ToMethod(assemblyPath)
                        : methodLookup.TryGetValue((assemblyFullPath, methodToken), out var knownMethod)
                        ? knownMethod
                        : CreateFallbackMethod(reader, methodHandle, methodDefinition, assemblyPath, sourceMap);
                    var sourceId = sourceMethod.AssemblySignature ?? BuildMethodSymbol(reader, methodHandle, methodDefinition, assemblyPath).Symbol;
                    AddNode(nodes, sourceId, sourceMethod.Name ?? sourceId, sourceMethod.ClassName, sourceMethod.Namespace, sourceMethod.FileName, sourceMethod.Assembly, sourceMethod.Module, sourceMethod.Name == ".ctor" ? "Constructor" : "Method", sourceMethod.LineNumber, sourceMethod.ColumnNumber, isExternal: false, isGeneratedStateMachine ? AnalysisEvidenceKind.AssemblyIlGeneratedState : AnalysisEvidenceKind.AssemblyIlDirect);
                    var body = peReader.GetMethodBody(methodDefinition.RelativeVirtualAddress);
                    var delegateState = new AssemblyDelegateState();
                    foreach (var instruction in DecodeInstructions(body.GetILReader()))
                    {
                        var location = sourceMap.Resolve(methodToken, instruction.Offset, assemblyPath);
                        foreach (var resolvedDelegate in TrackDelegateInstruction(reader, instruction, delegateState, assemblyPath, sourceMap, assemblyFullPath, methodLookup))
                        {
                            AddResolvedDelegateEdge(calls, nodes, edges, edgeKeys, sourceMethod, sourceId, resolvedDelegate, location);
                        }

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
                        var evidenceKind = isGeneratedStateMachine ? AnalysisEvidenceKind.AssemblyIlGeneratedState : callType == CallType.DelegateInvoke ? AnalysisEvidenceKind.AssemblyIlDelegateTarget : AnalysisEvidenceKind.AssemblyIlDirect;
                        var targetId = ResolveInternalTargetId(assemblyFullPath, target.MetadataToken, target.Symbol, methodLookup);
                        AddNode(nodes, targetId, target.Name, target.ClassName, target.Namespace, GetTargetFileName(target), target.AssemblyName, GetTargetModuleName(target), callType == CallType.ConstructorCall ? "Constructor" : "Method", target.LineNumber, target.ColumnNumber, isExternal: !target.IsInternal);
                        var evidenceDescription = isGeneratedStateMachine
                            ? "Call edge discovered from generated async/iterator state-machine IL and collapsed to the user method."
                            : "Call edge discovered from assembly IL method body.";
                        var call = new MethodCalls
                        {
                            Path = location.FilePath,
                            FileName = Path.GetFileName(location.FilePath),
                            Assembly = target.AssemblyName,
                            Module = GetTargetModuleName(target),
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
                            IsInternal = target.IsInternal,
                            EvidenceKind = evidenceKind,
                            Evidence = [CreateEvidence(evidenceKind, location, evidenceDescription)]
                        };
                        calls.Add(call);
                        var edgeKey = $"{sourceId}\u001f{targetId}\u001f{location.FilePath}\u001f{location.LineNumber}\u001f{location.ColumnNumber}\u001f{callType}\u001f{evidenceKind}";
                        if (edgeKeys.Add(edgeKey))
                        {
                            edges.Add(new MethodCallEdge
                            {
                                SourceId = sourceId,
                                TargetId = targetId,
                                CallLocation = new CallLocation { FileName = Path.GetFileName(location.FilePath), LineNumber = location.LineNumber, ColumnNumber = location.ColumnNumber },
                                Path = location.FilePath,
                                FileName = Path.GetFileName(location.FilePath),
                                IsInternal = target.IsInternal,
                                CalledMethodName = target.Name,
                                SourceName = sourceMethod.Name,
                                TargetName = target.Name,
                                Arguments = call.Arguments,
                                ArgumentExpressions = call.ArgumentExpressions,
                                CallType = callType,
                                EvidenceKind = evidenceKind,
                                Evidence = [CreateEvidence(evidenceKind, location, evidenceDescription)]
                            });
                        }

                        if (instruction.OpCode == OpCodes.Callvirt)
                        {
                            foreach (var candidate in dispatchIndex.FindDispatchCandidates(target.Name, target.ClassName, target.ParameterCount))
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
                                        IsInternal = true,
                                        EvidenceKind = AnalysisEvidenceKind.AssemblyIlVirtualCandidate,
                                        Evidence = [CreateEvidence(AnalysisEvidenceKind.AssemblyIlVirtualCandidate, location, "Virtual candidate inferred by shared assembly CHA/RTA resolver.")]
                                    });
                                    edges.Add(new MethodCallEdge
                                    {
                                        SourceId = sourceId,
                                        TargetId = candidateId,
                                        CallLocation = new CallLocation { FileName = Path.GetFileName(location.FilePath), LineNumber = location.LineNumber, ColumnNumber = location.ColumnNumber },
                                        Path = location.FilePath,
                                        FileName = Path.GetFileName(location.FilePath),
                                        IsInternal = true,
                                        CalledMethodName = candidate.Name,
                                        SourceName = sourceMethod.Name,
                                        TargetName = candidate.Name,
                                        Arguments = call.Arguments,
                                        ArgumentExpressions = ["virtual-candidate"],
                                        CallType = CallType.MethodCall,
                                        EvidenceKind = AnalysisEvidenceKind.AssemblyIlVirtualCandidate,
                                        Evidence = [CreateEvidence(AnalysisEvidenceKind.AssemblyIlVirtualCandidate, location, "Virtual candidate inferred by shared assembly CHA/RTA resolver.")]
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

    private static IEnumerable<AssemblyResolvedDelegateCall> TrackDelegateInstruction(MetadataReader reader, AssemblyCallInstruction instruction, AssemblyDelegateState state, string assemblyPath, AssemblyCallSourceMap sourceMap, string assemblyFullPath, IReadOnlyDictionary<(string Path, int Token), Method> methodLookup)
    {
        var opCode = instruction.OpCode;
        if ((opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn) && instruction.Operand is int methodToken && ResolveMember(reader, methodToken, assemblyPath, sourceMap) is { } target)
        {
            var targetId = ResolveInternalTargetId(assemblyFullPath, target.MetadataToken, target.Symbol, methodLookup);
            state.Stack.Add(new AssemblyDelegateTarget(target, targetId));
            yield break;
        }

        if (TryGetLdlocIndex(opCode, instruction.Operand, out var ldlocIndex))
        {
            state.Stack.Add(state.Locals.GetValueOrDefault(ldlocIndex));
            yield break;
        }

        if (TryGetStlocIndex(opCode, instruction.Operand, out var stlocIndex))
        {
            state.Locals[stlocIndex] = state.Pop();
            yield break;
        }

        if ((opCode == OpCodes.Ldfld || opCode == OpCodes.Ldsfld) && instruction.Operand is int loadFieldToken && ResolveMember(reader, loadFieldToken, assemblyPath, sourceMap) is { } loadField)
        {
            if (opCode == OpCodes.Ldfld) _ = state.Pop();
            state.Stack.Add(state.Fields.GetValueOrDefault(loadField.Symbol));
            yield break;
        }

        if ((opCode == OpCodes.Stfld || opCode == OpCodes.Stsfld) && instruction.Operand is int storeFieldToken && ResolveMember(reader, storeFieldToken, assemblyPath, sourceMap) is { } storeField)
        {
            var value = state.Pop();
            if (opCode == OpCodes.Stfld) _ = state.Pop();
            state.Fields[storeField.Symbol] = value;
            yield break;
        }

        if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj) && instruction.Operand is int callToken && ResolveMember(reader, callToken, assemblyPath, sourceMap) is { } member)
        {
            var arguments = new List<AssemblyDelegateTarget?>();
            for (var i = 0; i < member.ParameterCount; i++) arguments.Add(state.Pop());
            arguments.Reverse();
            var receiver = opCode != OpCodes.Newobj && (member.HasThis || opCode == OpCodes.Callvirt) ? state.Pop() : null;

            if (opCode == OpCodes.Newobj)
            {
                var delegateTarget = IsDelegateConstructor(member) ? arguments.FirstOrDefault(argument => argument is not null) : null;
                state.Stack.Add(delegateTarget);
                yield break;
            }

            if (member.Name == "Invoke" && receiver is not null)
            {
                yield return new AssemblyResolvedDelegateCall(receiver, CallType.DelegateInvoke, "delegate-invoke", "Delegate.Invoke resolved to target method through IL delegate tracking.");
            }
            else if ((member.Name.StartsWith("add_", StringComparison.Ordinal) || member.Name.StartsWith("remove_", StringComparison.Ordinal)) && arguments.FirstOrDefault(argument => argument is not null) is { } eventTarget)
            {
                yield return new AssemblyResolvedDelegateCall(eventTarget, member.Name.StartsWith("add_", StringComparison.Ordinal) ? CallType.EventSubscribe : CallType.EventUnsubscribe, "event-callback-target", "Event accessor callback target resolved through IL delegate tracking.");
            }

            if (!member.ReturnsVoid)
            {
                state.Stack.Add(null);
            }
            yield break;
        }

        if (opCode == OpCodes.Dup)
        {
            state.Stack.Add(state.Stack.Count > 0 ? state.Stack[^1] : null);
            yield break;
        }

        if (opCode == OpCodes.Pop)
        {
            _ = state.Pop();
            yield break;
        }

        ApplyDefaultDelegateStackBehaviour(opCode, state);
    }

    private static void AddResolvedDelegateEdge(List<MethodCalls> calls, Dictionary<string, MethodNode> nodes, List<MethodCallEdge> edges, HashSet<string> edgeKeys, Method sourceMethod, string sourceId, AssemblyResolvedDelegateCall resolved, AssemblyCallSourceLocation location)
    {
        var target = resolved.Target.Member;
        var targetId = resolved.Target.TargetId;
        AddNode(nodes, targetId, target.Name, target.ClassName, target.Namespace, GetTargetFileName(target), target.AssemblyName, GetTargetModuleName(target), "Method", target.LineNumber, target.ColumnNumber, isExternal: !target.IsInternal, AnalysisEvidenceKind.AssemblyIlDelegateTarget);
        var call = new MethodCalls
        {
            Path = location.FilePath,
            FileName = Path.GetFileName(location.FilePath),
            Assembly = target.AssemblyName,
            Module = GetTargetModuleName(target),
            Namespace = target.Namespace,
            ClassName = target.ClassName,
            CalledMethod = target.Name,
            LineNumber = location.LineNumber,
            ColumnNumber = location.ColumnNumber,
            Arguments = Enumerable.Repeat("?", Math.Max(0, target.ParameterCount)).ToList(),
            ArgumentExpressions = [resolved.ArgumentExpression],
            CallType = resolved.CallType,
            SourceId = sourceId,
            TargetId = targetId,
            CallerMethod = sourceMethod.Name,
            CallerNamespace = sourceMethod.Namespace,
            CallerClass = sourceMethod.ClassName,
            IsInternal = target.IsInternal,
            EvidenceKind = AnalysisEvidenceKind.AssemblyIlDelegateTarget,
            Evidence = [CreateEvidence(AnalysisEvidenceKind.AssemblyIlDelegateTarget, location, resolved.Description)]
        };
        calls.Add(call);
        var edgeKey = $"{sourceId}\u001f{targetId}\u001f{location.FilePath}\u001f{location.LineNumber}\u001f{location.ColumnNumber}\u001f{resolved.CallType}\u001fResolvedDelegate";
        if (edgeKeys.Add(edgeKey))
        {
            edges.Add(new MethodCallEdge
            {
                SourceId = sourceId,
                TargetId = targetId,
                CallLocation = new CallLocation { FileName = Path.GetFileName(location.FilePath), LineNumber = location.LineNumber, ColumnNumber = location.ColumnNumber },
                Path = location.FilePath,
                FileName = Path.GetFileName(location.FilePath),
                IsInternal = target.IsInternal,
                CalledMethodName = target.Name,
                SourceName = sourceMethod.Name,
                TargetName = target.Name,
                Arguments = call.Arguments,
                ArgumentExpressions = call.ArgumentExpressions,
                CallType = resolved.CallType,
                EvidenceKind = AnalysisEvidenceKind.AssemblyIlDelegateTarget,
                Evidence = [CreateEvidence(AnalysisEvidenceKind.AssemblyIlDelegateTarget, location, resolved.Description)]
            });
        }
    }

    private static string GetTargetFileName(AssemblyCallMember target) =>
        target.IsInternal ? Path.GetFileName(target.FilePath) : GetExternalModuleName(target.AssemblyName);

    private static string GetTargetModuleName(AssemblyCallMember target) =>
        target.IsInternal ? Path.GetFileName(target.FilePath) : GetExternalModuleName(target.AssemblyName);

    private static string GetExternalModuleName(string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName)) return string.Empty;
        var simpleName = assemblyName.Split(',')[0].Trim();
        return simpleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || simpleName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? simpleName
            : simpleName + ".dll";
    }

    private static bool IsDelegateConstructor(AssemblyCallMember member) => member is { Name: ".ctor", ParameterCount: >= 2 };

    private static void ApplyDefaultDelegateStackBehaviour(OpCode opCode, AssemblyDelegateState state)
    {
        var popCount = GetPopCount(opCode.StackBehaviourPop);
        for (var i = 0; i < popCount; i++) _ = state.Pop();
        var pushCount = GetPushCount(opCode.StackBehaviourPush);
        for (var i = 0; i < pushCount; i++) state.Stack.Add(null);
    }

    private static int GetPopCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
        _ => 0
    };

    private static int GetPushCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        _ => 0
    };

    private static bool TryGetLdlocIndex(OpCode opCode, object? operand, out int index)
    {
        index = opCode == OpCodes.Ldloc_0 ? 0 : opCode == OpCodes.Ldloc_1 ? 1 : opCode == OpCodes.Ldloc_2 ? 2 : opCode == OpCodes.Ldloc_3 ? 3 : operand is int value && opCode == OpCodes.Ldloc ? value : operand is byte shortValue && opCode == OpCodes.Ldloc_S ? shortValue : -1;
        return index >= 0;
    }

    private static bool TryGetStlocIndex(OpCode opCode, object? operand, out int index)
    {
        index = opCode == OpCodes.Stloc_0 ? 0 : opCode == OpCodes.Stloc_1 ? 1 : opCode == OpCodes.Stloc_2 ? 2 : opCode == OpCodes.Stloc_3 ? 3 : operand is int value && opCode == OpCodes.Stloc ? value : operand is byte shortValue && opCode == OpCodes.Stloc_S ? shortValue : -1;
        return index >= 0;
    }

    private static string ResolveInternalTargetId(string assemblyPath, int metadataToken, string fallbackSymbol, IReadOnlyDictionary<(string Path, int Token), Method> methodLookup) =>
        metadataToken != 0 && methodLookup.TryGetValue((assemblyPath, metadataToken), out var method) && !string.IsNullOrWhiteSpace(method.AssemblySignature)
            ? method.AssemblySignature!
            : fallbackSymbol;

    private static AnalysisEvidence CreateEvidence(AnalysisEvidenceKind kind, AssemblyCallSourceLocation location, string description) => new()
    {
        Kind = kind,
        Source = GetEvidenceSource(kind),
        Description = description,
        FileName = Path.GetFileName(location.FilePath),
        LineNumber = location.LineNumber,
        ColumnNumber = location.ColumnNumber
    };

    private static string GetEvidenceSource(AnalysisEvidenceKind kind) => kind switch
    {
        AnalysisEvidenceKind.AssemblyReflection => "assembly-metadata",
        AnalysisEvidenceKind.ExternalSummary => "external-metadata",
        _ => "assembly-il"
    };

    private static Dictionary<int, AssemblyStateMachineOwner> BuildStateMachineMethodMap(MetadataReader reader, string assemblyPath, AssemblyCallSourceMap sourceMap)
    {
        var typeByName = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            AddTypeAlias(typeByName, GetFullTypeName(reader, type).FullName, typeHandle);
            AddTypeAlias(typeByName, reader.GetString(type.Name), typeHandle);
        }

        var result = new Dictionary<int, AssemblyStateMachineOwner>();
        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            foreach (var customAttributeHandle in method.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(customAttributeHandle);
                if (!IsStateMachineAttribute(reader, attribute) || !TryReadStateMachineTypeName(reader, attribute, out var stateMachineTypeName))
                {
                    continue;
                }

                if (!TryResolveStateMachineType(typeByName, stateMachineTypeName, out var stateMachineTypeHandle))
                {
                    continue;
                }

                var ownerSymbol = BuildMethodSymbol(reader, methodHandle, method, assemblyPath);
                var ownerLocation = sourceMap.Resolve(MetadataTokens.GetToken(methodHandle), 1, assemblyPath);
                var owner = new AssemblyStateMachineOwner(ownerSymbol.Symbol, ownerSymbol.Name, ownerSymbol.ClassName, ownerSymbol.Namespace, ownerSymbol.AssemblyName, ownerSymbol.ReturnType, ownerLocation.LineNumber, ownerLocation.ColumnNumber);
                foreach (var generatedMethodHandle in reader.GetTypeDefinition(stateMachineTypeHandle).GetMethods())
                {
                    var generatedMethod = reader.GetMethodDefinition(generatedMethodHandle);
                    if (reader.GetString(generatedMethod.Name) == "MoveNext")
                    {
                        result[MetadataTokens.GetToken(generatedMethodHandle)] = owner;
                    }
                }
            }
        }

        return result;
    }

    private static void AddTypeAlias(Dictionary<string, TypeDefinitionHandle> typeByName, string typeName, TypeDefinitionHandle handle)
    {
        var normalized = NormalizeStateMachineTypeName(typeName);
        if (!string.IsNullOrWhiteSpace(normalized)) typeByName.TryAdd(normalized, handle);
        var simpleName = normalized.Split('.').LastOrDefault();
        if (!string.IsNullOrWhiteSpace(simpleName)) typeByName.TryAdd(simpleName, handle);
    }

    private static bool TryResolveStateMachineType(Dictionary<string, TypeDefinitionHandle> typeByName, string attributeTypeName, out TypeDefinitionHandle handle)
    {
        var normalized = NormalizeStateMachineTypeName(attributeTypeName);
        if (typeByName.TryGetValue(normalized, out handle)) return true;
        var simpleName = normalized.Split('.').LastOrDefault();
        if (!string.IsNullOrWhiteSpace(simpleName) && typeByName.TryGetValue(simpleName, out handle)) return true;
        foreach (var (candidateName, candidateHandle) in typeByName)
        {
            if (normalized.EndsWith(candidateName, StringComparison.Ordinal) || candidateName.EndsWith(simpleName ?? normalized, StringComparison.Ordinal))
            {
                handle = candidateHandle;
                return true;
            }
        }
        handle = default;
        return false;
    }

    private static bool IsStateMachineAttribute(MetadataReader reader, CustomAttribute attribute)
    {
        var attributeTypeName = ResolveCustomAttributeTypeName(reader, attribute.Constructor);
        return attributeTypeName.EndsWith("AsyncStateMachineAttribute", StringComparison.Ordinal)
               || attributeTypeName.EndsWith("IteratorStateMachineAttribute", StringComparison.Ordinal)
               || attributeTypeName.EndsWith("AsyncIteratorStateMachineAttribute", StringComparison.Ordinal);
    }

    private static string ResolveCustomAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle parent = constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default
        };
        return parent.Kind switch
        {
            HandleKind.TypeReference => GetFullTypeName(reader, reader.GetTypeReference((TypeReferenceHandle)parent)).FullName,
            HandleKind.TypeDefinition => GetFullTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)parent)).FullName,
            _ => string.Empty
        };
    }

    private static bool TryReadStateMachineTypeName(MetadataReader reader, CustomAttribute attribute, out string typeName)
    {
        typeName = string.Empty;
        try
        {
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.RemainingBytes < 2 || blob.ReadUInt16() != 1)
            {
                return false;
            }
            typeName = blob.ReadSerializedString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(typeName);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeStateMachineTypeName(string typeName)
    {
        var normalized = typeName.Split(',', 2)[0].Trim();
        normalized = normalized.Replace('+', '.');
        normalized = Regex.Replace(normalized, "`[0-9]+", string.Empty);
        return normalized;
    }

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
            MetadataToken = MetadataTokens.GetToken(methodHandle),
            Identity = MethodIdentityFactory.FromParts(symbol.Symbol, null, symbol.Symbol, symbol.Symbol, Path.GetFileNameWithoutExtension(assemblyPath), Path.GetFileName(assemblyPath), symbol.Namespace, symbol.ClassName, symbol.Name, MetadataTokens.GetToken(methodHandle), null, AnalysisEvidenceKind.AssemblyReflection),
            Evidence = [CreateEvidence(AnalysisEvidenceKind.AssemblyReflection, location, "Method discovered from assembly metadata while extracting IL call graph.")]
        };
    }

    private static void AddNode(Dictionary<string, MethodNode> nodes, string id, string name, string? className, string? namespaceName, string? fileName, string? assembly, string? module, string kind, int lineNumber, int columnNumber, bool isExternal, AnalysisEvidenceKind? evidenceKindOverride = null)
    {
        var evidenceKind = evidenceKindOverride ?? (isExternal ? AnalysisEvidenceKind.ExternalSummary : AnalysisEvidenceKind.AssemblyIlDirect);
        var evidence = new AnalysisEvidence
        {
            Kind = evidenceKind,
            Source = GetEvidenceSource(evidenceKind),
            Description = evidenceKind == AnalysisEvidenceKind.AssemblyIlGeneratedState
                ? "Application call graph node collapsed from generated async/iterator state-machine IL."
                : evidenceKind == AnalysisEvidenceKind.AssemblyReflection
                    ? "Application call graph node discovered from assembly metadata."
                    : isExternal ? "External call graph node referenced from assembly IL." : "Application call graph node discovered from assembly IL.",
            FileName = fileName,
            LineNumber = lineNumber,
            ColumnNumber = columnNumber
        };
        if (nodes.TryGetValue(id, out var existingNode))
        {
            MergeNode(existingNode, className, namespaceName, fileName, assembly, module, lineNumber, columnNumber, isExternal, evidenceKind, evidence);
            return;
        }

        nodes.Add(id, new MethodNode
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
            IsExternal = isExternal,
            Identity = MethodIdentityFactory.FromParts(id, null, id, id, assembly, module, namespaceName, className, name, 0, null, evidenceKind),
            Evidence = [evidence]
        });
    }

    private static void MergeNode(MethodNode target, string? className, string? namespaceName, string? fileName, string? assembly, string? module, int lineNumber, int columnNumber, bool isExternal, AnalysisEvidenceKind evidenceKind, AnalysisEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(target.ClassName) && !string.IsNullOrWhiteSpace(className)) target.ClassName = className;
        if (string.IsNullOrWhiteSpace(target.Namespace) && !string.IsNullOrWhiteSpace(namespaceName)) target.Namespace = namespaceName;
        if (string.IsNullOrWhiteSpace(target.FileName) && !string.IsNullOrWhiteSpace(fileName)) target.FileName = fileName;
        target.Assembly ??= assembly;
        target.Module ??= module;
        if (target.LineNumber <= 0 && lineNumber > 0) target.LineNumber = lineNumber;
        if (target.ColumnNumber <= 0 && columnNumber > 0) target.ColumnNumber = columnNumber;
        target.IsExternal &= isExternal;

        if (!target.Evidence.Any(item => item.Kind == evidence.Kind && item.Source == evidence.Source && item.FileName == evidence.FileName && item.LineNumber == evidence.LineNumber && item.ColumnNumber == evidence.ColumnNumber))
        {
            target.Evidence.Add(evidence);
        }

        target.Identity ??= MethodIdentityFactory.FromParts(target.Id, null, target.Id, target.Id, assembly, module, namespaceName, className, target.Name, 0, target.Purl, evidenceKind);
        if (!target.Identity.Evidence.Contains(evidenceKind)) target.Identity.Evidence.Add(evidenceKind);
        target.Identity.AssemblyName ??= assembly;
        target.Identity.ModuleName ??= module;
        target.Identity.Namespace ??= namespaceName;
        target.Identity.ClassName ??= className;
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
        return new AssemblyCallMember(symbol, name, containingType.Name, containingType.Namespace, containingType.AssemblyName, assemblyPath, signature.ParameterCount, 0, false, signature.ReturnType, 0, 0, signature.ReturnsVoid, signature.HasThis);
    }

    private static AssemblyCallMember ResolveMethodDefinition(MetadataReader reader, MethodDefinitionHandle handle, string assemblyPath, AssemblyCallSourceMap sourceMap)
    {
        var method = reader.GetMethodDefinition(handle);
        var symbol = BuildMethodSymbol(reader, handle, method, assemblyPath);
        var location = sourceMap.Resolve(MetadataTokens.GetToken(handle), 1, assemblyPath);
        var signature = ReadSignatureInfo(reader, method.Signature, method.Attributes);
        return new AssemblyCallMember(symbol.Symbol, symbol.Name, symbol.ClassName, symbol.Namespace, symbol.AssemblyName, assemblyPath, symbol.ParameterCount, MetadataTokens.GetToken(handle), true, symbol.ReturnType, location.LineNumber, location.ColumnNumber, signature.ReturnsVoid, signature.HasThis);
    }

    private static AssemblyCallMember? ResolveMethodSpecification(MetadataReader reader, MethodSpecificationHandle handle, string assemblyPath, AssemblyCallSourceMap sourceMap)
    {
        var specification = reader.GetMethodSpecification(handle);
        if (specification.Method.Kind is not (HandleKind.MemberReference or HandleKind.MethodDefinition) || ResolveMember(reader, MetadataTokens.GetToken(specification.Method), assemblyPath, sourceMap) is not { } member)
        {
            return null;
        }

        var genericArguments = ReadMethodSpecificationArguments(reader, specification.Signature);
        if (genericArguments.Count == 0)
        {
            return member;
        }

        var genericName = member.Name.Contains('<', StringComparison.Ordinal) ? member.Name : $"{member.Name}<{string.Join(',', genericArguments)}>";
        var symbol = member.Symbol.Replace($".{member.Name}(", $".{genericName}(", StringComparison.Ordinal);
        return member with { Symbol = symbol, Name = genericName };
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
        HandleKind.TypeSpecification => DecodeTypeSpecification(reader, (TypeSpecificationHandle)parent),
        _ => new AssemblyCallType(string.Empty, string.Empty, string.Empty, string.Empty)
    };

    private static AssemblyCallType DecodeTypeSpecification(MetadataReader reader, TypeSpecificationHandle handle)
    {
        try
        {
            var blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
            var typeName = ReadSignatureType(reader, ref blob);
            return new AssemblyCallType(typeName, typeName.Split('.').LastOrDefault() ?? typeName, GetNamespace(typeName), string.Empty);
        }
        catch
        {
            return new AssemblyCallType("<type-spec>", "<type-spec>", string.Empty, string.Empty);
        }
    }

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

    private static string GetNamespace(string typeName)
    {
        var genericIndex = typeName.IndexOf('<', StringComparison.Ordinal);
        var plainType = genericIndex >= 0 ? typeName[..genericIndex] : typeName;
        var index = plainType.LastIndexOf('.');
        return index <= 0 ? string.Empty : plainType[..index];
    }

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
        if (raw == 0x15) // GENERICINST
        {
            var genericType = ReadSignatureType(reader, ref blob);
            var argumentCount = blob.RemainingBytes > 0 ? blob.ReadCompressedInteger() : 0;
            var arguments = new List<string>();
            for (var i = 0; i < argumentCount && blob.RemainingBytes > 0; i++) arguments.Add(ReadSignatureType(reader, ref blob));
            return $"{genericType}<{string.Join(',', arguments)}>";
        }
        if (raw is 0x11 or 0x12)
        {
            var coded = blob.ReadCompressedInteger();
            return ResolveTypeDefOrRef(reader, coded);
        }
        if (raw == 0x14) return ReadArraySignatureType(reader, ref blob);
        if (raw == 0x1d) return ReadSignatureType(reader, ref blob) + "[]";
        if (raw == 0x10) return ReadSignatureType(reader, ref blob) + "&";
        if (raw == 0x0f) return ReadSignatureType(reader, ref blob) + "*";
        if (raw == 0x13) return $"!{(blob.RemainingBytes > 0 ? blob.ReadCompressedInteger() : 0)}";
        if (raw == 0x1e) return $"!!{(blob.RemainingBytes > 0 ? blob.ReadCompressedInteger() : 0)}";
        if (raw is 0x1f or 0x20)
        {
            if (blob.RemainingBytes > 0) _ = blob.ReadCompressedInteger();
            return ReadSignatureType(reader, ref blob);
        }
        if (raw is 0x45 or 0x41) return ReadSignatureType(reader, ref blob);
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

    private static string ReadArraySignatureType(MetadataReader reader, ref BlobReader blob)
    {
        var elementType = ReadSignatureType(reader, ref blob);
        if (blob.RemainingBytes <= 0) return elementType + "[]";
        var rank = blob.ReadCompressedInteger();
        var sizes = blob.RemainingBytes > 0 ? blob.ReadCompressedInteger() : 0;
        for (var i = 0; i < sizes && blob.RemainingBytes > 0; i++) _ = blob.ReadCompressedInteger();
        var lowerBounds = blob.RemainingBytes > 0 ? blob.ReadCompressedInteger() : 0;
        for (var i = 0; i < lowerBounds && blob.RemainingBytes > 0; i++) _ = blob.ReadCompressedSignedInteger();
        return rank <= 1 ? elementType + "[]" : elementType + "[" + new string(',', rank - 1) + "]";
    }

    private static List<string> ReadMethodSpecificationArguments(MetadataReader reader, BlobHandle signatureHandle)
    {
        try
        {
            var blob = reader.GetBlobReader(signatureHandle);
            if (blob.RemainingBytes == 0) return [];
            _ = blob.ReadByte();
            var count = blob.RemainingBytes > 0 ? blob.ReadCompressedInteger() : 0;
            var arguments = new List<string>();
            for (var i = 0; i < count && blob.RemainingBytes > 0; i++) arguments.Add(ReadSignatureType(reader, ref blob));
            return arguments;
        }
        catch
        {
            return [];
        }
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
                if (ilReader.RemainingBytes == 0)
                {
                    yield break;
                }
                var second = ilReader.ReadByte();
                if (!MultiByteOpCodes.TryGetValue(unchecked((short)(0xfe00 | second)), out opCode))
                {
                    yield break;
                }
            }
            else
            {
                if (!SingleByteOpCodes.TryGetValue(first, out opCode))
                {
                    yield break;
                }
            }

            object? operand;
            try
            {
                operand = opCode.OperandType switch
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
                    OperandType.InlineVar => (int)ilReader.ReadUInt16(),
                    OperandType.InlineSwitch => ReadSwitchOperand(ref ilReader),
                    OperandType.InlineString or OperandType.InlineSig or OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok => ilReader.ReadInt32(),
                    _ => null
                };
            }
            catch (BadImageFormatException)
            {
                yield break;
            }
            yield return new AssemblyCallInstruction(offset, opCode, operand);
        }
    }

    private static int[] ReadSwitchOperand(ref BlobReader reader)
    {
        if (reader.RemainingBytes < sizeof(int))
        {
            throw new BadImageFormatException("Switch operand is missing its target count.");
        }

        var count = reader.ReadInt32();
        if (count < 0 || count > MaxSwitchTargets || count > reader.RemainingBytes / sizeof(int))
        {
            throw new BadImageFormatException("Switch operand target count is invalid or truncated.");
        }

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
        return AssemblyScope.GetAssemblyFiles(path, includeBuildArtifacts: false, excludeBinWhenSourceFilesPresent: true);
    }

    private static bool IsManagedAssembly(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream);
            return peReader is { HasMetadata: true, PEHeaders.CorHeader: not null };
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
    private sealed record AssemblyCallMember(string Symbol, string Name, string ClassName, string Namespace, string AssemblyName, string FilePath, int ParameterCount, int MetadataToken, bool IsInternal, string ReturnType, int LineNumber, int ColumnNumber, bool ReturnsVoid = false, bool HasThis = false);
    private sealed record AssemblyDelegateTarget(AssemblyCallMember Member, string TargetId);
    private sealed record AssemblyResolvedDelegateCall(AssemblyDelegateTarget Target, CallType CallType, string ArgumentExpression, string Description);
    private sealed class AssemblyDelegateState
    {
        public List<AssemblyDelegateTarget?> Stack { get; } = [];
        public Dictionary<int, AssemblyDelegateTarget?> Locals { get; } = [];
        public Dictionary<string, AssemblyDelegateTarget?> Fields { get; } = new(StringComparer.Ordinal);

        public AssemblyDelegateTarget? Pop()
        {
            if (Stack.Count == 0) return null;
            var value = Stack[^1];
            Stack.RemoveAt(Stack.Count - 1);
            return value;
        }
    }
    private sealed record AssemblyStateMachineOwner(string Symbol, string Name, string ClassName, string Namespace, string AssemblyName, string ReturnType, int LineNumber, int ColumnNumber)
    {
        public Method ToMethod(string assemblyPath) => new()
        {
            Path = assemblyPath,
            FileName = Path.GetFileName(assemblyPath),
            Assembly = AssemblyName,
            Module = Path.GetFileName(assemblyPath),
            Namespace = Namespace,
            ClassName = ClassName,
            Name = Name,
            ReturnType = ReturnType,
            LineNumber = LineNumber,
            ColumnNumber = ColumnNumber,
            AssemblySignature = Symbol,
            Identity = MethodIdentityFactory.FromParts(Symbol, null, Symbol, Symbol, AssemblyName, Path.GetFileName(assemblyPath), Namespace, ClassName, Name, 0, null, AnalysisEvidenceKind.AssemblyIlGeneratedState),
            Evidence = [new AnalysisEvidence { Kind = AnalysisEvidenceKind.AssemblyIlGeneratedState, Source = "assembly-il", Description = "User method associated with generated async/iterator state-machine IL.", FileName = Path.GetFileName(assemblyPath), LineNumber = LineNumber, ColumnNumber = ColumnNumber }]
        };
    }
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
