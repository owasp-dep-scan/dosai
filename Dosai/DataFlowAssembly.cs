using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Depscan;

public static partial class DataFlowAnalyzer
{
    private static readonly Dictionary<short, OpCode> SingleByteOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.GetValue(null) is OpCode opCode && opCode.Size == 1)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((short)(ushort)opCode.Value));

    private static readonly Dictionary<short, OpCode> MultiByteOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.GetValue(null) is OpCode opCode && opCode.Size == 2)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((short)(ushort)opCode.Value));

    private static int AnalyzeAssemblyDataFlows(string path, DataFlowPatternSet patterns, DataFlowResult result, bool includeBuildArtifacts)
    {
        var assemblyPaths = GetAssemblyFiles(path, includeBuildArtifacts, result.Diagnostics);
        if (assemblyPaths.Count == 0)
        {
            return 0;
        }

        var context = new AssemblyDataFlowContext(result, patterns, path);
        var summaries = new Dictionary<string, AssemblyMethodSummary>(StringComparer.Ordinal);
        var analyzedAssemblies = 0;
        foreach (var assemblyPath in assemblyPaths)
        {
            if (!IsManagedAssemblyFile(assemblyPath))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata)
                {
                    continue;
                }

                analyzedAssemblies++;
                var reader = peReader.GetMetadataReader();
                var sourceMap = LoadPortablePdbSourceMap(assemblyPath, result.Diagnostics);
                CollectAssemblyMethodSummaries(peReader, reader, sourceMap, assemblyPath, context, summaries);
                PreseedAssemblyFieldTaints(peReader, reader, sourceMap, assemblyPath, context);
                foreach (var methodHandle in reader.MethodDefinitions)
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    try
                    {
                        AnalyzeAssemblyMethod(peReader, reader, methodHandle, method, assemblyPath, context, sourceMap, summaries);
                    }
                    catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or ArgumentOutOfRangeException)
                    {
                        result.Diagnostics.Add($"Could not analyze IL body {DescribeMethod(reader, methodHandle, method)} in {assemblyPath}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                result.Diagnostics.Add($"Could not inspect assembly {assemblyPath}: {ex.Message}");
            }
        }

        return analyzedAssemblies;
    }

    private static void CollectAssemblyMethodSummaries(PEReader peReader, MetadataReader reader, AssemblySourceMap sourceMap, string assemblyPath, AssemblyDataFlowContext context, Dictionary<string, AssemblyMethodSummary> summaries)
    {
        var methodDefinitions = reader.MethodDefinitions
            .Select(handle => (Handle: handle, Definition: reader.GetMethodDefinition(handle)))
            .Where(item => item.Definition.RelativeVirtualAddress != 0)
            .ToList();

        for (var iteration = 0; iteration < 3; iteration++)
        {
            var changed = false;
            foreach (var (methodHandle, method) in methodDefinitions)
            {
                try
                {
                    var summary = BuildAssemblyMethodSummary(peReader, reader, methodHandle, method, assemblyPath, sourceMap, context, summaries);
                    if (!summaries.TryGetValue(summary.Method, out var existing) || existing.Merge(summary))
                    {
                        summaries[summary.Method] = existing ?? summary;
                        changed = true;
                    }
                }
                catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or ArgumentOutOfRangeException)
                {
                    // Best-effort: body-level diagnostics are emitted by the main analysis pass.
                }
            }

            if (!changed)
            {
                break;
            }
        }
    }

    private static void PreseedAssemblyFieldTaints(PEReader peReader, MetadataReader reader, AssemblySourceMap sourceMap, string assemblyPath, AssemblyDataFlowContext context)
    {
        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue;
            try
            {
                var methodInfo = BuildMethodInfo(reader, methodHandle, method, assemblyPath, sourceMap);
                var isStatic = (method.Attributes & MethodAttributes.Static) != 0;
                var argumentTaints = SeedAssemblyParameters(reader, method, methodInfo, isStatic, context);
                if (argumentTaints.Count == 0) continue;
                var stack = new List<AssemblyTaint?>();
                foreach (var instruction in DecodeInstructions(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILReader()))
                {
                    if (TryGetLdargIndex(instruction.OpCode, instruction.Operand, out var argIndex))
                    {
                        stack.Add(argumentTaints.TryGetValue(argIndex, out var taint) ? taint : null);
                        continue;
                    }

                    if (instruction.OpCode == OpCodes.Stfld || instruction.OpCode == OpCodes.Stsfld)
                    {
                        var valueTaint = stack.Count == 0 ? null : stack[^1];
                        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                        if (instruction.OpCode == OpCodes.Stfld && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                        if (valueTaint is not null && instruction.Operand is int fieldToken && ResolveMember(reader, fieldToken) is { } field)
                        {
                            context.RecordFieldTaint(field.Symbol, valueTaint);
                        }
                        continue;
                    }

                    ApplyDefaultListStackBehaviour(instruction.OpCode, stack);
                }
            }
            catch
            {
                // Best-effort field preseed only.
            }
        }
    }

    private static void AnalyzeAssemblyMethod(PEReader peReader, MetadataReader reader, MethodDefinitionHandle methodHandle, MethodDefinition method, string assemblyPath, AssemblyDataFlowContext context, AssemblySourceMap sourceMap, IReadOnlyDictionary<string, AssemblyMethodSummary> summaries)
    {
        var methodInfo = BuildMethodInfo(reader, methodHandle, method, assemblyPath, sourceMap);
        var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
        var il = body.GetILReader();
        var instructions = DecodeInstructions(il).ToList();
        if (instructions.Count == 0)
        {
            return;
        }

        var instructionIndexByOffset = instructions.Select((instruction, index) => (instruction.Offset, index)).ToDictionary(item => item.Offset, item => item.index);
        var isStatic = (method.Attributes & MethodAttributes.Static) != 0;
        var initialState = new AssemblyMethodState([], [], SeedAssemblyParameters(reader, method, methodInfo, isStatic, context));
        var worklist = new Queue<(int Index, AssemblyMethodState State)>();
        var visitCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        worklist.Enqueue((0, initialState));

        while (worklist.Count > 0)
        {
            var (instructionIndex, state) = worklist.Dequeue();
            if (instructionIndex < 0 || instructionIndex >= instructions.Count)
            {
                continue;
            }

            var visitKey = $"{instructionIndex}:{state.Signature()}";
            visitCounts.TryGetValue(visitKey, out var visitCount);
            if (visitCount >= 2 || visitCounts.Values.Count(value => value > 0) > 20000)
            {
                continue;
            }
            visitCounts[visitKey] = visitCount + 1;

            state = state.Clone();
            var instruction = instructions[instructionIndex];
            var opCode = instruction.OpCode;
            if (opCode == OpCodes.Nop)
            {
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (TryGetLdargIndex(opCode, instruction.Operand, out var ldargIndex))
            {
                state.Push(state.Arguments.TryGetValue(ldargIndex, out var taint) ? taint : null);
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (TryGetStargIndex(opCode, instruction.Operand, out var stargIndex))
            {
                state.Arguments[stargIndex] = state.Pop();
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (TryGetLdlocIndex(opCode, instruction.Operand, out var ldlocIndex))
            {
                state.Push(state.Locals.TryGetValue(ldlocIndex, out var taint) ? taint : null);
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (TryGetStlocIndex(opCode, instruction.Operand, out var stlocIndex))
            {
                var localTaint = state.Pop();
                if (localTaint is not null)
                {
                    var localName = sourceMap.GetLocalName(methodInfo.MetadataToken, stlocIndex, instruction.Offset) ?? $"local_{stlocIndex}";
                    var assignmentNode = context.AddNode("Assignment", localName, methodInfo, assemblyPath, isSource: false, isSink: false, [], null, $"{methodInfo.Symbol}.{localName}", null, localName, instruction.Offset + 1);
                    context.AddEdges(localTaint.NodeIds, assignmentNode.Id, "AssemblyLocalAssignment", methodInfo, assemblyPath, instruction.Offset + 1, localName);
                    localTaint = localTaint.Append(assignmentNode.Id);
                }
                state.Locals[stlocIndex] = localTaint;
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Ldstr && instruction.Operand is int userStringToken)
            {
                var value = reader.GetUserString(MetadataTokens.UserStringHandle(userStringToken));
                var matched = context.MatchSourceCode(value).ToList();
                if (matched.Count > 0)
                {
                    var node = context.AddNode("Source", "string", methodInfo, assemblyPath, isSource: true, isSink: false, matched, matched.FirstOrDefault()?.Category, "System.String", "System.String", value, instruction.Offset + 1);
                    state.Push(new AssemblyTaint([node.Id], ToTaintKinds(matched), []));
                }
                else
                {
                    state.Push(null);
                }
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Ldfld || opCode == OpCodes.Ldsfld)
            {
                var instanceTaint = opCode == OpCodes.Ldfld ? state.Pop() : null;
                var member = instruction.Operand is int fieldToken ? ResolveMember(reader, fieldToken) : null;
                var fieldTaint = member is not null && state.Fields.TryGetValue(member.Symbol, out var taint)
                    ? taint
                    : member is not null && context.TryGetFieldTaint(member.Symbol, out var storedTaint)
                        ? storedTaint
                        : instanceTaint;
                state.Push(fieldTaint);
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Stfld || opCode == OpCodes.Stsfld)
            {
                var valueTaint = state.Pop();
                if (opCode == OpCodes.Stfld)
                {
                    _ = state.Pop();
                }
                if (instruction.Operand is int fieldToken && ResolveMember(reader, fieldToken) is { } member)
                {
                    state.Fields[member.Symbol] = valueTaint;
                    if (valueTaint is not null)
                    {
                        context.RecordFieldTaint(member.Symbol, valueTaint);
                    }
                }
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Ldelem || opCode == OpCodes.Ldelem_I || opCode == OpCodes.Ldelem_I1 || opCode == OpCodes.Ldelem_I2 || opCode == OpCodes.Ldelem_I4 || opCode == OpCodes.Ldelem_I8 || opCode == OpCodes.Ldelem_R4 || opCode == OpCodes.Ldelem_R8 || opCode == OpCodes.Ldelem_Ref || opCode == OpCodes.Ldelem_U1 || opCode == OpCodes.Ldelem_U2 || opCode == OpCodes.Ldelem_U4)
            {
                _ = state.Pop(); // index
                state.Push(state.Pop()); // array/reference
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Stelem || opCode == OpCodes.Stelem_I || opCode == OpCodes.Stelem_I1 || opCode == OpCodes.Stelem_I2 || opCode == OpCodes.Stelem_I4 || opCode == OpCodes.Stelem_I8 || opCode == OpCodes.Stelem_R4 || opCode == OpCodes.Stelem_R8 || opCode == OpCodes.Stelem_Ref)
            {
                _ = state.Pop(); // value
                _ = state.Pop(); // index
                _ = state.Pop(); // array
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj)
            {
                ProcessAssemblyCall(reader, instruction, opCode, methodInfo, assemblyPath, context, state, summaries);
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Dup)
            {
                state.Push(state.Stack.Count > 0 ? state.Stack[^1] : null);
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Pop)
            {
                _ = state.Pop();
                EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Ret)
            {
                if (state.Stack.Count > 0 && state.Pop() is { } returnTaint)
                {
                    var node = context.AddNode("Return", "return", methodInfo, assemblyPath, isSource: false, isSink: false, [], null, methodInfo.Symbol, methodInfo.ReturnType, "ret", instruction.Offset + 1);
                    context.AddEdges(returnTaint.NodeIds, node.Id, "AssemblyReturn", methodInfo, assemblyPath, instruction.Offset + 1, "returned value");
                }
                continue;
            }

            ApplyDefaultStackBehaviour(opCode, state);
            EnqueueSuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
        }
    }

    private static void ProcessAssemblyCall(MetadataReader reader, AssemblyInstruction instruction, OpCode opCode, AssemblyMethodInfo currentMethod, string assemblyPath, AssemblyDataFlowContext context, AssemblyMethodState state, IReadOnlyDictionary<string, AssemblyMethodSummary> summaries)
    {
        if (instruction.Operand is not int token || ResolveMember(reader, token) is not { } member)
        {
            ApplyDefaultStackBehaviour(opCode, state);
            return;
        }

        var argumentTaints = new List<AssemblyTaint?>();
        for (var i = 0; i < member.ParameterCount; i++)
        {
            argumentTaints.Add(state.Pop());
        }
        argumentTaints.Reverse();
        var receiverTaint = member.HasThis && opCode != OpCodes.Newobj ? state.Pop() : null;
        var allTaints = argumentTaints.Concat([receiverTaint]).Where(taint => taint is not null).Cast<AssemblyTaint>().ToList();

        var sourcePatterns = context.MatchSource(member).ToList();
        if (sourcePatterns.Count > 0)
        {
            var node = context.AddNode("Source", member.Name, currentMethod, assemblyPath, isSource: true, isSink: false, sourcePatterns, sourcePatterns.FirstOrDefault()?.Category, member.Symbol, member.ContainingType, member.Symbol, instruction.Offset + 1);
            var sourceTaint = new AssemblyTaint([node.Id], ToTaintKinds(sourcePatterns), []);
            if (!member.ReturnsVoid || opCode == OpCodes.Newobj)
            {
                state.Push(sourceTaint);
            }
            return;
        }

        var sanitizerPatterns = context.MatchSanitizer(member).ToList();
        var combined = sanitizerPatterns.Count > 0 ? null : CombineAssemblyTaints(allTaints);
        var sinkPatterns = context.MatchSink(member).ToList();
        if (sinkPatterns.Count > 0 && combined is not null)
        {
            var sinkNode = context.AddNode("Sink", member.Name, currentMethod, assemblyPath, isSource: false, isSink: true, sinkPatterns, sinkPatterns.FirstOrDefault()?.Category, member.Symbol, member.ContainingType, member.Symbol, instruction.Offset + 1);
            context.AddEdges(combined.NodeIds, sinkNode.Id, opCode == OpCodes.Newobj ? "AssemblySinkObjectCreation" : "AssemblySinkCall", currentMethod, assemblyPath, instruction.Offset + 1, member.Name);
            var sinkArgumentIndex = argumentTaints.FindIndex(taint => taint is not null);
            context.AddSlice(combined, sinkNode, sinkPatterns.FirstOrDefault(), member.Symbol, sinkArgumentIndex >= 0 ? sinkArgumentIndex : -1);
        }

        if (summaries.TryGetValue(member.Symbol, out var summary))
        {
            foreach (var sinkParameterIndex in summary.SinkParameterIndexes.Where(index => index >= 0 && index < argumentTaints.Count && argumentTaints[index] is not null))
            {
                var taint = argumentTaints[sinkParameterIndex]!;
                var summaryPattern = new DataFlowPattern
                {
                    Target = DataFlowPatternTarget.Sink,
                    Kind = DataFlowPatternKind.Method,
                    Pattern = member.Symbol,
                    Category = summary.SinkCategories.FirstOrDefault() ?? "interprocedural",
                    Description = "Assembly IL sink reached through a summarized callee"
                };
                var sinkNode = context.AddNode("Sink", member.Name, currentMethod, assemblyPath, isSource: false, isSink: true, [summaryPattern], summaryPattern.Category, member.Symbol, member.ContainingType, member.Symbol, instruction.Offset + 1);
                sinkNode.Properties["summaryMethod"] = summary.Method;
                context.AddEdges(taint.NodeIds, sinkNode.Id, "AssemblyInterproceduralSink", currentMethod, assemblyPath, instruction.Offset + 1, member.Name);
                context.AddSlice(taint, sinkNode, summaryPattern, member.Symbol, sinkParameterIndex);
            }
        }

        if (member.ReturnsVoid && opCode != OpCodes.Newobj)
        {
            return;
        }

        if (summaries.TryGetValue(member.Symbol, out var returnSummary))
        {
            var returnTaints = returnSummary.ReturnParameterIndexes
                .Where(index => index >= 0 && index < argumentTaints.Count)
                .Select(index => argumentTaints[index])
                .Where(taint => taint is not null)
                .Cast<AssemblyTaint>()
                .ToList();
            if (returnTaints.Count > 0 && CombineAssemblyTaints(returnTaints) is { } returnCombined)
            {
                var callNode = context.AddNode("CallSummary", member.Name, currentMethod, assemblyPath, isSource: false, isSink: false, [], null, member.Symbol, member.ContainingType, member.Symbol, instruction.Offset + 1);
                context.AddEdges(returnCombined.NodeIds, callNode.Id, "AssemblyInterproceduralReturn", currentMethod, assemblyPath, instruction.Offset + 1, member.Name);
                state.Push(returnCombined.Append(callNode.Id));
                return;
            }
        }

        if (combined is null)
        {
            state.Push(null);
            return;
        }

        var passthroughPatterns = context.MatchPassthrough(member).ToList();
        if (passthroughPatterns.Count > 0 || member.Symbol.StartsWith("System.", StringComparison.Ordinal))
        {
            var callNode = context.AddNode("Call", member.Name, currentMethod, assemblyPath, isSource: false, isSink: false, [], null, member.Symbol, member.ContainingType, member.Symbol, instruction.Offset + 1);
            context.AddEdges(combined.NodeIds, callNode.Id, "AssemblyCallReturn", currentMethod, assemblyPath, instruction.Offset + 1, member.Name);
            state.Push(combined.Append(callNode.Id));
        }
        else
        {
            state.Push(combined);
        }
    }

    private static Dictionary<int, AssemblyTaint?> SeedAssemblyParameters(MetadataReader reader, MethodDefinition method, AssemblyMethodInfo methodInfo, bool isStatic, AssemblyDataFlowContext context)
    {
        var argumentTaints = new Dictionary<int, AssemblyTaint?>();
        foreach (var parameterHandle in method.GetParameters())
        {
            var parameter = reader.GetParameter(parameterHandle);
            if (parameter.SequenceNumber == 0)
            {
                continue;
            }

            var parameterName = reader.GetString(parameter.Name);
            var ilIndex = isStatic ? parameter.SequenceNumber - 1 : parameter.SequenceNumber;
            var matched = context.MatchParameterSource(parameterName, methodInfo)
                .Concat(context.MatchAttributeSource(GetAttributeNames(reader, method.GetCustomAttributes()).Concat(GetAttributeNames(reader, parameter.GetCustomAttributes()))))
                .DistinctBy(pattern => pattern.Pattern)
                .ToList();
            if (matched.Count == 0)
            {
                continue;
            }

            var node = context.AddNode("Source", parameterName, methodInfo, methodInfo.AssemblyPath, isSource: true, isSink: false, matched, matched.FirstOrDefault()?.Category, $"{methodInfo.Symbol}.{parameterName}", null, parameterName, 1);
            argumentTaints[ilIndex] = new AssemblyTaint([node.Id], ToTaintKinds(matched), []);
        }
        return argumentTaints;
    }

    private static AssemblyMethodSummary BuildAssemblyMethodSummary(PEReader peReader, MetadataReader reader, MethodDefinitionHandle methodHandle, MethodDefinition method, string assemblyPath, AssemblySourceMap sourceMap, AssemblyDataFlowContext context, IReadOnlyDictionary<string, AssemblyMethodSummary> summaries)
    {
        var methodInfo = BuildMethodInfo(reader, methodHandle, method, assemblyPath, sourceMap);
        var summary = new AssemblyMethodSummary(methodInfo.Symbol);
        var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
        var instructions = DecodeInstructions(body.GetILReader()).ToList();
        if (instructions.Count == 0)
        {
            return summary;
        }

        var instructionIndexByOffset = instructions.Select((instruction, index) => (instruction.Offset, index)).ToDictionary(item => item.Offset, item => item.index);
        var isStatic = (method.Attributes & MethodAttributes.Static) != 0;
        var argumentTaints = new Dictionary<int, AssemblySummaryTaint?>();
        foreach (var parameterHandle in method.GetParameters())
        {
            var parameter = reader.GetParameter(parameterHandle);
            if (parameter.SequenceNumber == 0) continue;
            var ilIndex = isStatic ? parameter.SequenceNumber - 1 : parameter.SequenceNumber;
            argumentTaints[ilIndex] = new AssemblySummaryTaint([parameter.SequenceNumber - 1]);
        }

        var worklist = new Queue<(int Index, AssemblySummaryState State)>();
        var visits = new Dictionary<string, int>(StringComparer.Ordinal);
        worklist.Enqueue((0, new AssemblySummaryState([], [], argumentTaints)));
        while (worklist.Count > 0)
        {
            var (instructionIndex, state) = worklist.Dequeue();
            if (instructionIndex < 0 || instructionIndex >= instructions.Count) continue;
            var visitKey = $"{instructionIndex}:{state.Signature()}";
            visits.TryGetValue(visitKey, out var visitCount);
            if (visitCount >= 2 || visits.Count > 10000) continue;
            visits[visitKey] = visitCount + 1;

            state = state.Clone();
            var instruction = instructions[instructionIndex];
            var opCode = instruction.OpCode;

            if (TryGetLdargIndex(opCode, instruction.Operand, out var ldargIndex))
            {
                state.Push(state.Arguments.TryGetValue(ldargIndex, out var taint) ? taint : null);
                EnqueueSummarySuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (TryGetStargIndex(opCode, instruction.Operand, out var stargIndex))
            {
                state.Arguments[stargIndex] = state.Pop();
                EnqueueSummarySuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (TryGetLdlocIndex(opCode, instruction.Operand, out var ldlocIndex))
            {
                state.Push(state.Locals.TryGetValue(ldlocIndex, out var taint) ? taint : null);
                EnqueueSummarySuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (TryGetStlocIndex(opCode, instruction.Operand, out var stlocIndex))
            {
                state.Locals[stlocIndex] = state.Pop();
                EnqueueSummarySuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Ldelem || opCode == OpCodes.Ldelem_I || opCode == OpCodes.Ldelem_I1 || opCode == OpCodes.Ldelem_I2 || opCode == OpCodes.Ldelem_I4 || opCode == OpCodes.Ldelem_I8 || opCode == OpCodes.Ldelem_R4 || opCode == OpCodes.Ldelem_R8 || opCode == OpCodes.Ldelem_Ref || opCode == OpCodes.Ldelem_U1 || opCode == OpCodes.Ldelem_U2 || opCode == OpCodes.Ldelem_U4)
            {
                _ = state.Pop();
                state.Push(state.Pop());
                EnqueueSummarySuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj)
            {
                ProcessAssemblySummaryCall(reader, instruction, opCode, context, state, summaries, summary);
                EnqueueSummarySuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Dup)
            {
                state.Push(state.Stack.Count > 0 ? state.Stack[^1] : null);
                EnqueueSummarySuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Pop)
            {
                _ = state.Pop();
                EnqueueSummarySuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
                continue;
            }

            if (opCode == OpCodes.Ret)
            {
                if (state.Stack.Count > 0 && state.Pop() is { } returnTaint)
                {
                    foreach (var index in returnTaint.ParameterIndexes)
                    {
                        summary.AddReturnParameter(index);
                    }
                }
                continue;
            }

            ApplyDefaultSummaryStackBehaviour(opCode, state);
            EnqueueSummarySuccessors(instructionIndex, instruction, instructions, instructionIndexByOffset, state, worklist);
        }

        return summary;
    }

    private static void ProcessAssemblySummaryCall(MetadataReader reader, AssemblyInstruction instruction, OpCode opCode, AssemblyDataFlowContext context, AssemblySummaryState state, IReadOnlyDictionary<string, AssemblyMethodSummary> summaries, AssemblyMethodSummary currentSummary)
    {
        if (instruction.Operand is not int token || ResolveMember(reader, token) is not { } member)
        {
            ApplyDefaultSummaryStackBehaviour(opCode, state);
            return;
        }

        var argumentTaints = new List<AssemblySummaryTaint?>();
        for (var i = 0; i < member.ParameterCount; i++) argumentTaints.Add(state.Pop());
        argumentTaints.Reverse();
        var receiverTaint = member.HasThis && opCode != OpCodes.Newobj ? state.Pop() : null;
        var combined = CombineSummaryTaints(argumentTaints.Concat([receiverTaint]).Where(taint => taint is not null).Cast<AssemblySummaryTaint>());

        var sinkPatterns = context.MatchSink(member).ToList();
        if (sinkPatterns.Count > 0 && combined is not null)
        {
            foreach (var index in combined.ParameterIndexes) currentSummary.AddSinkParameter(index);
            foreach (var category in sinkPatterns.Select(pattern => pattern.Category).Where(category => !string.IsNullOrWhiteSpace(category))) currentSummary.AddSinkCategory(category!);
        }

        if (summaries.TryGetValue(member.Symbol, out var calleeSummary))
        {
            foreach (var sinkParameterIndex in calleeSummary.SinkParameterIndexes.Where(index => index >= 0 && index < argumentTaints.Count && argumentTaints[index] is not null))
            {
                foreach (var index in argumentTaints[sinkParameterIndex]!.ParameterIndexes) currentSummary.AddSinkParameter(index);
                foreach (var category in calleeSummary.SinkCategories) currentSummary.AddSinkCategory(category);
            }
        }

        if (member.ReturnsVoid && opCode != OpCodes.Newobj) return;

        if (summaries.TryGetValue(member.Symbol, out var returnSummary))
        {
            var returnTaints = returnSummary.ReturnParameterIndexes.Where(index => index >= 0 && index < argumentTaints.Count).Select(index => argumentTaints[index]).Where(taint => taint is not null).Cast<AssemblySummaryTaint>().ToList();
            state.Push(CombineSummaryTaints(returnTaints));
            return;
        }

        var passthroughPatterns = context.MatchPassthrough(member).ToList();
        state.Push(passthroughPatterns.Count > 0 || member.Symbol.StartsWith("System.", StringComparison.Ordinal) ? combined : null);
    }

    private static AssemblyMethodInfo BuildMethodInfo(MetadataReader reader, MethodDefinitionHandle methodHandle, MethodDefinition method, string assemblyPath, AssemblySourceMap? sourceMap = null)
    {
        var declaringType = reader.GetTypeDefinition(method.GetDeclaringType());
        var typeName = GetFullTypeName(reader, declaringType);
        var methodName = reader.GetString(method.Name);
        var signature = ReadSignatureInfo(reader, method.Signature, method.Attributes);
        var normalizedMethodName = methodName == ".ctor" || methodName == ".cctor" ? methodName : methodName;
        var symbol = $"{typeName}.{normalizedMethodName}({string.Join(',', Enumerable.Repeat("?", signature.ParameterCount))})";
        if (!signature.ReturnsVoid && methodName != ".ctor" && methodName != ".cctor")
        {
            symbol += $":{signature.ReturnType}";
        }

        var metadataToken = MetadataTokens.GetToken(methodHandle);
        return new AssemblyMethodInfo(symbol, methodName, typeName, GetNamespace(typeName), Path.GetFileNameWithoutExtension(assemblyPath), signature.ReturnType, assemblyPath, metadataToken, sourceMap?.GetLocations(metadataToken) ?? []);
    }

    private static string DescribeMethod(MetadataReader reader, MethodDefinitionHandle methodHandle, MethodDefinition method)
    {
        try
        {
            return BuildMethodInfo(reader, methodHandle, method, string.Empty).Symbol;
        }
        catch
        {
            return $"method:{MetadataTokens.GetToken(methodHandle):x8}";
        }
    }

    private static AssemblyMemberInfo? ResolveMember(MetadataReader reader, int metadataToken)
    {
        var handle = MetadataTokens.EntityHandle(metadataToken);
        return handle.Kind switch
        {
            HandleKind.MemberReference => ResolveMemberReference(reader, (MemberReferenceHandle)handle),
            HandleKind.MethodDefinition => ResolveMethodDefinition(reader, (MethodDefinitionHandle)handle),
            HandleKind.MethodSpecification => ResolveMethodSpecification(reader, (MethodSpecificationHandle)handle),
            HandleKind.FieldDefinition => ResolveFieldDefinition(reader, (FieldDefinitionHandle)handle),
            _ => null
        };
    }

    private static IEnumerable<string> GetAttributeNames(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (var attributeHandle in attributes)
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            EntityHandle constructor = attribute.Constructor;
            EntityHandle parent = constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
                _ => default
            };
            var name = parent.Kind switch
            {
                HandleKind.TypeReference => GetFullTypeName(reader, reader.GetTypeReference((TypeReferenceHandle)parent)),
                HandleKind.TypeDefinition => GetFullTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)parent)),
                _ => string.Empty
            };
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
                var simpleName = name.Split('.').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(simpleName)) yield return simpleName!;
            }
        }
    }

    private static AssemblyMemberInfo ResolveMemberReference(MetadataReader reader, MemberReferenceHandle handle)
    {
        var member = reader.GetMemberReference(handle);
        var name = reader.GetString(member.Name);
        var containingType = ResolveMemberParent(reader, member.Parent);
        var signature = ReadSignatureInfo(reader, member.Signature, null);
        var symbol = $"{containingType}.{name}({string.Join(',', Enumerable.Repeat("?", signature.ParameterCount))})";
        if (!signature.ReturnsVoid && name != ".ctor")
        {
            symbol += $":{signature.ReturnType}";
        }
        return new AssemblyMemberInfo(symbol, name, containingType, signature.ParameterCount, signature.HasThis, signature.ReturnsVoid, signature.ReturnType);
    }

    private static AssemblyMemberInfo ResolveMethodDefinition(MetadataReader reader, MethodDefinitionHandle handle)
    {
        var method = reader.GetMethodDefinition(handle);
        var info = BuildMethodInfo(reader, handle, method, string.Empty);
        var signature = ReadSignatureInfo(reader, method.Signature, method.Attributes);
        return new AssemblyMemberInfo(info.Symbol, info.Name, info.ContainingType, signature.ParameterCount, signature.HasThis, signature.ReturnsVoid, signature.ReturnType);
    }

    private static AssemblyMemberInfo? ResolveMethodSpecification(MetadataReader reader, MethodSpecificationHandle handle)
    {
        var specification = reader.GetMethodSpecification(handle);
        if (specification.Method.Kind is HandleKind.MemberReference or HandleKind.MethodDefinition)
        {
            return ResolveMember(reader, MetadataTokens.GetToken(specification.Method));
        }
        return null;
    }

    private static AssemblyMemberInfo ResolveFieldDefinition(MetadataReader reader, FieldDefinitionHandle handle)
    {
        var field = reader.GetFieldDefinition(handle);
        var declaringType = reader.GetTypeDefinition(field.GetDeclaringType());
        var containingType = GetFullTypeName(reader, declaringType);
        var name = reader.GetString(field.Name);
        return new AssemblyMemberInfo($"{containingType}.{name}", name, containingType, 0, false, false, string.Empty);
    }

    private static string ResolveMemberParent(MetadataReader reader, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => GetFullTypeName(reader, reader.GetTypeReference((TypeReferenceHandle)parent)),
        HandleKind.TypeDefinition => GetFullTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)parent)),
        HandleKind.TypeSpecification => "<type-spec>",
        HandleKind.MethodDefinition => BuildMethodInfo(reader, (MethodDefinitionHandle)parent, reader.GetMethodDefinition((MethodDefinitionHandle)parent), string.Empty).ContainingType,
        _ => string.Empty
    };

    private static string GetFullTypeName(MetadataReader reader, TypeReference type)
    {
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
    }

    private static string GetFullTypeName(MetadataReader reader, TypeDefinition type)
    {
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
    }

    private static AssemblySignatureInfo ReadSignatureInfo(MetadataReader reader, BlobHandle signatureHandle, MethodAttributes? attributes)
    {
        try
        {
            var blob = reader.GetBlobReader(signatureHandle);
            if (blob.Length == 0)
            {
                return new AssemblySignatureInfo(0, false, true, "void");
            }

            var header = blob.ReadByte();
            if ((header & 0x10) != 0 && blob.RemainingBytes > 0)
            {
                _ = blob.ReadCompressedInteger();
            }

            var parameterCount = blob.RemainingBytes > 0 ? blob.ReadCompressedInteger() : 0;
            var returnType = blob.RemainingBytes > 0 ? ReadPrimitiveSignatureType(ref blob) : "void";
            var returnsVoid = string.Equals(returnType, "void", StringComparison.OrdinalIgnoreCase);
            var hasThis = attributes.HasValue ? (attributes.Value & MethodAttributes.Static) == 0 : (header & 0x20) != 0;
            return new AssemblySignatureInfo(parameterCount, hasThis, returnsVoid, returnType);
        }
        catch
        {
            return new AssemblySignatureInfo(0, false, false, string.Empty);
        }
    }

    private static string ReadPrimitiveSignatureType(ref BlobReader blob)
    {
        if (blob.RemainingBytes <= 0)
        {
            return string.Empty;
        }

        var rawElement = blob.ReadByte();
        if (rawElement is 0x11 or 0x12) // valuetype/class followed by a TypeDefOrRef coded token
        {
            if (blob.RemainingBytes > 0)
            {
                _ = blob.ReadCompressedInteger();
            }
            return "object";
        }

        if (rawElement == 0x1d) // SZARRAY
        {
            return ReadPrimitiveSignatureType(ref blob) + "[]";
        }

        if (rawElement == 0x10 && blob.RemainingBytes > 0) // BYREF
        {
            return ReadPrimitiveSignatureType(ref blob) + "&";
        }

        var element = (SignatureTypeCode)rawElement;
        return element switch
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
            _ => element.ToString()
        };
    }

    private static IEnumerable<AssemblyInstruction> DecodeInstructions(BlobReader ilReader)
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

            yield return new AssemblyInstruction(offset, ilReader.Offset, opCode, operand);
        }
    }

    private static int[] ReadSwitchOperand(ref BlobReader reader)
    {
        var count = reader.ReadInt32();
        var targets = new int[count];
        for (var i = 0; i < count; i++)
        {
            targets[i] = reader.ReadInt32();
        }
        return targets;
    }

    private static void EnqueueSuccessors(int instructionIndex, AssemblyInstruction instruction, IReadOnlyList<AssemblyInstruction> instructions, IReadOnlyDictionary<int, int> instructionIndexByOffset, AssemblyMethodState state, Queue<(int Index, AssemblyMethodState State)> worklist)
    {
        foreach (var successor in GetSuccessorIndexes(instructionIndex, instruction, instructions, instructionIndexByOffset))
        {
            worklist.Enqueue((successor, state.Clone()));
        }
    }

    private static void EnqueueSummarySuccessors(int instructionIndex, AssemblyInstruction instruction, IReadOnlyList<AssemblyInstruction> instructions, IReadOnlyDictionary<int, int> instructionIndexByOffset, AssemblySummaryState state, Queue<(int Index, AssemblySummaryState State)> worklist)
    {
        foreach (var successor in GetSuccessorIndexes(instructionIndex, instruction, instructions, instructionIndexByOffset))
        {
            worklist.Enqueue((successor, state.Clone()));
        }
    }

    private static IEnumerable<int> GetSuccessorIndexes(int instructionIndex, AssemblyInstruction instruction, IReadOnlyList<AssemblyInstruction> instructions, IReadOnlyDictionary<int, int> instructionIndexByOffset)
    {
        if (instruction.OpCode.FlowControl == FlowControl.Return || instruction.OpCode.FlowControl == FlowControl.Throw)
        {
            yield break;
        }

        if (instruction.OpCode.FlowControl == FlowControl.Branch)
        {
            if (TryGetBranchTargetIndex(instruction, instructionIndexByOffset, out var branchIndex)) yield return branchIndex;
            yield break;
        }

        if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
        {
            if (instruction.OpCode == OpCodes.Switch && instruction.Operand is int[] switchTargets)
            {
                foreach (var target in switchTargets)
                {
                    if (instructionIndexByOffset.TryGetValue(instruction.NextOffset + target, out var switchIndex)) yield return switchIndex;
                }
            }
            else if (TryGetBranchTargetIndex(instruction, instructionIndexByOffset, out var branchIndex))
            {
                yield return branchIndex;
            }
        }

        if (instructionIndex + 1 < instructions.Count)
        {
            yield return instructionIndex + 1;
        }
    }

    private static bool TryGetBranchTargetIndex(AssemblyInstruction instruction, IReadOnlyDictionary<int, int> instructionIndexByOffset, out int instructionIndex)
    {
        instructionIndex = -1;
        var relativeTarget = instruction.Operand switch
        {
            sbyte shortTarget => shortTarget,
            int target => target,
            _ => int.MinValue
        };
        return relativeTarget != int.MinValue && instructionIndexByOffset.TryGetValue(instruction.NextOffset + relativeTarget, out instructionIndex);
    }

    private static AssemblySourceMap LoadPortablePdbSourceMap(string assemblyPath, List<string> diagnostics)
    {
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(pdbPath))
        {
            return AssemblySourceMap.Empty;
        }

        try
        {
            using var pdbStream = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            var reader = provider.GetMetadataReader();
            var locations = new Dictionary<int, List<AssemblySequencePoint>>();
            var locals = new Dictionary<int, List<AssemblyLocalScope>>();
            foreach (var methodDebugHandle in reader.MethodDebugInformation)
            {
                var rowNumber = MetadataTokens.GetRowNumber(methodDebugHandle);
                var methodDebugInfo = reader.GetMethodDebugInformation(methodDebugHandle);
                var points = new List<AssemblySequencePoint>();
                foreach (var sequencePoint in methodDebugInfo.GetSequencePoints())
                {
                    if (sequencePoint.IsHidden || sequencePoint.Document.IsNil) continue;
                    var document = reader.GetDocument(sequencePoint.Document);
                    var documentName = reader.GetString(document.Name);
                    points.Add(new AssemblySequencePoint(sequencePoint.Offset, documentName, sequencePoint.StartLine, sequencePoint.StartColumn));
                }
                if (points.Count > 0)
                {
                    locations[MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(rowNumber))] = points.OrderBy(point => point.Offset).ToList();
                }
            }

            foreach (var localScopeHandle in reader.LocalScopes)
            {
                var localScope = reader.GetLocalScope(localScopeHandle);
                var methodToken = MetadataTokens.GetToken(localScope.Method);
                if (!locals.TryGetValue(methodToken, out var localScopes))
                {
                    localScopes = [];
                    locals[methodToken] = localScopes;
                }

                foreach (var variableHandle in localScope.GetLocalVariables())
                {
                    var variable = reader.GetLocalVariable(variableHandle);
                    if (variable.Attributes.HasFlag(LocalVariableAttributes.DebuggerHidden)) continue;
                    var name = reader.GetString(variable.Name);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    localScopes.Add(new AssemblyLocalScope(variable.Index, name, localScope.StartOffset, localScope.Length));
                }
            }

            return new AssemblySourceMap(locations, locals);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            diagnostics.Add($"Could not read portable PDB {pdbPath}: {ex.Message}");
            return AssemblySourceMap.Empty;
        }
    }

    private static void ApplyDefaultStackBehaviour(OpCode opCode, AssemblyMethodState state)
    {
        var popCount = GetPopCount(opCode.StackBehaviourPop);
        for (var i = 0; i < popCount; i++)
        {
            _ = state.Pop();
        }

        var pushCount = GetPushCount(opCode.StackBehaviourPush);
        for (var i = 0; i < pushCount; i++)
        {
            state.Push(null);
        }
    }

    private static void ApplyDefaultSummaryStackBehaviour(OpCode opCode, AssemblySummaryState state)
    {
        var popCount = GetPopCount(opCode.StackBehaviourPop);
        for (var i = 0; i < popCount; i++)
        {
            _ = state.Pop();
        }

        var pushCount = GetPushCount(opCode.StackBehaviourPush);
        for (var i = 0; i < pushCount; i++)
        {
            state.Push(null);
        }
    }

    private static void ApplyDefaultListStackBehaviour(OpCode opCode, List<AssemblyTaint?> stack)
    {
        var popCount = GetPopCount(opCode.StackBehaviourPop);
        for (var i = 0; i < popCount && stack.Count > 0; i++) stack.RemoveAt(stack.Count - 1);
        var pushCount = GetPushCount(opCode.StackBehaviourPush);
        for (var i = 0; i < pushCount; i++) stack.Add(null);
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

    private static bool TryGetLdargIndex(OpCode opCode, object? operand, out int index)
    {
        index = opCode == OpCodes.Ldarg_0 ? 0 : opCode == OpCodes.Ldarg_1 ? 1 : opCode == OpCodes.Ldarg_2 ? 2 : opCode == OpCodes.Ldarg_3 ? 3 : operand is int value && opCode == OpCodes.Ldarg ? value : operand is byte shortValue && opCode == OpCodes.Ldarg_S ? shortValue : -1;
        return index >= 0;
    }

    private static bool TryGetStargIndex(OpCode opCode, object? operand, out int index)
    {
        index = operand is int value && opCode == OpCodes.Starg ? value : operand is byte shortValue && opCode == OpCodes.Starg_S ? shortValue : -1;
        return index >= 0;
    }

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

    private static AssemblyTaint? PopOrNull(Stack<AssemblyTaint?> stack) => stack.Count == 0 ? null : stack.Pop();

    private static AssemblyTaint? CombineAssemblyTaints(IEnumerable<AssemblyTaint> traces)
    {
        var nodeIds = new List<string>();
        var taintKinds = new List<string>();
        var fieldPaths = new List<string>();
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var seenTaints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var trace in traces)
        {
            foreach (var nodeId in trace.NodeIds)
            {
                if (seenNodes.Add(nodeId)) nodeIds.Add(nodeId);
            }
            foreach (var taintKind in trace.TaintKinds)
            {
                if (seenTaints.Add(taintKind)) taintKinds.Add(taintKind);
            }
            foreach (var fieldPath in trace.FieldPaths)
            {
                if (seenFields.Add(fieldPath)) fieldPaths.Add(fieldPath);
            }
        }

        return nodeIds.Count == 0 ? null : new AssemblyTaint(nodeIds, taintKinds, fieldPaths);
    }

    private static AssemblySummaryTaint? CombineSummaryTaints(IEnumerable<AssemblySummaryTaint> traces)
    {
        var indexes = new List<int>();
        foreach (var index in traces.SelectMany(trace => trace.ParameterIndexes).Distinct().Order())
        {
            indexes.Add(index);
        }
        return indexes.Count == 0 ? null : new AssemblySummaryTaint(indexes);
    }

    private static List<string> ToTaintKinds(IEnumerable<DataFlowPattern> patterns) => patterns
        .SelectMany(pattern => pattern.TaintKinds.Count > 0 ? pattern.TaintKinds : [pattern.Category ?? "user-input"])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static List<string> GetAssemblyFiles(string path, bool includeBuildArtifacts, List<string> diagnostics)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(Constants.AssemblyExtension, StringComparison.OrdinalIgnoreCase) || extension.Equals(Constants.ExeExtension, StringComparison.OrdinalIgnoreCase) ? [path] : [];
        }

        var applicationAssemblyNames = GetApplicationAssemblyNames(path, diagnostics);
        return new DirectoryInfo(path)
            .EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(file => file.Extension.Equals(Constants.AssemblyExtension, StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(Constants.ExeExtension, StringComparison.OrdinalIgnoreCase))
            .Where(file => includeBuildArtifacts || !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => includeBuildArtifacts || !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => applicationAssemblyNames.Count == 0 ? IsLikelyApplicationAssembly(file.Name) : applicationAssemblyNames.Contains(Path.GetFileNameWithoutExtension(file.Name)))
            .ToDictionary(file => Path.GetFullPath(file.FullName), file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Values
            .ToList();
    }

    private static HashSet<string> GetApplicationAssemblyNames(string directory, List<string> diagnostics)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var depsFile in Directory.EnumerateFiles(directory, "*.deps.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(depsFile));
                if (!document.RootElement.TryGetProperty("libraries", out var libraries)) continue;
                foreach (var library in libraries.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("type", out var typeElement) || !typeElement.GetString()!.Equals("project", StringComparison.OrdinalIgnoreCase)) continue;
                    var slashIndex = library.Name.IndexOf('/');
                    names.Add(slashIndex > 0 ? library.Name[..slashIndex] : library.Name);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"Could not read assembly dependency scope from {depsFile}: {ex.Message}");
            }
        }
        return names;
    }

    private static bool IsLikelyApplicationAssembly(string fileName) =>
        !fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) &&
        !fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) &&
        !fileName.StartsWith("Newtonsoft.", StringComparison.OrdinalIgnoreCase) &&
        !fileName.StartsWith("FSharp.", StringComparison.OrdinalIgnoreCase) &&
        !fileName.StartsWith("Humanizer", StringComparison.OrdinalIgnoreCase);

    private static bool IsManagedAssemblyFile(string filePath)
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

    private static string GetNamespace(string typeName)
    {
        var index = typeName.LastIndexOf('.');
        return index <= 0 ? string.Empty : typeName[..index];
    }

    private static bool AssemblyPatternMatches(string value, DataFlowPattern pattern)
    {
        return pattern.Match switch
        {
            DataFlowMatchKind.Exact => value.Equals(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
            DataFlowMatchKind.Prefix => value.StartsWith(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
            DataFlowMatchKind.Suffix => value.EndsWith(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
            DataFlowMatchKind.Regex => Regex.IsMatch(value, pattern.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            _ => value.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase)
        };
    }

    private sealed class AssemblyDataFlowContext(DataFlowResult result, DataFlowPatternSet patterns, string basePath)
    {
        private int _nodeCounter = result.Nodes.Count;
        private int _edgeCounter = result.Edges.Count;
        private int _sliceCounter = result.Slices.Count;
        private readonly Dictionary<string, DataFlowNode> _nodesById = result.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        private readonly Dictionary<string, DataFlowNode> _nodesByKey = new(StringComparer.Ordinal);
        private readonly HashSet<string> _edgeKeys = result.Edges.Select(edge => $"{edge.SourceId}\u001f{edge.TargetId}\u001f{edge.Kind}\u001f{edge.Label}").ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _sliceKeys = result.Slices.Select(slice => $"{slice.SourceId}\u001f{slice.SinkId}\u001f{slice.SinkArgumentIndex}\u001f{slice.SinkArgument}").ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DataFlowEdge>> _outgoingEdgesBySource = result.Edges.GroupBy(edge => edge.SourceId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        private readonly DataFlowPatternIndex _patternIndex = new(patterns);
        private readonly PackageUrlResolver _purlResolver = PackageUrlResolver.Create(basePath);
        private readonly Dictionary<string, AssemblyTaint> _fieldTaints = new(StringComparer.Ordinal);

        public IEnumerable<DataFlowPattern> MatchParameterSource(string parameterName, AssemblyMethodInfo method)
        {
            var parameterText = parameterName;
            foreach (var pattern in _patternIndex.SourceParameters.Where(pattern => AssemblyPatternMatches(parameterName, pattern) || AssemblyPatternMatches(parameterText, pattern)))
            {
                yield return pattern;
            }

            if (method.Name == "Main" && parameterName.Equals("args", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pattern in _patternIndex.SourceParameters.Where(pattern => AssemblyPatternMatches("Main", pattern)))
                {
                    yield return pattern;
                }
            }
        }

        public IEnumerable<DataFlowPattern> MatchSource(AssemblyMemberInfo member) => MatchMember(member, _patternIndex.Sources.Where(IsAssemblySourceMemberPattern));
        public IEnumerable<DataFlowPattern> MatchSink(AssemblyMemberInfo member) => MatchMember(member, _patternIndex.Sinks);
        public IEnumerable<DataFlowPattern> MatchSanitizer(AssemblyMemberInfo member) => MatchMember(member, _patternIndex.Sanitizers);
        public IEnumerable<DataFlowPattern> MatchPassthrough(AssemblyMemberInfo member) => MatchMember(member, _patternIndex.Passthroughs);
        public IEnumerable<DataFlowPattern> MatchSourceCode(string code) => _patternIndex.SourceCode.Where(pattern => AssemblyPatternMatches(code, pattern));
        public IEnumerable<DataFlowPattern> MatchAttributeSource(IEnumerable<string> attributes) => _patternIndex.SourceAttributes.Where(pattern => attributes.Any(attribute => AssemblyPatternMatches(attribute, pattern)));

        public void RecordFieldTaint(string fieldSymbol, AssemblyTaint taint)
        {
            if (_fieldTaints.TryGetValue(fieldSymbol, out var existing) && CombineAssemblyTaints([existing, taint]) is { } combined)
            {
                _fieldTaints[fieldSymbol] = combined;
                return;
            }
            _fieldTaints[fieldSymbol] = taint;
        }

        public bool TryGetFieldTaint(string fieldSymbol, out AssemblyTaint taint) => _fieldTaints.TryGetValue(fieldSymbol, out taint!);

        private static bool IsAssemblySourceMemberPattern(DataFlowPattern pattern) => pattern.Kind switch
        {
            DataFlowPatternKind.Code or DataFlowPatternKind.Parameter or DataFlowPatternKind.Attribute => false,
            DataFlowPatternKind.Name when pattern.Match == DataFlowMatchKind.Contains && pattern.Pattern.Length < 4 => false,
            _ => true
        };

        private static IEnumerable<DataFlowPattern> MatchMember(AssemblyMemberInfo member, IEnumerable<DataFlowPattern> candidatePatterns)
        {
            foreach (var pattern in candidatePatterns)
            {
                string? value = pattern.Kind switch
                {
                    DataFlowPatternKind.Method or DataFlowPatternKind.Symbol => member.Symbol,
                    DataFlowPatternKind.Type => member.ContainingType,
                    DataFlowPatternKind.Namespace => GetNamespace(member.ContainingType),
                    DataFlowPatternKind.Name => member.Name,
                    DataFlowPatternKind.Code => null,
                    _ => member.Symbol
                };

                if (value is not null && AssemblyPatternMatches(value, pattern))
                {
                    yield return pattern;
                }
            }
        }

        public DataFlowNode AddNode(string kind, string name, AssemblyMethodInfo method, string assemblyPath, bool isSource, bool isSink, IReadOnlyCollection<DataFlowPattern> matchedPatterns, string? category, string? symbol, string? typeName, string? code, int ilOffset)
        {
            var location = method.ResolveLocation(ilOffset, assemblyPath);
            var nodeKey = $"{kind}\u001f{name}\u001f{symbol}\u001f{method.MetadataToken}\u001f{ilOffset}\u001f{isSource}\u001f{isSink}\u001f{category}\u001f{string.Join(',', matchedPatterns.Select(pattern => pattern.Pattern).Order(StringComparer.Ordinal))}";
            if (_nodesByKey.TryGetValue(nodeKey, out var existingNode))
            {
                return existingNode;
            }

            var path = Directory.Exists(basePath) ? Path.GetRelativePath(basePath, location.FilePath) : Path.GetFileName(location.FilePath);
            var purl = matchedPatterns.Select(pattern => pattern.Purl).FirstOrDefault(purl => !string.IsNullOrWhiteSpace(purl)) ??
                       _purlResolver.Resolve(method.AssemblyName, Path.GetFileName(assemblyPath), symbol, method.Namespace, typeName);
            var node = new DataFlowNode
            {
                Id = $"dfn{++_nodeCounter}",
                Kind = kind,
                Name = name,
                Symbol = symbol,
                Type = typeName,
                Purl = purl,
                Code = TrimAssemblyCode(code ?? symbol ?? name),
                Path = path,
                FileName = Path.GetFileName(location.FilePath),
                Namespace = method.Namespace,
                ClassName = method.ContainingType.Split('.').LastOrDefault() ?? method.ContainingType,
                MethodName = method.Name,
                LineNumber = location.LineNumber,
                ColumnNumber = location.ColumnNumber,
                IsSource = isSource,
                IsSink = isSink,
                MatchedPatterns = matchedPatterns.Select(pattern => pattern.Pattern).Distinct(StringComparer.Ordinal).ToList(),
                Category = category,
                Properties =
                {
                    ["analysis"] = "assembly-il",
                    ["method"] = method.Symbol,
                    ["assembly"] = Path.GetFileName(assemblyPath),
                    ["ilOffset"] = ilOffset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["metadataToken"] = $"0x{method.MetadataToken:x8}"
                }
            };
            result.Nodes.Add(node);
            _nodesById[node.Id] = node;
            _nodesByKey[nodeKey] = node;
            return node;
        }

        public void AddEdges(IEnumerable<string> sourceIds, string targetId, string kind, AssemblyMethodInfo method, string assemblyPath, int ilOffset, string? label)
        {
            var location = method.ResolveLocation(ilOffset, assemblyPath);
            foreach (var sourceId in sourceIds.Distinct(StringComparer.Ordinal))
            {
                var key = $"{sourceId}\u001f{targetId}\u001f{kind}\u001f{label}";
                if (!_edgeKeys.Add(key))
                {
                    continue;
                }

                var edge = new DataFlowEdge
                {
                    Id = $"dfe{++_edgeCounter}",
                    SourceId = sourceId,
                    TargetId = targetId,
                    Kind = kind,
                    Label = label,
                    SourcePurl = _nodesById.TryGetValue(sourceId, out var sourceNode) ? sourceNode.Purl : null,
                    TargetPurl = _nodesById.TryGetValue(targetId, out var targetNode) ? targetNode.Purl : null,
                    FileName = Path.GetFileName(location.FilePath),
                    LineNumber = location.LineNumber,
                    ColumnNumber = location.ColumnNumber
                };
                result.Edges.Add(edge);
                if (!_outgoingEdgesBySource.TryGetValue(edge.SourceId, out var outgoing))
                {
                    outgoing = [];
                    _outgoingEdgesBySource[edge.SourceId] = outgoing;
                }
                outgoing.Add(edge);
            }
        }

        public void AddSlice(AssemblyTaint trace, DataFlowNode sinkNode, DataFlowPattern? sinkPattern, string? sinkArgument, int sinkArgumentIndex)
        {
            var nodeIds = trace.NodeIds.Concat([sinkNode.Id]).Distinct(StringComparer.Ordinal).ToList();
            var nodeIdSet = nodeIds.ToHashSet(StringComparer.Ordinal);
            var edgeIds = nodeIds
                .Where(nodeId => _outgoingEdgesBySource.ContainsKey(nodeId))
                .SelectMany(nodeId => _outgoingEdgesBySource[nodeId])
                .Where(edge => nodeIdSet.Contains(edge.TargetId))
                .Select(edge => edge.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var firstSource = trace.NodeIds.FirstOrDefault(id => _nodesById.TryGetValue(id, out var candidate) && candidate.IsSource) ?? trace.NodeIds.First();
            var sliceKey = $"{firstSource}\u001f{sinkNode.Id}\u001f{sinkArgumentIndex}\u001f{sinkArgument}";
            if (!_sliceKeys.Add(sliceKey))
            {
                return;
            }

            _nodesById.TryGetValue(firstSource, out var sourceNode);
            var sliceNodes = nodeIds.Select(nodeId => _nodesById.TryGetValue(nodeId, out var node) ? node : null).Where(node => node is not null).ToList();
            var patternPurls = new[] { sinkPattern?.Purl, sourceNode?.Purl, sinkNode.Purl }.Where(purl => !string.IsNullOrWhiteSpace(purl));
            result.Slices.Add(new DataFlowSlice
            {
                Id = $"dfs{++_sliceCounter}",
                SourceId = firstSource,
                SinkId = sinkNode.Id,
                NodeIds = nodeIds,
                EdgeIds = edgeIds,
                SourceCategory = sourceNode?.Category,
                SinkCategory = sinkPattern?.Category ?? sinkNode.Category,
                SourcePurl = sourceNode?.Purl,
                SinkPurl = sinkNode.Purl,
                Purls = sliceNodes.Select(node => node!.Purl).Concat(patternPurls).Where(purl => !string.IsNullOrWhiteSpace(purl)).Distinct(StringComparer.Ordinal).ToList()!,
                SinkArgument = sinkArgument,
                SinkArgumentIndex = sinkArgumentIndex,
                TaintKinds = trace.TaintKinds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FieldPaths = trace.FieldPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Confidence = sinkPattern?.Confidence ?? "Medium",
                Summary = $"Assembly IL data flows from {firstSource} to {sinkNode.Name} argument {sinkArgumentIndex}."
            });
        }

        private static string TrimAssemblyCode(string code)
        {
            code = code.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
            return code.Length <= 240 ? code : code[..240] + "…";
        }
    }

    private sealed record AssemblyInstruction(int Offset, int NextOffset, OpCode OpCode, object? Operand);
    private sealed record AssemblySignatureInfo(int ParameterCount, bool HasThis, bool ReturnsVoid, string ReturnType);
    private sealed record AssemblyMemberInfo(string Symbol, string Name, string ContainingType, int ParameterCount, bool HasThis, bool ReturnsVoid, string ReturnType);
    private sealed record AssemblySourceLocation(string FilePath, int LineNumber, int ColumnNumber);
    private sealed record AssemblySequencePoint(int Offset, string FilePath, int LineNumber, int ColumnNumber);
    private sealed record AssemblyLocalScope(int Index, string Name, int StartOffset, int Length)
    {
        public bool Contains(int offset) => offset >= StartOffset && offset <= StartOffset + Length;
    }
    private sealed record AssemblyMethodInfo(string Symbol, string Name, string ContainingType, string Namespace, string AssemblyName, string ReturnType, string AssemblyPath, int MetadataToken, IReadOnlyList<AssemblySequencePoint> SourceLocations)
    {
        public AssemblySourceLocation ResolveLocation(int ilOffset, string assemblyPath)
        {
            var point = SourceLocations.Where(candidate => candidate.Offset <= ilOffset).OrderByDescending(candidate => candidate.Offset).FirstOrDefault();
            return point is null
                ? new AssemblySourceLocation(assemblyPath, Math.Max(1, ilOffset), 1)
                : new AssemblySourceLocation(point.FilePath, Math.Max(1, point.LineNumber), Math.Max(1, point.ColumnNumber));
        }
    }

    private sealed class AssemblySourceMap(Dictionary<int, List<AssemblySequencePoint>> locationsByMethodToken, Dictionary<int, List<AssemblyLocalScope>> localsByMethodToken)
    {
        public static AssemblySourceMap Empty { get; } = new([], []);
        public IReadOnlyList<AssemblySequencePoint> GetLocations(int methodToken) => locationsByMethodToken.TryGetValue(methodToken, out var locations) ? locations : [];
        public string? GetLocalName(int methodToken, int localIndex, int ilOffset) => localsByMethodToken.TryGetValue(methodToken, out var locals)
            ? locals.Where(local => local.Index == localIndex && local.Contains(ilOffset)).OrderBy(local => local.Length).Select(local => local.Name).FirstOrDefault() ?? locals.FirstOrDefault(local => local.Index == localIndex)?.Name
            : null;
    }

    private sealed class AssemblyMethodSummary(string method)
    {
        public string Method { get; } = method;
        public List<int> ReturnParameterIndexes { get; } = [];
        public List<int> SinkParameterIndexes { get; } = [];
        public List<string> SinkCategories { get; } = [];

        public void AddReturnParameter(int index) => AddUnique(ReturnParameterIndexes, index);
        public void AddSinkParameter(int index) => AddUnique(SinkParameterIndexes, index);
        public void AddSinkCategory(string category)
        {
            if (!SinkCategories.Contains(category, StringComparer.OrdinalIgnoreCase)) SinkCategories.Add(category);
        }

        public bool Merge(AssemblyMethodSummary other)
        {
            var changed = false;
            foreach (var index in other.ReturnParameterIndexes) changed |= AddUnique(ReturnParameterIndexes, index);
            foreach (var index in other.SinkParameterIndexes) changed |= AddUnique(SinkParameterIndexes, index);
            foreach (var category in other.SinkCategories)
            {
                if (!SinkCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
                {
                    SinkCategories.Add(category);
                    changed = true;
                }
            }
            return changed;
        }

        private static bool AddUnique(List<int> values, int value)
        {
            if (values.Contains(value)) return false;
            values.Add(value);
            values.Sort();
            return true;
        }
    }

    private sealed class AssemblyMethodState(List<AssemblyTaint?> stack, Dictionary<int, AssemblyTaint?> locals, Dictionary<int, AssemblyTaint?> arguments)
    {
        public List<AssemblyTaint?> Stack { get; } = stack;
        public Dictionary<int, AssemblyTaint?> Locals { get; } = locals;
        public Dictionary<int, AssemblyTaint?> Arguments { get; } = arguments;
        public Dictionary<string, AssemblyTaint?> Fields { get; } = new(StringComparer.Ordinal);
        public void Push(AssemblyTaint? taint) => Stack.Add(taint);
        public AssemblyTaint? Pop()
        {
            if (Stack.Count == 0) return null;
            var value = Stack[^1];
            Stack.RemoveAt(Stack.Count - 1);
            return value;
        }
        public AssemblyMethodState Clone()
        {
            var clone = new AssemblyMethodState([.. Stack], new Dictionary<int, AssemblyTaint?>(Locals), new Dictionary<int, AssemblyTaint?>(Arguments));
            foreach (var (key, value) in Fields) clone.Fields[key] = value;
            return clone;
        }
        public string Signature() => string.Join('|', Stack.Select(TaintSignature)) + ";L=" + string.Join(',', Locals.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}:{TaintSignature(kvp.Value)}")) + ";A=" + string.Join(',', Arguments.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}:{TaintSignature(kvp.Value)}"));
    }

    private sealed class AssemblySummaryState(List<AssemblySummaryTaint?> stack, Dictionary<int, AssemblySummaryTaint?> locals, Dictionary<int, AssemblySummaryTaint?> arguments)
    {
        public List<AssemblySummaryTaint?> Stack { get; } = stack;
        public Dictionary<int, AssemblySummaryTaint?> Locals { get; } = locals;
        public Dictionary<int, AssemblySummaryTaint?> Arguments { get; } = arguments;
        public void Push(AssemblySummaryTaint? taint) => Stack.Add(taint);
        public AssemblySummaryTaint? Pop()
        {
            if (Stack.Count == 0) return null;
            var value = Stack[^1];
            Stack.RemoveAt(Stack.Count - 1);
            return value;
        }
        public AssemblySummaryState Clone() => new([.. Stack], new Dictionary<int, AssemblySummaryTaint?>(Locals), new Dictionary<int, AssemblySummaryTaint?>(Arguments));
        public string Signature() => string.Join('|', Stack.Select(SummaryTaintSignature)) + ";L=" + string.Join(',', Locals.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}:{SummaryTaintSignature(kvp.Value)}")) + ";A=" + string.Join(',', Arguments.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}:{SummaryTaintSignature(kvp.Value)}"));
    }

    private sealed record AssemblySummaryTaint(List<int> ParameterIndexes);
    private sealed record AssemblyTaint(List<string> NodeIds, List<string> TaintKinds, List<string> FieldPaths)
    {
        public AssemblyTaint Append(string nodeId)
        {
            if (NodeIds.Contains(nodeId, StringComparer.Ordinal))
            {
                return this;
            }
            return new AssemblyTaint(NodeIds.Concat([nodeId]).ToList(), TaintKinds, FieldPaths);
        }
    }

    private static string TaintSignature(AssemblyTaint? taint) => taint is null ? "_" : string.Join('+', taint.NodeIds.Order(StringComparer.Ordinal));
    private static string SummaryTaintSignature(AssemblySummaryTaint? taint) => taint is null ? "_" : string.Join('+', taint.ParameterIndexes.Order());
}
