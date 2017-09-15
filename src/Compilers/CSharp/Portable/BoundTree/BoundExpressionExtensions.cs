﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal static partial class BoundExpressionExtensions
    {
        public static bool IsLiteralNull(this BoundExpression node)
        {
            return node.Kind == BoundKind.Literal && node.ConstantValue.Discriminator == ConstantValueTypeDiscriminator.Null;
        }

        public static bool IsLiteralDefault(this BoundExpression node)
        {
            return node.Kind == BoundKind.DefaultExpression && node.Syntax.Kind() == SyntaxKind.DefaultLiteralExpression;
        }

        // returns true when expression has no side-effects and produces
        // default value (null, zero, false, default(T) ...)
        //
        // NOTE: This method is a very shallow check.
        //       It does not make any assumptions about what this node could become 
        //       after some folding/propagation/algebraic transformations.
        public static bool IsDefaultValue(this BoundExpression node)
        {
            if (node.Kind == BoundKind.DefaultExpression)
            {
                return true;
            }

            var constValue = node.ConstantValue;
            if (constValue != null)
            {
                return constValue.IsDefaultValue;
            }

            return false;
        }

        public static bool HasExpressionType(this BoundExpression node)
        {
            // null literal, method group, and anonymous function expressions have no type.
            return (object)node.Type != null;
        }

        public static bool HasDynamicType(this BoundExpression node)
        {
            var type = node.Type;
            return (object)type != null && type.IsDynamic();
        }

        public static bool MethodGroupReceiverIsDynamic(this BoundMethodGroup node)
        {
            return node.InstanceOpt != null && node.InstanceOpt.HasDynamicType();
        }

        public static bool HasExpressionSymbols(this BoundExpression node)
        {
            switch (node.Kind)
            {
                case BoundKind.Call:
                case BoundKind.Local:
                case BoundKind.FieldAccess:
                case BoundKind.PropertyAccess:
                case BoundKind.IndexerAccess:
                case BoundKind.EventAccess:
                case BoundKind.MethodGroup:
                case BoundKind.ObjectCreationExpression:
                case BoundKind.TypeExpression:
                case BoundKind.NamespaceExpression:
                    return true;
                case BoundKind.BadExpression:
                    return ((BoundBadExpression)node).Symbols.Length > 0;
                default:
                    return false;
            }
        }

        public static void GetExpressionSymbols(this BoundExpression node, ArrayBuilder<Symbol> symbols, BoundNode parent, Binder binder)
        {
            switch (node.Kind)
            {
                case BoundKind.MethodGroup:
                    // Special case: if we are looking for info on "M" in "new Action(M)" in the context of a parent 
                    // then we want to get the symbol that overload resolution chose for M, not on the whole method group M.
                    var delegateCreation = parent as BoundDelegateCreationExpression;
                    if (delegateCreation != null && (object)delegateCreation.MethodOpt != null)
                    {
                        symbols.Add(delegateCreation.MethodOpt);
                    }
                    else
                    {
                        symbols.AddRange(CSharpSemanticModel.GetReducedAndFilteredMethodGroupSymbols(binder, (BoundMethodGroup)node));
                    }
                    break;

                case BoundKind.BadExpression:
                    symbols.AddRange(((BoundBadExpression)node).Symbols);
                    break;

                case BoundKind.DelegateCreationExpression:
                    var expr = (BoundDelegateCreationExpression)node;
                    var ctor = expr.Type.GetMembers(WellKnownMemberNames.InstanceConstructorName).FirstOrDefault();
                    if ((object)ctor != null)
                    {
                        symbols.Add(ctor);
                    }
                    break;

                case BoundKind.Call:
                    // Either overload resolution succeeded for this call or it did not. If it did not
                    // succeed then we've stashed the original method symbols from the method group,
                    // and we should use those as the symbols displayed for the call. If it did succeed
                    // then we did not stash any symbols; just fall through to the default case.

                    var originalMethods = ((BoundCall)node).OriginalMethodsOpt;
                    if (originalMethods.IsDefault)
                    {
                        goto default;
                    }
                    symbols.AddRange(originalMethods);
                    break;

                case BoundKind.IndexerAccess:
                    // Same behavior as for a BoundCall: if overload resolution failed, pull out stashed candidates;
                    // otherwise use the default behavior.

                    var originalIndexers = ((BoundIndexerAccess)node).OriginalIndexersOpt;
                    if (originalIndexers.IsDefault)
                    {
                        goto default;
                    }
                    symbols.AddRange(originalIndexers);
                    break;

                default:
                    var symbol = node.ExpressionSymbol;
                    if ((object)symbol != null)
                    {
                        symbols.Add(symbol);
                    }
                    break;
            }
        }

        // Get the conversion associated with a bound node, or else Identity.
        public static Conversion GetConversion(this BoundExpression boundNode)
        {
            switch (boundNode.Kind)
            {
                case BoundKind.Conversion:
                    BoundConversion conversionNode = (BoundConversion)boundNode;
                    return conversionNode.Conversion;

                default:
                    return Conversion.Identity;
            }
        }

        internal static bool IsExpressionOfComImportType(this BoundExpression expressionOpt)
        {
            // NOTE: Dev11 also returns false if expressionOpt is a TypeExpression.  Unfortunately,
            // that makes it impossible to handle TypeOrValueExpression in a consistent way, since
            // we don't know whether it's a type until after overload resolution and we can't do
            // overload resolution without knowing whether 'ref' can be omitted (which is what this
            // method is used to determine).  Since there is no intuitive reason to disallow
            // omitting 'ref' for static methods, we'll drop the restriction on TypeExpression.
            if (expressionOpt == null) return false;

            TypeSymbol receiverType = expressionOpt.Type;
            return (object)receiverType != null && receiverType.Kind == SymbolKind.NamedType && ((NamedTypeSymbol)receiverType).IsComImport;
        }

        internal static TypeSymbolWithAnnotations GetTypeAndNullability(this BoundExpression expr, bool includeNullability)
        {
            var type = expr.Type;
            if ((object)type == null)
            {
                return null;
            }
            // PROTOTYPE(NullableReferenceTypes): Could we track nullability always,
            // even in C#7, but only report warnings when the feature is enabled?
            var isNullable = includeNullability && !type.IsErrorType() ? expr.IsNullable() : null;
            return TypeSymbolWithAnnotations.Create(type, isNullable);
        }

        internal static bool? IsNullable(this BoundExpression expr)
        {
            switch (expr.Kind)
            {
                case BoundKind.SuppressNullableWarningExpression:
                    return false;
                case BoundKind.Local:
                    return ((BoundLocal)expr).LocalSymbol.Type.IsNullable;
                case BoundKind.Parameter:
                    return ((BoundParameter)expr).ParameterSymbol.Type.IsNullable;
                case BoundKind.FieldAccess:
                    return ((BoundFieldAccess)expr).FieldSymbol.Type.IsNullable;
                case BoundKind.PropertyAccess:
                    return ((BoundPropertyAccess)expr).PropertySymbol.Type.IsNullable;
                case BoundKind.Call:
                    return ((BoundCall)expr).Method.ReturnType.IsNullable;
                case BoundKind.BinaryOperator:
                    return ((BoundBinaryOperator)expr).MethodOpt?.ReturnType.IsNullable;
                case BoundKind.NullCoalescingOperator:
                    {
                        var op = (BoundNullCoalescingOperator)expr;
                        var left = op.LeftOperand.IsNullable();
                        var right = op.RightOperand.IsNullable();
                        return (left == true) ? right : left;
                    }
                case BoundKind.ObjectCreationExpression:
                case BoundKind.DelegateCreationExpression:
                    return false;
                case BoundKind.TupleLiteral:
                    return false;
                case BoundKind.DefaultExpression:
                case BoundKind.Literal:
                case BoundKind.UnboundLambda:
                    break;
                default:
                    // PROTOTYPE(NullableReferenceTypes): Handle all expression kinds.
                    //Debug.Assert(false, "Unhandled expression: " + expr.Kind);
                    break;
            }

            var constant = expr.ConstantValue;
            if (constant != null && constant.IsNull)
            {
                return true;
            }

            return null;
        }
    }
}
