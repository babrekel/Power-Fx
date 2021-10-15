//------------------------------------------------------------------------------
// <copyright company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using Microsoft.AppMagic.Common;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.PowerFx.Core.App;
using Microsoft.PowerFx.Core.App.Components;
using Microsoft.PowerFx.Core.App.Controls;
using Microsoft.PowerFx.Core.App.ErrorContainers;
using Microsoft.PowerFx.Core.Glue;
using Microsoft.PowerFx.Core.Types;
using PowerApps.Language.Entities;
using Conditional = System.Diagnostics.ConditionalAttribute;
using Microsoft.PowerFx.Core.Delegation;
using Microsoft.PowerFx.Core.Entities.QueryOptions;
using Microsoft.PowerFx.Core.Binding;

namespace Microsoft.AppMagic.Authoring.Texl
{
    internal sealed partial class TexlBinding
    {
        private readonly IBinderGlue _glue;

        // The parse tree for this binding.
        public readonly TexlNode Top;

        // Path of entity where this formula was bound.
        public readonly DPath EntityPath;

        // Name of entity where this formula was bound.
        public readonly DName EntityName;

        // The name resolver associated with this binding.
        public readonly INameResolver NameResolver;

        // The local scope resolver associated with this binding.
        public readonly IExternalRuleScopeResolver LocalRuleScopeResolver;

        // Maps Ids to Types, where the Id is an index in the array.
        private DType[] _typeMap;
        private DType[] _coerceMap;

        // Maps Ids to whether the node/subtree is async or not. A subtree
        // that has async components is itself async, so the async aspect of an expression
        // propagates up the parse tree all the way to the root.
        private bool[] _asyncMap;

        // Used to mark nodes as delegatable or not.
        private BitArray _isDelegatable;

        // Used to mark node as pageable or not.
        private BitArray _isPageable;

        // Extra information. We have a slot for each node.
        // Maps Ids to Info, where the Id is an index in the array.
        private object[] _infoMap;

        private IDictionary<int, IList<FirstNameInfo>> _lambdaParams;

        // Whether a node is stateful or has side effects or is contextual or is constant.
        private BitArray _isStateful;
        private BitArray _hasSideEffects;
        private BitArray _isContextual;
        private BitArray _isConstant;
        private BitArray _isSelfContainedConstant;

        // Whether a node supports its rowscoped param exempted from delegation check. e.g. The 3rd argument in AddColumns function
        private BitArray _supportsRowScopedParamDelegationExempted;
        // Whether a node is an ECS excempt lambda. e.g. filter lambdas
        private BitArray _isEcsExcemptLambda;
        // Whether a node is inside delegable function but its value only depends on the outer scope(higher than current scope)
        private BitArray _isBlockScopedConstant;

        // Property to which current rule is being bound to. It could be null in the absence of NameResolver.
        private readonly IExternalControlProperty _property;
        private readonly IExternalControl _control;

        // Whether a node is scoped to app or not. Used by translator for component scoped variable references.
        private BitArray _isAppScopedVariable;

        // The scope use sets associated with all the nodes.
        private ScopeUseSet[] _lambdaScopingMap;

        private List<DType> _typesNeedingMetadata;
        private bool _hasThisItemReference;
        private bool _forceUpdateDisplayNames;
        private bool _hasLocalReferences;

        // If As is used at the toplevel, contains the rhs value of the As operand;
        private DName _renamedOutputAccessor;

        // A mapping of node ids to lists of variable identifiers that are to have been altered in runtime prior
        // to the node of the id, e.g. Set(x, 1); Set(y, x + 1);
        // All child nodes of the chaining operator that come after Set(x, 1); will have a variable weight that
        // contains x
        private ImmutableHashSet<string>[] _volatileVariables;

        // This is set when a First Name node or child First Name node contains itself in its variable weight
        // and can be read by the back end to determine whether it may generate code that lifts or caches an
        // expression
        private BitArray _isUnliftable;

        public bool HasLocalScopeReferences => _hasLocalReferences;

        public ErrorContainer ErrorContainer { get; } = new ErrorContainer();

        /// <summary>
        /// The maximum number of selects in a table that will be included in data call.
        /// </summary>
        public const int MaxSelectsToInclude = 100;

        /// <summary>
        /// Default name used to access a Lambda scope
        /// </summary>
        internal DName ThisRecordDefaultName => new DName("ThisRecord");

        // Property to which current rule is being bound to. It could be null in the absence of NameResolver.
        public IExternalControlProperty Property
        {
            get
            {
#if DEBUG
                if (NameResolver?.CurrentEntity?.IsControl == true && NameResolver.CurrentProperty.IsValid && NameResolver.TryGetCurrentControlProperty(out var currentProperty))
                    Contracts.Assert(_property == currentProperty);
#endif
                return _property;
            }
        }

        // Control to which current rule is being bound to. It could be null in the absence of NameResolver.
        public IExternalControl Control
        {
            get
            {
#if DEBUG
                if (NameResolver != null && NameResolver.CurrentEntity != null && NameResolver.CurrentEntity.IsControl)
                    Contracts.Assert(NameResolver.CurrentEntity == _control);
#endif
                return _control;
            }
        }

        // We store this information here instead of on TabularDataSourceInfo is that this information should change as the rules gets edited
        // and we shouldn't store information about the fields user tried but didn't end up in final rule.
        public DataSourceToQueryOptionsMap QueryOptions { get; }

        public bool UsesGlobals { get; private set; }
        public bool UsesAliases { get; private set; }
        public bool UsesScopeVariables { get; private set; }
        public bool UsesThisItem { get; private set; }
        public bool UsesResources { get; private set; }
        public bool UsesOptionSets { get; private set; }
        public bool UsesViews { get; private set; }
        public bool TransitionsFromAsyncToSync { get; private set; }
        public int IdLim => _infoMap == null ? 0 : _infoMap.Length;
        public DType ResultType => GetType(Top);

        // The coerced type of the rule after name-mapping.
        public DType CoercedToplevelType { get; internal set; }
        public bool HasThisItemReference => _hasThisItemReference || UsesThisItem;
        public bool HasParentItemReference { get; private set; }
        public bool HasSelfReference { get; private set; }
        public bool IsBehavior => NameResolver != null && NameResolver.CurrentPropertyIsBehavior;
        public bool IsConstantData => NameResolver != null && NameResolver.CurrentPropertyIsConstantData;
        public bool IsNavigationAllowed => NameResolver != null && NameResolver.CurrentPropertyAllowsNavigation;
        public IExternalDocument Document => (NameResolver != null) ? NameResolver.Document : null;

        public bool AffectsAliases { get; private set; }
        public bool AffectsScopeVariable { get; private set; }
        public bool AffectsScopeVariableName { get; private set; }
        public bool AffectsTabularDataSources { get; private set; } = false;
        public bool HasControlReferences { get; private set; }

        /// <summary>
        /// UsedControlProperties  is for processing edges required for indirect control property references
        /// </summary>
        public HashSet<DName> UsedControlProperties { get; } = new HashSet<DName>();

        public bool HasSelectFunc { get; private set; }
        public bool HasReferenceToAttachment { get; private set; }
        public bool IsGloballyPure => !(UsesGlobals || UsesThisItem || UsesAliases || UsesScopeVariables || UsesResources) && IsPure(Top);
        public bool IsCurrentPropertyPageable => Property != null && Property.SupportsPaging;
        public bool CurrentPropertyRequiresDefaultableReferences => Property != null && Property.RequiresDefaultablePropertyReferences;
        public bool ContainsAnyPageableNode => _isPageable.Cast<bool>().Any(isPageable => isPageable);
        public IExternalEntityScope EntityScope => NameResolver?.EntityScope;
        public string TopParentUniqueId => EntityPath.IsRoot ? string.Empty : EntityPath[0].Value;

        // Stores tokens that need replacement (Display Name -> Logical Name) for serialization
        // Replace Nodes (Display Name -> Logical Name) for serialization
        public IList<KeyValuePair<Token, string>> NodesToReplace { get; }
        public bool UpdateDisplayNames { get; }

        /// <summary>
        /// The fields of this type are defined as valid keywords for this binding.
        /// </summary>
        public DType ContextScope { get; }

        private TexlBinding(IBinderGlue glue, IExternalRuleScopeResolver scopeResolver, DataSourceToQueryOptionsMap queryOptions, TexlNode node, INameResolver resolver, DType ruleScope, bool useThisRecordForRuleScope, bool updateDisplayNames = false, bool forceUpdateDisplayNames = false, IExternalRule rule = null)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValueOrNull(resolver);
            Contracts.AssertValueOrNull(scopeResolver);

            QueryOptions = queryOptions;
            _glue = glue;
            Top = node;
            NameResolver = resolver;
            LocalRuleScopeResolver = scopeResolver;

            int idLim = node.Id + 1;

            _typeMap = new DType[idLim];
            _coerceMap = new DType[idLim];
            for (int i = 0; i < idLim; ++i)
            {
                _typeMap[i] = DType.Invalid;
                _coerceMap[i] = DType.Invalid;
            }

            CoercedToplevelType = DType.Invalid;
            _infoMap = new object[idLim];
            _asyncMap = new bool[idLim];
            _lambdaParams = new Dictionary<int, IList<FirstNameInfo>>(idLim);
            _isStateful = new BitArray(idLim);
            _hasSideEffects = new BitArray(idLim);
            _isAppScopedVariable = new BitArray(idLim);
            _isContextual = new BitArray(idLim);
            _isConstant = new BitArray(idLim);
            _isSelfContainedConstant = new BitArray(idLim);
            _lambdaScopingMap = new ScopeUseSet[idLim];
            _isDelegatable = new BitArray(idLim);
            _isPageable = new BitArray(idLim);
            _isEcsExcemptLambda = new BitArray(idLim);
            _supportsRowScopedParamDelegationExempted = new BitArray(idLim);
            _isBlockScopedConstant = new BitArray(idLim);
            _hasThisItemReference = false;
            _renamedOutputAccessor = default;

            _volatileVariables = new ImmutableHashSet<string>[idLim];
            _isUnliftable = new BitArray(idLim);

            HasParentItemReference = false;

            ContextScope = ruleScope;
            BinderNodeMetadataArgTypeVisitor = new BinderNodesMetadataArgTypeVisitor(this, resolver, ruleScope, useThisRecordForRuleScope);
            HasReferenceToAttachment = false;
            NodesToReplace = new List<KeyValuePair<Token, string>>();
            UpdateDisplayNames = updateDisplayNames;
            _forceUpdateDisplayNames = forceUpdateDisplayNames;
            _hasLocalReferences = false;
            TransitionsFromAsyncToSync = false;
            Rule = rule;
            if (resolver != null)
            {
                EntityPath = resolver.CurrentEntityPath;
                EntityName = resolver.CurrentEntity == null ? default(DName) : resolver.CurrentEntity.EntityName;
            }

            resolver?.TryGetCurrentControlProperty(out _property);
            _control = resolver?.CurrentEntity as IExternalControl;
        }

        // Binds a Texl parse tree.
        // * resolver provides the name context used to bind names to globals, resources, etc. This may be null.
        public static TexlBinding Run(IBinderGlue glue, IExternalRuleScopeResolver scopeResolver, DataSourceToQueryOptionsMap queryOptionsMap, TexlNode node, INameResolver resolver, bool updateDisplayNames = false, DType ruleScope = null, bool forceUpdateDisplayNames = false, IExternalRule rule = null, bool useThisRecordForRuleScope = false)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValueOrNull(resolver);

            TexlBinding txb = new TexlBinding(glue, scopeResolver, queryOptionsMap, node, resolver, ruleScope, useThisRecordForRuleScope, updateDisplayNames, forceUpdateDisplayNames, rule: rule);
            Visitor vis = new Visitor(txb, resolver, ruleScope, useThisRecordForRuleScope);
            vis.Run();

            // Determine if a rename has occured at the top level
            if (txb.Top is AsNode asNode)
                txb._renamedOutputAccessor = txb.GetInfo(asNode).AsIdentifier;

