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

    public static (List<MethodCalls> Calls, CallGraph Graph) Analyze(string path, IReadOnlyList<Method> knownMethods, ICollection<string> diagnostics)
    {
        // A built tree holds a copy of each referenced assembly in every referencing project's
        // output, and each copy gave every call in it once more (issue #83, the same pass
        // shape as the data-flow one): each distinct file is analyzed once.
        var copies = AssemblyCopies.Collapse(path, GetAssemblyFiles(path));
        if (copies.Diagnostic(path, "each call in them is reported once") is { } copiesDiagnostic)
        {
            diagnostics.Add(copiesDiagnostic);
        }

        var assemblyPaths = copies.Distinct;
        var context = new AnalysisContext(path, knownMethods, copies.AnalyzedByFullPath());
        var calls = new List<MethodCalls>();
        var nodes = new Dictionary<string, MethodNode>(StringComparer.Ordinal);
        var edges = new List<MethodCallEdge>();
        var edgeKeys = new GraphAssembly.EdgeSiteIndex();

        foreach (var method in knownMethods.Where(method => !string.IsNullOrWhiteSpace(method.AssemblySignature)))
        {
            AddNode(nodes, method.AssemblySignature!, method.Name ?? method.AssemblySignature!, method.ClassName, method.Namespace, method.FileName, method.Assembly, method.Module, method.Name == ".ctor" ? "Constructor" : "Method", method.LineNumber, method.ColumnNumber, isExternal: false, AnalysisEvidenceKind.AssemblyReflection);
        }

        // Each assembly decodes on the worker team into its own fragment (issue #65: this loop
        // ran on one core for two thirds of the scan, decoding 247 of the tree's 1662
        // assemblies). Fragments merge in assembly order, so nodes, call records, edges and the
        // cross-assembly call-site de-duplication come out exactly as the sequential loop
        // produced them, for every worker count.
        DedicatedStack.ForEachInOrder("Dosai assembly call graph", Math.Max(1, Dosai.MaxSymbolAnalysisWorkers), assemblyPaths.Count,
            index => AnalyzeAssembly(assemblyPaths[index], context),
            fragment => fragment.MergeInto(calls, nodes, edges, edgeKeys));

        GraphAssembly.SortEdgesInPlace(edges);
        GraphAssembly.AssignEdgeIds(edges, "ae");
        var orderedNodes = nodes.Values.ToList();
        GraphAssembly.SortNodesInPlace(orderedNodes);
        return (calls, new CallGraph { Nodes = orderedNodes, Edges = edges });
    }

    /// <summary>
    ///     One assembly's share of the call graph. Runs on a worker: it touches only its own
    ///     fragment and per-assembly caches, and reads the shared context. A failure keeps what
    ///     the assembly produced before it, as the sequential loop did.
    /// </summary>
    private static AssemblyFragment AnalyzeAssembly(string assemblyPath, AnalysisContext context)
    {
        // Only managed assemblies reach here: the copy collapse drops every other file.
        var fragment = new AssemblyFragment();
        try
        {
            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata) return fragment;
            var reader = peReader.GetMetadataReader();
            var sourceMap = LoadPortablePdbSourceMap(assemblyPath);
            var scan = new AssemblyScan(reader, assemblyPath, Path.GetFullPath(assemblyPath), sourceMap, context);
            var stateMachineMethods = BuildStateMachineMethodMap(reader, assemblyPath, sourceMap);
            var instantiatedTypes = CollectInstantiatedTypes(peReader, scan);
            var dispatchIndex = DispatchResolver.AssemblyIndex.Create(context.MethodsOf(scan.AssemblyFullPath), instantiatedTypes);
            foreach (var methodHandle in reader.MethodDefinitions)
            {
                AnalyzeMethodBody(peReader, scan, fragment, dispatchIndex, stateMachineMethods, methodHandle);
            }
        }
        catch
        {
            // Methods already best-effort skips unloadable assemblies; keep call graph extraction equally non-fatal.
        }

        fragment.CompleteAnalysis();
        return fragment;
    }

    private static void AnalyzeMethodBody(PEReader peReader, AssemblyScan scan, AssemblyFragment fragment, DispatchResolver.AssemblyIndex dispatchIndex, Dictionary<int, AssemblyStateMachineOwner> stateMachineMethods, MethodDefinitionHandle methodHandle)
    {
        var reader = scan.Reader;
        var assemblyPath = scan.AssemblyPath;
        var methodDefinition = reader.GetMethodDefinition(methodHandle);
        if (methodDefinition.RelativeVirtualAddress == 0) return;
        var methodToken = MetadataTokens.GetToken(methodHandle);
        var isGeneratedStateMachine = stateMachineMethods.TryGetValue(methodToken, out var stateMachineOwner);
        var sourceMethod = isGeneratedStateMachine && stateMachineOwner is not null
            ? stateMachineOwner.ToMethod(assemblyPath)
            : scan.Context.MethodLookup.TryGetValue((scan.AssemblyFullPath, methodToken), out var knownMethod)
            ? knownMethod
            : CreateFallbackMethod(reader, methodHandle, methodDefinition, assemblyPath, scan.SourceMap);
        var sourceId = sourceMethod.AssemblySignature ?? BuildMethodSymbol(reader, methodHandle, methodDefinition, assemblyPath).Symbol;
        AddNode(fragment.Nodes, sourceId, sourceMethod.Name ?? sourceId, sourceMethod.ClassName, sourceMethod.Namespace, sourceMethod.FileName, sourceMethod.Assembly, sourceMethod.Module, sourceMethod.Name == ".ctor" ? "Constructor" : "Method", sourceMethod.LineNumber, sourceMethod.ColumnNumber, isExternal: false, isGeneratedStateMachine ? AnalysisEvidenceKind.AssemblyIlGeneratedState : AnalysisEvidenceKind.AssemblyIlDirect);
        var body = peReader.GetMethodBody(methodDefinition.RelativeVirtualAddress);
        var delegateState = new AssemblyDelegateState();
        foreach (var instruction in DecodeInstructions(body.GetILReader()))
        {
            // Only instructions that produce a call record need a source location.
            AssemblyCallSourceLocation? location = null;
            if (TrackDelegateInstruction(scan, instruction, delegateState) is { } resolvedDelegate)
            {
                location = scan.SourceMap.Resolve(methodToken, instruction.Offset, assemblyPath);
                AddResolvedDelegateEdge(scan, fragment, sourceMethod, sourceId, resolvedDelegate, location);
            }

            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Newobj && instruction.OpCode != OpCodes.Ldftn && instruction.OpCode != OpCodes.Ldvirtftn)
            {
                continue;
            }

            if (instruction.Operand is not int token || scan.Resolve(token) is not { } resolved)
            {
                continue;
            }

            location ??= scan.SourceMap.Resolve(methodToken, instruction.Offset, assemblyPath);
            var target = resolved.Member;
            var callType = instruction.OpCode == OpCodes.Newobj
                ? CallType.ConstructorCall
                : instruction.OpCode == OpCodes.Ldftn || instruction.OpCode == OpCodes.Ldvirtftn
                    ? CallType.DelegateInvoke
                    : CallType.MethodCall;
            var evidenceKind = isGeneratedStateMachine ? AnalysisEvidenceKind.AssemblyIlGeneratedState : callType == CallType.DelegateInvoke ? AnalysisEvidenceKind.AssemblyIlDelegateTarget : AnalysisEvidenceKind.AssemblyIlDirect;
            var targetId = resolved.TargetId;
            AddNode(fragment.Nodes, targetId, target.Name, target.ClassName, target.Namespace, resolved.TargetFileName, target.AssemblyName, resolved.TargetFileName, callType == CallType.ConstructorCall ? "Constructor" : "Method", target.LineNumber, target.ColumnNumber, isExternal: !target.IsInternal);
            var evidenceDescription = isGeneratedStateMachine
                ? "Call edge discovered from generated async/iterator state-machine IL and collapsed to the user method."
                : "Call edge discovered from assembly IL method body.";
            // The call record and its edge name the call site's file alike, relative to the scan
            // root, as the source analysis does (issue #79).
            var relativePath = scan.RelativeSourcePath(location.FilePath);
            var call = new MethodCalls
            {
                Path = relativePath,
                FileName = location.FileName,
                Assembly = target.AssemblyName,
                Module = resolved.TargetFileName,
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
            var edgeKey = GraphAssembly.EdgeSiteKey.Tagged(sourceId, targetId, location.FilePath, location.LineNumber, location.ColumnNumber, GraphAssembly.CallTypeName(callType), GraphAssembly.EvidenceKindName(evidenceKind));
            var edgeHash = edgeKey.GetHashCode();
            MethodCallEdge? edge = null;
            if (fragment.Keys.Add(edgeKey, edgeHash))
            {
                edge = new MethodCallEdge
                {
                    SourceId = sourceId,
                    TargetId = targetId,
                    CallLocation = new CallLocation { FileName = location.FileName, LineNumber = location.LineNumber, ColumnNumber = location.ColumnNumber },
                    Path = relativePath,
                    FileName = location.FileName,
                    IsInternal = target.IsInternal,
                    CalledMethodName = target.Name,
                    SourceName = sourceMethod.Name,
                    TargetName = target.Name,
                    Arguments = call.Arguments,
                    ArgumentExpressions = call.ArgumentExpressions,
                    CallType = callType,
                    EvidenceKind = evidenceKind,
                    Evidence = [CreateEvidence(evidenceKind, location, evidenceDescription)]
                };
            }

            fragment.Emit(call, edge, edgeKey, edgeHash, callNeedsNewSite: false);

            if (instruction.OpCode == OpCodes.Callvirt)
            {
                foreach (var candidate in dispatchIndex.FindDispatchCandidates(target.Name, target.ClassName, target.ParameterCount))
                {
                    var candidateId = candidate.AssemblySignature!;
                    AddNode(fragment.Nodes, candidateId, candidate.Name ?? candidateId, candidate.ClassName, candidate.Namespace, candidate.FileName, candidate.Assembly, candidate.Module, "Method", candidate.LineNumber, candidate.ColumnNumber, isExternal: false);
                    var candidateKey = GraphAssembly.EdgeSiteKey.Tagged(sourceId, candidateId, location.FilePath, location.LineNumber, location.ColumnNumber, GraphAssembly.CallTypeName(CallType.MethodCall), "VirtualCandidate");
                    var candidateHash = candidateKey.GetHashCode();
                    if (!fragment.Keys.Add(candidateKey, candidateHash))
                    {
                        continue;
                    }

                    fragment.Emit(new MethodCalls
                    {
                        Path = relativePath,
                        FileName = location.FileName,
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
                    }, new MethodCallEdge
                    {
                        SourceId = sourceId,
                        TargetId = candidateId,
                        CallLocation = new CallLocation { FileName = location.FileName, LineNumber = location.LineNumber, ColumnNumber = location.ColumnNumber },
                        Path = relativePath,
                        FileName = location.FileName,
                        IsInternal = true,
                        CalledMethodName = candidate.Name,
                        SourceName = sourceMethod.Name,
                        TargetName = candidate.Name,
                        Arguments = call.Arguments,
                        ArgumentExpressions = ["virtual-candidate"],
                        CallType = CallType.MethodCall,
                        EvidenceKind = AnalysisEvidenceKind.AssemblyIlVirtualCandidate,
                        Evidence = [CreateEvidence(AnalysisEvidenceKind.AssemblyIlVirtualCandidate, location, "Virtual candidate inferred by shared assembly CHA/RTA resolver.")]
                    }, candidateKey, candidateHash, callNeedsNewSite: true);
                }
            }
        }
    }

    /// <summary>
    ///     Steps the delegate-tracking stack over one instruction and returns the delegate call it
    ///     resolves, if any (an instruction resolves at most one).
    /// </summary>
    private static AssemblyResolvedDelegateCall? TrackDelegateInstruction(AssemblyScan scan, AssemblyCallInstruction instruction, AssemblyDelegateState state)
    {
        var opCode = instruction.OpCode;
        if ((opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn) && instruction.Operand is int methodToken && scan.Resolve(methodToken) is { } target)
        {
            state.Stack.Add(new AssemblyDelegateTarget(target.Member, target.TargetId));
            return null;
        }

        if (TryGetLdlocIndex(opCode, instruction.Operand, out var ldlocIndex))
        {
            state.Stack.Add(state.Locals.GetValueOrDefault(ldlocIndex));
            return null;
        }

        if (TryGetStlocIndex(opCode, instruction.Operand, out var stlocIndex))
        {
            state.Locals[stlocIndex] = state.Pop();
            return null;
        }

        if ((opCode == OpCodes.Ldfld || opCode == OpCodes.Ldsfld) && instruction.Operand is int loadFieldToken && scan.Resolve(loadFieldToken)?.Member is { } loadField)
        {
            if (opCode == OpCodes.Ldfld) _ = state.Pop();
            state.Stack.Add(state.Fields.GetValueOrDefault(loadField.Symbol));
            return null;
        }

        if ((opCode == OpCodes.Stfld || opCode == OpCodes.Stsfld) && instruction.Operand is int storeFieldToken && scan.Resolve(storeFieldToken)?.Member is { } storeField)
        {
            var value = state.Pop();
            if (opCode == OpCodes.Stfld) _ = state.Pop();
            state.Fields[storeField.Symbol] = value;
            return null;
        }

        if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj) && instruction.Operand is int callToken && scan.Resolve(callToken)?.Member is { } member)
        {
            var arguments = new List<AssemblyDelegateTarget?>();
            for (var i = 0; i < member.ParameterCount; i++) arguments.Add(state.Pop());
            arguments.Reverse();
            var receiver = opCode != OpCodes.Newobj && (member.HasThis || opCode == OpCodes.Callvirt) ? state.Pop() : null;

            if (opCode == OpCodes.Newobj)
            {
                var delegateTarget = IsDelegateConstructor(member) ? arguments.FirstOrDefault(argument => argument is not null) : null;
                state.Stack.Add(delegateTarget);
                return null;
            }

            AssemblyResolvedDelegateCall? resolved = null;
            if (member.Name == "Invoke" && receiver is not null)
            {
                resolved = new AssemblyResolvedDelegateCall(receiver, CallType.DelegateInvoke, "delegate-invoke", "Delegate.Invoke resolved to target method through IL delegate tracking.");
            }
            else if ((member.Name.StartsWith("add_", StringComparison.Ordinal) || member.Name.StartsWith("remove_", StringComparison.Ordinal)) && arguments.FirstOrDefault(argument => argument is not null) is { } eventTarget)
            {
                resolved = new AssemblyResolvedDelegateCall(eventTarget, member.Name.StartsWith("add_", StringComparison.Ordinal) ? CallType.EventSubscribe : CallType.EventUnsubscribe, "event-callback-target", "Event accessor callback target resolved through IL delegate tracking.");
            }

            if (!member.ReturnsVoid)
            {
                state.Stack.Add(null);
            }
            return resolved;
        }

        if (opCode == OpCodes.Dup)
        {
            state.Stack.Add(state.Stack.Count > 0 ? state.Stack[^1] : null);
            return null;
        }

        if (opCode == OpCodes.Pop)
        {
            _ = state.Pop();
            return null;
        }

        ApplyDefaultDelegateStackBehaviour(opCode, state);
        return null;
    }

    private static void AddResolvedDelegateEdge(AssemblyScan scan, AssemblyFragment fragment, Method sourceMethod, string sourceId, AssemblyResolvedDelegateCall resolved, AssemblyCallSourceLocation location)
    {
        var target = resolved.Target.Member;
        var targetId = resolved.Target.TargetId;
        var targetFileName = GetTargetFileName(target);
        AddNode(fragment.Nodes, targetId, target.Name, target.ClassName, target.Namespace, targetFileName, target.AssemblyName, targetFileName, "Method", target.LineNumber, target.ColumnNumber, isExternal: !target.IsInternal, AnalysisEvidenceKind.AssemblyIlDelegateTarget);
        var relativePath = scan.RelativeSourcePath(location.FilePath);
        var call = new MethodCalls
        {
            Path = relativePath,
            FileName = location.FileName,
            Assembly = target.AssemblyName,
            Module = targetFileName,
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
        var edgeKey = GraphAssembly.EdgeSiteKey.Tagged(sourceId, targetId, location.FilePath, location.LineNumber, location.ColumnNumber, GraphAssembly.CallTypeName(resolved.CallType), "ResolvedDelegate");
        var edgeHash = edgeKey.GetHashCode();
        MethodCallEdge? edge = null;
        if (fragment.Keys.Add(edgeKey, edgeHash))
        {
            edge = new MethodCallEdge
            {
                SourceId = sourceId,
                TargetId = targetId,
                CallLocation = new CallLocation { FileName = location.FileName, LineNumber = location.LineNumber, ColumnNumber = location.ColumnNumber },
                Path = relativePath,
                FileName = location.FileName,
                IsInternal = target.IsInternal,
                CalledMethodName = target.Name,
                SourceName = sourceMethod.Name,
                TargetName = target.Name,
                Arguments = call.Arguments,
                ArgumentExpressions = call.ArgumentExpressions,
                CallType = resolved.CallType,
                EvidenceKind = AnalysisEvidenceKind.AssemblyIlDelegateTarget,
                Evidence = [CreateEvidence(AnalysisEvidenceKind.AssemblyIlDelegateTarget, location, resolved.Description)]
            };
        }

        fragment.Emit(call, edge, edgeKey, edgeHash, callNeedsNewSite: false);
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
        FileName = location.FileName,
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

    private static HashSet<string> CollectInstantiatedTypes(PEReader peReader, AssemblyScan scan)
    {
        var reader = scan.Reader;
        var instantiatedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue;
            try
            {
                foreach (var instruction in DecodeInstructions(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILReader()))
                {
                    if (instruction.OpCode == OpCodes.Newobj && instruction.Operand is int token && scan.Resolve(token)?.Member is { } member)
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
            FileName = location.FileName,
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
        var source = GetEvidenceSource(evidenceKind);
        if (nodes.TryGetValue(id, out var existingNode))
        {
            // Most call sites re-reference a known node with evidence it already carries; the
            // evidence record is only built when it is new.
            MergeNodeFields(existingNode, className, namespaceName, fileName, assembly, module, lineNumber, columnNumber, isExternal);
            if (!HasNodeEvidence(existingNode, evidenceKind, source, fileName, lineNumber, columnNumber))
            {
                existingNode.Evidence.Add(CreateNodeEvidence(evidenceKind, source, isExternal, fileName, lineNumber, columnNumber));
            }

            MergeNodeIdentity(existingNode, assembly, module, namespaceName, className, evidenceKind);
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
            Evidence = [CreateNodeEvidence(evidenceKind, source, isExternal, fileName, lineNumber, columnNumber)]
        });
    }

    private static AnalysisEvidence CreateNodeEvidence(AnalysisEvidenceKind evidenceKind, string source, bool isExternal, string? fileName, int lineNumber, int columnNumber) => new()
    {
        Kind = evidenceKind,
        Source = source,
        Description = evidenceKind == AnalysisEvidenceKind.AssemblyIlGeneratedState
            ? "Application call graph node collapsed from generated async/iterator state-machine IL."
            : evidenceKind == AnalysisEvidenceKind.AssemblyReflection
                ? "Application call graph node discovered from assembly metadata."
                : isExternal ? "External call graph node referenced from assembly IL." : "Application call graph node discovered from assembly IL.",
        FileName = fileName,
        LineNumber = lineNumber,
        ColumnNumber = columnNumber
    };

    private static bool HasNodeEvidence(MethodNode node, AnalysisEvidenceKind kind, string? source, string? fileName, int lineNumber, int columnNumber)
    {
        foreach (var item in node.Evidence)
        {
            if (item.Kind == kind && item.Source == source && item.FileName == fileName && item.LineNumber == lineNumber && item.ColumnNumber == columnNumber)
            {
                return true;
            }
        }

        return false;
    }

    private static void MergeNodeFields(MethodNode target, string? className, string? namespaceName, string? fileName, string? assembly, string? module, int lineNumber, int columnNumber, bool isExternal)
    {
        if (string.IsNullOrWhiteSpace(target.ClassName) && !string.IsNullOrWhiteSpace(className)) target.ClassName = className;
        if (string.IsNullOrWhiteSpace(target.Namespace) && !string.IsNullOrWhiteSpace(namespaceName)) target.Namespace = namespaceName;
        if (string.IsNullOrWhiteSpace(target.FileName) && !string.IsNullOrWhiteSpace(fileName)) target.FileName = fileName;
        target.Assembly ??= assembly;
        target.Module ??= module;
        if (target.LineNumber <= 0 && lineNumber > 0) target.LineNumber = lineNumber;
        if (target.ColumnNumber <= 0 && columnNumber > 0) target.ColumnNumber = columnNumber;
        target.IsExternal &= isExternal;
    }

    private static void MergeNodeIdentity(MethodNode target, string? assembly, string? module, string? namespaceName, string? className, AnalysisEvidenceKind evidenceKind)
    {
        target.Identity ??= MethodIdentityFactory.FromParts(target.Id, null, target.Id, target.Id, assembly, module, namespaceName, className, target.Name, 0, target.Purl, evidenceKind);
        if (!target.Identity.Evidence.Contains(evidenceKind)) target.Identity.Evidence.Add(evidenceKind);
        target.Identity.AssemblyName ??= assembly;
        target.Identity.ModuleName ??= module;
        target.Identity.Namespace ??= namespaceName;
        target.Identity.ClassName ??= className;
    }

    /// <summary>
    ///     Folds a node one assembly built on its own into the graph's node of the same id. A
    ///     fragment node is the in-order replay of that assembly's <see cref="AddNode" /> calls
    ///     for the id, and every merge rule keeps the first non-empty value, ANDs IsExternal or
    ///     appends what is not yet present, so merging the replay result equals replaying the
    ///     calls one by one into the graph node.
    /// </summary>
    private static void MergeFragmentNode(MethodNode target, MethodNode fragmentNode)
    {
        MergeNodeFields(target, fragmentNode.ClassName, fragmentNode.Namespace, fragmentNode.FileName, fragmentNode.Assembly, fragmentNode.Module, fragmentNode.LineNumber, fragmentNode.ColumnNumber, fragmentNode.IsExternal);
        foreach (var evidence in fragmentNode.Evidence)
        {
            if (!HasNodeEvidence(target, evidence.Kind, evidence.Source, evidence.FileName, evidence.LineNumber, evidence.ColumnNumber))
            {
                target.Evidence.Add(evidence);
            }
        }

        var identity = fragmentNode.Identity!;
        foreach (var evidenceKind in identity.Evidence)
        {
            MergeNodeIdentity(target, identity.AssemblyName, identity.ModuleName, identity.Namespace, identity.ClassName, evidenceKind);
        }
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
        if (!File.Exists(pdbPath)) return new AssemblyCallSourceMap([], assemblyPath);
        try
        {
            using var pdbStream = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            var reader = provider.GetMetadataReader();
            var locations = new Dictionary<int, AssemblyCallSequencePoint[]>();
            // One path string and one file-name string per document: every call record and edge
            // of the assembly shares them instead of holding its own copies.
            var documents = new Dictionary<DocumentHandle, (string Path, string FileName)>();
            var points = new List<AssemblyCallSequencePoint>();
            var kickoffEntries = new List<(int KickoffToken, AssemblyCallSequencePoint Start)>();
            foreach (var methodDebugHandle in reader.MethodDebugInformation)
            {
                var rowNumber = MetadataTokens.GetRowNumber(methodDebugHandle);
                var methodDebugInfo = reader.GetMethodDebugInformation(methodDebugHandle);
                points.Clear();
                foreach (var sequencePoint in methodDebugInfo.GetSequencePoints())
                {
                    if (sequencePoint.IsHidden || sequencePoint.Document.IsNil) continue;
                    if (!documents.TryGetValue(sequencePoint.Document, out var document))
                    {
                        var documentPath = reader.GetString(reader.GetDocument(sequencePoint.Document).Name);
                        document = (documentPath, SourceDocumentPaths.FileName(documentPath));
                        documents.Add(sequencePoint.Document, document);
                    }

                    var location = new AssemblyCallSourceLocation(document.Path, Math.Max(1, sequencePoint.StartLine), Math.Max(1, sequencePoint.StartColumn)) { FileName = document.FileName };
                    points.Add(new AssemblyCallSequencePoint(sequencePoint.Offset, location));
                }
                if (points.Count > 0)
                {
                    AssemblyCallSequencePoint[] ordered = [.. points.OrderBy(point => point.Offset)];
                    locations[MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(rowNumber))] = ordered;
                    if (methodDebugInfo.GetStateMachineKickoffMethod() is { IsNil: false } kickoff)
                    {
                        kickoffEntries.Add((MetadataTokens.GetToken(kickoff), ordered[0]));
                    }
                }
            }

            // An async or iterator stub has no sequence point of its own; it resolves to its
            // state machine's MoveNext entry (issue #84, same rule as the data-flow IL pass), so
            // the call-graph node it owns is placed in the source, not at the assembly's line 1.
            foreach (var (kickoffToken, start) in kickoffEntries)
            {
                locations.TryAdd(kickoffToken, [start with { Offset = 0 }]);
            }

            return new AssemblyCallSourceMap(locations, assemblyPath);
        }
        catch
        {
            return new AssemblyCallSourceMap([], assemblyPath);
        }
    }

    private static List<string> GetAssemblyFiles(string path)
    {
        return AssemblyScope.GetAssemblyFiles(path, includeBuildArtifacts: false, excludeBinWhenSourceFilesPresent: true);
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
    private sealed record AssemblyCallSequencePoint(int Offset, AssemblyCallSourceLocation Location);

    private sealed record AssemblyCallSourceLocation(string FilePath, int LineNumber, int ColumnNumber)
    {
        /// <summary>The file name of <see cref="FilePath" />; built once per document and shared.</summary>
        public required string FileName { get; init; }
    }

    /// <summary>
    ///     IL offset to source location for one assembly. Each method's sequence points are sorted
    ///     by offset once; a lookup is a binary search for the last point at or before the offset
    ///     (the first of several sharing that offset), returning the point's shared location.
    ///     Offsets before the first point, and methods without debug information, fall back to the
    ///     assembly path with the IL offset as the line.
    /// </summary>
    private sealed class AssemblyCallSourceMap(Dictionary<int, AssemblyCallSequencePoint[]> pointsByToken, string assemblyPath)
    {
        private readonly string _assemblyFileName = Path.GetFileName(assemblyPath);

        public AssemblyCallSourceLocation Resolve(int methodToken, int ilOffset, string inspectedAssemblyPath)
        {
            if (pointsByToken.TryGetValue(methodToken, out var points))
            {
                var low = 0;
                var high = points.Length - 1;
                var found = -1;
                while (low <= high)
                {
                    var middle = low + ((high - low) >> 1);
                    if (points[middle].Offset <= ilOffset)
                    {
                        found = middle;
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle - 1;
                    }
                }

                if (found >= 0)
                {
                    while (found > 0 && points[found - 1].Offset == points[found].Offset) found--;
                    return points[found].Location;
                }
            }

            var fileName = string.Equals(inspectedAssemblyPath, assemblyPath, StringComparison.Ordinal) ? _assemblyFileName : Path.GetFileName(inspectedAssemblyPath);
            return new AssemblyCallSourceLocation(inspectedAssemblyPath, Math.Max(1, ilOffset), 1) { FileName = fileName };
        }
    }

    /// <summary>A member token resolved once per assembly, with the graph id and module name every call site of it shares.</summary>
    private sealed record ResolvedMember(AssemblyCallMember Member, string TargetId, string TargetFileName);

    /// <summary>Read-only state shared by every assembly worker.</summary>
    private sealed class AnalysisContext
    {
        private readonly Dictionary<string, List<Method>> _methodsByAssembly = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="analyzedByFullPath">
        ///     <see cref="AssemblyCopies.AnalyzedByFullPath" />: a known method the inventory read
        ///     from a copy the pass leaves out belongs to the copy it analyzes, so its bodies keep
        ///     their known identities (issue #83).
        /// </param>
        public AnalysisContext(string inspectedPath, IReadOnlyList<Method> knownMethods, IReadOnlyDictionary<string, string> analyzedByFullPath)
        {
            RelativeRoot = SourceDocumentPaths.Root(inspectedPath);
            var methodLookup = new Dictionary<(string Path, int Token), Method>();
            // The file whose methods each analyzed copy took: the inventory reads one copy per
            // assembly identity, and methods of a second copy of the same file would repeat them.
            var inventoriedCopyByAnalyzed = new Dictionary<string, string>(SafeFileRead.PathComparer);
            // One full-path resolution per known method (issue #65: the dispatch index used to
            // re-resolve every known method's path once per assembly). Grouping keeps the known
            // methods' order, which is the order the per-assembly filter produced. A path that
            // does not resolve belongs to no assembly; it used to fail every assembly's analysis.
            foreach (var method in knownMethods)
            {
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(method.Path ?? string.Empty);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (analyzedByFullPath.TryGetValue(fullPath, out var analyzed))
                {
                    if (inventoriedCopyByAnalyzed.TryGetValue(analyzed, out var inventoried) && !SafeFileRead.PathComparer.Equals(inventoried, fullPath))
                    {
                        continue;
                    }

                    inventoriedCopyByAnalyzed[analyzed] = fullPath;
                    fullPath = analyzed;
                }

                if (!_methodsByAssembly.TryGetValue(fullPath, out var methods))
                {
                    methods = [];
                    _methodsByAssembly.Add(fullPath, methods);
                }

                methods.Add(method);
                if (method.MetadataToken != 0 && !string.IsNullOrWhiteSpace(method.AssemblySignature))
                {
                    methodLookup.TryAdd((fullPath, method.MetadataToken), method);
                }
            }

            MethodLookup = methodLookup;
        }

        public string? RelativeRoot { get; }

        public IReadOnlyDictionary<(string Path, int Token), Method> MethodLookup { get; }

        public IReadOnlyList<Method> MethodsOf(string assemblyFullPath) => _methodsByAssembly.TryGetValue(assemblyFullPath, out var methods) ? methods : [];
    }

    /// <summary>Per-assembly metadata access with memoized member resolution and relative paths; owned by one worker.</summary>
    private sealed class AssemblyScan(MetadataReader reader, string assemblyPath, string assemblyFullPath, AssemblyCallSourceMap sourceMap, AnalysisContext context)
    {
        private readonly Dictionary<int, ResolvedMember?> _members = [];
        private readonly Dictionary<string, string?> _relativePaths = new(StringComparer.Ordinal);
        private bool? _isBuiltFromSource;

        public MetadataReader Reader => reader;
        public string AssemblyPath => assemblyPath;
        public string AssemblyFullPath => assemblyFullPath;
        public AssemblyCallSourceMap SourceMap => sourceMap;
        public AnalysisContext Context => context;

        /// <summary>
        ///     The member a token names, resolved once: every call site of a member shares one
        ///     symbol string, target id and module name instead of rebuilding them. A token that
        ///     fails to resolve throws every time, as before, and is not cached.
        /// </summary>
        public ResolvedMember? Resolve(int token)
        {
            if (_members.TryGetValue(token, out var cached))
            {
                return cached;
            }

            var resolved = ResolveMember(reader, token, assemblyPath, sourceMap) is { } member
                ? new ResolvedMember(member, ResolveInternalTargetId(assemblyFullPath, member.MetadataToken, member.Symbol, context.MethodLookup), GetTargetFileName(member))
                : null;
            _members.Add(token, resolved);
            return resolved;
        }

        /// <summary>
        ///     A call site's file (a PDB document, or the assembly where no sequence point covers the
        ///     call) as the scan tree names it, or null when it has no place there. Call records and
        ///     edges both carry this path (issue #79).
        /// </summary>
        public string? RelativeSourcePath(string sourcePath)
        {
            if (!_relativePaths.TryGetValue(sourcePath, out var relative))
            {
                relative = SourceDocumentPaths.InTree(context.RelativeRoot, sourcePath, IsBuiltFromSource);
                _relativePaths.Add(sourcePath, relative);
            }

            return relative;
        }

        private bool IsBuiltFromSource => _isBuiltFromSource ??= context.RelativeRoot is { } root && TreeFrameworks.IsBuiltFromSource(root, assemblyPath);
    }

    /// <summary>
    ///     What one assembly contributes, in the order the sequential loop produced it: its nodes
    ///     (each the replay of the assembly's node additions for that id), and its call records
    ///     and edges with their call-site keys. Duplicate sites within the assembly are dropped
    ///     while it is analyzed; duplicates of earlier assemblies are dropped at the in-order merge.
    /// </summary>
    private sealed class AssemblyFragment
    {
        private readonly List<Emission> _emissions = [];

        public Dictionary<string, MethodNode> Nodes { get; } = new(StringComparer.Ordinal);

        /// <summary>The assembly's own call-site keys; released once the assembly is analyzed.</summary>
        public GraphAssembly.EdgeSiteIndex Keys { get; private set; } = new();

        /// <summary>
        ///     A call record with its edge (null when the assembly already emitted that site). A
        ///     record that only exists for a new site (a dispatch candidate) also drops with a
        ///     site an earlier assembly emitted.
        /// </summary>
        public void Emit(MethodCalls call, MethodCallEdge? edge, GraphAssembly.EdgeSiteKey key, int hash, bool callNeedsNewSite) =>
            _emissions.Add(new Emission(call, edge, key, hash, callNeedsNewSite));

        public void CompleteAnalysis() => Keys = null!;

        public void MergeInto(List<MethodCalls> calls, Dictionary<string, MethodNode> nodes, List<MethodCallEdge> edges, GraphAssembly.EdgeSiteIndex edgeKeys)
        {
            foreach (var (id, node) in Nodes)
            {
                if (nodes.TryGetValue(id, out var existing))
                {
                    MergeFragmentNode(existing, node);
                }
                else
                {
                    nodes.Add(id, node);
                }
            }

            foreach (var emission in _emissions)
            {
                var newSite = emission.Edge is not null && edgeKeys.Add(emission.Key, emission.Hash);
                if (newSite || !emission.CallNeedsNewSite)
                {
                    calls.Add(emission.Call);
                }

                if (newSite)
                {
                    edges.Add(emission.Edge!);
                }
            }
        }

        private readonly record struct Emission(MethodCalls Call, MethodCallEdge? Edge, GraphAssembly.EdgeSiteKey Key, int Hash, bool CallNeedsNewSite);
    }
}
