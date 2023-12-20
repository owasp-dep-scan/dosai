// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

namespace Analyzer.Utilities.FlowAnalysis.Analysis.TaintedDataAnalysis
{
    using Microsoft.CodeAnalysis.FlowAnalysis.DataFlow;

    public partial class TaintedDataAnalysis
    {
        public sealed class TaintedDataAnalysisDomain : PredicatedAnalysisDataDomain<TaintedDataAnalysisData, TaintedDataAbstractValue>
        {
            public TaintedDataAnalysisDomain(MapAbstractDomain<AnalysisEntity, TaintedDataAbstractValue> coreDataAnalysisDomain)
                : base(coreDataAnalysisDomain)
            {
            }
        }
    }
}