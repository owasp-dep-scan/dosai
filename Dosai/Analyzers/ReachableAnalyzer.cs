using System;
using System.Collections.Generic;
using Analyzer.Utilities;
using Analyzer.Utilities.Extensions;
using Analyzer.Utilities.FlowAnalysis.Analysis.TaintedDataAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis.DataFlow;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis.DataFlow.ValueContentAnalysis;
using System.Collections.Immutable;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Threading.Tasks.Dataflow;
using System.Threading;
using Analyzer.Utilities.PooledObjects;
using Microsoft.CodeAnalysis.FlowAnalysis.DataFlow.PointsToAnalysis;

namespace Dosai.Analyzers
{
    using ValueContentAnalysisResult = DataFlowAnalysisResult<ValueContentBlockAnalysisResult, ValueContentAbstractValue>;

    internal abstract class Runner
    {
        protected int _analysis_warnings = 0;
        protected int _errors = 0;
        protected int _warnings = 0;

        private List<DiagnosticAnalyzer>? _analyzers;

        public Runner()
        {
            _analyzers.Add(new ReachableDiagnosticAnalyzer());
        }

        public abstract Task Run(Project project);

        public virtual async Task<(int, int, int)> WaitForCompletion()
        {
            return await Task.FromResult((_analysis_warnings, _errors, _warnings)).ConfigureAwait(false);
        }

        protected async Task<ImmutableArray<Diagnostic>> GetDiagnostics(Project project)
        {
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            var compilationWithAnalyzers = compilation?.WithAnalyzers([.. _analyzers], project.AnalyzerOptions);
            return await compilationWithAnalyzers.GetAllDiagnosticsAsync()
                                                  .ConfigureAwait(false);
        }
    }

    internal class ReachableFlowsFinder : Runner
    {
        private TransformBlock<Project, ImmutableArray<Diagnostic>> _trackBlock;

        public ReachableFlowsFinder() : base()
        {
            _trackBlock = new TransformBlock<Project, ImmutableArray<Diagnostic>>(async project =>
            {
                return await GetDiagnostics(project).ConfigureAwait(false);
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 4,
                EnsureOrdered = false,
                BoundedCapacity = 32
            });
        }

        public override async Task Run(Project project)
        {
            if (!await _trackBlock.SendAsync(project).ConfigureAwait(false))
            {
                throw new Exception("Thread synchronization error.");
            }
        }

        public override async Task<(int, int, int)> WaitForCompletion()
        {
            _trackBlock.Complete();
            return await base.WaitForCompletion().ConfigureAwait(false);
        }
    }

    public class ReachableDiagnosticAnalyzer : DiagnosticAnalyzer
    {
        protected DiagnosticDescriptor TaintedDataEnteringSinkDescriptor { get; }

