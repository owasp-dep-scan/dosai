using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

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
        var assemblyPaths = GetAssemblyFiles(path, includeBuildArtifacts);
        if (assemblyPaths.Count == 0)
        {
            return 0;
        }

        var context = new AssemblyDataFlowContext(result, patterns, path);
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
                foreach (var methodHandle in reader.MethodDefinitions)
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    try
                    {
                        AnalyzeAssemblyMethod(peReader, reader, methodHandle, method, assemblyPath, context);
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

    private static void AnalyzeAssemblyMethod(PEReader peReader, MetadataReader reader, MethodDefinitionHandle methodHandle, MethodDefinition method, string assemblyPath, AssemblyDataFlowContext context)
    {
        var methodInfo = BuildMethodInfo(reader, methodHandle, method, assemblyPath);
        var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
        var il = body.GetILReader();
        var instructions = DecodeInstructions(il).ToList();
        var stack = new Stack<AssemblyTaint?>();
        var locals = new Dictionary<int, AssemblyTaint?>();
        var fields = new Dictionary<string, AssemblyTaint?>(StringComparer.Ordinal);
        var isStatic = (method.Attributes & MethodAttributes.Static) != 0;
        var argumentTaints = SeedAssemblyParameters(reader, method, methodInfo, isStatic, context);

        foreach (var instruction in instructions)
        {
            var opCode = instruction.OpCode;
            if (opCode == OpCodes.Nop)
            {
                continue;
            }

            if (TryGetLdargIndex(opCode, instruction.Operand, out var ldargIndex))
            {
                stack.Push(argumentTaints.TryGetValue(ldargIndex, out var taint) ? taint : null);
                continue;
            }

            if (TryGetStargIndex(opCode, instruction.Operand, out var stargIndex))
            {
                argumentTaints[stargIndex] = PopOrNull(stack);
                continue;
            }

            if (TryGetLdlocIndex(opCode, instruction.Operand, out var ldlocIndex))
            {
                stack.Push(locals.TryGetValue(ldlocIndex, out var taint) ? taint : null);
                continue;
            }

            if (TryGetStlocIndex(opCode, instruction.Operand, out var stlocIndex))
            {
                locals[stlocIndex] = PopOrNull(stack);
                continue;
            }

            if (opCode == OpCodes.Ldstr && instruction.Operand is int userStringToken)
            {
                var value = reader.GetUserString(MetadataTokens.UserStringHandle(userStringToken));
                var matched = context.MatchSourceCode(value).ToList();
                if (matched.Count > 0)
                {
                    var node = context.AddNode("Source", "string", methodInfo, assemblyPath, isSource: true, isSink: false, matched, matched.FirstOrDefault()?.Category, "System.String", "System.String", value, instruction.Offset + 1);
                    stack.Push(new AssemblyTaint([node.Id], ToTaintKinds(matched), []));
                }
                else
                {
                    stack.Push(null);
                }
                continue;
            }

            if (opCode == OpCodes.Ldfld || opCode == OpCodes.Ldsfld)
            {
                var instanceTaint = opCode == OpCodes.Ldfld ? PopOrNull(stack) : null;
                var member = instruction.Operand is int fieldToken ? ResolveMember(reader, fieldToken) : null;
                var fieldTaint = member is not null && fields.TryGetValue(member.Symbol, out var taint) ? taint : instanceTaint;
                stack.Push(fieldTaint);
                continue;
            }

            if (opCode == OpCodes.Stfld || opCode == OpCodes.Stsfld)
            {
                var valueTaint = PopOrNull(stack);
                if (opCode == OpCodes.Stfld)
                {
                    _ = PopOrNull(stack);
                }
                if (instruction.Operand is int fieldToken && ResolveMember(reader, fieldToken) is { } member)
                {
                    fields[member.Symbol] = valueTaint;
                }
                continue;
            }

            if (opCode == OpCodes.Ldelem || opCode == OpCodes.Ldelem_I || opCode == OpCodes.Ldelem_I1 || opCode == OpCodes.Ldelem_I2 || opCode == OpCodes.Ldelem_I4 || opCode == OpCodes.Ldelem_I8 || opCode == OpCodes.Ldelem_R4 || opCode == OpCodes.Ldelem_R8 || opCode == OpCodes.Ldelem_Ref || opCode == OpCodes.Ldelem_U1 || opCode == OpCodes.Ldelem_U2 || opCode == OpCodes.Ldelem_U4)
            {
                _ = PopOrNull(stack); // index
                stack.Push(PopOrNull(stack)); // array/reference
                continue;
            }

            if (opCode == OpCodes.Stelem || opCode == OpCodes.Stelem_I || opCode == OpCodes.Stelem_I1 || opCode == OpCodes.Stelem_I2 || opCode == OpCodes.Stelem_I4 || opCode == OpCodes.Stelem_I8 || opCode == OpCodes.Stelem_R4 || opCode == OpCodes.Stelem_R8 || opCode == OpCodes.Stelem_Ref)
            {
                var valueTaint = PopOrNull(stack);
                _ = PopOrNull(stack); // index
                _ = PopOrNull(stack); // array
                if (valueTaint is not null)
                {
                    stack.Push(valueTaint);
                    _ = PopOrNull(stack);
                }
                continue;
            }

            if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj)
            {
                ProcessAssemblyCall(reader, instruction, opCode, methodInfo, assemblyPath, context, stack);
                continue;
            }

            if (opCode == OpCodes.Dup)
            {
                stack.Push(stack.Count > 0 ? stack.Peek() : null);
                continue;
            }

            if (opCode == OpCodes.Pop)
            {
                _ = PopOrNull(stack);
                continue;
            }

            if (opCode == OpCodes.Ret)
            {
                if (stack.Count > 0 && PopOrNull(stack) is { } returnTaint)
                {
                    var node = context.AddNode("Return", "return", methodInfo, assemblyPath, isSource: false, isSink: false, [], null, methodInfo.Symbol, methodInfo.ReturnType, "ret", instruction.Offset + 1);
                    context.AddEdges(returnTaint.NodeIds, node.Id, "AssemblyReturn", assemblyPath, instruction.Offset + 1, "returned value");
                }
                continue;
            }

            ApplyDefaultStackBehaviour(opCode, stack);
        }
    }

    private static void ProcessAssemblyCall(MetadataReader reader, AssemblyInstruction instruction, OpCode opCode, AssemblyMethodInfo currentMethod, string assemblyPath, AssemblyDataFlowContext context, Stack<AssemblyTaint?> stack)
    {
        if (instruction.Operand is not int token || ResolveMember(reader, token) is not { } member)
        {
            ApplyDefaultStackBehaviour(opCode, stack);
            return;
        }

        var argumentTaints = new List<AssemblyTaint?>();
        for (var i = 0; i < member.ParameterCount; i++)
        {
            argumentTaints.Add(PopOrNull(stack));
        }
        argumentTaints.Reverse();
        var receiverTaint = member.HasThis && opCode != OpCodes.Newobj ? PopOrNull(stack) : null;
        var allTaints = argumentTaints.Concat([receiverTaint]).Where(taint => taint is not null).Cast<AssemblyTaint>().ToList();

        var sourcePatterns = context.MatchSource(member).ToList();
        if (sourcePatterns.Count > 0)
        {
            var node = context.AddNode("Source", member.Name, currentMethod, assemblyPath, isSource: true, isSink: false, sourcePatterns, sourcePatterns.FirstOrDefault()?.Category, member.Symbol, member.ContainingType, member.Symbol, instruction.Offset + 1);
            var sourceTaint = new AssemblyTaint([node.Id], ToTaintKinds(sourcePatterns), []);
            if (!member.ReturnsVoid || opCode == OpCodes.Newobj)
            {
                stack.Push(sourceTaint);
            }
            return;
        }

        var sanitizerPatterns = context.MatchSanitizer(member).ToList();
        var combined = sanitizerPatterns.Count > 0 ? null : CombineAssemblyTaints(allTaints);
        var sinkPatterns = context.MatchSink(member).ToList();
        if (sinkPatterns.Count > 0 && combined is not null)
        {
            var sinkNode = context.AddNode("Sink", member.Name, currentMethod, assemblyPath, isSource: false, isSink: true, sinkPatterns, sinkPatterns.FirstOrDefault()?.Category, member.Symbol, member.ContainingType, member.Symbol, instruction.Offset + 1);
            context.AddEdges(combined.NodeIds, sinkNode.Id, opCode == OpCodes.Newobj ? "AssemblySinkObjectCreation" : "AssemblySinkCall", assemblyPath, instruction.Offset + 1, member.Name);
            var sinkArgumentIndex = argumentTaints.FindIndex(taint => taint is not null);
            context.AddSlice(combined, sinkNode, sinkPatterns.FirstOrDefault(), member.Symbol, sinkArgumentIndex >= 0 ? sinkArgumentIndex : -1);
        }

        if (member.ReturnsVoid && opCode != OpCodes.Newobj)
        {
            return;
        }

        if (combined is null)
        {
            stack.Push(null);
            return;
        }

        var passthroughPatterns = context.MatchPassthrough(member).ToList();
        if (passthroughPatterns.Count > 0 || member.Symbol.StartsWith("System.", StringComparison.Ordinal))
        {
            var callNode = context.AddNode("Call", member.Name, currentMethod, assemblyPath, isSource: false, isSink: false, [], null, member.Symbol, member.ContainingType, member.Symbol, instruction.Offset + 1);
            context.AddEdges(combined.NodeIds, callNode.Id, "AssemblyCallReturn", assemblyPath, instruction.Offset + 1, member.Name);
            stack.Push(combined.Append(callNode.Id));
        }
        else
        {
            stack.Push(combined);
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
            var matched = context.MatchParameterSource(parameterName, methodInfo).ToList();
            if (matched.Count == 0)
            {
                continue;
            }

            var node = context.AddNode("Source", parameterName, methodInfo, methodInfo.AssemblyPath, isSource: true, isSink: false, matched, matched.FirstOrDefault()?.Category, $"{methodInfo.Symbol}.{parameterName}", null, parameterName, 1);
            argumentTaints[ilIndex] = new AssemblyTaint([node.Id], ToTaintKinds(matched), []);
        }
        return argumentTaints;
    }

    private static AssemblyMethodInfo BuildMethodInfo(MetadataReader reader, MethodDefinitionHandle methodHandle, MethodDefinition method, string assemblyPath)
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

        return new AssemblyMethodInfo(symbol, methodName, typeName, GetNamespace(typeName), Path.GetFileNameWithoutExtension(assemblyPath), signature.ReturnType, assemblyPath, MetadataTokens.GetToken(methodHandle));
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

            yield return new AssemblyInstruction(offset, opCode, operand);
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

    private static void ApplyDefaultStackBehaviour(OpCode opCode, Stack<AssemblyTaint?> stack)
    {
        var popCount = GetPopCount(opCode.StackBehaviourPop);
        for (var i = 0; i < popCount; i++)
        {
            _ = PopOrNull(stack);
        }

        var pushCount = GetPushCount(opCode.StackBehaviourPush);
        for (var i = 0; i < pushCount; i++)
        {
            stack.Push(null);
        }
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

    private static List<string> ToTaintKinds(IEnumerable<DataFlowPattern> patterns) => patterns
        .SelectMany(pattern => pattern.TaintKinds.Count > 0 ? pattern.TaintKinds : [pattern.Category ?? "user-input"])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static List<string> GetAssemblyFiles(string path, bool includeBuildArtifacts)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(Constants.AssemblyExtension, StringComparison.OrdinalIgnoreCase) || extension.Equals(Constants.ExeExtension, StringComparison.OrdinalIgnoreCase) ? [path] : [];
        }

        return new DirectoryInfo(path)
            .EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(file => file.Extension.Equals(Constants.AssemblyExtension, StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(Constants.ExeExtension, StringComparison.OrdinalIgnoreCase))
            .Where(file => includeBuildArtifacts || !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => includeBuildArtifacts || !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => Path.GetFullPath(file.FullName), file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Values
            .ToList();
    }

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
        private readonly HashSet<string> _edgeKeys = result.Edges.Select(edge => $"{edge.SourceId}\u001f{edge.TargetId}\u001f{edge.Kind}\u001f{edge.Label}").ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DataFlowEdge>> _outgoingEdgesBySource = result.Edges.GroupBy(edge => edge.SourceId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        private readonly DataFlowPatternIndex _patternIndex = new(patterns);
        private readonly PackageUrlResolver _purlResolver = PackageUrlResolver.Create(basePath);

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
            var path = Directory.Exists(basePath) ? Path.GetRelativePath(basePath, assemblyPath) : Path.GetFileName(assemblyPath);
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
                FileName = Path.GetFileName(assemblyPath),
                Namespace = method.Namespace,
                ClassName = method.ContainingType.Split('.').LastOrDefault() ?? method.ContainingType,
                MethodName = method.Name,
                LineNumber = ilOffset,
                ColumnNumber = 1,
                IsSource = isSource,
                IsSink = isSink,
                MatchedPatterns = matchedPatterns.Select(pattern => pattern.Pattern).Distinct(StringComparer.Ordinal).ToList(),
                Category = category,
                Properties =
                {
                    ["analysis"] = "assembly-il",
                    ["method"] = method.Symbol,
                    ["metadataToken"] = $"0x{method.MetadataToken:x8}"
                }
            };
            result.Nodes.Add(node);
            _nodesById[node.Id] = node;
            return node;
        }

        public void AddEdges(IEnumerable<string> sourceIds, string targetId, string kind, string assemblyPath, int ilOffset, string? label)
        {
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
                    FileName = Path.GetFileName(assemblyPath),
                    LineNumber = ilOffset,
                    ColumnNumber = 1
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

    private sealed record AssemblyInstruction(int Offset, OpCode OpCode, object? Operand);
    private sealed record AssemblySignatureInfo(int ParameterCount, bool HasThis, bool ReturnsVoid, string ReturnType);
    private sealed record AssemblyMemberInfo(string Symbol, string Name, string ContainingType, int ParameterCount, bool HasThis, bool ReturnsVoid, string ReturnType);
    private sealed record AssemblyMethodInfo(string Symbol, string Name, string ContainingType, string Namespace, string AssemblyName, string ReturnType, string AssemblyPath, int MetadataToken);
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
}
