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

namespace Dosai.Analyzers
{
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