        protected SinkKind SinkKind { get; }

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(TaintedDataEnteringSinkDescriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
            context.RegisterCompilationStartAction(
            (CompilationStartAnalysisContext compilationContext) =>
            {
                // FIXME: These two needs to be populated.
                TaintedDataSymbolMap<SourceInfo> sourceInfoSymbolMap = null;
                TaintedDataSymbolMap<SinkInfo> sinkInfoSymbolMap = null;
                TaintedDataSymbolMap<SanitizerInfo> sanitizerInfoSymbolMap = null;

                Compilation compilation = compilationContext.Compilation;
                compilationContext.RegisterOperationBlockStartAction(
                    operationBlockStartContext =>
                    {
                        ISymbol owningSymbol = operationBlockStartContext.OwningSymbol;
                        AnalyzerOptions options = operationBlockStartContext.Options;
                        CancellationToken cancellationToken = operationBlockStartContext.CancellationToken;
                        WellKnownTypeProvider wellKnownTypeProvider = WellKnownTypeProvider.GetOrCreate(compilation);
                        Lazy<ControlFlowGraph?> controlFlowGraphFactory = new(
                            () => operationBlockStartContext.OperationBlocks.GetControlFlowGraph());
                        Lazy<PointsToAnalysisResult?> pointsToFactory = new(
                                    () =>
                                    {
                                        if (controlFlowGraphFactory.Value == null)
                                        {
                                            return null;
                                        }

                                        InterproceduralAnalysisConfiguration interproceduralAnalysisConfiguration = InterproceduralAnalysisConfiguration.Create(
                                                                    options,
                                                                    SupportedDiagnostics,
                                                                    controlFlowGraphFactory.Value,
                                                                    operationBlockStartContext.Compilation,
                                                                    defaultInterproceduralAnalysisKind: InterproceduralAnalysisKind.ContextSensitive,
                                                                    defaultMaxInterproceduralMethodCallChain: 3,
                                                                    defaultMaxInterproceduralLambdaOrLocalFunctionCallChain: 3);
                                        return PointsToAnalysis.TryGetOrComputeResult(
                                                                    controlFlowGraphFactory.Value,
                                                                    owningSymbol,
                                                                    options,
                                                                    wellKnownTypeProvider,
                                                                    PointsToAnalysisKind.Complete,
                                                                    interproceduralAnalysisConfiguration,
                                                                    interproceduralAnalysisPredicate: null);
                                    });
                        Lazy<(PointsToAnalysisResult?, ValueContentAnalysisResult?)> valueContentFactory = new Lazy<(PointsToAnalysisResult?, ValueContentAnalysisResult?)>(
                            () =>
                            {
                                if (controlFlowGraphFactory.Value == null)
                                {
                                    return (null, null);
                                }

                                InterproceduralAnalysisConfiguration interproceduralAnalysisConfiguration = InterproceduralAnalysisConfiguration.Create(
                                                            options,
                                                            SupportedDiagnostics,
                                                            controlFlowGraphFactory.Value,
                                                            operationBlockStartContext.Compilation,
                                                            defaultInterproceduralAnalysisKind: InterproceduralAnalysisKind.ContextSensitive,
                                                            defaultMaxInterproceduralMethodCallChain: 3,
                                                            defaultMaxInterproceduralLambdaOrLocalFunctionCallChain: 3);
                                ValueContentAnalysisResult? valuecontentAnalysisResult = ValueContentAnalysis.TryGetOrComputeResult(
                                                                controlFlowGraphFactory.Value,
                                                                owningSymbol,
                                                                options,
                                                                wellKnownTypeProvider,
                                                                PointsToAnalysisKind.Complete,
                                                                interproceduralAnalysisConfiguration,
                                                                out _,
                                                                out PointsToAnalysisResult? p);

                                return (p, valuecontentAnalysisResult);
                            });

                        var rootOperationsNeedingAnalysis = PooledHashSet<IOperation>.GetInstance();
                        operationBlockStartContext.RegisterOperationAction(
                            operationAnalysisContext =>
                            {
                                IPropertyReferenceOperation propertyReferenceOperation = (IPropertyReferenceOperation)operationAnalysisContext.Operation;
                                if (sourceInfoSymbolMap.IsSourceProperty(propertyReferenceOperation.Property))
                                {
                                    lock (rootOperationsNeedingAnalysis)
                                    {
                                        rootOperationsNeedingAnalysis.Add(propertyReferenceOperation.GetRoot());
                                    }
                                }
                            },
                            OperationKind.PropertyReference);
                        operationBlockStartContext.RegisterOperationAction(
                            operationAnalysisContext =>
                            {
                                IInvocationOperation invocationOperation = (IInvocationOperation)operationAnalysisContext.Operation;
                                if (sourceInfoSymbolMap.IsSourceMethod(
                                        invocationOperation.TargetMethod,
                                        invocationOperation.Arguments,
                                        pointsToFactory,
                                        valueContentFactory,
                                        out _))
                                {
                                    lock (rootOperationsNeedingAnalysis)
                                    {
                                        rootOperationsNeedingAnalysis.Add(invocationOperation.GetRoot());
                                    }
                                }
                            },
                            OperationKind.Invocation);
                        operationBlockStartContext.RegisterOperationAction(
                            operationAnalysisContext =>
                            {
                                IArrayInitializerOperation arrayInitializerOperation = (IArrayInitializerOperation)operationAnalysisContext.Operation;
                                if (arrayInitializerOperation.GetAncestor<IArrayCreationOperation>(OperationKind.ArrayCreation)?.Type is IArrayTypeSymbol arrayTypeSymbol
                                    && sourceInfoSymbolMap.IsSourceConstantArrayOfType(arrayTypeSymbol, arrayInitializerOperation))
                                {
                                    lock (rootOperationsNeedingAnalysis)
                                    {
                                        rootOperationsNeedingAnalysis.Add(operationAnalysisContext.Operation.GetRoot());
                                    }
                                }
                            },
                            OperationKind.ArrayInitializer);
                        operationBlockStartContext.RegisterOperationBlockEndAction(
                                    operationBlockAnalysisContext =>
                                    {
                                        try
                                        {
                                            lock (rootOperationsNeedingAnalysis)
                                            {
                                                if (!rootOperationsNeedingAnalysis.Any())
                                                {
                                                    return;
                                                }

                                                if (controlFlowGraphFactory.Value == null)
                                                {
                                                    return;
                                                }

                                                foreach (IOperation rootOperation in rootOperationsNeedingAnalysis)
                                                {
                                                    TaintedDataAnalysisResult? taintedDataAnalysisResult = TaintedDataAnalysis.TryGetOrComputeResult(
                                                        controlFlowGraphFactory.Value,
                                                        operationBlockAnalysisContext.Compilation,
                                                        operationBlockAnalysisContext.OwningSymbol,
                                                        operationBlockAnalysisContext.Options,
                                                        TaintedDataEnteringSinkDescriptor,
                                                        sourceInfoSymbolMap,
                                                        sanitizerInfoSymbolMap,
                                                        sinkInfoSymbolMap
                                                        );
                                                    if (taintedDataAnalysisResult == null)
                                                    {
                                                        return;
                                                    }

                                                    foreach (TaintedDataSourceSink sourceSink in taintedDataAnalysisResult.TaintedDataSourceSinks)
                                                    {
                                                        if (!sourceSink.SinkKinds.Contains(this.SinkKind))
                                                        {
                                                            continue;
                                                        }

                                                        foreach (SymbolAccess sourceOrigin in sourceSink.SourceOrigins)
                                                        {
                                                            Diagnostic diagnostic = Diagnostic.Create(
                                                                this.TaintedDataEnteringSinkDescriptor,
                                                                sourceSink.Sink.Location,
                                                                additionalLocations: new Location[] { sourceOrigin.Location },
                                                                messageArgs: new object[] {
                                                                sourceSink.Sink.Symbol.Name,
                                                                sourceSink.Sink.AccessingMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                                                sourceOrigin.Symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                                                sourceOrigin.AccessingMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)});
                                                            operationBlockAnalysisContext.ReportDiagnostic(diagnostic);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        finally
                                        {
                                            rootOperationsNeedingAnalysis.Free(compilationContext.CancellationToken);
                                        }
                                    });
                    });
            });
        }
    }

    public class ReachableAnalyzer : TaintedDataAnalysis
    {

        private ReachableAnalyzer(TaintedDataAnalysisDomain analysisDomain, TaintedDataOperationVisitor operationVisitor)
            : base(analysisDomain, operationVisitor)
        {
        }

        public override bool Equals(object? obj)
        {
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }

        public override string? ToString()
        {
            return base.ToString();
        }

        protected override TaintedDataBlockAnalysisResult ToBlockResult(BasicBlock basicBlock, TaintedDataAnalysisData blockAnalysisData)
        {
            return base.ToBlockResult(basicBlock, blockAnalysisData);
        }

        protected override TaintedDataAnalysisResult ToResult(TaintedDataAnalysisContext analysisContext, DataFlowAnalysisResult<TaintedDataBlockAnalysisResult, TaintedDataAbstractValue> dataFlowAnalysisResult)
        {
            return base.ToResult(analysisContext, dataFlowAnalysisResult);
        }
    }
}