            return txb;
        }

        public static TexlBinding Run(IBinderGlue glue, TexlNode node, INameResolver resolver, bool updateDisplayNames = false, DType ruleScope = null, bool forceUpdateDisplayNames = false, IExternalRule rule = null)
        {
            return Run(glue, null, new DataSourceToQueryOptionsMap(), node, resolver, updateDisplayNames, ruleScope, forceUpdateDisplayNames, rule);
        }

        public static TexlBinding Run(IBinderGlue glue, TexlNode node, INameResolver resolver, DType ruleScope, bool useThisRecordForRuleScope = false)
        {
            return Run(glue, null, new DataSourceToQueryOptionsMap(), node, resolver, false, ruleScope, false, null, useThisRecordForRuleScope);
        }

        public void WidenResultType()
        {
            SetType(Top, DType.Error);
            ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, Top, TexlStrings.ErrTypeError);
        }

        public DType GetType(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);
            Contracts.Assert(_typeMap[node.Id].IsValid);

            return _typeMap[node.Id];
        }

        private void SetType(TexlNode node, DType type)
        {
            Contracts.AssertValue(node);
            Contracts.Assert(type.IsValid);
            Contracts.AssertIndex(node.Id, _typeMap.Length);
            Contracts.Assert(_typeMap[node.Id] == null || !_typeMap[node.Id].IsValid || type.IsError);

            _typeMap[node.Id] = type;
        }

        private void SetContextual(TexlNode node, bool isContextual)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);
            Contracts.Assert(isContextual || !_isContextual.Get(node.Id));

            _isContextual.Set(node.Id, isContextual);
        }

        private void SetConstant(TexlNode node, bool isConstant)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);
            Contracts.Assert(isConstant || !_isConstant.Get(node.Id));

            _isConstant.Set(node.Id, isConstant);
        }

        private void SetSelfContainedConstant(TexlNode node, bool isConstant)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);
            Contracts.Assert(isConstant || !_isSelfContainedConstant.Get(node.Id));

            _isSelfContainedConstant.Set(node.Id, isConstant);
        }

        private void SetSideEffects(TexlNode node, bool hasSideEffects)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);
            Contracts.Assert(hasSideEffects || !_hasSideEffects.Get(node.Id));

            _hasSideEffects.Set(node.Id, hasSideEffects);
        }

        private void SetStateful(TexlNode node, bool isStateful)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);
            Contracts.Assert(isStateful || !_isStateful.Get(node.Id));

            _isStateful.Set(node.Id, isStateful);
        }

        private void SetAppScopedVariable(FirstNameNode node, bool isAppScopedVariable)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);
            Contracts.Assert(isAppScopedVariable || !_isAppScopedVariable.Get(node.Id));

            _isAppScopedVariable.Set(node.Id, isAppScopedVariable);
        }

        public bool IsAppScopedVariable(FirstNameNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);

            return _isAppScopedVariable.Get(node.Id);
        }

        /// <summary>
        /// See documentation for <see cref="GetVolatileVariables"/> for more information
        /// </summary>
        /// <param name="node">
        /// Node to which volatile variables are being added
        /// </param>
        /// <param name="variables">
        /// The variables that are to be added to the list associated with <see cref="node"/>
        /// </param>
        private void AddVolatileVariables(TexlNode node, ImmutableHashSet<string> variables)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _volatileVariables.Length);

            var volatileVariables = _volatileVariables[node.Id] ?? ImmutableHashSet<string>.Empty;
            _volatileVariables[node.Id] = volatileVariables.Union(variables);
        }

        /// <summary>
        /// See documentation for <see cref="GetVolatileVariables"/> for more information.
        /// </summary>
        /// <param name="node">
        /// Node whose liftability will be altered by this invocation
        /// </param>
        /// <param name="value">
        /// The value that the node's liftability should assume by the invocation of this method
        /// </param>
        private void SetIsUnliftable(TexlNode node, bool value)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _isUnliftable.Length);

            _isUnliftable[node.Id] = value;
        }

        private bool SupportsServerDelegation(CallNode node)
        {
            Contracts.AssertValue(node);

            CallInfo info = GetInfo(node).VerifyValue();
            var function = info.Function;
            if (function == null)
                return false;

            var isServerDelegatable = function.IsServerDelegatable(node, this);
            BinderUtils.LogTelemetryForFunction(function, node, this, isServerDelegatable);
            return isServerDelegatable;
        }

        private bool SupportsPaging(FirstNameNode node)
        {
            Contracts.AssertValue(node);

            FirstNameInfo info = GetInfo(node).VerifyValue();
            IExternalDataSource dataSourceInfo;
            if (info.Kind == BindKind.Data &&
                (dataSourceInfo = info.Data as IExternalDataSource) != null
                && dataSourceInfo.IsPageable)
            {
                return true;
            }

            // One To N Relationships are pagable using nextlinks
            if (info.Kind == BindKind.DeprecatedImplicitThisItem && (GetType(node).ExpandInfo?.IsTable ?? false))
                return true;

            return false;
        }

        private bool SupportsPaging(TexlNode node)
        {
            Contracts.AssertValue(node);

            switch (node.Kind)
            {
                case NodeKind.FirstName:
                    return SupportsPaging(node.AsFirstName());
                case NodeKind.DottedName:
                    return SupportsPaging(node.AsDottedName());
                case NodeKind.Call:
                    return SupportsPaging(node.AsCall());
                default:
                    Contracts.Assert(false, "This should only be called for FirstNameNode, DottedNameNode and CallNode.");
                    return false;
            }
        }

        private bool SupportsPaging(CallNode node)
        {
            Contracts.AssertValue(node);

            var info = GetInfo(node);
            return info?.Function?.SupportsPaging(node, this) ?? false;
        }

        private bool TryGetEntityInfo(DottedNameNode node, out IExpandInfo info)
        {
            Contracts.AssertValue(node);

            info = null;
            DottedNameNode dottedNameNode = node.AsDottedName();
            if (dottedNameNode == null)
                return false;

            info = GetInfo(dottedNameNode)?.Data as IExpandInfo;
            return info != null;
        }

        private bool TryGetEntityInfo(FirstNameNode node, out IExpandInfo info)
        {
            Contracts.AssertValue(node);

            info = null;
            FirstNameNode firstNameNode = node.AsFirstName();
            if (firstNameNode == null)
                return false;

            info = GetInfo(firstNameNode)?.Data as IExpandInfo;
            return info != null;
        }

        private bool TryGetEntityInfo(CallNode node, out IExpandInfo info)
        {
            Contracts.AssertValue(node);

            info = null;
            CallNode callNode = node.AsCall();
            if (callNode == null)
                return false;

            // It is possible for function to be null here if it referred to
            // a service function from a service we are in the process of
            // deregistering.
            return GetInfo(callNode).VerifyValue().Function?.TryGetEntityInfo(node, this, out info) ?? false;
        }

        internal IExternalRule Rule { get; }

        // When getting projections from a chain rule, ensure that the projection belongs to the same DS as the one we're operating on (using match param)
        internal bool TryGetDataQueryOptions(TexlNode node, bool forCodegen, out DataSourceToQueryOptionsMap tabularDataQueryOptionsMap)
        {
            Contracts.AssertValue(node);

            if (node.Kind == NodeKind.As)
            {
                node = node.AsAsNode().Left;
            }

            if (node.Kind == NodeKind.Call)
            {
                if (node.AsCall().Args.Children.Length == 0)
                {
                    tabularDataQueryOptionsMap = null;
                    return false;
                }
                node = node.AsCall().Args.Children[0];

                // Call nodes may have As nodes as the lhs, make sure query options are pulled from the lhs of the as node
                if (node.Kind == NodeKind.As)
                {
                    node = node.AsAsNode().Left;
                }
            }

            if (!Rule.TexlNodeQueryOptions.ContainsKey(node.Id))
            {
                tabularDataQueryOptionsMap = null;
                return false;
            }

            TexlNode topNode = null;
            foreach (var top in TopChain)
            {
                if (!node.InTree(top)) continue;

                topNode = top;
                break;
            }

            Contracts.AssertValue(topNode);

            if (node.Kind == NodeKind.FirstName
                && Rule.TexlNodeQueryOptions.Count > 1)
            {
                if (!(Rule.Document.GlobalScope.GetTabularDataSource(node.AsFirstName().Ident.Name) is IExternalTabularDataSource tabularDs))
                {
                    tabularDataQueryOptionsMap = Rule.TexlNodeQueryOptions[node.Id];
                    return true;
                }

                tabularDataQueryOptionsMap = new DataSourceToQueryOptionsMap();
                tabularDataQueryOptionsMap.AddDataSource(tabularDs);

                foreach (var x in Rule.TexlNodeQueryOptions)
                {
                    if (topNode.MinChildID > x.Key || x.Key > topNode.Id) continue;

                    var qo = x.Value.GetQueryOptions(tabularDs);

                    if (qo == null) continue;

                    tabularDataQueryOptionsMap.GetQueryOptions(tabularDs).Merge(qo);
                }

                return true;
            }
            else
            {
                tabularDataQueryOptionsMap = Rule.TexlNodeQueryOptions[node.Id];
                return true;
            }
        }

        private static IExternalControl GetParentControl(ParentNode parent, INameResolver nameResolver)
        {
            Contracts.AssertValue(parent);
            Contracts.AssertValueOrNull(nameResolver);

            if (nameResolver == null || nameResolver.CurrentEntity == null)
                return null;

            NameLookupInfo lookupInfo;
            if (!nameResolver.CurrentEntity.IsControl || !nameResolver.LookupParent(out lookupInfo))
                return null;

            return lookupInfo.Data as IExternalControl;
        }

        private static IExternalControl GetSelfControl(SelfNode self, INameResolver nameResolver)
        {
            Contracts.AssertValue(self);
            Contracts.AssertValueOrNull(nameResolver);

            if (nameResolver == null || nameResolver.CurrentEntity == null)
                return null;

            NameLookupInfo lookupInfo;
            if (!nameResolver.LookupSelf(out lookupInfo))
                return null;

            return lookupInfo.Data as IExternalControl;
        }

        private bool IsDataComponentDataSource(NameLookupInfo lookupInfo)
        {
            return lookupInfo.Kind == BindKind.Data &&
                _glue.IsComponentDataSource(lookupInfo.Data);
        }

        private bool IsDataComponentDefinition(NameLookupInfo lookupInfo)
        {
            return lookupInfo.Kind == BindKind.Control &&
                   _glue.IsDataComponentDefinition(lookupInfo.Data);
        }

        private bool IsDataComponentInstance(NameLookupInfo lookupInfo)
        {
            return lookupInfo.Kind == BindKind.Control &&
                   _glue.IsDataComponentInstance(lookupInfo.Data);
        }

        private IExternalControl GetDataComponentControl(DottedNameNode dottedNameNode, INameResolver nameResolver, TexlVisitor visitor)
        {
            Contracts.AssertValue(dottedNameNode);
            Contracts.AssertValueOrNull(nameResolver);
            Contracts.AssertValueOrNull(visitor);

            if (nameResolver == null || !(dottedNameNode.Left is FirstNameNode lhsNode))
                return null;

            if (!nameResolver.LookupGlobalEntity(lhsNode.Ident.Name, out NameLookupInfo lookupInfo) ||
                (!IsDataComponentDataSource(lookupInfo) &&
                !IsDataComponentDefinition(lookupInfo) &&
                !IsDataComponentInstance(lookupInfo)))
            {
                return null;
            }

            if (GetInfo(lhsNode) == null)
                lhsNode.Accept(visitor);

            var lhsInfo = GetInfo(lhsNode);
            if (lhsInfo?.Data is IExternalControl dataCtrlInfo)
                return dataCtrlInfo;

            if (lhsInfo?.Kind == BindKind.Data &&
                _glue.TryGetCdsDataSourceByBind(lhsInfo.Data, out var info))
            {
                return info;
            }

            return null;
        }

        private DPath GetFunctionNamespace(CallNode node, TexlVisitor visitor)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(visitor);

            var leftNode = (node.HeadNode as DottedNameNode)?.Left;
            var ctrlInfo = leftNode switch
            {
                ParentNode parentNode => GetParentControl(parentNode, NameResolver),
                SelfNode selfNode => GetSelfControl(selfNode, NameResolver),
                FirstNameNode firstNameNode => GetDataComponentControl(node.HeadNode.AsDottedName(), NameResolver, visitor),
                _ => null,
            };

            return ctrlInfo != null
                ? DPath.Root.Append(new DName(ctrlInfo.DisplayName))
                : node.Head.Namespace;
        }

        internal bool TryGetDataQueryOptions(out DataSourceToQueryOptionsMap tabularDataQueryOptionsMap)
        {
            return TryGetDataQueryOptions(Top, false, out tabularDataQueryOptionsMap);
        }

        internal IEnumerable<string> GetDataQuerySelects(TexlNode node)
        {
            if (!Document.Properties.EnabledFeatures.IsProjectionMappingEnabled)
                return Enumerable.Empty<string>();

            if (!TryGetDataQueryOptions(node, true, out var tabularDataQueryOptionsMap))
                return Enumerable.Empty<string>();

            var currNodeQueryOptions = tabularDataQueryOptionsMap.GetQueryOptions();

            if (currNodeQueryOptions.Count() == 0)
                return Enumerable.Empty<string>();

            if (currNodeQueryOptions.Count() == 1)
            {
                var ds = currNodeQueryOptions.First().TabularDataSourceInfo;

                if (!ds.IsSelectable)
                    return Enumerable.Empty<string>();

                var ruleQueryOptions = Rule.Binding.QueryOptions.GetQueryOptions(ds);
                if (ruleQueryOptions != null)
                {
                    foreach (var nodeQO in Rule.TexlNodeQueryOptions)
                    {
                        var nodeQOSelects = nodeQO.Value.GetQueryOptions(ds)?.Selects;
                        ruleQueryOptions.AddSelectMultiple(nodeQOSelects);
                    }
                    ruleQueryOptions.AddRelatedColumns();

                    if (ruleQueryOptions.HasNonKeySelects())
                        return ruleQueryOptions.Selects;
                }
                else
                {
                    if (ds.QueryOptions.HasNonKeySelects())
                    {
                        ds.QueryOptions.AddRelatedColumns();
                        return ds.QueryOptions.Selects;
                    }
                }
            }

            return Enumerable.Empty<string>();
        }

        internal IEnumerable<string> GetExpandQuerySelects(TexlNode node, string expandEntityLogicalName)
        {
            if (Document.Properties.EnabledFeatures.IsProjectionMappingEnabled
                && TryGetDataQueryOptions(node, true, out var tabularDataQueryOptionsMap))
            {
                var currNodeQueryOptions = tabularDataQueryOptionsMap.GetQueryOptions();

                foreach (var qoItem in currNodeQueryOptions)
                {
                    foreach (var expandQueryOptions in qoItem.Expands)
                    {
                        if (expandQueryOptions.Value.ExpandInfo.Identity == expandEntityLogicalName)
                        {
                            if (!expandQueryOptions.Value.SelectsEqualKeyColumns() &&
                                expandQueryOptions.Value.Selects.Count() <= MaxSelectsToInclude)
                            {
                                return expandQueryOptions.Value.Selects;
                            }
                            else
                            {
                                return Enumerable.Empty<string>();
                            }
                        }
                    }
                }
            }

            return Enumerable.Empty<string>();
        }

        public bool TryGetEntityInfo(TexlNode node, out IExpandInfo info)
        {
            Contracts.AssertValue(node);

            switch (node.Kind)
            {
                case NodeKind.DottedName:
                    return TryGetEntityInfo(node.AsDottedName(), out info);
                case NodeKind.FirstName:
                    return TryGetEntityInfo(node.AsFirstName(), out info);
                case NodeKind.Call:
                    return TryGetEntityInfo(node.AsCall(), out info);
                default:
                    info = null;
                    return false;
            }
        }

        public bool HasExpandInfo(TexlNode node)
        {
            Contracts.AssertValue(node);

            Object data;
            switch (node.Kind)
            {
                case NodeKind.DottedName:
                    data = GetInfo(node.AsDottedName())?.Data;
                    break;
                case NodeKind.FirstName:
                    data = GetInfo(node.AsFirstName())?.Data;
                    break;
                default:
                    data = null;
                    break;
            }

            return (data != null) && (data is IExpandInfo);
        }

        internal bool TryGetDataSourceInfo(TexlNode node, out IExternalDataSource dataSourceInfo)
        {
            Contracts.AssertValue(node);

            var kind = node.Kind;


            switch (kind)
            {
                case NodeKind.Call:
                    var callNode = node.AsCall().VerifyValue();
                    var callFunction = GetInfo(callNode)?.Function;
                    if (callFunction != null)
                        return callFunction.TryGetDataSource(callNode, this, out dataSourceInfo);
                    break;
                case NodeKind.FirstName:
                    var firstNameNode = node.AsFirstName().VerifyValue();
                    dataSourceInfo = GetInfo(firstNameNode)?.Data as IExternalDataSource;
                    return dataSourceInfo != null;
                case NodeKind.DottedName:
                    IExpandInfo info;
                    if (TryGetEntityInfo(node.AsDottedName(), out info))
                    {
                        dataSourceInfo = info.ParentDataSource;
                        return dataSourceInfo != null;
                    }
                    break;
                case NodeKind.As:
                    return TryGetDataSourceInfo(node.AsAsNode().Left, out dataSourceInfo);
                default:
                    break;
            }

            dataSourceInfo = null;
            return false;
        }

        private bool SupportsPaging(DottedNameNode node)
        {
            Contracts.AssertValue(node);

            if (HasExpandInfo(node) && SupportsPaging(node.Left))
                return true;

            return TryGetEntityInfo(node, out IExpandInfo entityInfo) && entityInfo.IsTable;
        }

        public void CheckAndMarkAsDelegatable(CallNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);

            if (SupportsServerDelegation(node))
            {
                _isDelegatable.Set(node.Id, true);

                // Delegatable calls are async as well.
                FlagPathAsAsync(node);
            }
        }

        public void CheckAndMarkAsDelegatable(AsNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);

            if (_isDelegatable[node.Left.Id])
            {
                _isDelegatable.Set(node.Id, true);
                // Mark this as async, as this may result in async invocation.
                FlagPathAsAsync(node);
            }
        }

        public void CheckAndMarkAsPageable(CallNode node, TexlFunction func)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);
            Contracts.AssertValue(func);

            // Server delegatable call always returns a pageable object.
            if (func.SupportsPaging(node, this))
            {
                _isPageable.Set(node.Id, true);
            }
            else
            {
                // If we are transitioning from pageable call node to non-pageable node then it results in an
                // async call. So mark the path as async if current node is non-pageable with pageable child.
                // This also means that we will need an error context
                var args = node.Args.Children;
                if (args.Any(cnode => IsPageable(cnode)))
                {
                    FlagPathAsAsync(node);
                    TransitionsFromAsyncToSync = true;
                }
            }
        }

        public void CheckAndMarkAsPageable(FirstNameNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);

            if (SupportsPaging(node))
            {
                _isPageable.Set(node.Id, true);
                // Mark this as async, as this may result in async invocation.
                FlagPathAsAsync(node);

                // Pageable nodes are also stateful as data is always pulled from outside.
                SetStateful(node, isStateful: true);
            }
        }

        public void CheckAndMarkAsPageable(AsNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);

            if (_isPageable[node.Left.Id])
            {
                _isPageable.Set(node.Id, true);
                // Mark this as async, as this may result in async invocation.
                FlagPathAsAsync(node);

                // Pageable nodes are also stateful as data is always pulled from outside.
                SetStateful(node, isStateful: true);
            }
        }

        public void CheckAndMarkAsPageable(DottedNameNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);

            if (SupportsPaging(node))
            {
                _isPageable.Set(node.Id, true);
                // Mark this as async, as this may result in async invocation.
                FlagPathAsAsync(node);

                // Pageable nodes are also stateful as data is always pulled from outside.
                SetStateful(node, isStateful: true);
            }
        }

        public void CheckAndMarkAsDelegatable(DottedNameNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _typeMap.Length);

            if (SupportsPaging(node))
            {
                _isDelegatable.Set(node.Id, true);
                // Mark this as async, as this may result in async invocation.
                FlagPathAsAsync(node);

                // Pageable nodes are also stateful as data is always pulled from outside.
                SetStateful(node, isStateful: true);
            }
        }

        public bool IsDelegatable(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _isDelegatable.Length);

            return _isDelegatable.Get(node.Id);
        }

        public bool IsPageable(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _isPageable.Length);

            return _isPageable.Get(node.Id);
        }

        public bool HasSideEffects(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _hasSideEffects.Length);

            return _hasSideEffects.Get(node.Id);
        }

        public bool IsContextual(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _isContextual.Length);

            return _isContextual.Get(node.Id);
        }

        public bool IsConstant(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _isConstant.Length);

            return _isConstant.Get(node.Id);
        }

        public bool IsSelfContainedConstant(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _isSelfContainedConstant.Length);

            return _isSelfContainedConstant.Get(node.Id);
        }

        public bool TryGetConstantValue(TexlNode node, out string nodeValue)
        {
            Contracts.AssertValue(node);
            nodeValue = null;
            string left, right;
            switch (node.Kind)
            {
                case NodeKind.StrLit:
                    nodeValue = node.AsStrLit().Value;
                    return true;
                case NodeKind.BinaryOp:
                    BinaryOpNode binaryOpNode = node.AsBinaryOp();
                    if (binaryOpNode.Op == BinaryOp.Concat)
                    {
                        if (TryGetConstantValue(binaryOpNode.Left, out left) && TryGetConstantValue(binaryOpNode.Right, out right))
                        {
                            nodeValue = String.Concat(left, right);
                            return true;
                        }
                    }

                    break;
                case NodeKind.Call:
                    CallNode callNode = node.AsCall();
                    if (callNode.Head.Name.Value == BuiltinFunctionsCore.Concatenate.Name)
                    {
                        List<string> parameters = new List<string>();
                        foreach (var argNode in callNode.Args.Children)
                        {
                            string argValue;
                            if (TryGetConstantValue(argNode, out argValue))
                            {
                                parameters.Add(argValue);
                            }
                            else
                            {
                                break;
                            }
                        }

                        if (parameters.Count == callNode.Args.Count)
                        {
                            nodeValue = string.Join("", parameters);
                            return true;
                        }
                    }

                    break;
                case NodeKind.FirstName:
                    // Possibly a non-qualified enum value
                    FirstNameNode firstNameNode = node.AsFirstName();
                    FirstNameInfo firstNameInfo = this.GetInfo(firstNameNode);
                    if (firstNameInfo.Kind == BindKind.Enum)
                    {
                        var enumValue = firstNameInfo.Data as string;
                        if (enumValue != null)
                        {
                            nodeValue = enumValue;
                            return true;
                        }
                    }

                    break;
                case NodeKind.DottedName:
                    // Possibly an enumeration
                    DottedNameNode dottedNameNode = node.AsDottedName();
                    if (dottedNameNode.Left.Kind == NodeKind.FirstName)
                    {
                        DType enumType;
                        if (Document.GlobalScope.TryGetNamedEnum(dottedNameNode.Left.AsFirstName().Ident.Name, out enumType))
                        {
                            object enumValue;
                            if (enumType.TryGetEnumValue(dottedNameNode.Right.Name, out enumValue))
                            {
                                string strValue = enumValue as string;
                                if (strValue != null)
                                {
                                    nodeValue = strValue;
                                    return true;
                                }
                            }
                        }
                    }

                    break;
            }

            return false;
        }

        /// <summary>
        /// A node's "volatile variables" are the names whose values may at runtime have be modified at some
        /// point before the node to which these variables pertain is executed.
        ///
        /// e.g. <code>Set(w, 1); Set(x, w); Set(y, x); Set(z, y);</code>
        /// The call node Set(x, w); will have an entry in volatile variables containing just "w", Set(y, x); will
        /// have [w, x], and Set(z, y); will have [w, x, y].
        ///
        /// <see cref="TexlFunction.GetIdentifierOfModifiedValue"/> reports which variables may be
        /// changed by a call node, and they are recorded when the call node is analyzed and a reference to
        /// its TexlFunction is acquired. They are propagated to subsequent nodes in the variadic operator as
        /// the children of the variadic node are being accepted by the visitor.
        ///
        /// When the children of the variadic expression are visited, the volatile variables are transferred to the
        /// children's children, and so on and so forth, in a manner obeying that which is being commented.
        /// As the tree is descended, the visitor may encounter a first name node that will receive itself among
        /// the volatile variables of its parent. In such a case, neither this node nor any of its ancestors up to
        /// the root of the chained node may be lifted during code generation.
        ///
        /// The unliftability propagates back to the ancestors during the post visit traversal of the tree, and is
        /// ultimately read by the code generator when it visits these nodes and may attempt to lift their
        /// expressions.
        /// </summary>
        /// <param name="node">
        /// The node of which volatile variables are being requested
        /// </param>
        /// <returns>
        /// A list containing the volatile variables of <see cref="node"/>
        /// </returns>
        private ImmutableHashSet<string> GetVolatileVariables(TexlNode node)
        {
            Contracts.AssertValue(node);

            return _volatileVariables[node.Id] ?? ImmutableHashSet<string>.Empty;
        }

        public bool IsFullRecordRowScopeAccess(TexlNode node)
        {
            return TryGetFullRecordRowScopeAccessInfo(node, out _);
        }

        public bool TryGetFullRecordRowScopeAccessInfo(TexlNode node, out FirstNameInfo firstNameInfo)
        {
            Contracts.CheckValue(node, nameof(node));
            firstNameInfo = null;

            if (!(node is DottedNameNode dottedNameNode))
                return false;

            if (!(dottedNameNode.Left is FirstNameNode fullRecordAccess))
                return false;

            var info = GetInfo(fullRecordAccess);
            if (info?.Kind != BindKind.LambdaFullRecord)
                return false;

            firstNameInfo = info;
            return true;
        }

        /// <summary>
        /// Gets the renamed ident and returns true if the node is an AsNode
        /// Otherwise returns false and sets scopeIdent to the default
        /// </summary>
        /// <returns></returns>
        private bool GetScopeIdent(TexlNode node, out DName scopeIdent)
        {
            scopeIdent = ThisRecordDefaultName;
            if (node is AsNode asNode)
            {
                scopeIdent = GetInfo(asNode).AsIdentifier;
                return true;
            }
            return false;
        }

        public bool IsRowScope(TexlNode node)
        {
            Contracts.AssertValue(node);

            return GetScopeUseSet(node).IsLambdaScope;
        }

        private void SetEcsExcemptLambdaNode(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _isEcsExcemptLambda.Length);

            _isEcsExcemptLambda.Set(node.Id, true);
        }

        // Some lambdas don't need to be propagated to ECS (for example when used as filter predicates within Filter or LookUp)
        public bool IsInECSExcemptLambda(TexlNode node)
        {
            Contracts.AssertValue(node);

            if (node == null)
                return false;

            // No need to go further if node is outside row scope.
            if (!IsRowScope(node))
                return false;

            TexlNode parentNode = node;
            while ((parentNode = parentNode.Parent) != null)
            {
                if (_isEcsExcemptLambda.Get(parentNode.Id))
                    return true;
            }

            return false;
        }

        public bool IsStateful(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _isStateful.Length);

            return _isStateful.Get(node.Id);
        }

        public bool IsPure(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _isStateful.Length);
            Contracts.AssertIndex(node.Id, _hasSideEffects.Length);

            return !_isStateful.Get(node.Id) && !_hasSideEffects.Get(node.Id);
        }

        public bool IsGlobal(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _lambdaScopingMap.Length);

            return _lambdaScopingMap[node.Id].IsGlobalOnlyScope;
        }

        public bool IsLambdaScoped(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _lambdaScopingMap.Length);

            return _lambdaScopingMap[node.Id].IsLambdaScope;
        }

        public int GetInnermostLambdaScopeLevel(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _lambdaScopingMap.Length);

            return _lambdaScopingMap[node.Id].GetInnermost();
        }

        private void SetLambdaScopeLevel(TexlNode node, int upCount)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, IdLim);
            Contracts.Assert(IsGlobal(node) || upCount >= 0);

            // Ensure we don't exceed the supported up-count limit.
            if (upCount > ScopeUseSet.MaxUpCount)
                ErrorContainer.Error(node, TexlStrings.ErrTooManyUps);

            SetScopeUseSet(node, new ScopeUseSet(upCount));
        }

        private ScopeUseSet GetScopeUseSet(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, IdLim);

            return _lambdaScopingMap[node.Id];
        }

        private void SetScopeUseSet(TexlNode node, ScopeUseSet set)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, IdLim);
            Contracts.Assert(IsGlobal(node) || set.IsLambdaScope);

            _lambdaScopingMap[node.Id] = set;
        }

        private void SetSupportingRowScopedDelegationExemptionNode(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _supportsRowScopedParamDelegationExempted.Length);

            _supportsRowScopedParamDelegationExempted.Set(node.Id, true);
        }

        internal bool IsDelegationExempted(FirstNameNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _lambdaScopingMap.Length);

            if (node == null)
                return false;

            // No need to go further if the lambda scope is global.
            if (!IsLambdaScoped(node))
                return false;

            FirstNameInfo info;
            TryGetFirstNameInfo(node.Id, out info);
            int upCount = info.UpCount;
            TexlNode parentNode = node;
            while ((parentNode = parentNode.Parent) != null)
            {
                CallInfo callInfo;
                if (TryGetCall(parentNode.Id, out callInfo) && callInfo.Function != null && callInfo.Function.HasLambdas)
                {
                    upCount--;
                }

                if (upCount < 0)
                    return false;

                if (_supportsRowScopedParamDelegationExempted.Get(parentNode.Id) && upCount == 0)
                    return true;
            }

            return false;
        }

        internal void SetBlockScopedConstantNode(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, IdLim);

            _isBlockScopedConstant.Set(node.Id, true);
        }

        public bool IsBlockScopedConstant(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _isBlockScopedConstant.Length);

            return _isBlockScopedConstant.Get(node.Id);
        }

        public bool CanCoerce(TexlNode node)
        {
            Contracts.AssertValue(node);

            if (!TryGetCoercedType(node, out var toType))
            {
                return false;
            }

            DType fromType = GetType(node);
            Contracts.Assert(fromType.IsValid);
            Contracts.Assert(!toType.IsError);

            if (fromType.IsUniversal)
            {
                return false;
            }

            return true;
        }

        public bool TryGetCoercedType(TexlNode node, out DType coercedType)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _coerceMap.Length);

            coercedType = _coerceMap[node.Id];
            return coercedType.IsValid;
        }

        public void SetCoercedType(TexlNode node, DType type)
        {
            Contracts.AssertValue(node);
            Contracts.Assert(type.IsValid);
            Contracts.AssertIndex(node.Id, _coerceMap.Length);
            Contracts.Assert(!_coerceMap[node.Id].IsValid);

            _coerceMap[node.Id] = type;
        }

        public void SetCoercedToplevelType(DType type)
        {
            Contracts.Assert(type.IsValid);
            Contracts.Assert(!CoercedToplevelType.IsValid);

            CoercedToplevelType = type;
        }

        public FirstNameInfo GetInfo(FirstNameNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null || _infoMap[node.Id] is FirstNameInfo);

            return _infoMap[node.Id] as FirstNameInfo;
        }

        private BinderNodesVisitor _lazyInitializedBinderNodesVisitor = null;
        private BinderNodesVisitor _binderNodesVisitor
        {
            get
            {
                if (_lazyInitializedBinderNodesVisitor == null)
                {
                    _lazyInitializedBinderNodesVisitor = BinderNodesVisitor.Run(Top);
                }
                return _lazyInitializedBinderNodesVisitor;
            }
        }
        private BinderNodesMetadataArgTypeVisitor BinderNodeMetadataArgTypeVisitor { get; }

        public IEnumerable<BinaryOpNode> GetBinaryOperators() { return _binderNodesVisitor.BinaryOperators; }
        public IEnumerable<VariadicOpNode> GetVariadicOperators() { return _binderNodesVisitor.VariadicOperators; }
        public IEnumerable<NodeKind> GetKeywords() { return _binderNodesVisitor.Keywords; }
        public IEnumerable<BoolLitNode> GetBooleanLiterals() { return _binderNodesVisitor.BooleanLiterals; }
        public IEnumerable<NumLitNode> GetNumericLiterals() { return _binderNodesVisitor.NumericLiterals; }
        public IEnumerable<StrLitNode> GetStringLiterals() { return _binderNodesVisitor.StringLiterals; }
        public IEnumerable<UnaryOpNode> GetUnaryOperators() { return _binderNodesVisitor.UnaryOperators; }

        public bool IsEmpty => !_infoMap.Any(info => info != null);

        public IEnumerable<TexlNode> TopChain
        {
            get
            {
                if (IsEmpty) return Enumerable.Empty<TexlNode>();

                if (Top is VariadicBase)
                {
                    return (Top as VariadicBase).Children;
                }

                return new TexlNode[] { Top as TexlNode };
            }
        }

        public IEnumerable<FirstNameInfo> GetFirstNamesInTree(TexlNode node)
        {
            for (int id = 0; id < IdLim; id++)
            {
                FirstNameInfo info;
                if ((info = _infoMap[id] as FirstNameInfo) != null
                     && info.Node.InTree(node))
                    yield return info;
            }
        }

        public IEnumerable<FirstNameInfo> GetFirstNames()
        {
            return _infoMap.OfType<FirstNameInfo>();
        }

        public IEnumerable<FirstNameInfo> GetGlobalNames()
        {
            if (!UsesGlobals && !UsesResources)
                return Enumerable.Empty<FirstNameInfo>();

            return _infoMap
                .OfType<FirstNameInfo>()
                .Where(
                    info => info.Kind == BindKind.Control ||
                    info.Kind == BindKind.Data ||
                    info.Kind == BindKind.Resource ||
                    info.Kind == BindKind.NamedValue ||
                    info.Kind == BindKind.ComponentNameSpace ||
                    info.Kind == BindKind.WebResource ||
                    info.Kind == BindKind.QualifiedValue);
        }

        public IEnumerable<FirstNameInfo> GetGlobalControlNames()
        {
            if (!UsesGlobals)
                return Enumerable.Empty<FirstNameInfo>();

            return _infoMap
                .OfType<FirstNameInfo>()
                .Where(info => info.Kind == BindKind.Control);
        }

        public IEnumerable<ControlKeywordInfo> GetControlKeywordInfos()
        {
            if (!UsesGlobals)
                return Enumerable.Empty<ControlKeywordInfo>();

            return _infoMap.OfType<ControlKeywordInfo>();
        }

        public bool TryGetGlobalNameNode(string globalName, out TexlNode firstName)
        {
            Contracts.AssertNonEmpty(globalName);

            firstName = null;
            if (!UsesGlobals && !UsesResources)
                return false;

            foreach (var info in _infoMap.OfType<FirstNameInfo>())
            {
                var kind = info.Kind;
                if (info.Name.Value.Equals(globalName) &&
                    (kind == BindKind.Control || kind == BindKind.Data || kind == BindKind.Resource || kind == BindKind.QualifiedValue || kind == BindKind.WebResource))
                {
                    firstName = info.Node;
                    return true;
                }
            }

            return false;
        }

        public IEnumerable<FirstNameInfo> GetAliasNames()
        {
            if (!UsesAliases)
                return Enumerable.Empty<FirstNameInfo>();

            return _infoMap
                .OfType<FirstNameInfo>()
                .Where(info => info.Kind == BindKind.Alias);
        }

        public IEnumerable<FirstNameInfo> GetScopeVariableNames()
        {
            if (!UsesScopeVariables)
                return Enumerable.Empty<FirstNameInfo>();

            return _infoMap
                .OfType<FirstNameInfo>()
                .Where(info => info.Kind == BindKind.ScopeVariable);
        }

        public IEnumerable<FirstNameInfo> GetThisItemFirstNames()
        {
            if (!HasThisItemReference)
                return Enumerable.Empty<FirstNameInfo>();

            return _infoMap.OfType<FirstNameInfo>().Where(info => info.Kind == BindKind.ThisItem);
        }

        public IEnumerable<FirstNameInfo> GetImplicitThisItemFirstNames()
        {
            if (!HasThisItemReference)
                return Enumerable.Empty<FirstNameInfo>();

            return _infoMap.OfType<FirstNameInfo>().Where(info => info.Kind == BindKind.DeprecatedImplicitThisItem);
        }

        public IEnumerable<FirstNameInfo> GetLambdaParamNames(int nest)
        {
            Contracts.Assert(nest >= 0);
            IList<FirstNameInfo> infos;
            if (_lambdaParams.TryGetValue(nest, out infos))
                return infos;

            return Enumerable.Empty<FirstNameInfo>();
        }

        internal IEnumerable<DottedNameInfo> GetDottedNamesInTree(TexlNode node)
        {
            for (int id = 0; id < IdLim; id++)
            {
                DottedNameInfo info;
                if ((info = _infoMap[id] as DottedNameInfo) != null
                    && info.Node.InTree(node))
                    yield return info;
            }
        }

        public IEnumerable<DottedNameInfo> GetDottedNames()
        {
            for (int id = 0; id < IdLim; id++)
            {
                DottedNameInfo info;
                if ((info = _infoMap[id] as DottedNameInfo) != null)
                    yield return info;
            }
        }

        internal IEnumerable<CallInfo> GetCallsInTree(TexlNode node)
        {
            for (int id = 0; id < IdLim; id++)
            {
                CallInfo info;
                if ((info = _infoMap[id] as CallInfo) != null
                    && info.Node.InTree(node))
                    yield return info;
            }
        }

        public IEnumerable<CallInfo> GetCalls()
        {
            for (int id = 0; id < IdLim; id++)
            {
                CallInfo info;
                if ((info = _infoMap[id] as CallInfo) != null)
                    yield return info;
            }
        }

        public IEnumerable<CallInfo> GetCalls(TexlFunction function)
        {
            Contracts.AssertValue(function);

            for (int id = 0; id < IdLim; id++)
            {
                CallInfo info;
                if ((info = _infoMap[id] as CallInfo) != null && info.Function == function)
                    yield return info;
            }
        }

        public bool TryGetCall(int nodeId, out CallInfo callInfo)
        {
            Contracts.AssertIndex(nodeId, IdLim);

            callInfo = _infoMap[nodeId] as CallInfo;
            return callInfo != null;
        }

        // Try to get the text span from a give nodeId
        // The node could be CallInfo, FirstNameInfo or DottedNameInfo
        public bool TryGetTextSpan(int nodeId, out Span span)
        {
            Contracts.AssertIndex(nodeId, IdLim);

            var node = _infoMap[nodeId];
            CallInfo callInfo = node as CallInfo;
            if (callInfo != null)
            {
                span = callInfo.Node.GetTextSpan();
                return true;
            }

            FirstNameInfo firstNameInfo = node as FirstNameInfo;
            if (firstNameInfo != null)
            {
                span = firstNameInfo.Node.GetTextSpan();
                return true;
            }

            DottedNameInfo dottedNameInfo = node as DottedNameInfo;
            if (dottedNameInfo != null)
            {
                span = dottedNameInfo.Node.GetTextSpan();
                return true;
            }

            span = null;
            return false;
        }

        public bool TryGetFirstNameInfo(int nodeId, out FirstNameInfo info)
        {
            if (nodeId < 0)
            {
                info = null;
                return false;
            }

            Contracts.AssertIndex(nodeId, IdLim);

            info = _infoMap[nodeId] as FirstNameInfo;
            return info != null;
        }

        public bool TryGetInfo<T>(int nodeId, out T info) where T : class
        {
            if (nodeId < 0 || nodeId > IdLim)
            {
                info = null;
                return false;
            }

            info = _infoMap[nodeId] as T;
            return info != null;
        }

        // Returns all scope fields consumed by this rule that match the given scope type.
        // This is always a subset of the scope type.
        // Returns DType.EmptyRecord if no scope fields are consumed by the rule.
        public DType GetTopUsedScopeFields(DName sourceControlName, DName outputTablePropertyName)
        {
            Contracts.AssertValid(sourceControlName);
            Contracts.AssertValid(outputTablePropertyName);

            // Begin with an empty record until we find an access to the specified output table.
            DType accumulatedType = DType.EmptyRecord;

            // Identify all accesses to the specified output table in this rule.
            IEnumerable<DottedNameInfo> sourceTableAccesses = GetDottedNames().Where(d => d.Node.Matches(sourceControlName, outputTablePropertyName));

            foreach (DottedNameInfo sourceTableAccess in sourceTableAccesses)
            {
                // Start with the type of the table access.
                DType currentRecordType = GetType(sourceTableAccess.Node).ToRecord();

                TexlNode node = sourceTableAccess.Node;

                // Reduce the type if the table is being sliced.
                if (node.Parent != null && node.Parent.Kind == NodeKind.DottedName)
                    currentRecordType = GetType(node.Parent).ToRecord();

                // Walk up the parse tree to find the first CallNode, then determine if the
                // required type can be reduced to scope fields.
                for (; node.Parent != null && node.Parent.Parent != null; node = node.Parent)
                {
                    if (node.Parent.Parent.Kind == NodeKind.Call)
                    {
                        CallInfo callInfo = GetInfo(node.Parent.Parent as CallNode);

                        if (callInfo.Function.ScopeInfo != null)
                        {
                            var scopeFunction = callInfo.Function;

                            Contracts.Assert(callInfo.Node.Args.Children.Length > 0);
                            TexlNode firstArg = callInfo.Node.Args.Children[0];

                            // Determine if we arrived as the first (scope) argument of the function call
                            // and whether we can reduce the type to contain only the used scope fields
                            // for the call.
                            if (firstArg == node && !scopeFunction.ScopeInfo.UsesAllFieldsInScope)
                            {
                                // The cursor type must be the same as the current type.
                                Contracts.Assert(currentRecordType.Accepts(callInfo.CursorType));
                                currentRecordType = GetUsedScopeFields(callInfo);
                            }
                        }

                        // Always break if we have reached a CallNode.
                        break;
                    }
                }

                // Accumulate the current type.
                accumulatedType = DType.Union(accumulatedType, currentRecordType);
            }

            return accumulatedType;
        }

        // Returns the scope fields used by the lambda parameters in the given invocation.
        // This is always a subset of the scope type (call.CursorType).
        // Returns DType.Error for anything other than invocations of functions with scope.
        public DType GetUsedScopeFields(CallInfo call)
        {
            Contracts.AssertValue(call);

            if (ErrorContainer.HasErrors() ||
                call.Function == null ||
                call.Function.ScopeInfo == null ||
                !call.CursorType.IsAggregate ||
                call.Node.Args.Count < 1)
            {
                return DType.Error;
            }

            DType fields = DType.EmptyRecord;
            TexlNode arg0 = call.Node.Args.Children[0].VerifyValue();

            foreach (var name in GetLambdaParamNames(call.ScopeNest + 1))
            {
                DType lambdaParamType;
                bool fError = false;
                if (!name.Node.InTree(arg0) &&
                    name.Node.InTree(call.Node) &&
                    call.CursorType.TryGetType(name.Name, out lambdaParamType))
                {
                    DottedNameNode dotted;
                    if ((dotted = name.Node.Parent as DottedNameNode) != null)
                    {
                        DType accParamType, propertyType;
                        // Get the param type accumulated so far
                        if (!fields.TryGetType(name.Name, out accParamType))
                            accParamType = DType.EmptyRecord;
                        // Get the RHS property type reported by the scope
                        DType tempRhsType = lambdaParamType.IsControl ? lambdaParamType.ToRecord() : lambdaParamType;
                        if (!tempRhsType.TryGetType(dotted.Right.Name, out propertyType))
                            propertyType = DType.Unknown;
                        // Accumulate into the param type
                        accParamType = accParamType.Add(ref fError, DPath.Root, dotted.Right.Name, propertyType);
                        lambdaParamType = accParamType;
                    }
                    fields = DType.Union(fields, DType.EmptyRecord.Add(ref fError, DPath.Root, name.Name, lambdaParamType));
                }
            }

            Contracts.Assert(fields.IsRecord);
            return fields;
        }

        private void SetInfo(FirstNameNode node, FirstNameInfo info)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(info);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null);

            if (info.Kind == BindKind.LambdaField || info.Kind == BindKind.LambdaFullRecord)
            {
                if (!_lambdaParams.ContainsKey(info.NestDst))
                    _lambdaParams[info.NestDst] = new List<FirstNameInfo>();

                _lambdaParams[info.NestDst].Add(info);
            }

            _infoMap[node.Id] = info;
        }

        public DottedNameInfo GetInfo(DottedNameNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null || _infoMap[node.Id] is DottedNameInfo);

            return _infoMap[node.Id] as DottedNameInfo;
        }

        private void SetInfo(DottedNameNode node, DottedNameInfo info)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(info);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null);

            _infoMap[node.Id] = info;
        }



        public AsInfo GetInfo(AsNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null || _infoMap[node.Id] is AsInfo);

            return _infoMap[node.Id] as AsInfo;
        }

        private void SetInfo(AsNode node, AsInfo info)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(info);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null);

            _infoMap[node.Id] = info;
        }

        public CallInfo GetInfo(CallNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null || _infoMap[node.Id] is CallInfo);

            return _infoMap[node.Id] as CallInfo;
        }

        private void SetInfo(CallNode node, CallInfo info, bool markIfAsync = true)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(info);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null);

            _infoMap[node.Id] = info;

            var function = info.Function;
            if (function != null)
            {
                // If the invocation is async then the whole call path is async.
                if (markIfAsync && function.IsAsyncInvocation(node, this))
                    FlagPathAsAsync(node);

                // If the invocation affects aliases, cache that info.
                if (function.AffectsAliases)
                    AffectsAliases = true;

                // If the invocation affects scope varialbe, cache that info.
                if (function.AffectsScopeVariable)
                    AffectsScopeVariable = true;

                if (function.AffectsDataSourceQueryOptions)
                    AffectsTabularDataSources = true;
            }
        }

        internal bool AddFieldToQuerySelects(DType type, string fieldName)
        {
            Contracts.AssertValid(type);
            Contracts.AssertNonEmpty(fieldName);
            Contracts.AssertValue(QueryOptions);

            var retVal = false;

            if (type.AssociatedDataSources == null)
                return retVal;

            foreach (var associatedDataSource in type.AssociatedDataSources)
            {
                if (!associatedDataSource.IsSelectable) continue;

                // If this is accessing datasource itself then we don't need to capture this.
                if (associatedDataSource.Name == fieldName)
                    continue;

                retVal |= QueryOptions.AddSelect(associatedDataSource, new DName(fieldName));

                AffectsTabularDataSources = true;
            }

            return retVal;
        }

        internal DName GetFieldLogicalName(Identifier ident)
        {
            DName rhsName = ident.Name;
            if (!UpdateDisplayNames && TryGetReplacedIdentName(ident, out var rhsLogicalName))
                rhsName = new DName(rhsLogicalName);

            return rhsName;
        }

        internal bool TryGetReplacedIdentName(Identifier ident, out string replacedIdent)
        {
            replacedIdent = string.Empty;

            // Check if the access was renamed:
            if (NodesToReplace != null)
            {
                // Token equality doesn't work here, compare the spans to be certain
                var newName = NodesToReplace.Where(kvp => kvp.Key.Span.Min == ident.Token.Span.Min && kvp.Key.Span.Lim == ident.Token.Span.Lim).FirstOrDefault();
                if (newName.Value != null && newName.Key != null)
                {
                    replacedIdent = newName.Value;
                    return true;
                }
            }

            return false;
        }

        public ParentInfo GetInfo(ParentNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null || _infoMap[node.Id] is ParentInfo);

            return _infoMap[node.Id] as ParentInfo;
        }

        public SelfInfo GetInfo(SelfNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null || _infoMap[node.Id] is SelfInfo);

            return _infoMap[node.Id] as SelfInfo;
        }

        private void SetInfo(ParentNode node, ParentInfo info)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(info);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null);

            _infoMap[node.Id] = info;
        }

        private void SetInfo(SelfNode node, SelfInfo info)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(info);
            Contracts.AssertIndex(node.Id, _infoMap.Length);
            Contracts.Assert(_infoMap[node.Id] == null);

            _infoMap[node.Id] = info;
        }

        private void FlagPathAsAsync(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _asyncMap.Length);

            while (node != null && !_asyncMap[node.Id])
            {
                _asyncMap[node.Id] = true;
                node = node.Parent;
            }
        }

        public bool IsAsync(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _asyncMap.Length);

            return _asyncMap[node.Id];
        }

        /// <summary>
        /// See documentation for <see cref="GetVolatileVariables"/> for more information.
        /// </summary>
        /// <param name="node">
        /// Node whose liftability is questioned.
        /// </param>
        /// <returns>
        /// Whether the current node is liftable.
        /// </returns>
        public bool IsUnliftable(TexlNode node)
        {
            Contracts.AssertValue(node);
            Contracts.AssertIndex(node.Id, _isUnliftable.Length);

            return _isUnliftable[node.Id];
        }

        public bool TryCastToFirstName(TexlNode node, out FirstNameInfo firstNameInfo)
        {
            Contracts.AssertValue(node);

            firstNameInfo = null;

            FirstNameNode firstNameNode;
            return (firstNameNode = node.AsFirstName()) != null &&
                (firstNameInfo = GetInfo(firstNameNode)) != null;
        }

        internal void DeclareMetadataNeeded(DType type)
        {
            Contracts.AssertValid(type);

            if (_typesNeedingMetadata == null)
                _typesNeedingMetadata = new List<DType>();

            if (!_typesNeedingMetadata.Contains(type))
                _typesNeedingMetadata.Add(type);
        }

        internal List<DType> GetExpandEntitiesMissingMetadata()
        {
            return _typesNeedingMetadata;
        }

        internal bool TryGetRenamedOutput(out DName outputName)
        {
            outputName = _renamedOutputAccessor;
            return outputName != default;
        }

        public bool IsAsyncWithNoSideEffects(TexlNode node)
        {
            return IsAsync(node) && !HasSideEffects(node);
        }

        private class Visitor : TexlVisitor
        {
            private sealed class Scope
            {
                public readonly CallNode Call;
                public readonly int Nest;
                public readonly Scope Parent;
                public readonly DType Type;
                public readonly bool CreatesRowScope;
                public readonly bool SkipForInlineRecords;
                public readonly DName ScopeIdentifier;
                public readonly bool RequireScopeIdentifier;

                // Optional data associated with scope. May be null.
                public readonly object Data;

                public Scope(DType type)
                {
                    Contracts.Assert(type.IsValid);
                    Type = type;
                }

                public Scope(CallNode call, Scope parent, DType type, DName scopeIdentifier = default, bool requireScopeIdentifier = false, object data = null, bool createsRowScope = true, bool skipForInlineRecords = false)
                {
                    Contracts.Assert(type.IsValid);
                    Contracts.AssertValueOrNull(data);

                    Call = call;
                    Parent = parent;
                    Type = type;
                    Data = data;
                    CreatesRowScope = createsRowScope;
                    SkipForInlineRecords = skipForInlineRecords;
                    ScopeIdentifier = scopeIdentifier;
                    RequireScopeIdentifier = requireScopeIdentifier;

                    Nest = parent?.Nest ?? 0;
                    // Scopes created for record scope only do not increase lambda param nesting
                    if (createsRowScope)
                        Nest += 1;
                }

                public Scope Up(int upCount)
                {
                    Contracts.AssertIndex(upCount, Nest);

                    Scope scope = this;
                    while (upCount-- > 0)
                    {
                        scope = scope.Parent;
                        Contracts.AssertValue(scope);
                    }

                    return scope;
                }
            }

            private readonly INameResolver _nameResolver;
            private readonly Scope _topScope;
            private TexlBinding _txb;
            private Scope _currentScope;
            private int _currentScopeDsNodeId;

            public Visitor(TexlBinding txb, INameResolver resolver, DType topScope, bool useThisRecordForRuleScope)
            {
                Contracts.AssertValue(txb);
                Contracts.AssertValueOrNull(resolver);

                _txb = txb;
                _nameResolver = resolver;

                _topScope = new Scope(null, null, topScope ?? DType.Error, useThisRecordForRuleScope ? txb.ThisRecordDefaultName : default);
                _currentScope = _topScope;
                _currentScopeDsNodeId = -1;
            }

            [Conditional("DEBUG")]
            private void AssertValid()
            {
#if DEBUG
                Contracts.AssertValueOrNull(_nameResolver);
                Contracts.AssertValue(_topScope);
                Contracts.AssertValue(_currentScope);

                Scope scope = _currentScope;
                while (scope != null && scope != _topScope)
                    scope = scope.Parent;
                Contracts.Assert(scope == _topScope, "_topScope should be in the parent chain of _currentScope.");
#endif
            }

            public void Run()
            {
                _txb.Top.Accept(this);
                Contracts.Assert(_currentScope == _topScope);
            }

            /// <summary>
            /// Helper for Lt/leq/geq/gt type checking. Restricts type to be one of the provided set, without coercion (except for primary output props).
            /// </summary>
            /// <param name="node">Node for which we are checking the type</param>
            /// <param name="alternateTypes">List of acceptable types for this operation, in order of suitability</param>
            /// <returns></returns>
            private bool CheckComparisonTypeOneOf(TexlNode node, params DType[] alternateTypes)
            {
                Contracts.AssertValue(node);
                Contracts.AssertValue(alternateTypes);
                Contracts.Assert(alternateTypes.Any());

                DType type = _txb.GetType(node);
                foreach (var altType in alternateTypes)
                {
                    if (!altType.Accepts(type))
                        continue;

                    return true;
                }

                // If the node is a control, we may be able to coerce its primary output property
                // to the desired type, and in the process support simplified syntax such as: slider2 <= slider4
                IExternalControlProperty primaryOutProp;
                if (type is IExternalControlType controlType && node.AsFirstName() != null && (primaryOutProp = controlType.ControlTemplate.PrimaryOutputProperty) != null)
                {
                    DType outType = primaryOutProp.GetOpaqueType();
                    var acceptedType = alternateTypes.FirstOrDefault(alt => alt.Accepts(outType));
                    if (acceptedType != default)
                    {
                        // We'll coerce the control to the desired type, by pulling from the control's
                        // primary output property. See codegen for details.
                        _txb.SetCoercedType(node, acceptedType);
                        return true;
                    }
                }

                _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, node, TexlStrings.ErrBadType_ExpectedTypesCSV, string.Join(", ", alternateTypes.Select(t => t.GetKindString())));
                return false;

            }


            // Returns whether the node was of the type wanted, and reports appropriate errors.
            // A list of allowed alternate types specifies what other types of values can be coerced to the wanted type.
            private bool CheckType(TexlNode node, DType typeWant, params DType[] alternateTypes)
            {
                Contracts.AssertValue(node);
                Contracts.Assert(typeWant.IsValid);
                Contracts.Assert(!typeWant.IsError);
                Contracts.AssertValue(alternateTypes);

                DType type = _txb.GetType(node);
                if (typeWant.Accepts(type))
                {
                    if (type.RequiresExplicitCast(typeWant))
                        _txb.SetCoercedType(node, typeWant);
                    return true;
                }

                // Normal (non-control) coercion
                foreach (var altType in alternateTypes)
                {
                    if (!altType.Accepts(type))
                        continue;

                    // Ensure that booleans only match bool valued option sets
                    if (typeWant.Kind == DKind.Boolean && altType.Kind == DKind.OptionSetValue && !(type.OptionSetInfo?.IsBooleanValued ?? false))
                        continue;

                    // We found an alternate type that is accepted and will be coerced.
                    _txb.SetCoercedType(node, typeWant);
                    return true;
                }

                // If the node is a control, we may be able to coerce its primary output property
                // to the desired type, and in the process support simplified syntax such as: label1 + slider4
                IExternalControlProperty primaryOutProp;
                if (type is IExternalControlType controlType && node.AsFirstName() != null && (primaryOutProp = controlType.ControlTemplate.PrimaryOutputProperty) != null)
                {
                    DType outType = primaryOutProp.GetOpaqueType();
                    if (typeWant.Accepts(outType) || alternateTypes.Any(alt => alt.Accepts(outType)))
                    {
                        // We'll "coerce" the control to the desired type, by pulling from the control's
                        // primary output property. See codegen for details.
                        _txb.SetCoercedType(node, typeWant);
                        return true;
                    }
                }

                ErrorResourceKey messageKey = alternateTypes.Length == 0 ? TexlStrings.ErrBadType_ExpectedType : TexlStrings.ErrBadType_ExpectedTypesCSV;
                string messageArg = alternateTypes.Length == 0 ? typeWant.GetKindString() : string.Join(", ", (new[] { typeWant }).Concat(alternateTypes).Select(t => t.GetKindString()));

                _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, node, messageKey, messageArg);
                return false;
            }

            // Performs type checking for the arguments passed to the membership "in"/"exactin" operators.
            private bool CheckInArgTypes(TexlNode left, TexlNode right)
            {
                Contracts.AssertValue(left);
                Contracts.AssertValue(right);

                DType typeLeft = _txb.GetType(left);
                if (!typeLeft.IsValid || typeLeft.IsUnknown || typeLeft.IsError)
                {
                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, left, TexlStrings.ErrTypeError);
                    return false;
                }

                DType typeRight = _txb.GetType(right);
                if (!typeRight.IsValid || typeRight.IsUnknown || typeRight.IsError)
                {
                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, right, TexlStrings.ErrTypeError);
                    return false;
                }

                Contracts.Assert(!typeLeft.IsAggregate || typeLeft.IsTable || typeLeft.IsRecord);
                Contracts.Assert(!typeRight.IsAggregate || typeRight.IsTable || typeRight.IsRecord);

                if (!typeLeft.IsAggregate)
                {
                    // scalar in scalar: RHS must be a string (or coercible to string when LHS type is string). We'll allow coercion of LHS.
                    // This case deals with substring matches, e.g. 'FirstName in "Aldous Huxley"' or "123" in 123.
                    if (!typeRight.IsAggregate)
                    {
                        if (!DType.String.Accepts(typeRight))
                        {
                            if (typeRight.CoercesTo(DType.String) && DType.String.Accepts(typeLeft))
                            {
                                // Coerce RHS to a string type.
                                _txb.SetCoercedType(right, DType.String);
                            }
                            else
                            {
                                _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, right, TexlStrings.ErrStringExpected);
                                return false;
                            }
                        }
                        if (DType.String.Accepts(typeLeft))
                            return true;
                        if (!typeLeft.CoercesTo(DType.String))
                        {
                            _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, left, TexlStrings.ErrCannotCoerce_SourceType_TargetType, typeLeft.GetKindString(), DType.String.GetKindString());
                            return false;
                        }
                        // Coerce LHS to a string type, to facilitate subsequent substring checks.
                        _txb.SetCoercedType(left, DType.String);
                        return true;
                    }

                    // scalar in table: RHS must be a one column table. We'll allow coercion.
                    if (typeRight.IsTable)
                    {
                        var names = typeRight.GetNames(DPath.Root);
                        if (names.Count() != 1)
                        {
                            _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, right, TexlStrings.ErrInvalidSchemaNeedCol);
                            return false;
                        }

                        TypedName typedName = names.Single();
                        if (typedName.Type.Accepts(typeLeft) || typeLeft.Accepts(typedName.Type))
                            return true;
                        if (!typeLeft.CoercesTo(typedName.Type))
                        {
                            _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, left, TexlStrings.ErrCannotCoerce_SourceType_TargetType, typeLeft.GetKindString(), typedName.Type.GetKindString());
                            return false;
                        }
                        // Coerce LHS to the table column type, to facilitate subsequent comparison.
                        _txb.SetCoercedType(left, typedName.Type);
                        return true;
                    }

                    // scalar in record: not supported. Flag an error on the RHS.
                    Contracts.Assert(typeRight.IsRecord);
                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, right, TexlStrings.ErrBadType_Type, typeRight.GetKindString());
                    return false;
                }

                if (typeLeft.IsRecord)
                {
                    // record in scalar: not supported
                    if (!typeRight.IsAggregate)
                    {
                        _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, right, TexlStrings.ErrBadType_Type, typeRight.GetKindString());
                        return false;
                    }

                    // record in table: RHS must be a table with a compatible schema. No coercion is allowed.
                    if (typeRight.IsTable)
                    {
                        DType typeLeftAsTable = typeLeft.ToTable();

                        if (typeLeftAsTable.Accepts(typeRight, out var typeRightDifferingSchema, out var typeRightDifferingSchemaType) ||
                            typeRight.Accepts(typeLeftAsTable, out var typeLeftDifferingSchema, out var typeLeftDifferingSchemaType))
                            return true;

                        _txb.ErrorContainer.Errors(left, typeLeft, typeLeftDifferingSchema, typeLeftDifferingSchemaType);
                        _txb.ErrorContainer.Errors(right, typeRight, typeRightDifferingSchema, typeRightDifferingSchemaType);

                        return false;
                    }

                    // record in record: not supported. Flag an error on the RHS.
                    Contracts.Assert(typeRight.IsRecord);
                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, right, TexlStrings.ErrBadType_Type, typeRight.GetKindString());
                    return false;
                }

                // table in anything: not supported
                _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, left, TexlStrings.ErrBadType_Type, typeLeft.GetKindString());
                return false;
            }

            private ScopeUseSet JoinScopeUseSets(params TexlNode[] nodes)
            {
                Contracts.AssertValue(nodes);
                Contracts.AssertAllValues(nodes);

                ScopeUseSet set = ScopeUseSet.GlobalsOnly;
                foreach (var node in nodes)
                    set = set.Union(_txb.GetScopeUseSet(node));

                return set;
            }

            public override void Visit(ReplaceableNode node)
            {
                throw new NotSupportedException("Replaceable nodes are not supported");
            }

            public override void Visit(ErrorNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                _txb.SetType(node, DType.Error);

                // Note that there is no need to log a binding error for this node. The fact that
                // an ErrorNode exists in the parse tree ensures that a parse/syntax error was
                // logged for it, and there is no need to duplicate it.
            }

            public override void Visit(BlankNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                _txb.SetConstant(node, true);
                _txb.SetSelfContainedConstant(node, true);
                _txb.SetType(node, DType.ObjNull);
            }

            public override void Visit(BoolLitNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                _txb.SetConstant(node, true);
                _txb.SetSelfContainedConstant(node, true);
                _txb.SetType(node, DType.Boolean);
            }

            public override void Visit(StrLitNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                _txb.SetConstant(node, true);
                _txb.SetSelfContainedConstant(node, true);
                _txb.SetType(node, DType.String);

                // For Data Table Scenario Only
                if (_txb.Property != null && _txb.Property.UseForDataQuerySelects)
                {
                    // Lookup ThisItem info
                    NameLookupInfo lookupInfo = default(NameLookupInfo);
                    if (_nameResolver == null || !_nameResolver.TryGetInnermostThisItemScope(out lookupInfo))
                        return;

                    _txb.AddFieldToQuerySelects(lookupInfo.Type, node.Value);
                }
            }

            public override void Visit(NumLitNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                _txb.SetConstant(node, true);
                _txb.SetSelfContainedConstant(node, true);
                _txb.SetType(node, DType.Number);
            }

            public DName GetLogicalNodeNameAndUpdateDisplayNames(DType type, Identifier ident, bool isThisItem = false)
            {
                string unused;
                return GetLogicalNodeNameAndUpdateDisplayNames(type, ident, out unused, isThisItem);
            }

            public DName GetLogicalNodeNameAndUpdateDisplayNames(DType type, Identifier ident, out string newDisplayName, bool isThisItem = false)
            {
                Contracts.AssertValid(type);
                Contracts.AssertValue(ident);

                DName logicalNodeName = ident.Name;
                newDisplayName = logicalNodeName.Value;

                if (type == DType.Invalid || (!type.IsOptionSet && !type.IsView && type.AssociatedDataSources == default))
                    return logicalNodeName;

                // Skip trying to match display names if the type isn't associated with a data source, an option set or view
                if (!type.AssociatedDataSources.Any() && !type.IsOptionSet && !type.IsView && !type.HasExpandInfo)
                    return logicalNodeName;

                bool useUpdatedDisplayNames = (type.AssociatedDataSources.FirstOrDefault()?.IsConvertingDisplayNameMapping ?? false) || (type.OptionSetInfo?.IsConvertingDisplayNameMapping ?? false) || (type.ViewInfo?.IsConvertingDisplayNameMapping ?? false) || _txb._forceUpdateDisplayNames;
                var updatedDisplayNamesType = type;

                if (!useUpdatedDisplayNames && type.HasExpandInfo && type.ExpandInfo.ParentDataSource.Kind == DataSourceKind.CdsNative)
                {
                    if (_txb.Document.GlobalScope.TryGetCdsDataSourceWithLogicalName(((IExternalCdsDataSource)type.ExpandInfo.ParentDataSource).DatasetName, type.ExpandInfo.Identity, out var relatedDataSource) &&
                        relatedDataSource.IsConvertingDisplayNameMapping)
                    {
                        useUpdatedDisplayNames = true;
                        updatedDisplayNamesType = relatedDataSource.Schema;
                    }
                }

                if (_txb.UpdateDisplayNames && useUpdatedDisplayNames)
                {
                    // Either we need to go Display Name -> Display Name here
                    // Or we need to go Logical Name -> Display Name
                    string maybeDisplayName, maybeLogicalName;
                    if (DType.TryGetConvertedDisplayNameAndLogicalNameForColumn(updatedDisplayNamesType, ident.Name.Value, out maybeLogicalName, out maybeDisplayName))
                    {
                        logicalNodeName = new DName(maybeLogicalName);
                        _txb.NodesToReplace.Add(new KeyValuePair<Token, string>(ident.Token, maybeDisplayName));
                    }
                    else if (DType.TryGetDisplayNameForColumn(updatedDisplayNamesType, ident.Name.Value, out maybeDisplayName))
                    {
                        _txb.NodesToReplace.Add(new KeyValuePair<Token, string>(ident.Token, maybeDisplayName));
                    }

                    if (maybeDisplayName != null)
                        newDisplayName = new DName(maybeDisplayName);
                }
                else
                {
                    string maybeLogicalName;
                    if (DType.TryGetLogicalNameForColumn(updatedDisplayNamesType, ident.Name.Value, out maybeLogicalName, isThisItem))
                    {
                        logicalNodeName = new DName(maybeLogicalName);
                        _txb.NodesToReplace.Add(new KeyValuePair<Token, string>(ident.Token, maybeLogicalName));
                    }
                }

                return logicalNodeName;
            }

            public override void Visit(FirstNameNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                FirstNameInfo info;
                bool haveNameResolver = _nameResolver != null;

                // Reset name lookup preferences.
                NameLookupPreferences lookupPrefs = NameLookupPreferences.None;
                DName nodeName = node.Ident.Name;
                bool fError = false;

                // If node is a global variable but it appears in its own weight table, we know its state has changed
                // in a "younger" sibling or cousin node, vis. some predecessor statement in a chained operation
                // changed the value of this variable, and we must ensure that it is not lifted by the back end.
                // e.g. With({}, Set(x, 1); Set(y, x + 1)) -- we need to indicate that "x + 1" cannot be cached and
                // expect to retain the same value throughout the chained operator's scope.
                if (_txb.GetVolatileVariables(node).Contains(node.Ident.Name))
                    _txb.SetIsUnliftable(node, true);

                // [@name]
                if (node.Ident.AtToken != null)
                {
                    if (haveNameResolver)
                        lookupPrefs |= NameLookupPreferences.GlobalsOnly;
                }
                // name[@field]
                else if (IsRowScopeAlias(node, out Scope scope))
                {
                    Contracts.Assert(scope.Type.IsRecord);

                    info = FirstNameInfo.Create(BindKind.LambdaFullRecord, node, scope.Nest, _currentScope.Nest, scope.Data);
                    Contracts.Assert(info.Kind == BindKind.LambdaFullRecord);

                    nodeName = GetLogicalNodeNameAndUpdateDisplayNames(scope.Type, node.Ident);

                    if (scope.Nest < _currentScope.Nest)
                        _txb.SetBlockScopedConstantNode(node);
                    _txb.SetType(node, scope.Type);
                    _txb.SetInfo(node, info);
                    _txb.SetLambdaScopeLevel(node, info.UpCount);
                    _txb.AddFieldToQuerySelects(scope.Type, nodeName);
                    return;
                }
                // fieldName (unqualified)
                else if (IsRowScopeField(node, out scope, out fError, out var isWholeScope))
                {
                    Contracts.Assert(scope.Type.IsRecord);

                    // Detected access to a pageable dataEntity in row scope, error was set
                    if (fError)
                        return;

                    DType nodeType = scope.Type;

                    if (!isWholeScope)
                    {
                        info = FirstNameInfo.Create(BindKind.LambdaField, node, scope.Nest, _currentScope.Nest, scope.Data);
                        nodeName = GetLogicalNodeNameAndUpdateDisplayNames(scope.Type, node.Ident);
                        nodeType = scope.Type.GetType(nodeName);
                    }
                    else
                    {
                        info = FirstNameInfo.Create(BindKind.LambdaFullRecord, node, scope.Nest, _currentScope.Nest, scope.Data);
                        if (scope.Nest < _currentScope.Nest)
                            _txb.SetBlockScopedConstantNode(node);
                    }

                    Contracts.Assert(info.UpCount >= 0);

                    _txb.SetType(node, nodeType);
                    _txb.SetInfo(node, info);
                    _txb.SetLambdaScopeLevel(node, info.UpCount);
                    _txb.AddFieldToQuerySelects(nodeType, nodeName);
                    return;
                }

                // Look up a global variable with this name.
                NameLookupInfo lookupInfo = default;
                if (_txb.AffectsScopeVariableName)
                {
                    if (haveNameResolver && _nameResolver.CurrentEntity != null)
                    {
                        IExternalControl scopedControl = _txb._glue.GetVariableScopedControlFromTexlBinding(_txb);
                        // App variable name cannot conflict with any existing global entity name, eg. control/data/table/enum.
                        if (scopedControl.IsAppInfoControl && _nameResolver.LookupGlobalEntity(node.Ident.Name, out lookupInfo))
                        {
                            _txb.ErrorContainer.Error(node, TexlStrings.ErrExpectedFound_Ex_Fnd, TokKind.Ident, lookupInfo.Kind);
                        }

                        _txb.SetAppScopedVariable(node, scopedControl.IsAppInfoControl);
                    }

                    // Set the variable name node as DType.String.
                    _txb.SetType(node, DType.String);
                    _txb.SetInfo(node, FirstNameInfo.Create(node, default(NameLookupInfo)));
                    return;
                }

                if (node.Parent is DottedNameNode)
                    lookupPrefs |= NameLookupPreferences.HasDottedNameParent;

                // Check if this control property has local scope name resolver.
                var localScopeNameResolver = _txb.LocalRuleScopeResolver;
                if (localScopeNameResolver != null && localScopeNameResolver.Lookup(node.Ident.Name, out ScopedNameLookupInfo scopedInfo))
                {
                    _txb.SetType(node, scopedInfo.Type);
                    _txb.SetInfo(node, FirstNameInfo.Create(node, scopedInfo));
                    _txb.SetStateful(node, scopedInfo.IsStateful);
                    _txb._hasLocalReferences = true;
                    return;
                }

                if (!haveNameResolver || !_nameResolver.Lookup(node.Ident.Name, out lookupInfo, preferences: lookupPrefs))
                {
                    _txb.ErrorContainer.Error(node, TexlStrings.ErrInvalidName);
                    _txb.SetType(node, DType.Error);
                    _txb.SetInfo(node, FirstNameInfo.Create(node, default(NameLookupInfo)));
                    return;
                }

                Contracts.Assert(lookupInfo.Kind != BindKind.LambdaField);
                Contracts.Assert(lookupInfo.Kind != BindKind.LambdaFullRecord);
                Contracts.Assert(lookupInfo.Kind != BindKind.Unknown);

                FirstNameInfo fnInfo = FirstNameInfo.Create(node, lookupInfo);
                var lookupType = lookupInfo.Type;
                // Internal control references are not allowed in component input properties.
                if (CheckComponentProperty(lookupInfo.Data as IExternalControl))
                {
                    _txb.ErrorContainer.Error(node, TexlStrings.ErrInternalControlInInputProperty);
                    _txb.SetType(node, DType.Error);
                    _txb.SetInfo(node, fnInfo ?? FirstNameInfo.Create(node, default(NameLookupInfo)));
                    return;
                }
                if (lookupInfo.Kind == BindKind.ThisItem)
                {
                    _txb._hasThisItemReference = true;
                    if (!TryProcessFirstNameNodeForThisItemAccess(node, lookupInfo, out lookupType, out fnInfo) || lookupType.IsError)
                    {
                        // Property should not include ThisItem, return an error
                        _txb.ErrorContainer.Error(node, TexlStrings.ErrInvalidName);
                        _txb.SetType(node, DType.Error);
                        _txb.SetInfo(node, fnInfo ?? FirstNameInfo.Create(node, default(NameLookupInfo)));
                        return;
                    }

                    _txb.SetContextual(node, true);
                }
                else if (lookupInfo.Kind == BindKind.DeprecatedImplicitThisItem)
                {
                    Contracts.Assert(_txb.Document.Properties.SupportsImplicitThisItem);
                    _txb._hasThisItemReference = true;

                    // Even though lookupInfo.Type isn't the full data source type, it still is tagged with the full datasource info if this is a thisitem node
                    nodeName = GetLogicalNodeNameAndUpdateDisplayNames(lookupType, node.Ident, /* isThisItem */ true);

                    // If the ThisItem reference is an entity, the type should be expanded.
                    if (lookupType.IsExpandEntity)
                    {
                        string parentEntityPath = string.Empty;

                        var thisItemType = default(DType);
                        if (lookupInfo.Data is IExternalControl outerControl)
                            thisItemType = outerControl.ThisItemType;
                        if (thisItemType != default && thisItemType.HasExpandInfo)
                            parentEntityPath = thisItemType.ExpandInfo.ExpandPath.ToString();

                        lookupType = GetExpandedEntityType(lookupType, parentEntityPath);
                        fnInfo = FirstNameInfo.Create(node, lookupInfo, lookupInfo.Type.ExpandInfo);
                    }
                }

                // Make a note of this global's type, as identifier by the resolver.
                _txb.SetType(node, lookupType);

                // If this is a reference to an Enum, it is constant.
                _txb.SetConstant(node, lookupInfo.Kind == BindKind.Enum);
                _txb.SetSelfContainedConstant(node, lookupInfo.Kind == BindKind.Enum);

                // Create a name info with an appropriate binding, defaulting to global binding in error cases.
                _txb.SetInfo(node, fnInfo);

                // If the firstName is a standalone global control reference (i.e. not a LHS for a property access)
                // make sure to record this, as it's something that is needed later during codegen.
                if (lookupType.IsControl && (node.Parent == null || node.Parent.AsDottedName() == null))
                {
                    _txb.HasControlReferences = true;

                    // If the current property doesn't support global control references, set an error
                    if (_txb.CurrentPropertyRequiresDefaultableReferences)
                        _txb.ErrorContainer.EnsureError(node, TexlStrings.ErrInvalidControlReference);
                }

                // Check if we are referencing an errored data source and report the error.
                IExternalTabularDataSource connectedDataSourceInfo = null;
                if (lookupInfo.Kind == BindKind.Data &&
                    (connectedDataSourceInfo = lookupInfo.Data as IExternalTabularDataSource) != null &&
                    connectedDataSourceInfo.Errors.Any(error => error.Severity >= DocumentErrorSeverity.Severe))
                {
                    _txb.ErrorContainer.EnsureError(node, TexlStrings.ErrInvalidDataSource);
                }

                // Update _usesGlobals, _usesResources, etc.
                UpdateBindKindUseFlags(lookupInfo.Kind);

                // Update statefulness of global datasources excluding dynamic datasources.
                if ((lookupInfo.Kind == BindKind.Data && !_txb._glue.IsDynamicDataSourceInfo(lookupInfo.Data)))
                {
                    _txb.SetStateful(node, true);
                }

                if (lookupInfo.Kind == BindKind.WebResource || (lookupInfo.Kind == BindKind.QualifiedValue && ((lookupInfo.Data as IQualifiedValuesInfo)?.IsAsyncAccess ?? false)))
                {
                    _txb.FlagPathAsAsync(node);
                    _txb.SetStateful(node, true);
                }

                _txb.CheckAndMarkAsPageable(node);

                if ((lookupInfo.Kind == BindKind.WebResource || lookupInfo.Kind == BindKind.QualifiedValue) && !(node.Parent is DottedNameNode))
                {
                    _txb.ErrorContainer.EnsureError(node, TexlStrings.ErrValueMustBeFullyQualified);
                }
                // Any connectedDataSourceInfo or option set or view needs to be accessed asynchronously to allow data to be loaded.
                if (connectedDataSourceInfo != null || lookupInfo.Kind == BindKind.OptionSet || lookupInfo.Kind == BindKind.View)
                {
                    _txb.FlagPathAsAsync(node);

                    NameLookupInfo entityLookupInfo;
                    // If we have a static declaration of an OptionSet (primarily from tests) there is no entity we need to import
                    // If view no need to verify entity existence
                    if (lookupInfo.Type.OptionSetInfo == null || lookupInfo.Kind == BindKind.View)
                        return;

                    var relatedEntityName = new DName(lookupInfo.Type.OptionSetInfo.RelatedEntityName);
                    if (!haveNameResolver || !_nameResolver.LookupGlobalEntity(relatedEntityName, out entityLookupInfo))
                    {
                        _txb.ErrorContainer.Error(node, TexlStrings.ErrNeedEntity_EntityName, relatedEntityName);
                        _txb.SetType(node, DType.Error);
                        return;
                    }
                }
            }

            private bool TryProcessFirstNameNodeForThisItemAccess(FirstNameNode node, NameLookupInfo lookupInfo, out DType nodeType, out FirstNameInfo info)
            {
                if (_nameResolver.CurrentEntity.IsControl)
                {
                    // Check to see if we only want to include ThisItem in specific
                    // properties of this Control
                    if (_nameResolver.CurrentEntity.EntityScope.TryGetEntity(_nameResolver.CurrentEntity.EntityName, out IExternalControl nodeAssociatedControl) &&
                        nodeAssociatedControl.Template.IncludesThisItemInSpecificProperty)
                    {
                        IExternalControlProperty nodeAssociatedProperty;
                        if (nodeAssociatedControl.Template.TryGetProperty(_nameResolver.CurrentProperty, out nodeAssociatedProperty) && !nodeAssociatedProperty.ShouldIncludeThisItemInFormula)
                        {
                            nodeType = null;
                            info = null;
                            return false;
                        }
                    }
                }

                // Check to see if ThisItem is used in a DottedNameNode and if there is a data control
                // accessible from this rule.
                DName dataControlName = default;
                if (node.Parent is DottedNameNode && _nameResolver.LookupDataControl(node.Ident.Name, out NameLookupInfo dataControlLookupInfo, out dataControlName))
                {
                    // Get the property name being accessed by the parent dotted name.
                    DName rightName = ((DottedNameNode)node.Parent).Right.Name;

                    Contracts.AssertValid(rightName);
                    Contracts.Assert(dataControlLookupInfo.Type.IsControl);

                    // Check to see if the dotted name is accessing a property of the data control.
                    if (((IExternalControlType)dataControlLookupInfo.Type).ControlTemplate.HasOutput(rightName))
                    {
                        // Set the result type to the data control type.
                        nodeType = dataControlLookupInfo.Type;
                        info = FirstNameInfo.Create(node, lookupInfo, dataControlName, true);
                        return true;
                    }
                }

                nodeType = lookupInfo.Type;
                info = FirstNameInfo.Create(node, lookupInfo, dataControlName, false);
                return true;
            }

            private bool IsRowScopeField(FirstNameNode node, out Scope scope, out bool fError, out bool isWholeScope)
            {
                Contracts.AssertValue(node);

                fError = false;
                isWholeScope = false;
                // [@foo] cannot be a scope field.
                if (node.Ident.AtToken != null)
                {
                    scope = default(Scope);
                    return false;
                }

                DName nodeName = node.Ident.Name;

                // Look up the name in the current scopes, innermost to outermost.
                // The logic here is as follows:
                // We need to find the innermost row scope where the FirstName we're searching for is present in the scope
                // Either as a field in the type, or as the scope identifier itself
                // We check the non-reqired identifier case first to preserve existing behavior when the field name is 'ThisRecord'
                for (scope = _currentScope; scope != null; scope = scope.Parent)
                {
                    Contracts.AssertValue(scope);

                    if (!scope.CreatesRowScope)
                        continue;

                    // If the scope identifier isn't required, look up implicit accesses
                    if (!scope.RequireScopeIdentifier)
                    {
                        // If scope type is a data source, the node may be a display name instead of logical.
                        // Attempt to get the logical name to use for type checking.
                        // If this is executed amidst a metadata refresh then the reference may refer to an old
                        // display name, so we need to check the old mapping as well as the current mapping.
                        var usesDisplayName =
                            DType.TryGetConvertedDisplayNameAndLogicalNameForColumn(scope.Type, nodeName.Value, out var maybeLogicalName, out _) ||
                            DType.TryGetLogicalNameForColumn(scope.Type, nodeName.Value, out maybeLogicalName);
                        if (usesDisplayName)
                            nodeName = new DName(maybeLogicalName);

                        DType typeTmp;
                        if (scope.Type.TryGetType(nodeName, out typeTmp))
                        {
                            // Expand the entity type here.
                            if (typeTmp.IsExpandEntity)
                            {
                                string parentEntityPath = string.Empty;
                                if (scope.Type.HasExpandInfo)
                                    parentEntityPath = scope.Type.ExpandInfo.ExpandPath.ToString();

                                // We cannot access pageable entities in row-scope, as it will generate too many calls to the connector
                                // Set an error and skip it.
                                if (typeTmp.ExpandInfo.IsTable)
                                {
                                    if (_txb.Document != null && _txb.Document.Properties.EnabledFeatures.IsEnableRowScopeOneToNExpandEnabled)
                                    {
                                        _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Warning, node, TexlStrings.WrnRowScopeOneToNExpandNumberOfCalls);
                                    }
                                    else
                                    {
                                        _txb.ErrorContainer.Error(node, TexlStrings.ErrColumnNotAccessibleInCurrentContext);
                                        _txb.SetType(node, DType.Error);
                                        fError = true;
                                        return true;
                                    }
                                }

                                DType expandedEntityType = GetExpandedEntityType(typeTmp, parentEntityPath);
                                DType type = scope.Type.SetType(ref fError, DPath.Root.Append(nodeName), expandedEntityType);
                                scope = new Scope(scope.Call, scope.Parent, type, scope.ScopeIdentifier, scope.RequireScopeIdentifier, expandedEntityType.ExpandInfo);
                            }
                            return true;
                        }
                    }

                    if (scope.ScopeIdentifier == nodeName)
                    {
                        isWholeScope = true;
                        return true;
                    }
                }

                scope = default(Scope);
                return false;
            }

            private bool IsRowScopeAlias(FirstNameNode node, out Scope scope)
            {
                Contracts.AssertValue(node);

                scope = default(Scope);

                if (!node.IsLhs)
                    return false;

                DottedNameNode dotted = node.Parent.AsDottedName().VerifyValue();
                if (!dotted.UsesBracket)
                    return false;

                // Look up the name as a scope alias.
                for (scope = _currentScope; scope != null; scope = scope.Parent)
                {
                    Contracts.AssertValue(scope);

                    if (!scope.CreatesRowScope || scope.Call == null)
                        continue;

                    // There is no row scope alias, so we have to rely on a heuristic here.
                    // Look for the first scope whose parent call specifies a matching FirstName arg0.
                    FirstNameNode arg0;
                    if (scope.Call.Args.Count > 0 &&
                        (arg0 = scope.Call.Args.Children[0].AsFirstName()) != null &&
                        arg0.Ident.Name == node.Ident.Name &&
                        arg0.Ident.Namespace == node.Ident.Namespace)
                    {
                        return true;
                    }
                }

                scope = default(Scope);
                return false;
            }

            public override void Visit(ParentNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                if (_nameResolver == null || _nameResolver.CurrentEntity == null)
                {
                    _txb.ErrorContainer.Error(node, TexlStrings.ErrInvalidName);
                    _txb.SetType(node, DType.Error);
                    return;
                }

                NameLookupInfo lookupInfo;
                if (!_nameResolver.CurrentEntity.IsControl || !_nameResolver.LookupParent(out lookupInfo))
                {
                    _txb.ErrorContainer.Error(node, TexlStrings.ErrInvalidParentUse);
                    _txb.SetType(node, DType.Error);
                    return;
                }

                // Treat this as a standard access to the parent control ("v" type).
                _txb.SetType(node, lookupInfo.Type);
                _txb.SetInfo(node, new ParentInfo(node, lookupInfo.Path, lookupInfo.Data as IExternalControl));
                _txb.HasParentItemReference = true;

                UpdateBindKindUseFlags(lookupInfo.Kind);
            }

            public override void Visit(SelfNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                if (_nameResolver == null || _nameResolver.CurrentEntity == null)
                {
                    _txb.ErrorContainer.Error(node, TexlStrings.ErrInvalidName);
                    _txb.SetType(node, DType.Error);
                    return;
                }

                NameLookupInfo lookupInfo;
                if (!_nameResolver.LookupSelf(out lookupInfo))
                {
                    _txb.ErrorContainer.Error(node, TexlStrings.ErrInvalidName);
                    _txb.SetType(node, DType.Error);
                    return;
                }

                // Treat this as a standard access to the current control ("v" type).
                _txb.SetType(node, lookupInfo.Type);
                _txb.SetInfo(node, new SelfInfo(node, lookupInfo.Path, lookupInfo.Data as IExternalControl));
                _txb.HasSelfReference = true;

                UpdateBindKindUseFlags(lookupInfo.Kind);
            }

            private void UpdateBindKindUseFlags(BindKind bindKind)
            {
                Contracts.Assert(BindKind._Min <= bindKind && bindKind < BindKind._Lim);

                switch (bindKind)
                {
                    case BindKind.Condition:
                    case BindKind.Control:
                    case BindKind.Data:
                    case BindKind.PowerFxResolvedObject:
                    case BindKind.NamedValue:
                    case BindKind.QualifiedValue:
                    case BindKind.WebResource:
                        _txb.UsesGlobals = true;
                        break;
                    case BindKind.Alias:
                        _txb.UsesAliases = true;
                        break;
                    case BindKind.ScopeVariable:
                        _txb.UsesScopeVariables = true;
                        break;
                    case BindKind.DeprecatedImplicitThisItem:
                    case BindKind.ThisItem:
                        _txb.UsesThisItem = true;
                        break;
                    case BindKind.Resource:
                        _txb.UsesResources = true;
                        _txb.UsesGlobals = true;
                        break;
                    case BindKind.OptionSet:
                        _txb.UsesGlobals = true;
                        _txb.UsesOptionSets = true;
                        break;
                    case BindKind.View:
                        _txb.UsesGlobals = true;
                        _txb.UsesViews = true;
                        break;
                    default:
                        Contracts.Assert(bindKind == BindKind.LambdaField || bindKind == BindKind.LambdaFullRecord || bindKind == BindKind.Enum || bindKind == BindKind.Unknown);
                        break;
                }
            }

            public override bool PreVisit(RecordNode node) => PreVisitVariadicBase(node);

            public override bool PreVisit(TableNode node) => PreVisitVariadicBase(node);

            private bool PreVisitVariadicBase(VariadicBase node)
            {
                Contracts.AssertValue(node);

                var volatileVariables = _txb.GetVolatileVariables(node);
                foreach (var child in node.Children)
                    _txb.AddVolatileVariables(child, volatileVariables);

                return true;
            }

            public override bool PreVisit(DottedNameNode node)
            {
                Contracts.AssertValue(node);

                _txb.AddVolatileVariables(node.Left, _txb.GetVolatileVariables(node));
                return true;
            }

            public override void PostVisit(DottedNameNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                DType leftType = _txb.GetType(node.Left);

                if (!leftType.IsControl && !leftType.IsAggregate && !leftType.IsEnum && !leftType.IsOptionSet && !leftType.IsView)
                {
                    SetDottedNameError(node, TexlStrings.ErrInvalidDot);
                    return;
                }

                object value = null;
                DType typeRhs = DType.Invalid;
                DName nameRhs = node.Right.Name;

                nameRhs = GetLogicalNodeNameAndUpdateDisplayNames(leftType, node.Right);

                // In order for the node to be constant, it must be a member of an enum,
                // a member of a constant aggregate,
                // or a reference to a constant rule (checked later).
                bool isConstant = leftType.IsEnum || (leftType.IsAggregate && _txb.IsConstant(node.Left));

                // Some nodes are never pageable, use this to
                // skip the check for pageability and default to non-pageable;
                bool canBePageable = true;

                if (leftType.IsEnum)
                {
                    if (_nameResolver == null)
                    {
                        SetDottedNameError(node, TexlStrings.ErrInvalidName);
                        return;
                    }

                    // The RHS is a locale-specific name (straight from the parse tree), so we need
                    // to look things up accordingly. If the LHS is a FirstName, fetch its embedded
                    // EnumInfo and look in it for a value with the given locale-specific name.
                    // This should be a fast O(1) lookup that covers 99% of all cases, such as
                    // Couleur!Rouge, Align.Droit, etc.
                    FirstNameNode firstNodeLhs = node.Left.AsFirstName();
                    FirstNameInfo firstInfoLhs = firstNodeLhs == null ? null : _txb.GetInfo(firstNodeLhs).VerifyValue();
                    if (firstInfoLhs != null && _nameResolver.LookupEnumValueByInfoAndLocName(firstInfoLhs.Data, nameRhs, out value))
                        typeRhs = leftType.GetEnumSupertype();
                    // ..otherwise do a slower lookup by type for the remaining 1% of cases,
                    // such as text1!Fill!Rouge, etc.
                    // This is O(n) in the number of registered enums.
                    else if (_nameResolver.LookupEnumValueByTypeAndLocName(leftType, nameRhs, out value))
                        typeRhs = leftType.GetEnumSupertype();
                    else
                    {
                        SetDottedNameError(node, TexlStrings.ErrInvalidName);
                        return;
                    }
                }
                else if (leftType.IsOptionSet || leftType.IsView)
                {
                    if (!leftType.TryGetType(nameRhs, out typeRhs))
                    {
                        SetDottedNameError(node, TexlStrings.ErrInvalidName);
                        return;
                    }
                }
                else if (leftType.IsAttachment)
                {
                    // Error: Attachment Type should never be the left hand side of dotted name node
                    SetDottedNameError(node, TexlStrings.ErrInvalidName);
                }
                else if (leftType is IExternalControlType leftControl)
                {
                    var result = GetLHSControlInfo(node);

                    if (result.isIndirectPropertyUsage)
                        _txb.UsedControlProperties.Add(nameRhs);

                    // Explicitly block accesses to the parent's nested-aware property.
                    if (result.controlInfo != null && UsesParentsNestedAwareProperty(result.controlInfo, nameRhs))
                    {
                        SetDottedNameError(node, TexlStrings.ErrNotAccessibleInCurrentContext);
                        return;
                    }

                    // The RHS is a control property name (locale-specific).
                    IExternalControlTemplate template = leftControl.ControlTemplate.VerifyValue();
                    if (!template.TryGetOutputProperty(nameRhs, out IExternalControlProperty property))
                    {
                        SetDottedNameError(node, TexlStrings.ErrInvalidName);
                        return;
                    }

                    // We block the property access usage for behavior component properties.
                    if (template.IsComponent && property.PropertyCategory.IsBehavioral())
                    {
                        SetDottedNameError(node, TexlStrings.ErrInvalidPropertyReference);
                        return;
                    }

                    // We block the property access usage for scoped component properties.
                    if (template.IsComponent && property.IsScopeVariable)
                    {
                        SetDottedNameError(node, TexlStrings.ErrInvalidPropertyReference);
                        return;
                    }

                    // We block the property access usage for datasource of the command component.
                    if (template.IsCommandComponent &&
                        (_txb._glue.IsPrimaryCommandComponentProperty(property)))
                    {
                        SetDottedNameError(node, TexlStrings.ErrInvalidPropertyReference);
                        return;
                    }

                    var lhsControlInfo = result.controlInfo;
                    var currentControl = _txb.Control;
                    // We block the property access usage for context property of the command component instance unless it's the same command control.
                    if (lhsControlInfo != null &&
                        lhsControlInfo.IsCommandComponentInstance &&
                        (_txb._glue.IsContextProperty(property)) &&
                        currentControl != null && currentControl != lhsControlInfo)
                    {
                        SetDottedNameError(node, TexlStrings.ErrInvalidPropertyReference);
                        return;
                    }

                    // Explicitly block access to design properties referenced via Selected/AllItems.
                    if (leftControl.IsDataLimitedControl && property.PropertyCategory != PropertyRuleCategory.Data)
                    {
                        SetDottedNameError(node, TexlStrings.ErrNotAccessibleInCurrentContext);
                        return;
                    }

                    // For properties requiring default references, block non-defaultable properties
                    if (_txb.CurrentPropertyRequiresDefaultableReferences && property.UnloadedDefault == null)
                    {
                        SetDottedNameError(node, TexlStrings.ErrInvalidControlReference);
                        return;
                    }
                    // If the property has pass-through input (e.g. AllItems, Selected, etc), the correct RHS (property)
                    // expando type is not available in the "v" type. We try delay calculating this until we need it as this is
                    // an expensive operation especially for form control which generally has tons of nested controls. So we calculate the type here.
                    // There might be cases where we are getting the schema from imported data that once belonged to a control and now,
                    // we don't have a pass-through input associated with it. Therefore, we need to get the opaqueType to avoid localizing the schema.
                    if (property.PassThroughInput == null)
                        typeRhs = property.GetOpaqueType();
                    else
                    {
                        FirstNameNode firstNodeLhs = node.Left.AsFirstName();
                        if (template.HasExpandoProperties &&
                            template.ExpandoProperties.Any(p => p.InvariantName == property.InvariantName) &&
                            result.controlInfo != null && (firstNodeLhs == null || _txb.GetInfo(firstNodeLhs).Kind != BindKind.ScopeVariable))
                        {
                            // If visiting an expando type property of control type variable, we cannot calculate the type here because
                            // The LHS associated ControlInfo is App/Component.
                            // e.g. Set(controlVariable1, DropDown1), Label1.Text = controlVariable1.Selected.Value.
                            leftType = (DType)result.controlInfo.GetControlDType(calculateAugmentedExpandoType: true, isDataLimited: false);
                        }

                        if (!leftType.ToRecord().TryGetType(property.InvariantName, out typeRhs))
                        {
                            SetDottedNameError(node, TexlStrings.ErrInvalidName);
                            return;
                        }
                    }

                    // If the reference is to Control.Property and the rule for that Property is a constant,
                    // we need to mark the node as constant, and save the control info so we may look up the
                    // rule later.
                    if (result.controlInfo?.GetRule(property.InvariantName) is {HasErrors: false} rule && rule.Binding.IsConstant(rule.Binding.Top))
                    {
                        value = result.controlInfo;
                        isConstant = true;
                    }

                    // Check access to custom scoped input properties. Such properties can only be accessed from within a component or output property of a component.
                    if (property.IsScopedProperty &&
                        _txb.Control != null && _txb.Property != null &&
                        result.controlInfo != null &&
                        !IsValidAccessToScopedProperty(result.controlInfo, property, _txb.Control, _txb.Property))
                    {
                        SetDottedNameError(node, TexlStrings.ErrUnSupportedComponentDataPropertyAccess);
                        return;
                    }

                    // Check for scoped property access with required scoped variable.
                    if (property.IsScopedProperty && property.ScopeFunctionPrototype.MinArity > 0)
                    {
                        SetDottedNameError(node, TexlStrings.ErrInvalidPropertyAccess);
                        return;
                    }

                    if (property.IsScopedProperty && property.ScopeFunctionPrototype.IsAsync)
                        _txb.FlagPathAsAsync(node);
                }
                else if (!leftType.TryGetType(nameRhs, out typeRhs))
                {
                    // We may be in the case of dropDown!Selected!RHS
                    // In this case, Selected embeds a meta field whose v-type encapsulates localization info
                    // for the sub-properties of "Selected". The localized sub-properties are NOT present in
                    // the Selected DType directly.
                    Contracts.Assert(leftType.IsAggregate);
                    if (leftType.TryGetMetaField(out var vType))
                    {
                        if (!vType.ControlTemplate.TryGetOutputProperty(nameRhs, out var property))
                        {
                            SetDottedNameError(node, TexlStrings.ErrInvalidName);
                            return;
                        }
                        typeRhs = property.Type;
                    }
                    else
                    {
                        SetDottedNameError(node, TexlStrings.ErrInvalidName);
                        return;
                    }
                }
                else if (typeRhs is IExternalControlType controlType && controlType.IsMetaField)
                {
                    // Meta fields are not directly accessible. E.g. dropdown!Selected!meta is an invalid access.
                    SetDottedNameError(node, TexlStrings.ErrInvalidName);
                    return;
                }
                else if (typeRhs.IsExpandEntity)
                {
                    typeRhs = GetEntitySchema(typeRhs, node);
                    value = typeRhs.ExpandInfo;
                    Contracts.Assert(typeRhs == DType.Error || typeRhs.ExpandInfo != null);

                    if (_txb.IsRowScope(node.Left) && (typeRhs.ExpandInfo != null && typeRhs.ExpandInfo.IsTable))
                    {
                        if (_txb.Document != null && _txb.Document.Properties.EnabledFeatures.IsEnableRowScopeOneToNExpandEnabled)
                        {
                            _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Warning, node, TexlStrings.WrnRowScopeOneToNExpandNumberOfCalls);
                        }
                        else
                        {
                            SetDottedNameError(node, TexlStrings.ErrColumnNotAccessibleInCurrentContext);
                            return;
                        }
                    }
                }

                // Consider the attachmentType as the type of the node for binding purposes
                // if it is being accessed from a record
                if (typeRhs.IsAttachment)
                {
                    // Disable accessing the attachment in RowScope or single column table
                    // to prevent a large number of calls to the service
                    if (_txb.IsRowScope(node.Left) || leftType.IsTable)
                    {
                        SetDottedNameError(node, TexlStrings.ErrColumnNotAccessibleInCurrentContext);
                        return;
                    }

                    DType attachmentType = typeRhs.AttachmentType;
                    Contracts.AssertValid(attachmentType);
                    Contracts.Assert(leftType.IsRecord);

                    typeRhs = attachmentType;
                    _txb.HasReferenceToAttachment = true;
                    _txb.FlagPathAsAsync(node);
                }

                // Set the type for the dotted node itself.
                if (leftType.IsEnum)
                {
                    // #T[id:val, ...] . id --> T
                    Contracts.Assert(typeRhs == leftType.GetEnumSupertype());
                    _txb.SetType(node, typeRhs);
                }
                else if (leftType.IsOptionSet || leftType.IsView)
                {
                    _txb.SetType(node, typeRhs);
                }
                else if (leftType.IsRecord)
                {
                    // ![id:type, ...] . id --> type
                    _txb.SetType(node, typeRhs);
                }
                else if (leftType.IsTable)
                {
                    // *[id:type, ...] . id  --> *[id:type]
                    // We don't support scenario when lhs is table and rhs is entity of table type (1-n)
                    if (value is IExpandInfo && typeRhs.IsTable)
                    {
                        SetDottedNameError(node, TexlStrings.ErrColumnNotAccessibleInCurrentContext);
                        return;
                    }
                    else if (value is IExpandInfo)
                    {
                        var resultType = DType.CreateTable(new TypedName(typeRhs, nameRhs));
                        foreach (var cds in leftType.AssociatedDataSources)
                        {
                            resultType = DType.AttachDataSourceInfo(resultType, cds, attachToNestedType: false);
                        }
                        _txb.SetType(node, resultType);
                        canBePageable = false;
                    }
                    else
                    {
                        _txb.SetType(node, DType.CreateDTypeWithConnectedDataSourceInfoMetadata(DType.CreateTable(new TypedName(typeRhs, nameRhs)), typeRhs.AssociatedDataSources));
                    }
                }
                else
                {
                    // v[prop:type, ...] . prop --> type
                    Contracts.Assert(leftType.IsControl || leftType.IsExpandEntity || leftType.IsAttachment);
                    _txb.SetType(node, typeRhs);
                }

                // Set the remaining bits -- name info, side effect info, etc.
                _txb.SetInfo(node, new DottedNameInfo(node, value));
                _txb.SetSideEffects(node, _txb.HasSideEffects(node.Left));
                _txb.SetStateful(node, _txb.IsStateful(node.Left));
                _txb.SetContextual(node, _txb.IsContextual(node.Left));

                _txb.SetConstant(node, isConstant);
                _txb.SetSelfContainedConstant(node, leftType.IsEnum || (leftType.IsAggregate && _txb.IsSelfContainedConstant(node.Left)));
                if (_txb.IsBlockScopedConstant(node.Left))
                    _txb.SetBlockScopedConstantNode(node);

                _txb.SetScopeUseSet(node, JoinScopeUseSets(node.Left));

                if (canBePageable)
                {
                    _txb.CheckAndMarkAsDelegatable(node);
                    _txb.CheckAndMarkAsPageable(node);
                }

                _txb.AddVolatileVariables(node, _txb.GetVolatileVariables(node.Left));
                _txb.SetIsUnliftable(node, _txb.IsUnliftable(node.Left));
            }

            private (IExternalControl controlInfo, bool isIndirectPropertyUsage) GetLHSControlInfo(DottedNameNode node)
            {
                var isIndirectPropertyUsage = false;
                if (!TryGetControlInfoLHS(node.Left, out var info))
                {
                    // App Global references need not be tracked for control references
                    // here as Global control edges are is already handled in analysis.
                    // Doing this here for global control reference can cause more than required aggressive edges
                    // and creating cross screen dependencies that are not required.
                    isIndirectPropertyUsage = !(node.Left.Kind == NodeKind.DottedName
                        && TryGetControlInfoLHS(node.Left.AsDottedName().Left, out var outerInfo)
                        && outerInfo.IsAppGlobalControl);
                }

                return (info, isIndirectPropertyUsage);
            }

            // Check if the control can be used in current component property
            private bool CheckComponentProperty(IExternalControl control)
            {
                return control != null && !_txb._glue.CanControlBeUsedInComponentProperty(_txb, control);
            }

            private DType GetEntitySchema(DType entityType, DottedNameNode node)
            {
                Contracts.AssertValid(entityType);
                Contracts.AssertValue(node);

                string entityPath = string.Empty;
                DType lhsType = _txb.GetType(node.Left);

                if (lhsType.HasExpandInfo)
                    entityPath = lhsType.ExpandInfo.ExpandPath.ToString();

                return GetExpandedEntityType(entityType, entityPath);
            }

            protected DType GetExpandedEntityType(DType expandEntityType, string relatedEntityPath)
            {
                Contracts.AssertValid(expandEntityType);
                Contracts.Assert(expandEntityType.HasExpandInfo);
                Contracts.AssertValue(relatedEntityPath);

                IExpandInfo expandEntityInfo = expandEntityType.ExpandInfo;
                var dsInfo = expandEntityInfo.ParentDataSource as IExternalTabularDataSource;

                if (dsInfo == null) return expandEntityType;

                DType type;

                // This will cache expandend types of entities in QueryOptions
                var entityTypes = _txb.QueryOptions.GetExpandDTypes(dsInfo);

                if (!entityTypes.TryGetValue(expandEntityInfo.ExpandPath, out type))
                {
                    if (!expandEntityType.TryGetEntityDelegationMetadata(out var metadata))
                    {
                        // We need more metadata to bind this fully
                        _txb.DeclareMetadataNeeded(expandEntityType);
                        return DType.Error;
                    }

                    type = expandEntityType.ExpandEntityType(metadata.Schema, metadata.Schema.AssociatedDataSources);
                    Contracts.Assert(type.HasExpandInfo);

                    // Update the datasource and relatedEntity path.
                    type.ExpandInfo.UpdateEntityInfo(expandEntityInfo.ParentDataSource, relatedEntityPath);
                    entityTypes.Add(expandEntityInfo.ExpandPath, type);
                }

                return type;
            }

            private bool TryGetControlInfoLHS(TexlNode node, out IExternalControl info)
            {
                Contracts.AssertValue(node);

                info = node switch
                {
                    ParentNode parentNode => _txb.GetInfo(parentNode)?.Data as IExternalControl,
                    SelfNode selfNode => _txb.GetInfo(selfNode)?.Data as IExternalControl,
                    FirstNameNode firstNameNode => _txb.GetInfo(firstNameNode)?.Data as IExternalControl,
                    _ => null,
                };

                return info != null;
            }

            protected void SetDottedNameError(DottedNameNode node, ErrorResourceKey errKey, params object[] args)
            {
                Contracts.AssertValue(node);
                Contracts.AssertValue(errKey.Key);
                Contracts.AssertValue(args);

                _txb.SetInfo(node, new DottedNameInfo(node));
                _txb.ErrorContainer.Error(node, errKey, args);
                _txb.SetType(node, DType.Error);
            }

            // Returns true if the currentControl is a replicating child of the controlName being passed and the propertyName passed is
            // a nestedAware out property of the parent and currentProperty is not a behaviour property.
            private bool UsesParentsNestedAwareProperty(IExternalControl controlInfo, DName propertyName)
            {
                Contracts.AssertValue(controlInfo);
                Contracts.Assert(propertyName.IsValid);

                IExternalControl currentControlInfo;
                if (_nameResolver == null || (currentControlInfo = _nameResolver.CurrentEntity as IExternalControl) == null)
                    return false;

                return currentControlInfo.IsReplicable &&
                        !currentControlInfo.Template.HasProperty(_nameResolver.CurrentProperty.Value, PropertyRuleCategory.Behavior) &&
                        controlInfo.Template.ReplicatesNestedControls &&
                        currentControlInfo.IsDescendentOf(controlInfo) &&
                        controlInfo.Template.NestedAwareTableOutputs.Contains(propertyName);
            }

            public override void PostVisit(UnaryOpNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                switch (node.Op)
                {
                    case UnaryOp.Not:
                        CheckType(node.Child, DType.Boolean, /* coerced: */ DType.Number, DType.String, DType.OptionSetValue);
                        _txb.SetType(node, DType.Boolean);
                        break;
                    case UnaryOp.Minus:
                        var childType = _txb.GetType(node.Child);
                        switch (childType.Kind)
                        {
                            case DKind.Date:
                                // Important to keep the type of minus-date as date, to allow D-D/d-D to be detected
                                _txb.SetType(node, DType.Date);
                                break;
                            case DKind.Time:
                                // Important to keep the type of minus-time as time, to allow T-T to be detected
                                _txb.SetType(node, DType.Time);
                                break;
                            case DKind.DateTime:
                                // Important to keep the type of minus-datetime as datetime, to allow d-d/D-d to be detected
                                _txb.SetType(node, DType.DateTime);
                                break;
                            default:
                                CheckType(node.Child, DType.Number, /* coerced: */ DType.String, DType.Boolean);
                                _txb.SetType(node, DType.Number);
                                break;
                        }
                        break;
                    case UnaryOp.Percent:
                        CheckType(node.Child, DType.Number, /* coerced: */ DType.String, DType.Boolean, DType.Date, DType.Time, DType.DateTimeNoTimeZone, DType.DateTime);
                        _txb.SetType(node, DType.Number);
                        break;
                    default:
                        Contracts.Assert(false);
                        _txb.SetType(node, DType.Error);
                        break;
                }

                _txb.SetSideEffects(node, _txb.HasSideEffects(node.Child));
                _txb.SetStateful(node, _txb.IsStateful(node.Child));
                _txb.SetContextual(node, _txb.IsContextual(node.Child));
                _txb.SetConstant(node, _txb.IsConstant(node.Child));
                _txb.SetSelfContainedConstant(node, _txb.IsSelfContainedConstant(node.Child));
                _txb.SetScopeUseSet(node, JoinScopeUseSets(node.Child));
                _txb.AddVolatileVariables(node, _txb.GetVolatileVariables(node.Child));
                _txb.SetIsUnliftable(node, _txb.IsUnliftable(node.Child));
            }

            // REVIEW ragru: Introduce a TexlOperator abstract base plus various subclasses
            // for handling operators and their overloads. That will offload the burden of dealing with
            // operator special cases to the various operator classes.
            public override void PostVisit(BinaryOpNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                switch (node.Op)
                {
                    case BinaryOp.Add:
                        PostVisitBinaryOpNodeAddition(node);
                        break;
                    case BinaryOp.Power:
                    case BinaryOp.Mul:
                    case BinaryOp.Div:
                        CheckType(node.Left, DType.Number, /* coerced: */ DType.String, DType.Boolean, DType.Date, DType.Time, DType.DateTimeNoTimeZone, DType.DateTime);
                        CheckType(node.Right, DType.Number, /* coerced: */ DType.String, DType.Boolean, DType.Date, DType.Time, DType.DateTimeNoTimeZone, DType.DateTime);
                        _txb.SetType(node, DType.Number);
                        break;

                    case BinaryOp.Or:
                    case BinaryOp.And:
                        CheckType(node.Left, DType.Boolean, /* coerced: */ DType.Number, DType.String, DType.OptionSetValue);
                        CheckType(node.Right, DType.Boolean, /* coerced: */ DType.Number, DType.String, DType.OptionSetValue);
                        _txb.SetType(node, DType.Boolean);
                        break;

                    case BinaryOp.Concat:
                        CheckType(node.Left, DType.String, /* coerced: */ DType.Number, DType.Date, DType.Time, DType.DateTimeNoTimeZone, DType.DateTime, DType.Boolean, DType.OptionSetValue, DType.ViewValue);
                        CheckType(node.Right, DType.String, /* coerced: */ DType.Number, DType.Date, DType.Time, DType.DateTimeNoTimeZone, DType.DateTime, DType.Boolean, DType.OptionSetValue, DType.ViewValue);
                        _txb.SetType(node, DType.String);
                        break;

                    case BinaryOp.Error:
                        _txb.SetType(node, DType.Error);
                        _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, node, TexlStrings.ErrOperatorExpected);
                        break;

                    case BinaryOp.Equal:
                    case BinaryOp.NotEqual:
                        CheckEqualArgTypes(node.Left, node.Right);
                        _txb.SetType(node, DType.Boolean);
                        break;

                    case BinaryOp.Less:
                    case BinaryOp.LessEqual:
                    case BinaryOp.Greater:
                    case BinaryOp.GreaterEqual:
                        // Excel's type coercion for inequality operators is inconsistent / borderline wrong, so we can't
                        // use it as a reference. For example, in Excel '2 < TRUE' produces TRUE, but so does '2 < FALSE'.
                        // Sticking to a restricted set of numeric-like types for now until evidence arises to support the need for coercion.
                        CheckComparisonArgTypes(node.Left, node.Right);
                        _txb.SetType(node, DType.Boolean);
                        break;

                    case BinaryOp.In:
                    case BinaryOp.Exactin:
                        CheckInArgTypes(node.Left, node.Right);
                        _txb.SetType(node, DType.Boolean);
                        break;

                    default:
                        Contracts.Assert(false);
                        _txb.SetType(node, DType.Error);
                        break;
                }

                _txb.SetSideEffects(node, _txb.HasSideEffects(node.Left) || _txb.HasSideEffects(node.Right));
                _txb.SetStateful(node, _txb.IsStateful(node.Left) || _txb.IsStateful(node.Right));
                _txb.SetContextual(node, _txb.IsContextual(node.Left) || _txb.IsContextual(node.Right));
                _txb.SetConstant(node, _txb.IsConstant(node.Left) && _txb.IsConstant(node.Right));
                _txb.SetSelfContainedConstant(node, _txb.IsSelfContainedConstant(node.Left) && _txb.IsSelfContainedConstant(node.Right));
                _txb.SetScopeUseSet(node, JoinScopeUseSets(node.Left, node.Right));
                _txb.AddVolatileVariables(node, _txb.GetVolatileVariables(node.Left));
                _txb.AddVolatileVariables(node, _txb.GetVolatileVariables(node.Right));
                _txb.SetIsUnliftable(node, _txb.IsUnliftable(node.Left) || _txb.IsUnliftable(node.Right));
            }

            private void PostVisitBinaryOpNodeAddition(BinaryOpNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);
                Contracts.Assert(node.Op == BinaryOp.Add);

                DType left = _txb.GetType(node.Left);
                DType right = _txb.GetType(node.Right);
                DKind leftKind = left.Kind;
                DKind rightKind = right.Kind;

                void ReportInvalidOperation()
                {
                    _txb.SetType(node, DType.Error);
                    _txb.ErrorContainer.EnsureError(
                        DocumentErrorSeverity.Severe,
                        node,
                        TexlStrings.ErrBadOperatorTypes,
                        left.GetKindString(),
                        right.GetKindString());
                }

                UnaryOpNode unary;

                switch (leftKind)
                {
                    case DKind.DateTime:
                        switch (rightKind)
                        {
                            case DKind.DateTime:
                            case DKind.Date:
                                unary = node.Right.AsUnaryOpLit();
                                if (unary != null && unary.Op == UnaryOp.Minus)
                                {
                                    // DateTime - DateTime = Number
                                    // DateTime - Date = Number
                                    _txb.SetType(node, DType.Number);
                                }
                                else
                                {
                                    // DateTime + DateTime in any other arrangement is an error
                                    // DateTime + Date in any other arrangement is an error
                                    ReportInvalidOperation();
                                }
                                break;
                            case DKind.Time:
                                // DateTime + Time in any other arrangement is an error
                                ReportInvalidOperation();
                                break;
                            default:
                                // DateTime + number = DateTime
                                CheckType(node.Right, DType.Number, /* coerced: */ DType.String, DType.Boolean);
                                _txb.SetType(node, DType.DateTime);
                                break;
                        }
                        break;
                    case DKind.Date:
                        switch (rightKind)
                        {
                            case DKind.Date:
                                // Date + Date = number but ONLY if its really subtraction Date + '-Date'
                                unary = node.Right.AsUnaryOpLit();
                                if (unary != null && unary.Op == UnaryOp.Minus)
                                {
                                    // Date - Date = Number
                                    _txb.SetType(node, DType.Number);
                                }
                                else
                                {
                                    // Date + Date in any other arrangement is an error
                                    ReportInvalidOperation();
                                }
                                break;
                            case DKind.Time:
                                unary = node.Right.AsUnaryOpLit();
                                if (unary != null && unary.Op == UnaryOp.Minus)
                                {
                                    // Date - Time is an error
                                    ReportInvalidOperation();
                                }
                                else
                                {
                                    // Date + Time = DateTime
                                    _txb.SetType(node, DType.DateTime);
                                }
                                break;
                            case DKind.DateTime:
                                // Date + DateTime = number but ONLY if its really subtraction Date + '-DateTime'
                                unary = node.Right.AsUnaryOpLit();
                                if (unary != null && unary.Op == UnaryOp.Minus)
                                {
                                    // Date - DateTime = Number
                                    _txb.SetType(node, DType.Number);
                                }
                                else
                                {
                                    // Date + DateTime in any other arrangement is an error
                                    ReportInvalidOperation();
                                }
                                break;
                            default:
                                // Date + number = Date
                                CheckType(node.Right, DType.Number, /* coerced: */ DType.String, DType.Boolean);
                                _txb.SetType(node, DType.Date);
                                break;
                        }
                        break;
                    case DKind.Time:
                        switch (rightKind)
                        {
                            case DKind.Time:
                                // Time + Time = number but ONLY if its really subtraction Time + '-Time'
                                unary = node.Right.AsUnaryOpLit();
                                if (unary != null && unary.Op == UnaryOp.Minus)
                                {
                                    // Time - Time = Number
                                    _txb.SetType(node, DType.Number);
                                }
                                else
                                {
                                    // Time + Time in any other arrangement is an error
                                    ReportInvalidOperation();
                                }
                                break;
                            case DKind.Date:
                                unary = node.Right.AsUnaryOpLit();
                                if (unary != null && unary.Op == UnaryOp.Minus)
                                {
                                    // Time - Date is an error
                                    ReportInvalidOperation();
                                }
                                else
                                {
                                    // Time + Date = DateTime
                                    _txb.SetType(node, DType.DateTime);
                                }
                                break;
                            case DKind.DateTime:
                                // Time + DateTime in any other arrangement is an error
                                ReportInvalidOperation();
                                break;
                            default:
                                // Time + number = Time
                                CheckType(node.Right, DType.Number, /* coerced: */ DType.String, DType.Boolean);
                                _txb.SetType(node, DType.Time);
                                break;
                        }
                        break;
                    default:
                        switch (rightKind)
                        {
                            case DKind.DateTime:
                                // number + DateTime = DateTime
                                CheckType(node.Left, DType.Number, /* coerced: */ DType.String, DType.Boolean);
                                _txb.SetType(node, DType.DateTime);
                                break;
                            case DKind.Date:
                                // number + Date = Date
                                CheckType(node.Left, DType.Number, /* coerced: */ DType.String, DType.Boolean);
                                _txb.SetType(node, DType.Date);
                                break;
                            case DKind.Time:
                                // number + Time = Time
                                CheckType(node.Left, DType.Number, /* coerced: */ DType.String, DType.Boolean);
                                _txb.SetType(node, DType.Time);
                                break;
                            default:
                                // Regular Addition
                                CheckType(node.Left, DType.Number, /* coerced: */ DType.String, DType.Boolean);
                                CheckType(node.Right, DType.Number, /* coerced: */ DType.String, DType.Boolean);
                                _txb.SetType(node, DType.Number);
                                break;
                        }
                        break;
                }
            }

            public override void PostVisit(AsNode node)
            {
                Contracts.AssertValue(node);

                // As must be either the top node, or an immediate child of a call node
                if (node.Id != _txb.Top.Id &&
                    (node.Parent?.Kind != NodeKind.List || node.Parent?.Parent?.Kind != NodeKind.Call))
                {
                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, node, TexlStrings.ErrAsNotInContext);
                }
                else if (node.Id == _txb.Top.Id &&
                    (_nameResolver == null || !(_nameResolver.CurrentEntity is IExternalControl currentControl) ||
                    !currentControl.Template.ReplicatesNestedControls ||
                    !(currentControl.Template.ThisItemInputInvariantName == _nameResolver.CurrentProperty)))
                {
                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, node, TexlStrings.ErrAsNotInContext);
                }

                _txb.SetInfo(node, new AsInfo(node, node.Right.Name));

                var left = node.Left;
                _txb.CheckAndMarkAsPageable(node);
                _txb.CheckAndMarkAsDelegatable(node);
                _txb.SetType(node, _txb.GetType(left));
                _txb.SetSideEffects(node, _txb.HasSideEffects(left));
                _txb.SetStateful(node, _txb.IsStateful(left));
                _txb.SetContextual(node, _txb.IsContextual(left));
                _txb.SetConstant(node, _txb.IsConstant(left));
                _txb.SetSelfContainedConstant(node, _txb.IsSelfContainedConstant(left));
                _txb.SetScopeUseSet(node, _txb.GetScopeUseSet(left));
                _txb.AddVolatileVariables(node, _txb.GetVolatileVariables(left));
                _txb.SetIsUnliftable(node, _txb.IsUnliftable(node.Left));
            }

            private void CheckComparisonArgTypes(TexlNode left, TexlNode right)
            {
                // Excel's type coercion for inequality operators is inconsistent / borderline wrong, so we can't
                // use it as a reference. For example, in Excel '2 < TRUE' produces TRUE, but so does '2 < FALSE'.
                // Sticking to a restricted set of numeric-like types for now until evidence arises to support the need for coercion.
                CheckComparisonTypeOneOf(left, DType.Number, DType.Date, DType.Time, DType.DateTime);
                CheckComparisonTypeOneOf(right, DType.Number, DType.Date, DType.Time, DType.DateTime);

                DType typeLeft = _txb.GetType(left);
                DType typeRight = _txb.GetType(right);

                if (!typeLeft.Accepts(typeRight) && !typeRight.Accepts(typeLeft))
                {
                    // Handle DateTime <=> Number comparison by coercing one side to Number
                    if (DType.Number.Accepts(typeLeft) && DType.DateTime.Accepts(typeRight))
                    {
                        _txb.SetCoercedType(right, DType.Number);
                        return;
                    }
                    else if (DType.Number.Accepts(typeRight) && DType.DateTime.Accepts(typeLeft))
                    {
                        _txb.SetCoercedType(left, DType.Number);
                        return;
                    }
                }
            }


            private void CheckEqualArgTypes(TexlNode left, TexlNode right)
            {
                Contracts.AssertValue(left);
                Contracts.AssertValue(right);
                Contracts.AssertValue(left.Parent);
                Contracts.Assert(object.ReferenceEquals(left.Parent, right.Parent));

                DType typeLeft = _txb.GetType(left);
                DType typeRight = _txb.GetType(right);

                // EqualOp is only allowed on primitive types, polymorphic lookups, and control types.
                if (!(typeLeft.IsPrimitive && typeRight.IsPrimitive) && !(typeLeft.IsPolymorphic && typeRight.IsPolymorphic) && !(typeLeft.IsControl && typeRight.IsControl)
                    && !(typeLeft.IsPolymorphic && typeRight.IsRecord) && !(typeLeft.IsRecord && typeRight.IsPolymorphic))
                {
                    var leftTypeDisambiguation = typeLeft.IsOptionSet && typeLeft.OptionSetInfo != null ? $"({typeLeft.OptionSetInfo.Name})" : "";
                    var rightTypeDisambiguation = typeRight.IsOptionSet && typeRight.OptionSetInfo != null ? $"({typeRight.OptionSetInfo.Name})" : "";

                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, left.Parent, TexlStrings.ErrIncompatibleTypesForEquality_Left_Right,
                        typeLeft.GetKindString() + leftTypeDisambiguation,
                        typeRight.GetKindString() + rightTypeDisambiguation);
                    return;
                }

                // Special case for guid, it should produce an error on being compared to non-guid types
                if ((typeLeft.Equals(DType.Guid) && !typeRight.Equals(DType.Guid)) ||
                    (typeRight.Equals(DType.Guid) && !typeLeft.Equals(DType.Guid)))
                {
                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, left.Parent,
                        TexlStrings.ErrGuidStrictComparison);
                    return;
                }

                // Special case for option set values, it should produce an error when the base option sets are different
                if (typeLeft.Kind == DKind.OptionSetValue && !typeLeft.Accepts(typeRight))
                {
                    var leftTypeDisambiguation = typeLeft.IsOptionSet && typeLeft.OptionSetInfo != null ? $"({typeLeft.OptionSetInfo.Name})" : "";
                    var rightTypeDisambiguation = typeRight.IsOptionSet && typeRight.OptionSetInfo != null ? $"({typeRight.OptionSetInfo.Name})" : "";

                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, left.Parent, TexlStrings.ErrIncompatibleTypesForEquality_Left_Right,
                        typeLeft.GetKindString() + leftTypeDisambiguation,
                        typeRight.GetKindString() + rightTypeDisambiguation);

                    return;
                }

                // Special case for view values, it should produce an error when the base views are different
                if (typeLeft.Kind == DKind.ViewValue && !typeLeft.Accepts(typeRight))
                {
                    var leftTypeDisambiguation = typeLeft.IsView && typeLeft.ViewInfo != null ? $"({typeLeft.ViewInfo.Name})" : "";
                    var rightTypeDisambiguation = typeRight.IsView && typeRight.ViewInfo != null ? $"({typeRight.ViewInfo.Name})" : "";

                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, left.Parent, TexlStrings.ErrIncompatibleTypesForEquality_Left_Right,
                        typeLeft.GetKindString() + leftTypeDisambiguation,
                        typeRight.GetKindString() + rightTypeDisambiguation);

                    return;
                }

                if (!typeLeft.Accepts(typeRight) && !typeRight.Accepts(typeLeft))
                {
                    // Handle DateTime <=> Number comparison
                    if (DType.Number.Accepts(typeLeft) && DType.DateTime.Accepts(typeRight))
                    {
                        _txb.SetCoercedType(right, DType.Number);
                        return;
                    }
                    else if (DType.Number.Accepts(typeRight) && DType.DateTime.Accepts(typeLeft))
                    {
                        _txb.SetCoercedType(left, DType.Number);
                        return;
                    }

                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Warning, left.Parent, TexlStrings.ErrIncompatibleTypesForEquality_Left_Right,
                        typeLeft.GetKindString(),
                        typeRight.GetKindString());
                }
            }

            private void SetVariadicNodePurity(VariadicBase node)
            {
                Contracts.AssertValue(node);
                Contracts.AssertIndex(node.Id, _txb.IdLim);
                Contracts.AssertValue(node.Children);

                // Check for side-effects and statefulness of operation
                bool hasSideEffects = false;
                bool isStateful = false;
                bool isContextual = false;
                bool isConstant = true;
                bool isSelfContainedConstant = true;
                bool isBlockScopedConstant = true;
                bool isUnliftable = false;

                foreach (TexlNode child in node.Children)
                {
                    hasSideEffects |= _txb.HasSideEffects(child);
                    isStateful |= _txb.IsStateful(child);
                    isContextual |= _txb.IsContextual(child);
                    isConstant &= _txb.IsConstant(child);
                    isSelfContainedConstant &= _txb.IsSelfContainedConstant(child);
                    isBlockScopedConstant &= _txb.IsBlockScopedConstant(child) || _txb.IsPure(child);
                    isUnliftable |= _txb.IsUnliftable(child);
                }

                // If any child is unliftable then the full expression is unliftable
                _txb.SetIsUnliftable(node, isUnliftable);

                _txb.SetSideEffects(node, hasSideEffects);
                _txb.SetStateful(node, isStateful);
                _txb.SetContextual(node, isContextual);
                _txb.SetConstant(node, isConstant);
                _txb.SetSelfContainedConstant(node, isSelfContainedConstant);

                if (isBlockScopedConstant)
                    _txb.SetBlockScopedConstantNode(node);
            }

            public override void PostVisit(VariadicOpNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                switch (node.Op)
                {
                    case VariadicOp.Chain:
                        _txb.SetType(node, _txb.GetType(node.Children.Last()));
                        break;

                    default:
                        Contracts.Assert(false);
                        _txb.SetType(node, DType.Error);
                        break;
                }

                // Determine constancy.
                bool isConstant = true;
                bool isSelfContainedConstant = true;

                foreach (var child in node.Children)
                {
                    isConstant &= _txb.IsConstant(child);
                    isSelfContainedConstant &= _txb.IsSelfContainedConstant(child);
                    if (!isConstant && !isSelfContainedConstant)
                        break;
                }

                _txb.SetConstant(node, isConstant);
                _txb.SetSelfContainedConstant(node, isSelfContainedConstant);

                SetVariadicNodePurity(node);
                _txb.SetScopeUseSet(node, JoinScopeUseSets(node.Children));
            }

            private static bool IsValidAccessToScopedProperty(IExternalControl lhsControl, IExternalControlProperty rhsProperty, IExternalControl currentControl, IExternalControlProperty currentProperty, bool isBehaviorOnly = false)
            {
                Contracts.AssertValue(lhsControl);
                Contracts.AssertValue(rhsProperty);
                Contracts.AssertValue(currentControl);
                Contracts.AssertValue(currentProperty);

                if (lhsControl.IsComponentControl &&
                   lhsControl.Template.ComponentType == ComponentType.CanvasComponent &&
                   (currentControl.IsComponentControl ||
                   (currentControl.TopParentOrSelf is IExternalControl{IsComponentControl: false})))
                {
                    // Behavior property is blocked from outside the component.
                    if (isBehaviorOnly)
                        return false;

                    // If current property is output property of the component then access is allowed.
                    // Or if the rhs property is out put property then it's allowed which could only be possible if the current control is component definition.
                    return currentProperty.IsImmutableOnInstance || rhsProperty.IsImmutableOnInstance;
                }

                return true;
            }

            private bool IsValidScopedPropertyFunction(CallNode node, CallInfo info)
            {
                Contracts.AssertValue(node);
                Contracts.AssertIndex(node.Id, _txb.IdLim);
                Contracts.AssertValue(info);
                Contracts.AssertValue(_txb.Control);

                var currentControl = _txb.Control;
                var currentProperty = _txb.Property;
                if (currentControl.IsComponentControl && currentControl.Template.ComponentType != ComponentType.CanvasComponent)
                    return true;

                var infoTexlFunction = info.Function;
                if (_txb._glue.IsComponentScopedPropertyFunction(infoTexlFunction))
                {
                    // Component custom behavior properties can only be accessed by controls within a component.
                    if (_txb.Document.TryGetControlByUniqueId(infoTexlFunction.Namespace.Name.Value, out var lhsControl) &&
                        lhsControl.Template.TryGetProperty(infoTexlFunction.Name, out var rhsProperty))
                    {
                        return IsValidAccessToScopedProperty(lhsControl, rhsProperty, currentControl, currentProperty, infoTexlFunction.IsBehaviorOnly);
                    }
                }

                return true;
            }

            private void SetCallNodePurity(CallNode node, CallInfo info)
            {
                Contracts.AssertValue(node);
                Contracts.AssertIndex(node.Id, _txb.IdLim);
                Contracts.AssertValue(node.Args);

                bool hasSideEffects = _txb.HasSideEffects(node.Args);
                bool isStateFul = _txb.IsStateful(node.Args);

                if (info?.Function != null)
                {
                    var infoTexlFunction = info.Function;

                    if (_txb._glue.IsComponentScopedPropertyFunction(infoTexlFunction))
                    {
                        // We only have to check the property's rule and the calling arguments for purity as scoped variables
                        // (default values) are by definition data rules and therefore always pure.
                        if (_txb.Document.TryGetControlByUniqueId(infoTexlFunction.Namespace.Name.Value, out var ctrl) &&
                            ctrl.TryGetRule(new DName(infoTexlFunction.Name), out IExternalRule rule))
                        {
                            hasSideEffects |= rule.Binding.HasSideEffects(rule.Binding.Top);
                            isStateFul |= rule.Binding.IsStateful(rule.Binding.Top);
                        }
                    }
                    else
                    {
                        hasSideEffects |= !infoTexlFunction.IsSelfContained;
                        isStateFul |= !infoTexlFunction.IsStateless;
                    }
                }

                _txb.SetSideEffects(node, hasSideEffects);
                _txb.SetStateful(node, isStateFul);
                _txb.SetContextual(node, _txb.IsContextual(node.Args)); // The head of a function cannot be contextual at the moment

                // Nonempty variable weight containing variable "x" implies this node or a node that is to be
                // evaluated before this node is non pure and modifies "x"
                _txb.AddVolatileVariables(node, _txb.GetVolatileVariables(node.Args));

                // True if this node or one of its children contains any element of this node's variable weight
                _txb.SetIsUnliftable(node, _txb.IsUnliftable(node.Args));
            }

            private ScopeUseSet GetCallNodeScopeUseSet(CallNode node, CallInfo info)
            {
                Contracts.AssertValue(node);

                // If there are lambda params, find their scopes
                if (info?.Function == null)
                {
                    return ScopeUseSet.GlobalsOnly;
                }
                else if (!info.Function.HasLambdas)
                {
                    return JoinScopeUseSets(node.Args);
                }
                else
                {
                    var args = node.Args.Children;
                    ScopeUseSet set = ScopeUseSet.GlobalsOnly;

                    for (int i = 0; i < args.Length; i++)
                    {
                        ScopeUseSet argScopeUseSet = _txb.GetScopeUseSet(args[i]);

                        // Translate the set to the parent (invocation) scope, to indicate that we are moving outside the lambda.
                        if (i <= info.Function.MaxArity && info.Function.IsLambdaParam(i))
                            argScopeUseSet = argScopeUseSet.TranslateToParentScope();

                        set = set.Union(argScopeUseSet);
                    }

                    return set;
                }
            }

            private bool TryGetFunctionNameLookupInfo(CallNode node, DPath functionNamespace, out NameLookupInfo lookupInfo)
            {
                Contracts.AssertValue(node);
                Contracts.AssertValid(functionNamespace);

                lookupInfo = default;
                if (!(node.HeadNode is DottedNameNode dottedNameNode))
                    return false;

                if (!(dottedNameNode.Left is FirstNameNode) &&
                    !(dottedNameNode.Left is ParentNode) &&
                    !(dottedNameNode.Left is SelfNode))
                {
                    return false;
                }

                if (!_nameResolver.LookupGlobalEntity(functionNamespace.Name, out lookupInfo) ||
                    lookupInfo.Data == null ||
                    !(lookupInfo.Data is IExternalControl))
                {
                    return false;
                }

                return true;
            }

            public override bool PreVisit(BinaryOpNode node)
            {
                Contracts.AssertValue(node);

                var volatileVariables = _txb.GetVolatileVariables(node);
                _txb.AddVolatileVariables(node.Left, volatileVariables);
                _txb.AddVolatileVariables(node.Right, volatileVariables);

                return true;
            }

            public override bool PreVisit(UnaryOpNode node)
            {
                Contracts.AssertValue(node);

                var volatileVariables = _txb.GetVolatileVariables(node);
                _txb.AddVolatileVariables(node.Child, volatileVariables);

                return true;
            }

            /// <summary>
            /// Accepts each child, records which identifiers are affected by each child and sets the binding
            /// appropriately.
            /// </summary>
            /// <param name="node"></param>
            /// <returns></returns>
            public override bool PreVisit(VariadicOpNode node)
            {
                var runningWeight = _txb.GetVolatileVariables(node);
                var isUnliftable = false;

                foreach (var child in node.Children)
                {
                    _txb.AddVolatileVariables(child, runningWeight);
                    child.Accept(this);
                    runningWeight = runningWeight.Union(_txb.GetVolatileVariables(child));
                    isUnliftable |= _txb.IsUnliftable(child);
                }

                _txb.AddVolatileVariables(node, runningWeight);
                _txb.SetIsUnliftable(node, isUnliftable);

                PostVisit(node);
                return false;
            }

            private void PreVisitHeadNode(CallNode node)
            {
                Contracts.AssertValue(node);

                // We want to set the correct error type. This is important for component instance rule replacement logic.
                if (_nameResolver == null && (node.HeadNode is DottedNameNode))
                {
                    node.HeadNode.Accept(this);
                }
            }

            private static void ArityError(int minArity, int maxArity, TexlNode node, int actual, IErrorContainer errors)
            {
                if (maxArity == int.MaxValue)
                    errors.Error(node, TexlStrings.ErrBadArityMinimum, actual, minArity);
                else if (minArity != maxArity)
                    errors.Error(node, TexlStrings.ErrBadArityRange, actual, minArity, maxArity);
                else
                    errors.Error(node, TexlStrings.ErrBadArity, actual, minArity);
            }

            public override bool PreVisit(CallNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                var funcNamespace = _txb.GetFunctionNamespace(node, this);
                var overloads = LookupFunctions(funcNamespace, node.Head.Name.Value);
                if (!overloads.Any())
                {
                    _txb.ErrorContainer.Error(node, TexlStrings.ErrUnknownFunction);
                    _txb.SetInfo(node, new CallInfo(node));
                    _txb.SetType(node, DType.Error);

                    PreVisitHeadNode(node);
                    PreVisitBottomUp(node, 0);
                    FinalizeCall(node);

                    return false;
                }

                var overloadsWithMetadataTypeSupportedArgs = overloads.Where(func => func.SupportsMetadataTypeArg && !func.HasLambdas);
                if (overloadsWithMetadataTypeSupportedArgs.Any())
                {
                    // Overloads are not supported for such functions yet.
                    Contracts.Assert(overloadsWithMetadataTypeSupportedArgs.Count() == 1);

                    PreVisitMetadataArg(node, overloadsWithMetadataTypeSupportedArgs.FirstOrDefault());
                    FinalizeCall(node);
                    return false;
                }

                // If there are no overloads with lambdas, we can continue the visitation and
                // yield to the normal overload resolution.
                var overloadsWithLambdas = overloads.Where(func => func.HasLambdas);
                if (!overloadsWithLambdas.Any())
                {
                    // We may still need a scope to determine inline-record types
                    Scope maybeScope = null;
                    int startArg = 0;

                    // Construct a scope if diplay names are enabled and this function requires a data source scope for inline records
                    if (_txb.Document != null && _txb.Document.Properties.EnabledFeatures.IsUseDisplayNameMetadataEnabled &&
                        overloads.Where(func => func.RequiresDataSourceScope).Any() && node.Args.Count > 0)
                    {
                        // Visit the first arg if it exists. This will give us the scope type for any subsequent lambda/predicate args.
                        TexlNode nodeInp = node.Args.Children[0];
                        nodeInp.Accept(this);

                        if (nodeInp.Kind == NodeKind.As)
                            _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, node, TexlStrings.ErrAsNotInContext);

                        // Only if there is a projection map associated with this will we need to set a scope
                        DType typescope = _txb.GetType(nodeInp);

                        if (typescope.AssociatedDataSources.Any() && typescope.IsTable)
                            maybeScope = new Scope(node, _currentScope, typescope.ToRecord(), createsRowScope: false);

                        startArg++;
                    }

                    PreVisitHeadNode(node);
                    PreVisitBottomUp(node, startArg, maybeScope);
                    FinalizeCall(node);
                    return false;
                }

                // We support a single overload with lambdas. Otherwise we have a conceptual chicken-and-egg
                // problem, whereby in order to bind the lambda args we need the precise overload (for
                // its lambda mask), which in turn requires binding the args (for their types).
                Contracts.Assert(overloadsWithLambdas.Count() == 1, "Incorrect multiple overloads with lambdas.");
                var maybeFunc = overloadsWithLambdas.Single();
                Contracts.Assert(maybeFunc.HasLambdas);

                var scopeInfo = maybeFunc.ScopeInfo;
                IDelegationMetadata metadata = null;
                int numOverloads = overloads.Count();

                Scope scopeNew = null;
                IExpandInfo ExpandInfo;

                // Check for matching arities.
                int carg = node.Args.Count;
                if (carg < maybeFunc.MinArity || carg > maybeFunc.MaxArity)
                {
                    int argCountVisited = 0;
                    if (numOverloads == 1)
                    {
                        DType scope = DType.Invalid;
                        var required = false;
                        DName scopeIdentifier = default;
                        if (scopeInfo.ScopeType != null)
                        {
                            scopeNew = new Scope(node, _currentScope, scopeInfo.ScopeType, skipForInlineRecords: maybeFunc.SkipScopeForInlineRecords);
                        }
                        else if (carg > 0)
                        {
                            // Visit the first arg. This will give us the scope type for any subsequent lambda/predicate args.
                            TexlNode nodeInp = node.Args.Children[0];
                            nodeInp.Accept(this);

                            // Determine the Scope Identifier using the 1st arg
                            required = _txb.GetScopeIdent(nodeInp, out scopeIdentifier);

                            if (scopeInfo.CheckInput(nodeInp, _txb.GetType(nodeInp), out scope))
                            {
                                if (_txb.TryGetEntityInfo(nodeInp, out ExpandInfo))
                                {
                                    scopeNew = new Scope(node, _currentScope, scope, scopeIdentifier, required, ExpandInfo, skipForInlineRecords: maybeFunc.SkipScopeForInlineRecords);
                                }
                                else
                                {
                                    maybeFunc.TryGetDelegationMetadata(node, _txb, out metadata);
                                    scopeNew = new Scope(node, _currentScope, scope, scopeIdentifier, required, metadata, skipForInlineRecords: maybeFunc.SkipScopeForInlineRecords);
                                }
                            }

                            argCountVisited = 1;
                        }

                        // If there is only one function with this name and its arity doesn't match,
                        // that means the invocation is erroneous.
                        ArityError(maybeFunc.MinArity, maybeFunc.MaxArity, node, carg, _txb.ErrorContainer);
                        _txb.SetInfo(node, new CallInfo(maybeFunc, node, scope, scopeIdentifier, required, _currentScope.Nest));
                        _txb.SetType(node, maybeFunc.ReturnType);
                    }

                    // Either way continue the visitation. If we do have overloads,
                    // a different overload with no lambdas may match (including the arity).
                    PreVisitBottomUp(node, argCountVisited, scopeNew);
                    FinalizeCall(node);

                    return false;
                }

                // All functions with lambdas have at least one arg.
                Contracts.Assert(carg > 0);

                // The zeroth arg should not be a lambda. Instead it defines the context type for the lambdas.
                Contracts.Assert(!maybeFunc.IsLambdaParam(0));

                TexlNode[] args = node.Args.Children;
                DType[] argTypes = new DType[args.Length];

                // We need to know which variables are volatile in case the first argument is or contains a
                // reference to a volatile variable and we need to control its liftability
                var volatileVariables = _txb.GetVolatileVariables(node);

                // Visit the first arg. This will give us the scope type for the subsequent lambda args.
                TexlNode nodeInput = args[0];
                _txb.AddVolatileVariables(nodeInput, volatileVariables);
                nodeInput.Accept(this);

                IList<FirstNameNode> dsNodes;
                FirstNameNode dsNode;
                if (maybeFunc.TryGetDataSourceNodes(node, _txb, out dsNodes) && ((dsNode = dsNodes.FirstOrDefault()) != default(FirstNameNode)))
                    _currentScopeDsNodeId = dsNode.Id;

                DType typeInput = (argTypes[0] = _txb.GetType(nodeInput));

                // Get the cursor type for this arg. Note we're not adding document errors at this point.
                DType typeScope;
                DName scopeIdent = default;
                var identRequired = false;
                bool fArgsValid = true;
                if (scopeInfo.ScopeType != null)
                {
                    typeScope = scopeInfo.ScopeType;

                    // For functions with a Scope Type, there is no ScopeIdent needed
                }
                else
                {
                    fArgsValid = scopeInfo.CheckInput(nodeInput, typeInput, out typeScope);

                    // Determine the scope identifier using the first node for lambda params
                    identRequired = _txb.GetScopeIdent(nodeInput, out scopeIdent);
                }

                if (!fArgsValid)
                {
                    if (numOverloads == 1)
                    {
                        // If there is a single function with this name, and the first arg is not
                        // a good match, then we have an erroneous invocation.
                        _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, nodeInput, TexlStrings.ErrBadType);
                        _txb.ErrorContainer.Error(node, TexlStrings.ErrInvalidArgs_Func, maybeFunc.Name);
                        _txb.SetInfo(node, new CallInfo(maybeFunc, node, typeScope, scopeIdent, identRequired, _currentScope.Nest));
                        _txb.SetType(node, maybeFunc.ReturnType);
                    }

                    // Yield to the normal overload resolution either way. We already visited and
                    // bound the first argument, hence the 1'.
                    PreVisitBottomUp(node, 1);
                    FinalizeCall(node);

                    return false;
                }

                // At this point we know we have an invocation of our function with lambdas (as opposed
                // to an invocation of a different overload). Pin that, and make a best effort to match
                // the rest of the args. Binding failures along the way become proper document errors.

                // We don't want to check and mark this function as async for now as IsAsyncInvocation function calls into IsServerDelegatable which
                // requires more contexts about the args which is only available after we visit all the children. So delay this after visiting
                // children.
                _txb.SetInfo(node, new CallInfo(maybeFunc, node, typeScope, scopeIdent, identRequired, _currentScope.Nest), markIfAsync: false);

                if (_txb.TryGetEntityInfo(nodeInput, out ExpandInfo))
                {
                    scopeNew = new Scope(node, _currentScope, typeScope, scopeIdent, identRequired, ExpandInfo, skipForInlineRecords: maybeFunc.SkipScopeForInlineRecords);
                }
                else
                {
                    maybeFunc.TryGetDelegationMetadata(node, _txb, out metadata);
                    scopeNew = new Scope(node, _currentScope, typeScope, scopeIdent, identRequired, metadata, skipForInlineRecords: maybeFunc.SkipScopeForInlineRecords);
                }

                // Process the rest of the args.
                for (int i = 1; i < carg; i++)
                {
                    Contracts.Assert(_currentScope == scopeNew || _currentScope == scopeNew.Parent);

                    if (maybeFunc.AllowsRowScopedParamDelegationExempted(i))
                        _txb.SetSupportingRowScopedDelegationExemptionNode(args[i]);
                    if (maybeFunc.IsEcsExcemptedLambda(i))
                        _txb.SetEcsExcemptLambdaNode(args[i]);

                    if (volatileVariables != null)
                        _txb.AddVolatileVariables(args[i], volatileVariables);

                    // Use the new scope only for lambda args.
                    _currentScope = (maybeFunc.IsLambdaParam(i) && scopeInfo.AppliesToArgument(i)) ? scopeNew : scopeNew.Parent;
                    args[i].Accept(this);

                    _txb.AddVolatileVariables(node, _txb.GetVolatileVariables(args[i]));

                    argTypes[i] = _txb.GetType(args[i]);
                    Contracts.Assert(argTypes[i].IsValid);

                    // Async lambdas are not (yet) supported for this function. Flag these with errors.
                    if (_txb.IsAsync(args[i]) && !scopeInfo.SupportsAsyncLambdas)
                    {
                        fArgsValid = false;
                        _txb.ErrorContainer.Error(DocumentErrorSeverity.Severe, node, TexlStrings.ErrAsyncLambda);
                    }

                    // Accept should leave the scope as it found it.
                    Contracts.Assert(_currentScope == ((maybeFunc.IsLambdaParam(i) && scopeInfo.AppliesToArgument(i)) ? scopeNew : scopeNew.Parent));
                }

                // Now check and mark the path as async.
                if (maybeFunc.IsAsyncInvocation(node, _txb))
                    _txb.FlagPathAsAsync(node);

                _currentScope = scopeNew.Parent;
                PostVisit(node.Args);

                // Typecheck the invocation.
                DType returnType;
                Dictionary<TexlNode, DType> nodeToCoercedTypeMap = null;

                // Typecheck the invocation and infer the return type.
                fArgsValid &= maybeFunc.CheckInvocation(_txb, args, argTypes, _txb.ErrorContainer, out returnType, out nodeToCoercedTypeMap);

                // This is done because later on, if a CallNode has a return type of Error, you can assert HasErrors on it.
                // This was not done for UnaryOpNodes, BinaryOpNodes, CompareNodes.
                // This doesn't need to be done on the other nodes (but can) because their return type doesn't depend
                // on their argument types.
                if (!fArgsValid)
                    _txb.ErrorContainer.Error(DocumentErrorSeverity.Severe, node, TexlStrings.ErrInvalidArgs_Func, maybeFunc.Name);

                // Set the inferred return type for the node.
                _txb.SetType(node, returnType);

                if (fArgsValid && nodeToCoercedTypeMap != null)
                {
                    foreach (var nodeToCoercedTypeKvp in nodeToCoercedTypeMap)
                        _txb.SetCoercedType(nodeToCoercedTypeKvp.Key, nodeToCoercedTypeKvp.Value);
                }

                FinalizeCall(node);

                // We fully processed the call, so don't visit children or call PostVisit.
                return false;
            }

            private void FinalizeCall(CallNode node)
            {
                Contracts.AssertValue(node);

                CallInfo callInfo = _txb.GetInfo(node);

                // Set the node purity and context
                SetCallNodePurity(node, callInfo);
                _txb.SetScopeUseSet(node, GetCallNodeScopeUseSet(node, callInfo));

                var func = callInfo?.Function;
                if (func == null)
                    return;

                // Invalid datasources always result in error
                if (func.IsBehaviorOnly && !_txb.IsBehavior)
                {
                    _txb.ErrorContainer.EnsureError(node, TexlStrings.ErrBehaviorPropertyExpected);
                }
                // Test-only functions can only be used within test cases.
                else if (func.IsTestOnly && _txb.Property != null && !_txb.Property.IsTestCaseProperty)
                {
                    _txb.ErrorContainer.EnsureError(node, TexlStrings.ErrTestPropertyExpected);
                }
                // Auto-refreshable functions cannot be used in behavior rules.
                else if (func.IsAutoRefreshable && _txb.IsBehavior)
                {
                    _txb.ErrorContainer.EnsureError(node, TexlStrings.ErrAutoRefreshNotAllowed);
                }
                // Give warning if returning dynamic metadata without a known dynamic type
                else if (func.IsDynamic && _nameResolver.Document.Properties.EnabledFeatures.IsDynamicSchemaEnabled)
                {
                    if (!func.CheckForDynamicReturnType(_txb, node.Args.Children))
                    {
                        _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Warning, node, TexlStrings.WarnDynamicMetadata);
                    }
                }
                else if (_txb.Control != null && _txb.Property != null && !IsValidScopedPropertyFunction(node, callInfo))
                {
                    var errorMessage = callInfo.Function.IsBehaviorOnly ? TexlStrings.ErrUnSupportedComponentBehaviorInvocation : TexlStrings.ErrUnSupportedComponentDataPropertyAccess;
                    _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Critical, node, errorMessage);
                }
                // Apply custom function validation last
                else if (!func.PostVisitValidation(_txb, node))
                {
                    // Check to see if we are a side-effectful function operating on on invalid datasource.
                    if (IsIncorrectlySideEffectful(node, out var errorKey, out var badAncestor))
                    {
                        _txb.ErrorContainer.EnsureError(node, errorKey, badAncestor.Head.Name);
                    }
                }

                _txb.CheckAndMarkAsDelegatable(node);
                _txb.CheckAndMarkAsPageable(node, func);

                // A function will produce a constant output (and have no side-effects, which is important for
                // caching/precomputing the result) iff the function is pure and its arguments are constant.
                _txb.SetConstant(node, func.IsPure && _txb.IsConstant(node.Args));
                _txb.SetSelfContainedConstant(node, func.IsPure && _txb.IsSelfContainedConstant(node.Args));

                // Mark node as blockscoped constant if the function's return value only depends on the global variable
                // This node will skip delegation check, be codegened as constant and be simply passed into the delegation query.
                // e.g. Today() in formula Filter(CDS, CreatedDate < Today())
                if (func.IsGlobalReliant || (func.IsPure && _txb.IsBlockScopedConstant(node.Args)))
                    _txb.SetBlockScopedConstantNode(node);

                // Update field projection info
                if (_txb.QueryOptions != null)
                    func.UpdateDataQuerySelects(node, _txb, _txb.QueryOptions);
            }

            private bool IsIncorrectlySideEffectful(CallNode node, out ErrorResourceKey errorKey, out CallNode badAncestor)
            {
                Contracts.AssertValue(node);

                badAncestor = null;
                errorKey = new ErrorResourceKey();

                CallInfo call = _txb.GetInfo(node).VerifyValue();
                TexlFunction func = call.Function;
                if (func == null || func.IsSelfContained)
                    return false;

                IExternalDataSource ds;
                if (!func.TryGetDataSource(node, _txb, out ds))
                    ds = null;

                Scope ancestorScope = _currentScope;
                while (ancestorScope != null)
                {
                    if (ancestorScope.Call != null)
                    {
                        CallInfo ancestorCall = _txb.GetInfo(ancestorScope.Call);

                        // For record-scoped rules, if we are processing a nested call node, it's possible the node info may not be set yet
                        // In that case, verify that the node has overloads that support record scoping.
                        if (ancestorCall == null && LookupFunctions(ancestorScope.Call.Head.Namespace, ancestorScope.Call.Head.Name.Value).Any(overload => overload.RequiresDataSourceScope))
                        {
                            ancestorScope = ancestorScope.Parent;
                            continue;
                        }

                        var ancestorFunc = ancestorCall.Function;
                        var ancestorScopeInfo = ancestorCall.Function?.ScopeInfo;

                        // Check for bad scope modification
                        if (ancestorFunc != null && ancestorScopeInfo != null && ds != null && ancestorScopeInfo.IteratesOverScope)
                        {
                            IExternalDataSource ancestorDs;
                            if (ancestorFunc.TryGetDataSource(ancestorScope.Call, _txb, out ancestorDs) && ancestorDs == ds)
                            {
                                errorKey = TexlStrings.ErrScopeModificationLambda;
                                badAncestor = ancestorScope.Call;
                                return true;
                            }
                        }

                        // Check for completely blocked functions.
                        if (ancestorFunc != null &&
                            ancestorScopeInfo != null &&
                            ancestorScopeInfo.HasNondeterministicOperationOrder &&
                            !func.AllowedWithinNondeterministicOperationOrder)
                        {
                            errorKey = TexlStrings.ErrFunctionDisallowedWithinNondeterministicOperationOrder;
                            badAncestor = ancestorScope.Call;
                            return true;
                        }
                    }

                    // Pop up to the next scope.
                    ancestorScope = ancestorScope.Parent;
                }

                return false;
            }

            public override void PostVisit(CallNode node)
            {
                Contracts.Assert(false, "Should never get here");
            }

            private bool TryGetAffectScopeVariableFunc(CallNode node, out TexlFunction func)
            {
                Contracts.AssertValue(node);

                var funcNamespace = _txb.GetFunctionNamespace(node, this);
                var overloads = LookupFunctions(funcNamespace, node.Head.Name.Value).Where(fnc => fnc.AffectsScopeVariable).ToArray();

                Contracts.Assert(overloads.Length == 1 || overloads.Length == 0, "Lookup Affect scopeVariable Function by CallNode should be 0 or 1");

                func = overloads.Length == 1 ? overloads[0].VerifyValue() : null;
                return func != null;
            }

            private void PreVisitMetadataArg(CallNode node, TexlFunction func)
            {
                AssertValid();
                Contracts.AssertValue(node);
                Contracts.AssertValue(func);
                Contracts.Assert(func.SupportsMetadataTypeArg);
                Contracts.Assert(!func.HasLambdas);

                TexlNode[] args = node.Args.Children;
                int argCount = args.Length;

                DType returnType = func.ReturnType;
                for (int i = 0; i < argCount; i++)
                {
                    if (func.IsMetadataTypeArg(i))
                        args[i].Accept(_txb.BinderNodeMetadataArgTypeVisitor);
                    else
                        args[i].Accept(this);

                    if (args[i].Kind == NodeKind.As)
                        _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, node, TexlStrings.ErrAsNotInContext);
                }

                PostVisit(node.Args);

                var info = _txb.GetInfo(node);
                // If PreVisit resulted in errors for the node (and a non-null CallInfo),
                // we're done -- we have a match and appropriate errors logged already.
                if (_txb.ErrorContainer.HasErrors(node))
                {
                    Contracts.Assert(info != null);

                    return;
                }

                Contracts.AssertNull(info);

                _txb.SetInfo(node, new CallInfo(func, node));
                if (argCount < func.MinArity || argCount > func.MaxArity)
                {
                    ArityError(func.MinArity, func.MaxArity, node, argCount, _txb.ErrorContainer);
                    _txb.SetType(node, returnType);
                    return;
                }

                DType[] argTypes = args.Select(_txb.GetType).ToArray();
                bool fArgsValid;

                // Typecheck the invocation and infer the return type.
                fArgsValid = func.CheckInvocation(_txb, args, argTypes, _txb.ErrorContainer, out returnType, out _);
                if (!fArgsValid)
                    _txb.ErrorContainer.Error(DocumentErrorSeverity.Severe, node, TexlStrings.ErrInvalidArgs_Func, func.Name);

                _txb.SetType(node, returnType);
            }

            private void PreVisitBottomUp(CallNode node, int argCountVisited, Scope scopeNew = null)
            {
                AssertValid();
                Contracts.AssertValue(node);
                Contracts.AssertIndexInclusive(argCountVisited, node.Args.Count);
                Contracts.AssertValueOrNull(scopeNew);

                TexlNode[] args = node.Args.Children;
                int argCount = args.Length;

                var info = _txb.GetInfo(node);
                Contracts.AssertValueOrNull(info);
                Contracts.Assert(info == null || _txb.ErrorContainer.HasErrors(node));

                // Attempt to get the overloads, so we can determine the scope to use for datasource name matching
                // We're only interested in the overloads without lambdas, since those were
                // already processed in PreVisit.
                var funcNamespace = _txb.GetFunctionNamespace(node, this);
                var overloads = LookupFunctions(funcNamespace, node.Head.Name.Value)
                    .Where(fnc => !fnc.HasLambdas)
                    .ToArray();

                TexlFunction funcWithScope = null;
                if (info != null && info.Function != null && scopeNew != null)
                    funcWithScope = info.Function;

                Contracts.Assert(scopeNew == null || funcWithScope != null || overloads.Any(fnc => fnc.RequiresDataSourceScope));

                bool affectScopeVariable = TryGetAffectScopeVariableFunc(node, out var affectScopeVariablefunc);

                Contracts.Assert(affectScopeVariable ^ affectScopeVariablefunc == null);

                var volatileVariables = _txb.GetVolatileVariables(node);
                for (int i = argCountVisited; i < argCount; i++)
                {
                    Contracts.AssertValue(args[i]);

                    if (affectScopeVariable)
                    {
                        // If the function affects app/component variable, update the cache info if it is the arg affects scopeVariableName.
                        _txb.AffectsScopeVariableName = affectScopeVariablefunc.ScopeVariableNameAffectingArg() == i;
                    }
                    // Use the new scope only for lambda args and args with datasource scope for display name matching.
                    if (scopeNew != null)
                    {
                        if (overloads.Any(fnc => fnc.ArgMatchesDatasourceType(i)) || (i <= funcWithScope.MaxArity && funcWithScope.IsLambdaParam(i)))
                            _currentScope = scopeNew;
                        else
                            _currentScope = scopeNew.Parent;
                    }

                    if (volatileVariables != null)
                        _txb.AddVolatileVariables(args[i], volatileVariables);

                    args[i].Accept(this);

                    // In case weight was added during visitation
                    _txb.AddVolatileVariables(node, _txb.GetVolatileVariables(args[i]));

                    if (args[i].Kind == NodeKind.As)
                        _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, args[i], TexlStrings.ErrAsNotInContext);
                }

                if (scopeNew != null)
                    _currentScope = scopeNew.Parent;

                // Since variable weight may have changed as we accepted the children, we need to propagate
                // this value to the args
                var adjustedVolatileVariables = _txb.GetVolatileVariables(node);
                if (adjustedVolatileVariables != null)
                    _txb.AddVolatileVariables(node.Args, adjustedVolatileVariables);

                PostVisit(node.Args);

                // If PreVisit resulted in errors for the node (and a non-null CallInfo),
                // we're done -- we have a match and appropriate errors logged already.
                if (_txb.ErrorContainer.HasErrors(node))
                {
                    Contracts.Assert(info != null);

                    return;
                }

                Contracts.AssertNull(info);

                // There should be at least one possible match at this point.
                Contracts.Assert(overloads.Length > 0);

                if (overloads.Length > 1)
                {
                    PreVisitWithOverloadResolution(node, overloads);
                    return;
                }

                // We have a single possible match. Bind as usual, which will generate appropriate
                // document errors for incorrect arguments, etc.
                TexlFunction func = overloads[0].VerifyValue();

                if (_txb._glue.IsComponentScopedPropertyFunction(func))
                {
                    if (TryGetFunctionNameLookupInfo(node, funcNamespace, out NameLookupInfo lookupInfo))
                    {
                        var headNode = node.HeadNode as DottedNameNode;
                        Contracts.AssertValue(headNode);

                        UpdateBindKindUseFlags(BindKind.Control);
                        _txb.SetInfo(node, new CallInfo(func, node, lookupInfo.Data));
                    }
                    else
                    {
                        _txb.ErrorContainer.Error(node, TexlStrings.ErrInvalidName);
                        _txb.SetInfo(node, new CallInfo(node));
                        _txb.SetType(node, DType.Error);
                        return;
                    }
                }
                else
                {
                    _txb.SetInfo(node, new CallInfo(func, node));
                }

                Contracts.Assert(!func.HasLambdas);

                DType returnType = func.ReturnType;
                if (argCount < func.MinArity || argCount > func.MaxArity)
                {
                    ArityError(func.MinArity, func.MaxArity, node, argCount, _txb.ErrorContainer);
                    _txb.SetType(node, returnType);
                    return;
                }

                var modifiedIdentifiers = func.GetIdentifierOfModifiedValue(args, out _);
                if (modifiedIdentifiers != null)
                    _txb.AddVolatileVariables(node, modifiedIdentifiers.Select(identifier => identifier.Name.ToString()).ToImmutableHashSet());

                // Typecheck the invocation and infer the return type.
                DType[] argTypes = args.Select(_txb.GetType).ToArray();
                bool fArgsValid;
                Dictionary<TexlNode, DType> nodeToCoercedTypeMap = null;

                // Typecheck the invocation and infer the return type.
                fArgsValid = func.CheckInvocation(_txb, args, argTypes, _txb.ErrorContainer, out returnType, out nodeToCoercedTypeMap);

                if (!fArgsValid && !func.HasPreciseErrors)
                    _txb.ErrorContainer.Error(DocumentErrorSeverity.Severe, node, TexlStrings.ErrInvalidArgs_Func, func.Name);

                _txb.SetType(node, returnType);

                if (fArgsValid && nodeToCoercedTypeMap != null)
                {
                    foreach (var nodeToCoercedTypeKvp in nodeToCoercedTypeMap)
                        _txb.SetCoercedType(nodeToCoercedTypeKvp.Key, nodeToCoercedTypeKvp.Value);
                }
            }

            private IEnumerable<TexlFunction> LookupFunctions(DPath theNamespace, string name)
            {
                Contracts.Assert(theNamespace.IsValid);
                Contracts.AssertNonEmpty(name);

                if (_nameResolver != null)
                    return _nameResolver.LookupFunctions(theNamespace, name);
                else
                    return Enumerable.Empty<TexlFunction>();
            }

            /// <summary>
            /// Tries to get the best suited overload for <see cref="node"/> according to <see cref="txb"/> and
            /// returns true if it is found.
            /// </summary>
            /// <param name="txb">
            /// Binding that will help select the best overload
            /// </param>
            /// <param name="node">
            /// CallNode for which the best overload will be determined
            /// </param>
            /// <param name="argTypes">
            /// List of argument types for <see cref="node.Args"/>
            /// </param>
            /// <param name="overloads">
            /// All overloads for <see cref="node"/>. An element of this list will be returned.
            /// </param>
            /// <param name="bestOverload">
            /// Set to the best overload when this method completes
            /// </param>
            /// <param name="nodeToCoercedTypeMap">
            /// Set to the types to which <see cref="node.Args"/> must be coerced in order for
            /// <see cref="bestOverload"/> to be valid
            /// </param>
            /// <param name="returnType">
            /// The return type for <see cref="bestOverload"/>
            /// </param>
            /// <returns>
            /// True if a valid overload was found, false if not.
            /// </returns>
            private static bool TryGetBestOverload(TexlBinding txb, CallNode node, DType[] argTypes, TexlFunction[] overloads, out TexlFunction bestOverload, out Dictionary<TexlNode, DType> nodeToCoercedTypeMap, out DType returnType)
            {
                Contracts.AssertValue(node, nameof(node));
                Contracts.AssertValue(overloads, nameof(overloads));

                TexlNode[] args = node.Args.Children;
                int carg = args.Length;
                returnType = DType.Unknown;

                TexlFunction matchingFuncWithCoercion = null;
                DType matchingFuncWithCoercionReturnType = DType.Invalid;
                nodeToCoercedTypeMap = null;
                Dictionary<TexlNode, DType> matchingFuncWithCoercionNodeToCoercedTypeMap = null;

                foreach (var maybeFunc in overloads)
                {
                    Contracts.Assert(!maybeFunc.HasLambdas);

                    nodeToCoercedTypeMap = null;

                    if (carg < maybeFunc.MinArity || carg > maybeFunc.MaxArity)
                        continue;

                    bool typeCheckSucceeded = false;

                    IErrorContainer warnings = new LimitedSeverityErrorContainer(txb.ErrorContainer, DocumentErrorSeverity.Warning);
                    // Typecheck the invocation and infer the return type.
                    typeCheckSucceeded = maybeFunc.CheckInvocation(txb, args, argTypes, warnings, out returnType, out nodeToCoercedTypeMap);

                    if (typeCheckSucceeded)
                    {
                        if (nodeToCoercedTypeMap == null)
                        {
                            // We found an overload that matches without type coercion.  The correct return type
                            // and, trivially, the nodeToCoercedTypeMap are properly set at this point.
                            bestOverload = maybeFunc;
                            return true;
                        }

                        // We found an overload that matches but with type coercion. Keep going
                        // until we find another overload that matches without type coercion.
                        // If we cannot find one, we will use this overload only if there is no other
                        // overload that involves fewer coercions.
                        if (matchingFuncWithCoercion == null || nodeToCoercedTypeMap.Count < matchingFuncWithCoercionNodeToCoercedTypeMap.VerifyValue().Count)
                        {
                            matchingFuncWithCoercionNodeToCoercedTypeMap = nodeToCoercedTypeMap;
                            matchingFuncWithCoercion = maybeFunc;
                            matchingFuncWithCoercionReturnType = returnType;
                        }
                    }
                }

                // We've matched, but with coercion required.
                if (matchingFuncWithCoercionNodeToCoercedTypeMap != null)
                {
                    bestOverload = matchingFuncWithCoercion;
                    nodeToCoercedTypeMap = matchingFuncWithCoercionNodeToCoercedTypeMap;
                    returnType = matchingFuncWithCoercionReturnType;
                    return true;
                }

                // There are no good overloads
                bestOverload = null;
                nodeToCoercedTypeMap = null;
                returnType = null;
                return false;
            }

            private void PreVisitWithOverloadResolution(CallNode node, TexlFunction[] overloads)
            {
                Contracts.AssertValue(node);
                Contracts.AssertNull(_txb.GetInfo(node));
                Contracts.AssertValue(overloads);
                Contracts.Assert(overloads.Length > 1);
                Contracts.AssertAllValues(overloads);

                TexlNode[] args = node.Args.Children;
                var carg = args.Length;
                var argTypes = args.Select(_txb.GetType).ToArray();

                if (TryGetBestOverload(_txb, node, argTypes, overloads, out var function, out var nodeToCoercedTypeMap, out var returnType))
                {
                    _txb.SetInfo(node, new CallInfo(function, node));
                    _txb.SetType(node, returnType);

                    // If we found an overload and this value is set then we require parameter conversion
                    if (nodeToCoercedTypeMap != null)
                    {
                        foreach (var nodeToCoercedTypeKvp in nodeToCoercedTypeMap)
                            _txb.SetCoercedType(nodeToCoercedTypeKvp.Key, nodeToCoercedTypeKvp.Value);
                    }

                    return;
                }

                // TASK: 75086: use the closest overload (e.g. by arity, types) that we can find, for info/type propagation.
                // For now we're using the first overload that matches arities. This is still better in terms of error reporting than
                // completely losing precision and defaulting to DType.Error.
                var someFunc = overloads.FirstOrDefault(func => func.MinArity <= carg && carg <= func.MaxArity);

                // If nothing matches even the arity, we're done.
                if (someFunc == null)
                {
                    int minArity = overloads.Min(func => func.MinArity);
                    int maxArity = overloads.Max(func => func.MaxArity);
                    ArityError(minArity, maxArity, node, carg, _txb.ErrorContainer);

                    _txb.SetInfo(node, new CallInfo(overloads.First(), node));
                    _txb.SetType(node, DType.Error);
                    return;
                }

                // We exhausted the overloads without finding an exact match, so post a document error.
                if (!someFunc.HasPreciseErrors)
                    _txb.ErrorContainer.Error(node, TexlStrings.ErrInvalidArgs_Func, someFunc.Name);

                // The final CheckInvocation call will post all the necessary document errors.
                someFunc.CheckInvocation(_txb, args, argTypes, _txb.ErrorContainer, out returnType, out _);

                _txb.SetInfo(node, new CallInfo(someFunc, node));
                _txb.SetType(node, returnType);
            }

            public override void PostVisit(ListNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);
                SetVariadicNodePurity(node);
                _txb.SetScopeUseSet(node, JoinScopeUseSets(node.Children));
            }

            private bool IsRecordScopeFieldName(DName name, out Scope scope)
            {
                Contracts.AssertValid(name);

                if (_txb.Document == null || !_txb.Document.Properties.EnabledFeatures.IsUseDisplayNameMetadataEnabled)
                {
                    scope = default(Scope);
                    return false;
                }

                // Look up the name in the current scopes, innermost to outermost.
                for (scope = _currentScope; scope != null; scope = scope.Parent)
                {
                    Contracts.AssertValue(scope);

                    // If scope type is a data source, the node may be a display name instead of logical.
                    // Attempt to get the logical name to use for type checking
                    string maybeLogicalName, tmp;
                    if (!scope.SkipForInlineRecords && (DType.TryGetConvertedDisplayNameAndLogicalNameForColumn(scope.Type, name.Value, out maybeLogicalName, out tmp) ||
                        DType.TryGetLogicalNameForColumn(scope.Type, name.Value, out maybeLogicalName)))
                    {
                        name = new DName(maybeLogicalName);
                    }

                    DType tmpType;
                    if (scope.Type.TryGetType(name, out tmpType))
                        return true;

                }
                return false;
            }

            public override void PostVisit(RecordNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);

                DType nodeType = DType.EmptyRecord;

                DType dataSourceBoundType = DType.Invalid;
                if (node.SourceRestriction != null && node.SourceRestriction.Kind == NodeKind.FirstName)
                {
                    var sourceRestrictionNode = node.SourceRestriction.AsFirstName().VerifyValue();

                    var info = _txb.GetInfo(sourceRestrictionNode);
                    var dataSourceInfo = info?.Data as IExternalDataSource;
                    if (dataSourceInfo == null)
                    {
                        _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, sourceRestrictionNode, TexlStrings.ErrExpectedDataSourceRestriction);
                        nodeType = DType.Error;
                    }
                    else
                    {
                        dataSourceBoundType = dataSourceInfo.Schema;
                        nodeType = DType.CreateDTypeWithConnectedDataSourceInfoMetadata(nodeType, dataSourceBoundType.AssociatedDataSources);
                    }
                }

                bool isSelfContainedConstant = true;
                for (int i = 0; i < node.Count; i++)
                {
                    string displayName = node.Ids[i].Name.Value;
                    DName fieldName = node.Ids[i].Name;
                    DType fieldType;

                    isSelfContainedConstant &= _txb.IsSelfContainedConstant(node.Children[i]);

                    if (dataSourceBoundType != DType.Invalid)
                    {
                        fieldName = GetLogicalNodeNameAndUpdateDisplayNames(dataSourceBoundType, node.Ids[i], out displayName);

                        if (!dataSourceBoundType.TryGetType(fieldName, out fieldType))
                        {
                            dataSourceBoundType.ReportNonExistingName(FieldNameKind.Display, _txb.ErrorContainer, fieldName, node.Children[i]);
                            nodeType = DType.Error;
                        }
                        else if (!fieldType.Accepts(_txb.GetType(node.Children[i])))
                        {
                            _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, node.Children[i], TexlStrings.ErrColumnTypeMismatch_ColName_ExpectedType_ActualType,
                                displayName, fieldType.GetKindString(), _txb.GetType(node.Children[i]).GetKindString());
                            nodeType = DType.Error;
                        }
                    }
                    else
                    {
                        // For local records, check name/type match with scope
                        Scope maybeScope;
                        if (IsRecordScopeFieldName(fieldName, out maybeScope))
                            fieldName = GetLogicalNodeNameAndUpdateDisplayNames(maybeScope.Type, node.Ids[i], out displayName);
                    }

                    if (nodeType != DType.Error)
                    {
                        if (nodeType.TryGetType(fieldName, out fieldType))
                            _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, node.Children[i], TexlStrings.ErrMultipleValuesForField_Name, displayName);
                        else
                            nodeType = nodeType.Add(fieldName, _txb.GetType(node.Children[i]));
                    }
                }

                _txb.SetType(node, nodeType);
                SetVariadicNodePurity(node);
                _txb.SetScopeUseSet(node, JoinScopeUseSets(node.Children));
                _txb.SetSelfContainedConstant(node, isSelfContainedConstant);
            }

            public override void PostVisit(TableNode node)
            {
                AssertValid();
                Contracts.AssertValue(node);
                DType exprType = DType.Invalid;
                bool isSelfContainedConstant = true;

                foreach (var child in node.Children)
                {
                    DType childType = _txb.GetType(child);
                    isSelfContainedConstant &= _txb.IsSelfContainedConstant(child);

                    if (!exprType.IsValid)
                        exprType = childType;
                    else if (exprType.CanUnionWith(childType))
                        exprType = DType.Union(exprType, childType);
                    else if (childType.CoercesTo(exprType))
                        _txb.SetCoercedType(child, exprType);
                    else
                        _txb.ErrorContainer.EnsureError(DocumentErrorSeverity.Severe, child, TexlStrings.ErrTableDoesNotAcceptThisType);
                }

                _txb.SetType(node, exprType.IsValid ?
                    DType.CreateTable(new TypedName(exprType, new DName("Value"))) :
                    DType.EmptyTable);
                SetVariadicNodePurity(node);
                _txb.SetScopeUseSet(node, JoinScopeUseSets(node.Children));
                _txb.SetSelfContainedConstant(node, isSelfContainedConstant);
            }

        }
    }
}