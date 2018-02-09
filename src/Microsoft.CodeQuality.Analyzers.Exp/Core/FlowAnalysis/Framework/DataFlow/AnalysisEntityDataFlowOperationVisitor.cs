﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Analyzer.Utilities.Extensions;
using Microsoft.CodeAnalysis.Operations.DataFlow.NullAnalysis;
using Microsoft.CodeAnalysis.Operations.DataFlow.PointsToAnalysis;

namespace Microsoft.CodeAnalysis.Operations.DataFlow
{
    /// <summary>
    /// Operation visitor to flow the abstract dataflow analysis values for <see cref="AnalysisEntity"/> instances across a given statement in a basic block.
    /// </summary>
    internal abstract class AnalysisEntityDataFlowOperationVisitor<TAnalysisData, TAbstractAnalysisValue> : DataFlowOperationVisitor<TAnalysisData, TAbstractAnalysisValue>
    {
        protected abstract IEnumerable<AnalysisEntity> TrackedEntities { get; }
        protected abstract void SetAbstractValue(AnalysisEntity analysisEntity, TAbstractAnalysisValue value);
        protected abstract TAbstractAnalysisValue GetAbstractValue(AnalysisEntity analysisEntity);
        protected abstract bool HasAbstractValue(AnalysisEntity analysisEntity);
        
        protected AnalysisEntityFactory AnalysisEntityFactory { get; }

        protected AnalysisEntityDataFlowOperationVisitor(
            AbstractValueDomain<TAbstractAnalysisValue> valueDomain,
            INamedTypeSymbol containingTypeSymbol,
            DataFlowAnalysisResult<NullBlockAnalysisResult, NullAbstractValue> nullAnalysisResultOpt,
            DataFlowAnalysisResult<PointsToBlockAnalysisResult, PointsToAbstractValue> pointsToAnalysisResultOpt)
            : base (valueDomain, containingTypeSymbol, nullAnalysisResultOpt, pointsToAnalysisResultOpt)
        {
            AnalysisEntityFactory = new AnalysisEntityFactory(
                (pointsToAnalysisResultOpt != null || IsPointsToAnalysis) ?
                    GetPointsToAbstractValue :
                    (Func<IOperation, PointsToAbstractValue>)null,
                containingTypeSymbol);
        }

        protected override TAbstractAnalysisValue ComputeAnalysisValueForReferenceOperation(IOperation operation, TAbstractAnalysisValue defaultValue)
        {
            if (AnalysisEntityFactory.TryCreate(operation, out AnalysisEntity analysisEntity))
            {
                if (!HasAbstractValue(analysisEntity))
                {
                    SetAbstractValue(analysisEntity, defaultValue);
                }

                return GetAbstractValue(analysisEntity);
            }
            else
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Helper method to reset analysis data for analysis entities.
        /// If <paramref name="newAnalysisDataOpt"/> is null, all the analysis values in <paramref name="currentAnalysisDataOpt"/> are set to <see cref="ValueDomain.UnknownOrMayBeValue"/>.
        /// Otherwise, all the key-value paris in <paramref name="newAnalysisDataOpt"/> are transfered to <paramref name="currentAnalysisDataOpt"/> and keys in <paramref name="currentAnalysisDataOpt"/> which
        /// are not present in <paramref name="newAnalysisDataOpt"/> are set to <see cref="ValueDomain.UnknownOrMayBeValue"/>.
        /// </summary>
        /// <param name="currentAnalysisDataOpt"></param>
        /// <param name="newAnalysisDataOpt"></param>
        protected void ResetAnalysisData(IDictionary<AnalysisEntity, TAbstractAnalysisValue> currentAnalysisDataOpt, IDictionary<AnalysisEntity, TAbstractAnalysisValue> newAnalysisDataOpt)
        {
            // Reset the current analysis data, while ensuring that we don't violate the monotonicity, i.e. we cannot remove any existing key from currentAnalysisData.
            if (newAnalysisDataOpt == null)
            {
                // Just set the values for existing keys to ValueDomain.UnknownOrMayBeValue.
                var keys = currentAnalysisDataOpt?.Keys.ToImmutableArray();
                foreach (var key in keys)
                {
                    SetAbstractValue(key, ValueDomain.UnknownOrMayBeValue);
                }
            }
            else
            {
                // Merge the values from current and new analysis data.
                var keys = currentAnalysisDataOpt?.Keys.Concat(newAnalysisDataOpt.Keys).ToImmutableArray();
                foreach (var key in keys)
                {
                    var value1 = currentAnalysisDataOpt != null && currentAnalysisDataOpt.TryGetValue(key, out var currentValue) ? currentValue : ValueDomain.Bottom;
                    var value2 = newAnalysisDataOpt.TryGetValue(key, out var newValue) ? newValue : ValueDomain.Bottom;
                    var mergedValue = ValueDomain.Merge(value1, value2);
                    SetAbstractValue(key, mergedValue);
                }
            }
        }

        #region Helper methods to handle initialization/assignment operations
        protected override void SetAbstractValueForSymbolDeclaration(ISymbol symbol, IOperation initializer, TAbstractAnalysisValue initializerValue)
        {
            if (AnalysisEntityFactory.TryCreateForSymbolDeclaration(symbol, out AnalysisEntity analysisEntity))
            {
                SetAbstractValueForAssignment(analysisEntity, initializer, initializerValue);
            }
        }

        protected override void SetAbstractValueForElementInitializer(IOperation instance, ImmutableArray<AbstractIndex> indices, ITypeSymbol elementType, IOperation initializer, TAbstractAnalysisValue value)
        {
            if (AnalysisEntityFactory.TryCreateForElementInitializer(instance, indices, elementType, out AnalysisEntity analysisEntity))
            {
                SetAbstractValueForAssignment(analysisEntity, initializer, value);
            }
        }

        protected override void SetAbstractValueForAssignment(IOperation target, IOperation assignedValueOperation, TAbstractAnalysisValue assignedValue)
        {
            if (AnalysisEntityFactory.TryCreate(target, out AnalysisEntity targetAnalysisEntity))
            {
                SetAbstractValueForAssignment(targetAnalysisEntity, assignedValueOperation, assignedValue);
            }
        }

        private void SetAbstractValueForAssignment(AnalysisEntity targetAnalysisEntity, IOperation assignedValueOperation, TAbstractAnalysisValue assignedValue)
        {
            // Value type and string type assignment has copy semantics.
            if (HasPointsToAnalysisResult &&
                targetAnalysisEntity.Type.HasValueCopySemantics())
            {
                if (HasAbstractValue(targetAnalysisEntity))
                {
                    // Reset the analysis values for analysis entities within the target instance.
                    ResetValueTypeInstanceAnalysisData(targetAnalysisEntity);
                }

                if (assignedValueOperation != null)
                {
                    // Transfer the values of symbols from the assigned instance to the analysis entities in the target instance.
                    TransferValueTypeInstanceAnalysisDataForAssignment(targetAnalysisEntity, assignedValueOperation);
                }
            }

            SetAbstractValue(targetAnalysisEntity, assignedValue);
        }

        #endregion

        #region Helper methods for reseting/transfer instance analysis data when PointsTo analysis results are available

        /// <summary>
        /// Resets all the analysis data for all <see cref="AnalysisEntity"/> instances that share the same <see cref="AnalysisEntity.InstanceLocation"/>
        /// as the given <paramref name="analysisEntity"/>.
        /// </summary>
        /// <param name="analysisEntity"></param>
        private void ResetValueTypeInstanceAnalysisData(AnalysisEntity analysisEntity)
        {
            Debug.Assert(HasPointsToAnalysisResult);
            Debug.Assert(analysisEntity.Type.HasValueCopySemantics());

            IEnumerable<AnalysisEntity> dependantAnalysisEntities = GetChildAnalysisEntities(analysisEntity);
            ResetInstanceAnalysisDataCore(dependantAnalysisEntities);
        }

        protected override void ResetValueTypeInstanceAnalysisData(IOperation operation)
        {
            if (AnalysisEntityFactory.TryCreate(operation, out AnalysisEntity analysisEntity))
            {
                ResetValueTypeInstanceAnalysisData(analysisEntity);
            }
        }

        /// <summary>
        /// Resets all the analysis data for all <see cref="AnalysisEntity"/> instances that share the same <see cref="AnalysisEntity.InstanceLocation"/>
        /// as pointed to by given reference type <paramref name="operation"/>.
        /// </summary>
        /// <param name="operation"></param>
        protected override void ResetReferenceTypeInstanceAnalysisData(IOperation operation)
        {
            Debug.Assert(HasPointsToAnalysisResult);
            Debug.Assert(!operation.Type.HasValueCopySemantics());

            var pointsToValue = GetPointsToAbstractValue(operation);
            if (pointsToValue.Kind != PointsToAbstractValueKind.Known)
            {
                return;
            }

            IEnumerable<AnalysisEntity> dependantAnalysisEntities = GetChildAnalysisEntities(pointsToValue);
            ResetInstanceAnalysisDataCore(dependantAnalysisEntities);
        }

        /// <summary>
        /// Resets the analysis data for the given <paramref name="dependantAnalysisEntities"/>.
        /// </summary>
        /// <param name="dependantAnalysisEntities"></param>
        private void ResetInstanceAnalysisDataCore(IEnumerable<AnalysisEntity> dependantAnalysisEntities)
        {
            foreach (var dependentAnalysisEntity in dependantAnalysisEntities)
            {
                // Reset value.
                SetAbstractValue(dependentAnalysisEntity, ValueDomain.UnknownOrMayBeValue);
            }
        }

        /// <summary>
        /// Transfers the analysis data rooted from <paramref name="assignedValueOperation"/> to <paramref name="targetAnalysisEntity"/>, for a value type assignment operation.
        /// This involves transfer of data for of all <see cref="AnalysisEntity"/> instances that share the same <see cref="AnalysisEntity.InstanceLocation"/> as the valueAnalysisEntity for the <paramref name="assignedValueOperation"/>
        /// to all <see cref="AnalysisEntity"/> instances that share the same <see cref="AnalysisEntity.InstanceLocation"/> as <paramref name="targetAnalysisEntity"/>.
        /// </summary>
        private void TransferValueTypeInstanceAnalysisDataForAssignment(AnalysisEntity targetAnalysisEntity, IOperation assignedValueOperation)
        {
            Debug.Assert(HasPointsToAnalysisResult);
            Debug.Assert(targetAnalysisEntity.Type.HasValueCopySemantics());

            IEnumerable<AnalysisEntity> dependentAnalysisEntities;
            if (AnalysisEntityFactory.TryCreate(assignedValueOperation, out AnalysisEntity valueAnalysisEntity))
            {
                dependentAnalysisEntities = GetChildAnalysisEntities(valueAnalysisEntity);
            }
            else
            {
                // For allocations.
                PointsToAbstractValue newValueLocation = GetPointsToAbstractValue(assignedValueOperation);
                if (newValueLocation.Kind == PointsToAbstractValueKind.NoLocation)
                {
                    return;
                }

                dependentAnalysisEntities = GetChildAnalysisEntities(newValueLocation);
            }

            foreach (AnalysisEntity dependentInstance in dependentAnalysisEntities)
            {
                // Clone the dependent instance but with with target as the root.
                AnalysisEntity newAnalysisEntity = AnalysisEntityFactory.CreateWithNewInstanceRoot(dependentInstance, targetAnalysisEntity);
                var dependentValue = GetAbstractValue(dependentInstance);
                SetAbstractValue(newAnalysisEntity, dependentValue);
            }
        }

        private IEnumerable<AnalysisEntity> GetChildAnalysisEntities(AnalysisEntity analysisEntity)
        {
            IEnumerable<AnalysisEntity> dependentAnalysisEntities = GetChildAnalysisEntities(analysisEntity.InstanceLocation);
            if (analysisEntity.Type.HasValueCopySemantics())
            {
                dependentAnalysisEntities = dependentAnalysisEntities.Where(info => info.HasAncestorOrSelf(analysisEntity));
            }

            return dependentAnalysisEntities;
        }

        private IEnumerable<AnalysisEntity> GetChildAnalysisEntities(PointsToAbstractValue instanceLocationOpt)
        {
            // We are interested only in dependent child/member infos, not the root info.
            if (instanceLocationOpt != null)
            {
                IEnumerable<AnalysisEntity> trackedEntities = TrackedEntities;
                if (trackedEntities != null)
                {
                    return trackedEntities.Where(entity => entity.InstanceLocation.Equals(instanceLocationOpt) && entity.IsChildOrInstanceMember)
                        .ToImmutableHashSet();
                }
            }

            return ImmutableHashSet<AnalysisEntity>.Empty;
        }

        #endregion

        // TODO: Remove these temporary methods once we move to compiler's CFG
        // https://github.com/dotnet/roslyn-analyzers/issues/1567
        #region Temporary methods to workaround lack of *real* CFG
        protected IDictionary<AnalysisEntity, TAbstractAnalysisValue> GetClonedAnalysisData(IDictionary<AnalysisEntity, TAbstractAnalysisValue> analysisData)
            => new Dictionary<AnalysisEntity, TAbstractAnalysisValue>(analysisData);
        #endregion
    }
}