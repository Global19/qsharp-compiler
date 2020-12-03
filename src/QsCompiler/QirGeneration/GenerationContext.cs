﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Quantum.QIR;
using Microsoft.Quantum.QsCompiler.SyntaxTokens;
using Microsoft.Quantum.QsCompiler.SyntaxTree;
using Ubiquity.NET.Llvm;
using Ubiquity.NET.Llvm.Instructions;
using Ubiquity.NET.Llvm.Interop;
using Ubiquity.NET.Llvm.Types;
using Ubiquity.NET.Llvm.Values;

namespace Microsoft.Quantum.QsCompiler.QirGenerator
{
    using QsArgumentTuple = QsTuple<LocalVariableDeclaration<QsLocalSymbol>>;
    using QsResolvedTypeKind = QsTypeKind<ResolvedType, UserDefinedType, QsTypeParameter, CallableInformation>;
    using ResolvedExpression = QsExpressionKind<TypedExpression, Identifier, ResolvedType>;

    /// <summary>
    /// This class holds the shared state used across a QIR generation pass.
    /// It also holds a large number of shared utility routines.
    /// </summary>
    [SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1124:Do not use regions")]
    [SuppressMessage("StyleCop.CSharp.LayoutRules", "SA1516:Elements should be separated by blank line")]
    public sealed class GenerationContext : IDisposable
    {
        private static readonly ILibLlvm LibContext;

        static GenerationContext()
        {
            LibContext = Library.InitializeLLVM();
            LibContext.RegisterTarget(CodeGenTarget.Native);
        }

        #region Member variables

        /// <summary>
        /// The configuration for QIR generation.
        /// </summary>
        public readonly Configuration Config;

        /// <summary>
        /// The compilation unit for which QIR is generated.
        /// </summary>
        public readonly QsCompilation Compilation;

        /// <summary>
        /// The context used for QIR generation.
        /// </summary>
        /// <inheritdoc cref="Ubiquity.NET.Llvm.Context"/>
        public readonly Context Context;

        /// <summary>
        /// The module used for QIR generation.
        /// Generated wrappers to facilitate interoperability are created in a separate <see cref="InteropModule"/>.
        /// </summary>
        /// <inheritdoc cref="BitcodeModule"/>
        public readonly BitcodeModule Module;

        /// <summary>
        /// The module used for constructing function wrappers to facilitate interoperability.
        /// </summary>
        /// <inheritdoc cref="BitcodeModule"/>
        public readonly BitcodeModule InteropModule;

        /// <summary>
        /// The used QIR types.
        /// </summary>
        public readonly Types Types;

        /// <summary>
        /// The used QIR constants.
        /// </summary>
        public readonly Constants Constants;

        private QirTransformation? transformation;

        /// <summary>
        /// The syntax tree transformation that constructs QIR.
        /// </summary>
        /// <exception cref="InvalidOperationException">The transformation has not been set via <see cref="SetTransformation"/>.</exception>
        public QirTransformation Transformation =>
            this.transformation ?? throw new InvalidOperationException("no transformation defined");

        /// <summary>
        /// Sets the syntax tree transformation that is used to construct QIR.
        /// </summary>
        public void SetTransformation(QirTransformation transformation) =>
            this.transformation = transformation;

        private readonly FunctionLibrary runtimeLibrary;
        private readonly FunctionLibrary quantumLibrary;

        /// <summary>
        /// Gets the LLVM object for a runtime library function.
        /// If this is the first reference to the function, its declaration is added to the module.
        /// </summary>
        /// <param name="name">The name of the function.</param>
        /// <returns>The LLVM function object</returns>
        public IrFunction GetRuntimeFunction(string name) => this.runtimeLibrary.GetFunction(name);

        /// <summary>
        /// Gets the LLVM object for a quantum instruction set function.
        /// If this is the first reference to the function, its declaration is added to the module.
        /// </summary>
        /// <param name="name">The name of the function.</param>
        /// <returns>The LLVM function object</returns>
        public IrFunction GetQuantumFunction(string name) => this.quantumLibrary.GetFunction(name);

        internal IrFunction? CurrentFunction { get; private set; }
        internal BasicBlock? CurrentBlock { get; private set; }
        internal InstructionBuilder CurrentBuilder { get; private set; }
        internal int CurrentInlineLevel { get; private set; } = 0;
        internal ITypeRef? BuiltType { get; set; }

        internal readonly ScopeManager ScopeMgr;
        internal readonly Stack<Value> ValueStack;
        internal readonly Stack<ResolvedType> ExpressionTypeStack;

        private readonly Dictionary<string, int> uniqueNameIds = new Dictionary<string, int>();
        private readonly Stack<Dictionary<string, (Value, bool)>> namesInScope = new Stack<Dictionary<string, (Value, bool)>>();

        private readonly Dictionary<string, ITypeRef> interopType = new Dictionary<string, ITypeRef>();
        private readonly Dictionary<string, (QsCallable, GlobalVariable)> wrapperQueue = new Dictionary<string, (QsCallable, GlobalVariable)>();

        #endregion

        /// <summary>
        /// Constructs a new generation context.
        /// Before using the constructed context, the following needs to be done:
        /// 1.) the transformation needs to be set by calling <see cref="SetTransformation"/>,
        /// 2.) the runtime library needs to be initialized by calling <see cref="InitializeRuntimeLibrary"/>, and
        /// 3.) the quantum instructions set needs to be registered by calling <see cref="RegisterQuantumInstructions"/>.
        /// </summary>
        /// <param name="compilation">The compilation unit for which QIR is generated.</param>
        /// <param name="config">The configuration for QIR generation.</param>
        internal GenerationContext(QsCompilation compilation, Configuration config)
        {
            this.Config = config;
            this.Compilation = compilation;
            this.Context = new Context();
            this.Module = this.Context.CreateBitcodeModule();
            this.InteropModule = this.Context.CreateBitcodeModule("bridge");

            this.Types = new Types(this.Context);
            this.Constants = new Constants(this.Context, this.Module, this.Types);
            this.transformation = null; // needs to be set by the instantiating transformation

            this.CurrentBuilder = new InstructionBuilder(this.Context);
            this.ValueStack = new Stack<Value>();
            this.ExpressionTypeStack = new Stack<ResolvedType>();
            this.ScopeMgr = new ScopeManager(this);

            this.runtimeLibrary = new FunctionLibrary(
                this.Module,
                s => Callables.FunctionName(Component.RuntimeLibrary, s));
            this.quantumLibrary = new FunctionLibrary(
                this.Module,
                s => Callables.FunctionName(Component.QuantumInstructionSet, s));
        }

        #region Static members

        /// <summary>
        /// Cleans a namespace name by replacing periods with double underscores.
        /// </summary>
        /// <param name="namespaceName">The namespace name to clean</param>
        /// <returns>The cleaned name</returns>
        public static string FlattenNamespaceName(string namespaceName) =>
            namespaceName.Replace(".", "__");

        /// <summary>
        /// Generates a mangled name for a callable specialization.
        /// QIR mangled names are the namespace name, with periods replaced by double underscores, followed
        /// by a double underscore and the callable name, then another double underscore and the name of the
        /// callable kind ("body", "adj", "ctl", or "ctladj").
        /// </summary>
        /// <param name="namespaceName">The callable's namespace name</param>
        /// <param name="name">The callable's name</param>
        /// <param name="kind">The specialization kind</param>
        /// <returns>The mangled name for the specialization</returns>
        public static string FunctionName(string namespaceName, string name, QsSpecializationKind kind)
        {
            var suffix =
                kind.IsQsBody ? "body" :
                kind.IsQsAdjoint ? "adj" :
                kind.IsQsControlled ? "ctrl" :
                kind.IsQsControlledAdjoint ? "ctrladj" :
                throw new NotImplementedException("unknown specialization kind");
            return $"{FlattenNamespaceName(namespaceName)}__{name}__{suffix}";
        }

        /// <summary>
        /// Generates a mangled name for a callable specialization wrapper.
        /// Wrapper names are the mangled specialization name followed by double underscore and "wrapper".
        /// </summary>
        /// <param name="namespaceName">The namespace of the Q# callable.</param>
        /// <param name="name">The unqualified name of the Q# callable.</param>
        /// <param name="kind">The specialization kind</param>
        /// <returns>The mangled name for the wrapper</returns>
        public static string FunctionWrapperName(string namespaceName, string name, QsSpecializationKind kind) =>
            $"{FunctionName(namespaceName, name, kind)}__wrapper";

        /// <summary>
        /// Generates a mangled name for a callable specialization wrapper.
        /// Wrapper names are the mangled specialization name followed by double underscore and "wrapper".
        /// </summary>
        /// <param name="callable">The Q# callable</param>
        /// <param name="kind">The specialization kind</param>
        /// <returns>The mangled name for the wrapper</returns>
        internal static string FunctionWrapperName(QsCallable callable, QsSpecializationKind kind) =>
            FunctionWrapperName(callable.FullName.Namespace, callable.FullName.Name, kind);

        /// <summary>
        /// Order of specializations in the wrapper array
        /// </summary>
        public static readonly ImmutableArray<QsSpecializationKind> FunctionArray = ImmutableArray.Create(
            QsSpecializationKind.QsBody,
            QsSpecializationKind.QsAdjoint,
            QsSpecializationKind.QsControlled,
            QsSpecializationKind.QsControlledAdjoint);

        #endregion

        #region Initialization and emission

        /// <summary>
        /// Initializes the QIR runtime library.
        /// See <see cref="FunctionLibrary"/>.
        /// </summary>
        public void InitializeRuntimeLibrary()
        {
            // int library functions
            this.runtimeLibrary.AddFunction(RuntimeLibrary.IntPower, this.Types.Int, this.Types.Int, this.Types.Int);

            // result library functions
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ResultReference, this.Context.VoidType, this.Types.Result);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ResultUnreference, this.Context.VoidType, this.Types.Result);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ResultEqual, this.Context.BoolType, this.Types.Result, this.Types.Result);

            // string library functions
            this.runtimeLibrary.AddFunction(RuntimeLibrary.StringCreate, this.Types.String, this.Context.Int32Type, this.Context.Int8Type.CreateArrayType(0));
            this.runtimeLibrary.AddFunction(RuntimeLibrary.StringReference, this.Context.VoidType, this.Types.String);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.StringUnreference, this.Context.VoidType, this.Types.String);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.StringConcatenate, this.Types.String, this.Types.String, this.Types.String);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.StringEqual, this.Context.BoolType, this.Types.String, this.Types.String);

            // to-string conversion functions
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintToString, this.Types.String, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BoolToString, this.Types.String, this.Context.BoolType);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.DoubleToString, this.Types.String, this.Context.DoubleType);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.IntToString, this.Types.String, this.Context.Int64Type);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.PauliToString, this.Types.String, this.Types.Pauli);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.QubitToString, this.Types.String, this.Types.Qubit);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.RangeToString, this.Types.String, this.Types.Range);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ResultToString, this.Types.String, this.Types.Result);

            // bigint library functions
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintCreateI64, this.Types.BigInt, this.Context.Int64Type);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintCreateArray, this.Types.BigInt, this.Context.Int32Type, this.Context.Int8Type.CreateArrayType(0));
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintReference, this.Context.VoidType, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintUnreference, this.Context.VoidType, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintNegate, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintAdd, this.Types.BigInt, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintSubtract, this.Types.BigInt, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintMultiply, this.Types.BigInt, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintDivide, this.Types.BigInt, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintModulus, this.Types.BigInt, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintPower, this.Types.BigInt, this.Types.BigInt, this.Context.Int32Type);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintBitand, this.Types.BigInt, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintBitor, this.Types.BigInt, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintBitxor, this.Types.BigInt, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintBitnot, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintShiftleft, this.Types.BigInt, this.Types.BigInt, this.Context.Int64Type);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintShiftright, this.Types.BigInt, this.Types.BigInt, this.Context.Int64Type);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintEqual, this.Context.BoolType, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintGreater, this.Context.BoolType, this.Types.BigInt, this.Types.BigInt);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.BigintGreaterEq, this.Context.BoolType, this.Types.BigInt, this.Types.BigInt);

            // tuple library functions
            this.runtimeLibrary.AddFunction(RuntimeLibrary.TupleInitStack, this.Context.VoidType, this.Types.Tuple);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.TupleInitHeap, this.Context.VoidType, this.Types.Tuple);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.TupleCreate, this.Types.Tuple, this.Context.Int64Type);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.TupleReference, this.Context.VoidType, this.Types.Tuple);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.TupleUnreference, this.Context.VoidType, this.Types.Tuple);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.TupleIsWritable, this.Context.BoolType, this.Types.Tuple);

            // array library functions
            this.runtimeLibrary.AddVarArgsFunction(RuntimeLibrary.ArrayCreate, this.Types.Array, this.Context.Int32Type, this.Context.Int32Type);
            this.runtimeLibrary.AddVarArgsFunction(RuntimeLibrary.ArrayGetElementPtr, this.Context.Int8Type.CreatePointerType(), this.Types.Array);
            // TODO: figure out how to call a varargs function and get rid of these two functions
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ArrayCreate1d, this.Types.Array, this.Context.Int32Type, this.Context.Int64Type);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ArrayGetElementPtr1d, this.Context.Int8Type.CreatePointerType(), this.Types.Array, this.Context.Int64Type);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ArrayGetLength, this.Context.Int64Type, this.Types.Array, this.Context.Int32Type);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ArrayReference, this.Context.VoidType, this.Types.Array);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ArrayUnreference, this.Context.VoidType, this.Types.Array);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ArrayCopy, this.Types.Array, this.Types.Array);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ArrayConcatenate, this.Types.Array, this.Types.Array, this.Types.Array);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.ArraySlice, this.Types.Array, this.Types.Array, this.Context.Int32Type, this.Types.Range);

            // callable library functions
            this.runtimeLibrary.AddFunction(
                RuntimeLibrary.CallableCreate,
                this.Types.Callable,
                this.Types.FunctionSignature.CreatePointerType().CreateArrayType(4).CreatePointerType(),
                this.Types.Tuple);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.CallableInvoke, this.Context.VoidType, this.Types.Callable, this.Types.Tuple, this.Types.Tuple);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.CallableCopy, this.Types.Callable, this.Types.Callable);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.CallableMakeAdjoint, this.Context.VoidType, this.Types.Callable);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.CallableMakeControlled, this.Context.VoidType, this.Types.Callable);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.CallableReference, this.Context.VoidType, this.Types.Callable);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.CallableUnreference, this.Context.VoidType, this.Types.Callable);

            // qubit library functions
            this.runtimeLibrary.AddFunction(RuntimeLibrary.QubitAllocate, this.Types.Qubit);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.QubitAllocateArray, this.Types.Array, this.Context.Int64Type);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.QubitRelease, this.Context.VoidType, this.Types.Qubit);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.QubitReleaseArray, this.Context.VoidType, this.Types.Array);

            // diagnostic library functions
            this.runtimeLibrary.AddFunction(RuntimeLibrary.Fail, this.Context.VoidType, this.Types.String);
            this.runtimeLibrary.AddFunction(RuntimeLibrary.Message, this.Context.VoidType, this.Types.String);
        }

        /// <summary>
        /// Finds and registers all of the quantum instructions in the current compilation.
        /// For this purpose, quantum instructions are any operations that have the "Intrinsic" attribute.
        /// <br/><br/>
        /// In addition, interop-compatible wrappers are generated for all of the quantum operations.
        /// </summary>
        public void RegisterQuantumInstructions()
        {
            foreach (var ns in this.Compilation.Namespaces)
            {
                foreach (var element in ns.Elements)
                {
                    if (element is QsNamespaceElement.QsCallable c
                        && SymbolResolution.TryGetQISCode(c.Item.Attributes) is var att && att.IsValue)
                    {
                        var name = att.Item;
                        // Special handling for Unit since by default it turns into an empty tuple
                        var returnType = c.Item.Signature.ReturnType.Resolution.IsUnitType
                            ? this.Context.VoidType
                            : this.LlvmTypeFromQsharpType(c.Item.Signature.ReturnType);
                        var argTypeArray = (c.Item.Signature.ArgumentType.Resolution is QsResolvedTypeKind.TupleType tuple)
                            ? tuple.Item.Select(this.LlvmTypeFromQsharpType).ToArray()
                            : new ITypeRef[] { this.LlvmTypeFromQsharpType(c.Item.Signature.ArgumentType) };
                        this.quantumLibrary.AddFunction(name, returnType, argTypeArray);
                        if (this.Config.GenerateInteropWrappers)
                        {
                            var func = this.quantumLibrary.GetFunction(name);
                            this.GenerateInteropWrapper(func, name);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Writes the current content to the output file.
        /// </summary>
        public void Emit()
        {
            this.GenerateQueuedWrappers();

            if (this.Module.Verify(out string validationErrors))
            {
                File.WriteAllText($"{this.Config.OutputFileName}.log", "No errors\n");
            }
            else
            {
                File.WriteAllText($"{this.Config.OutputFileName}.log", $"LLVM errors:\n{validationErrors}");
            }

            if (!this.Module.WriteToTextFile($"{this.Config.OutputFileName}.ll", out string errorMessage))
            {
                throw new IOException(errorMessage);
            }

            // Generate the wrappers for the runtime library that were used, if requested
            if (this.Config.GenerateInteropWrappers)
            {
                foreach (var kvp in this.runtimeLibrary)
                {
                    this.GenerateInteropWrapper(kvp.Value, kvp.Key);
                }

                var bridgeFileName = Path.Combine(Path.GetDirectoryName(this.Config.OutputFileName), "bridge.ll");

                if (!this.InteropModule.Verify(out string bridgeValidationErrors))
                {
                    File.WriteAllText(bridgeFileName, $"LLVM errors:\n{bridgeValidationErrors}");
                }
                else if (!this.InteropModule.WriteToTextFile(bridgeFileName, out string bridgeError))
                {
                    throw new IOException(bridgeError);
                }
            }
        }

        #endregion

        #region Type helpers

        /// <summary>
        /// Gets the QIR equivalent for a Q# type.
        /// Tuples are represented as QirTuplePointer, arrays as QirArray, and callables as QirCallable.
        /// </summary>
        /// <param name="resolvedType">The Q# type</param>
        /// <returns>The equivalent QIR type</returns>
        internal ITypeRef LlvmTypeFromQsharpType(ResolvedType resolvedType)
        {
            this.BuiltType = null;
            this.Transformation.Types.OnType(resolvedType);
            return this.BuiltType ?? throw new NotImplementedException("Llvm type could not be constructed");
        }

        /// <summary>
        /// Gets the QIR equivalent for a Q# type, as a structure.
        /// Tuples are represented as an anonymous LLVM structure type with a TupleHeader as the first element.
        /// Other types are represented as anonymous LLVM structure types with a TupleHeader in the first element
        /// and the "normal" converted type as the second element.
        /// </summary>
        /// <param name="resolvedType">The Q# type</param>
        /// <returns>The equivalent QIR structure type</returns>
        internal ITypeRef LlvmStructTypeFromQsharpType(ResolvedType resolvedType)
        {
            if (resolvedType.Resolution is QsResolvedTypeKind.TupleType tuple)
            {
                var elementTypes = tuple.Item.Select(this.LlvmTypeFromQsharpType).Prepend(this.Types.TupleHeader).ToArray();
                return this.Context.CreateStructType(false, elementTypes);
            }
            else
            {
                return this.Context.CreateStructType(false, this.Types.TupleHeader, this.LlvmTypeFromQsharpType(resolvedType));
            }
        }

        /// <summary>
        /// Computes the size in bytes of an LLVM type as an LLVM value.
        /// If the type isn't a simple pointer, integer, or double, we compute it using a standard LLVM idiom.
        /// </summary>
        /// <param name="t">The LLVM type to compute the size of</param>
        /// <param name="b">The builder to use to generate the struct size computation, if needed</param>
        /// <returns>An LLVM value containing the size of the type in bytes</returns>
        internal Value ComputeSizeForType(ITypeRef t, InstructionBuilder b)
        {
            if (t.IsInteger)
            {
                return this.Context.CreateConstant((long)((t.IntegerBitWidth + 7) / 8));
            }
            else if (t.IsDouble)
            {
                return this.Context.CreateConstant(8L);
            }
            else if (t.IsPointer)
            {
                // We assume 64-bit address space
                return this.Context.CreateConstant(8L);
            }
            else
            {
                // Everything else we let getelementptr compute for us
                var basePointer = Constant.ConstPointerToNullFor(t.CreatePointerType());
                var firstPtr = b.GetElementPtr(t, basePointer, new[] { this.Context.CreateConstant(0) });
                var first = b.PointerToInt(firstPtr, this.Context.Int64Type);
                var secondPtr = b.GetElementPtr(t, basePointer, new[] { this.Context.CreateConstant(1) });
                var second = b.PointerToInt(secondPtr, this.Context.Int64Type);
                return this.CurrentBuilder.Sub(second, first);
            }
        }

        /// <summary>
        /// Determines whether an LLVM type is a pointer to a tuple.
        /// Specifically, is the type a pointer to a structure whose first element is a TupleHeader?
        /// </summary>
        /// <param name="t">The type to check</param>
        /// <returns>true if t is a pointer to a tuple, false otherwise</returns>
        private bool IsTupleType(ITypeRef t) =>
            t is IPointerType pt
            && pt.ElementType is IStructType st
            && st.Members.Count > 0
            && st.Members[0] == this.Types.TupleHeader;

        #endregion

        #region Tuple and argument tuple creation

        /// <summary>
        /// Builds the LLVM type that represents a Q# argument tuple as a passed value.
        /// <br/><br/>
        /// See also <seealso cref="LlvmTypeFromQsharpType(ResolvedType)"/>.
        /// </summary>
        /// <param name="argItem">The Q# argument tuple</param>
        /// <returns>The LLVM type</returns>
        private ITypeRef BuildArgItemTupleType(QsArgumentTuple argItem)
        {
            switch (argItem)
            {
                case QsArgumentTuple.QsTuple tuple:
                    {
                        var elems = tuple.Item.Select(this.BuildArgItemTupleType).Prepend(this.Types.TupleHeader).ToArray();
                        return this.Context.CreateStructType(false, elems).CreatePointerType();
                    }

                case QsArgumentTuple.QsTupleItem item:
                    {
                        // Single items get translated to the appropriate LLVM type
                        return this.LlvmTypeFromQsharpType(item.Item.Type);
                    }
                default:
                    {
                        throw new NotImplementedException("Unknown item in argument tuple.");
                    }
            }
        }

        /// <summary>
        /// Builds the LLVM type that represents a Q# argument tuple as a structure.
        /// Note that tupled arguments generate an LLVM structure type.
        /// <br/><br/>
        /// See also <seealso cref="LlvmStructTypeFromQsharpType(ResolvedType)"/>.
        /// </summary>
        /// <param name="arg">The Q# argument tuple</param>
        /// <returns>The LLVM type</returns>
        private ITypeRef BuildArgTupleType(QsArgumentTuple arg)
        {
            if (arg is QsArgumentTuple.QsTuple tuple)
            {
                return tuple.Item.Length == 0
                    ? this.Context.VoidType
                    : this.Context.CreateStructType(
                        false,
                        tuple.Item.Select(this.BuildArgItemTupleType).Prepend(this.Types.TupleHeader).ToArray());
            }
            else if (arg is QsArgumentTuple.QsTupleItem item)
            {
                var itemTypeRef = this.LlvmTypeFromQsharpType(item.Item.Type);
                return this.Context.CreateStructType(false, this.Types.TupleHeader, itemTypeRef);
            }
            else
            {
                throw new NotImplementedException("Unknown item in argument tuple.");
            }
        }

        /// <summary>
        /// Creates a new tuple for an LLVM structure type.
        /// The new tuple is created using the current builder.
        /// </summary>
        /// <param name="t">The LLVM structure type for the tuple</param>
        /// <returns>A value containing the pointer to the new tuple</returns>
        internal Value CreateTupleForType(ITypeRef t)
        {
            var size = this.ComputeSizeForType(t, this.CurrentBuilder);
            var tuple = this.CurrentBuilder.Call(this.GetRuntimeFunction("tuple_create"), size);
            return tuple;
        }

        #endregion

        #region Function management

        /// <summary>
        /// Preps the shared state for a new specialization.
        /// </summary>
        internal void StartSpecialization()
        {
            this.ScopeMgr.Reset();
            this.namesInScope.Clear();
            this.CurrentInlineLevel = 0;
            this.uniqueNameIds.Clear();
        }

        /// <summary>
        /// Ends a specialization by finishing the current basic block.
        /// </summary>
        /// <exception cref="InvalidOperationException">The current function or the current block is set to null.</exception>
        internal void EndSpecialization()
        {
            if (this.CurrentFunction == null || this.CurrentBlock == null)
            {
                throw new InvalidOperationException("the current function or the current block is null");
            }

            bool HasAPredecessor(BasicBlock block)
            {
                foreach (var b in this.CurrentFunction.BasicBlocks)
                {
                    if ((b != block) && (b.Terminator != null))
                    {
                        var term = b.Terminator;
                        if (term is Branch br)
                        {
                            if (br.Successors.Contains(block))
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            this.ScopeMgr.CloseScope(this.CurrentBlock.Terminator != null);
            if (this.CurrentBlock.Instructions.Count() == 0 && !HasAPredecessor(this.CurrentBlock))
            {
                this.CurrentFunction.BasicBlocks.Remove(this.CurrentBlock);
            }
            else if (this.CurrentBlock.Terminator == null)
            {
                this.CurrentBuilder.Return();
            }
        }

        /// <summary>
        /// Generates the declaration for a specialization to the current module.
        /// Usually <see cref="GenerateFunctionHeader"/> is used, which generates the start of the actual definition.
        /// This method is primarily useful for specializations with external or intrinsic implementations, which get
        /// generated as declarations with no definition.
        /// </summary>
        /// <param name="spec">The specialization</param>
        /// <param name="argTuple">The specialization's argument tuple</param>
        internal IrFunction RegisterFunction(QsSpecialization spec, QsArgumentTuple argTuple)
        {
            // TODO: this won't work for parameter lists with embedded tuples (as opposed to arguments
            // of tuple type, which should be fine).

            IEnumerable<ITypeRef> ArgTupleToTypes(QsArgumentTuple arg)
            {
                if (arg is QsArgumentTuple.QsTuple tuple)
                {
                    return tuple.Item.Select(this.BuildArgItemTupleType).ToArray();
                }
                else
                {
                    var typeRef = this.BuildArgItemTupleType(arg);
                    return new ITypeRef[] { typeRef };
                }
            }

            var name = FunctionName(spec.Parent.Namespace, spec.Parent.Name, spec.Kind);
            var returnTypeRef = spec.Signature.ReturnType.Resolution.IsUnitType
                ? this.Context.VoidType
                : this.LlvmTypeFromQsharpType(spec.Signature.ReturnType);
            var argTypeRefs = ArgTupleToTypes(argTuple);
            var signature = this.Context.GetFunctionType(returnTypeRef, argTypeRefs);
            return this.Module.CreateFunction(name, signature);
        }

        /// <summary>
        /// Generates the start of the definition for a specialization in the current module.
        /// Specifically, an entry block for the function is created, and the function's arguments are given names.
        /// </summary>
        /// <param name="spec">The specialization</param>
        /// <param name="argTuple">The specialization's argument tuple</param>
        internal void GenerateFunctionHeader(QsSpecialization spec, QsArgumentTuple argTuple)
        {
            IEnumerable<string> ArgTupleToNames(QsArgumentTuple arg)
            {
                string LocalVarName(QsArgumentTuple v) =>
                    v is QsArgumentTuple.QsTupleItem item && item.Item.VariableName is QsLocalSymbol.ValidName varName
                    ? varName.Item
                    : this.GenerateUniqueName("arg");

                return arg is QsArgumentTuple.QsTuple tuple
                    ? tuple.Item.Select(item => LocalVarName(item))
                    : new[] { LocalVarName(arg) };
            }

            this.CurrentFunction = this.RegisterFunction(spec, argTuple);
            this.CurrentBlock = this.CurrentFunction.AppendBasicBlock("entry");
            this.CurrentBuilder = new InstructionBuilder(this.CurrentBlock);

            this.namesInScope.Push(new Dictionary<string, (Value, bool)>());
            var i = 0;
            foreach (var argName in ArgTupleToNames(argTuple))
            {
                this.CurrentFunction.Parameters[i].Name = argName;
                this.namesInScope.Peek().Add(argName, (this.CurrentFunction.Parameters[i], false));
                i++;
            }
        }

        /// <summary>
        /// Generates the default constructor for a Q# user-defined type.
        /// This routine generates all the code for the constructor, not just the header.
        /// </summary>
        /// <param name="udt">The Q# user-defined type</param>
        internal void GenerateConstructor(QsCustomType udt)
        {
            var name = FunctionName(udt.FullName.Namespace, udt.FullName.Name, QsSpecializationKind.QsBody);

            var args = udt.Type.Resolution switch
            {
                QsResolvedTypeKind.TupleType tup => tup.Item.Select(this.LlvmTypeFromQsharpType).Prepend(this.Types.TupleHeader).ToArray(),
                _ when udt.Type.Resolution.IsUnitType => new ITypeRef[] { this.Types.TupleHeader },
                _ => new ITypeRef[] { this.Types.TupleHeader, this.LlvmTypeFromQsharpType(udt.Type) }
            };
            var udtTupleType = this.Context.CreateStructType(false, args);
            var udtPointerType = args.Length > 1 ? udtTupleType.CreatePointerType() : this.Types.Tuple;
            var signature = this.Context.GetFunctionType(udtPointerType, args[1..]);

            this.StartSpecialization();
            this.CurrentFunction = this.Module.CreateFunction(name, signature);
            this.CurrentBlock = this.CurrentFunction.AppendBasicBlock("entry");
            this.CurrentBuilder = new InstructionBuilder(this.CurrentBlock);

            // An easy case -- (), a marker UDT
            if (args.Length == 1)
            {
                this.CurrentBuilder.Return(udtPointerType.GetNullValue());
            }
            else
            {
                var tuple = this.CreateTupleForType(udtTupleType);
                var udtTuple = this.CurrentBuilder.BitCast(tuple, udtPointerType);

                for (int i = 0; i < args.Length - 1; i++)
                {
                    this.CurrentFunction.Parameters[i].Name = $"arg{i}";
                    var itemPtr = this.CurrentBuilder.GetStructElementPointer(udtTupleType, udtTuple, (uint)i + 1);
                    this.CurrentBuilder.Store(this.CurrentFunction.Parameters[i], itemPtr);
                    // Add a reference to the value, if necessary
                    this.AddRef(this.CurrentFunction.Parameters[i]);
                }

                this.CurrentBuilder.Return(udtTuple);
            }
        }

        /// <summary>
        /// Gets the function for a specialization by name so it can be called.
        /// If the function hasn't been generated yet, its declaration is generated so that it can be called.
        /// </summary>
        /// <param name="namespaceName">The callable's namespace</param>
        /// <param name="name">The callable's name</param>
        /// <param name="kind">The specialization kind</param>
        /// <returns>The LLVM object for the corresponding LLVM function</returns>
        internal IrFunction GetFunctionByName(string namespaceName, string name, QsSpecializationKind kind)
        {
            // If the function is already defined, return it
            if (this.TryGetFunction(namespaceName, name, kind, out IrFunction? function))
            {
                return function;
            }
            // Otherwise, we need to find the function's callable to get the signature,
            // and then register the function
            if (this.TryFindGlobalCallable(namespaceName, name, out QsCallable? callable))
            {
                var spec = callable.Specializations.First(spec => spec.Kind == kind);
                return this.RegisterFunction(spec, callable.ArgumentTuple);
            }
            // If we can't find the function at all, it's a problem...
            throw new KeyNotFoundException($"Can't find callable {namespaceName}.{name}");
        }

        /// <summary>
        /// Tries to get the function for a specialization by name so it can be called.
        /// If the function hasn't been generated yet, false is returned.
        /// </summary>
        /// <param name="namespaceName">The callable's namespace</param>
        /// <param name="name">The callable's name</param>
        /// <param name="kind">The specialization kind</param>
        /// <param name="function">Gets filled in with the LLVM function object if it exists already</param>
        /// <returns>true if the function has already been declared/defined, or false otherwise</returns>
        private bool TryGetFunction(string namespaceName, string name, QsSpecializationKind kind, [MaybeNullWhen(false)] out IrFunction function)
        {
            var fullName = FunctionName(namespaceName, name, kind);
            foreach (var func in this.Module.Functions)
            {
                if (func.Name == fullName)
                {
                    function = func;
                    return true;
                }
            }

            function = null;
            return false;
        }

        /// <summary>
        /// If a wrapper for the given callable already exists, returns the corresponding global variable.
        /// If no such wrapper exists, queues the generation of the wrapper and returns the corresponding global variable.
        /// </summary>
        internal GlobalVariable GetWrapperName(QsCallable callable)
        {
            var key = $"{FlattenNamespaceName(callable.FullName.Namespace)}__{callable.FullName.Name}";
            if (this.wrapperQueue.TryGetValue(key, out (QsCallable, GlobalVariable) item))
            {
                return item.Item2;
            }
            else
            {
                // Generate the callable's function array
                Constant[] funcs = new Constant[4];
                for (var index = 0; index < 4; index++)
                {
                    QsSpecializationKind kind = FunctionArray[index];
                    if (callable.Specializations.Any(spec => spec.Kind == kind))
                    {
                        var f = this.Module.CreateFunction(FunctionWrapperName(callable, kind), this.Types.FunctionSignature);
                        funcs[index] = f;
                    }
                    else
                    {
                        funcs[index] = Constant.NullValueFor(funcs[0].NativeType);
                    }
                }

                ITypeRef t = funcs[0].NativeType;
                Constant array = ConstantArray.From(t, funcs);
                var table = this.Module.AddGlobal(array.NativeType, true, Linkage.DllExport, array, key);
                this.wrapperQueue.Add(key, (callable, table));
                return table;
            }
        }

        /// <summary>
        /// Tries to get the wrapper function for a specialization.
        /// If the wrapper function hasn't been generated yet, false is returned.
        /// </summary>
        /// <param name="callable">The callable</param>
        /// <param name="kind">The specialization kind</param>
        /// <param name="function">Gets filled in with the LLVM function object if it exists already</param>
        /// <returns>true if the function has already been declared/defined, or false otherwise</returns>
        private bool TryGetWrapper(QsCallable callable, QsSpecializationKind kind, [MaybeNullWhen(false)] out IrFunction function)
        {
            var fullName = FunctionWrapperName(callable, kind);
            foreach (var func in this.Module.Functions)
            {
                if (func.Name == fullName)
                {
                    function = func;
                    return true;
                }
            }

            function = null;
            return false;
        }

        private void GenerateQueuedWrappers()
        {
            // Generate the code that decomposes the tuple back into the named arguments
            // Note that we don't want to recurse here!!.
            List<Value> GenerateArgTupleDecomposition(QsArgumentTuple arg, Value value)
            {
                Value BuildLoadForArg(QsArgumentTuple arg, Value value)
                {
                    ITypeRef argTypeRef = arg is QsArgumentTuple.QsTupleItem item
                        ? this.BuildArgItemTupleType(item)
                        : this.Types.Tuple;
                    // value is a pointer to the argument
                    Value actualArg = this.CurrentBuilder.Load(argTypeRef, value);
                    return actualArg;
                }

                List<Value> args = new List<Value>();
                if (arg is QsArgumentTuple.QsTuple tuple)
                {
                    var items = tuple.Item;
                    var n = items.Length;
                    if (n > 0)
                    {
                        ITypeRef tupleTypeRef = this.BuildArgTupleType(arg);
                        // Convert value from TuplePointer to the proper type
                        Value asStructPointer = this.CurrentBuilder.BitCast(value, tupleTypeRef.CreatePointerType());
                        var indices = new Value[]
                        {
                            this.Context.CreateConstant(0L),
                            this.Context.CreateConstant(1)
                        };
                        for (var i = 0; i < n; i++)
                        {
                            indices[1] = this.Context.CreateConstant(i + 1);
                            Value ptr = this.CurrentBuilder.GetElementPtr(tupleTypeRef, asStructPointer, indices);
                            args.Add(BuildLoadForArg(items[i], ptr));
                        }
                    }
                }
                else
                {
                    args.Add(BuildLoadForArg(arg, value));
                }

                return args;
            }

            void PopulateResultTuple(ResolvedType resultType, Value resultValue, Value resultTuplePointer, int item)
            {
                var resultTupleTypeRef = this.LlvmStructTypeFromQsharpType(resultType);
                if (resultType.Resolution is QsResolvedTypeKind.TupleType tupleType)
                {
                    // Here we'll step through and recurse
                    var itemCount = tupleType.Item.Length;
                    // Start with 1 because the 0 element of the LLVM structures is the tuple header
                    for (int i = 1; i <= itemCount; i++)
                    {
                        // TODO: complete the implementation
                    }
                }
                else if (!resultType.Resolution.IsUnitType)
                {
                    Value structPointer = this.CurrentBuilder.BitCast(resultTuplePointer, resultTupleTypeRef.CreatePointerType());
                    Value resultPointer = this.CurrentBuilder.GetElementPtr(
                        resultTupleTypeRef,
                        structPointer,
                        new[] { this.Context.CreateConstant(0L), this.Context.CreateConstant(item) });
                    this.CurrentBuilder.Store(resultValue, resultPointer);
                }
            }

            Value GenerateBaseMethodCall(QsCallable callable, QsSpecialization spec, List<Value> args)
            {
                if (this.TryGetFunction(callable.FullName.Namespace, callable.FullName.Name, spec.Kind, out IrFunction? func))
                {
                    return this.CurrentBuilder.Call(func, args.ToArray());
                }
                else
                {
                    return Constant.UndefinedValueFor(this.Types.Tuple);
                }
            }

            bool GenerateWrapperHeader(QsCallable callable, QsSpecialization spec)
            {
                if (this.TryGetWrapper(callable, spec.Kind, out IrFunction? func))
                {
                    this.CurrentFunction = func;
                    this.CurrentFunction.Parameters[0].Name = "capture-tuple";
                    this.CurrentFunction.Parameters[1].Name = "arg-tuple";
                    this.CurrentFunction.Parameters[2].Name = "result-tuple";
                    this.CurrentBlock = this.CurrentFunction.AppendBasicBlock("entry");
                    this.CurrentBuilder = new InstructionBuilder(this.CurrentBlock);
                    this.namesInScope.Push(new Dictionary<string, (Value, bool)>());
                    return true;
                }
                else
                {
                    return false;
                }
            }

            foreach (var kvp in this.wrapperQueue)
            {
                var callable = kvp.Value.Item1;
                foreach (var spec in callable.Specializations)
                {
                    if (GenerateWrapperHeader(callable, spec) && this.CurrentFunction != null)
                    {
                        Value argTupleValue = this.CurrentFunction.Parameters[1];
                        var argList = GenerateArgTupleDecomposition(callable.ArgumentTuple, argTupleValue);
                        var result = GenerateBaseMethodCall(callable, spec, argList);
                        PopulateResultTuple(callable.Signature.ReturnType, result, this.CurrentFunction.Parameters[2], 1);
                        this.CurrentBuilder.Return();
                    }
                }
            }
        }

        #endregion

        #region Inlining support
        // Embedded inlining -- inlining while in the middle of inlining -- should work,
        // but is not tested.

        /// <summary>
        /// Start inlining a callable invocation.
        /// This opens a new naming scope and increases the inlining level.
        /// </summary>
        public void StartInlining()
        {
            this.OpenNamingScope();
            this.CurrentInlineLevel++;
        }

        /// <summary>
        /// Stop inlining a callable invocation.
        /// This pops the top naming scope and decreases the inlining level.
        /// </summary>
        public void StopInlining()
        {
            this.CurrentInlineLevel--;
            this.CloseNamingScope();
        }

        /// <summary>
        /// Maps a variable name to an inlining-safe name.
        /// This way, names declared in an inlined callable don't conflict with names defined in the calling routine.
        /// </summary>
        /// <param name="name">The name to map</param>
        /// <returns>The mapped name</returns>
        public string InlinedName(string name)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < this.CurrentInlineLevel; i++)
            {
                sb.Append('.');
            }
            sb.Append(name);
            return sb.ToString();
        }

        #endregion

        /// <summary>
        /// Makes the given basic block current, creates a new builder for it, and makes that builder current.
        /// This method does not check to make sure that the block isn't already current.
        /// </summary>
        /// <param name="b">The block to make current</param>
        internal void SetCurrentBlock(BasicBlock b)
        {
            this.CurrentBlock = b;
            this.CurrentBuilder = new InstructionBuilder(b);
        }

        /// <summary>
        /// Creates a new basic block and adds it to the current function immediately after the current block.
        /// </summary>
        /// <param name="name">The base name for the new block; a counter will be appended to ensure uniqueness</param>
        /// <returns>The new block</returns>
        /// <exception cref="InvalidOperationException">The current function is set to null.</exception>
        internal BasicBlock AddBlockAfterCurrent(string name)
        {
            if (this.CurrentFunction == null)
            {
                throw new InvalidOperationException("no current function specified");
            }

            var flag = false;
            BasicBlock? next = null;
            foreach (var block in this.CurrentFunction.BasicBlocks)
            {
                if (flag)
                {
                    next = block;
                    break;
                }
                if (block == this.CurrentBlock)
                {
                    flag = true;
                }
            }
            var continueName = this.GenerateUniqueName(name);
            return next == null
                ? this.CurrentFunction.AppendBasicBlock(continueName)
                : this.CurrentFunction.InsertBasicBlock(continueName, next);
        }

        /// <summary>
        /// Generates a unique string with a given prefix.
        /// </summary>
        /// <param name="prefix">The prefix</param>
        /// <returns>A string that is unique across calls to this method</returns>
        internal string GenerateUniqueName(string prefix)
        {
            var index = this.uniqueNameIds.TryGetValue(prefix, out int n) ? n + 1 : 1;
            this.uniqueNameIds[prefix] = index;
            return $"{prefix}__{index}";
        }

        /// <summary>
        /// Registers a variable name as an alias for an LLVM value.
        /// </summary>
        /// <param name="name">The name to register</param>
        /// <param name="value">The LLVM value</param>
        /// <param name="isMutable">true if the name binding is mutable, false if immutable; the default is false</param>
        internal void RegisterName(string name, Value value, bool isMutable = false)
        {
            if (string.IsNullOrEmpty(value.Name))
            {
                value.RegisterName(this.InlinedName(name));
            }
            this.namesInScope.Peek().Add(name, (value, isMutable));
        }

        /// <summary>
        /// Gets the pointer to a mutable variable by name.
        /// The name must have been registered as an alias for the pointer value using
        /// <see cref="RegisterName(string, Value, bool)"/>.
        /// </summary>
        /// <param name="name">The registered variable name to look for</param>
        /// <returns>The pointer value for the mutable value</returns>
        internal Value GetNamedPointer(string name)
        {
            foreach (var dict in this.namesInScope)
            {
                if (dict.TryGetValue(name, out (Value, bool) item))
                {
                    if (item.Item2)
                    {
                        return item.Item1;
                    }
                }
            }
            throw new KeyNotFoundException($"Could not find a Value for mutable symbol {name}");
        }

        /// <summary>
        /// Pushes the value of a named variable on the value stack.
        /// The name must have been registered as an alias for the value using
        /// <see cref="RegisterName(string, Value, bool)"/>.
        /// <para>
        /// If the variable is mutable, then the associated pointer value is used to load and push the actual
        /// variable value.
        /// </para>
        /// </summary>
        /// <param name="name">The registered variable name to look for</param>
        internal void PushNamedValue(string name)
        {
            foreach (var dict in this.namesInScope)
            {
                if (dict.TryGetValue(name, out (Value, bool) item))
                {
                    this.ValueStack.Push(
                        item.Item2 && item.Item1.NativeType is IPointerType ptr
                        // Mutable, so the value is a pointer; we need to load what it's pointing to
                        ? this.CurrentBuilder.Load(ptr.ElementType, item.Item1)
                        : item.Item1);
                    return;
                }
            }
            throw new KeyNotFoundException($"Could not find a Value for local symbol {name}");
        }

        /// <summary>
        /// Opens a new naming scope and pushes it on top of the naming scope stack.
        /// <para>
        /// Naming scopes map variable names to values.
        /// New names are always added to the scope on top of the stack.
        /// When looking for a name, the stack is searched top-down.
        /// </para>
        /// </summary>
        internal void OpenNamingScope()
        {
            this.namesInScope.Push(new Dictionary<string, (Value, bool)>());
        }

        /// <summary>
        /// Closes the current naming scope by popping it off of the naming scope stack.
        /// </summary>
        internal void CloseNamingScope()
        {
            this.namesInScope.Pop();
        }

        #region Utils for global declaration

        /// <summary>
        /// Tries to find a global Q# callable in the current compilation.
        /// </summary>
        /// <param name="nsName">The callable's namespace</param>
        /// <param name="name">The callable's name</param>
        /// <param name="callable">The Q# callable, if found</param>
        /// <returns>true if the callable is found, false if not</returns>
        public bool TryFindGlobalCallable(string nsName, string name, [MaybeNullWhen(false)] out QsCallable callable)
        {
            callable = null;

            foreach (var ns in this.Compilation.Namespaces)
            {
                if (ns.Name == nsName)
                {
                    foreach (var element in ns.Elements)
                    {
                        if (element is QsNamespaceElement.QsCallable c)
                        {
                            if (c.GetFullName().Name == name)
                            {
                                callable = c.Item;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Tries to find a Q# user-defined type in the current compilation.
        /// </summary>
        /// <param name="nsName">The UDT's namespace</param>
        /// <param name="name">The UDT's name</param>
        /// <param name="udt">The Q# UDT< if found</param>
        /// <returns>true if the UDT is found, false if not</returns>
        public bool TryFindUDT(string nsName, string name, [MaybeNullWhen(false)] out QsCustomType udt)
        {
            udt = null;

            foreach (var ns in this.Compilation.Namespaces)
            {
                if (ns.Name == nsName)
                {
                    foreach (var element in ns.Elements)
                    {
                        if (element is QsNamespaceElement.QsCustomType t)
                        {
                            if (t.GetFullName().Name == name)
                            {
                                udt = t.Item;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Tells whether or not a callable invocation should be inlined.
        /// For this purpose, any invocation of a top-level callable with the "Inline" attribute
        /// should be inlined.
        /// </summary>
        /// <param name="method">The Q# callable expression to invoke</param>
        /// <param name="callable">The top-level callable to inline, if appropriate</param>
        /// <returns>true if the callable should be inlined, false if not</returns>
        public bool IsInlined(TypedExpression method, [MaybeNullWhen(false)] out QsCallable callable)
        {
            callable = null;
            if ((method.Expression is ResolvedExpression.Identifier id) && id.Item1 is Identifier.GlobalCallable c)
            {
                var nsName = c.Item.Namespace;
                var name = c.Item.Name;
                if (this.TryFindGlobalCallable(nsName, name, out callable))
                {
                    return callable.Attributes.Any(att =>
                        att.TypeId.IsValue &&
                        att.TypeId.Item.Namespace == BuiltIn.Inline.FullName.Namespace &&
                        att.TypeId.Item.Name == BuiltIn.Inline.FullName.Name);
                }
            }
            return false;
        }

        /// <summary>
        /// Tells whether or not a Q# callable expression is a quantum instruction.
        /// For this purpose, quantum instructions are top-level operations that have the "Intrinsic" attribute.
        /// </summary>
        /// <param name="method">The Q# callable expression to invoke</param>
        /// <param name="instructionName">The quantum instruction name, if the callable is a quantum instruction</param>
        /// <returns>true if the callable is a quantum instruction, false if not</returns>
        public bool IsQuantumInstructionCall(TypedExpression method, out string instructionName)
        {
            instructionName = "";
            if ((method.Expression is ResolvedExpression.Identifier id) && id.Item1 is Identifier.GlobalCallable c)
            {
                var nsName = c.Item.Namespace;
                var name = c.Item.Name;

                if (this.TryFindGlobalCallable(nsName, name, out QsCallable? callable)
                    && SymbolResolution.TryGetQISCode(callable.Attributes) is var att && att.IsValue)
                {
                    instructionName = att.Item;
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region Interop utils

        /// <summary>
        /// This type is used by <see cref="GenerateEntryPoint(QsQualifiedName)"/> to map Q# types to
        /// interop-friendly types.
        /// </summary>
        private class ArgMapping
        {
            internal readonly string BaseName;

            /// <summary>
            /// The first item contains the array type, and the second item contains the array count name.
            /// </summary>
            private readonly (ITypeRef, string)? arrayInfo;

            /// <summary>
            /// Contains the struct type.
            /// </summary>
            private readonly ITypeRef? structInfo;

            internal bool IsArray => this.arrayInfo != null;
            internal ITypeRef ArrayType => this.arrayInfo?.Item1 ?? throw new InvalidOperationException("not an array");
            internal string ArrayCountName => this.arrayInfo?.Item2 ?? throw new InvalidOperationException("not an array");

            internal bool IsStruct => this.structInfo != null;
            internal ITypeRef StructType => this.structInfo ?? throw new InvalidOperationException("not a struct");

            private ArgMapping(string baseName, (ITypeRef, string)? arrayInfo = null, ITypeRef? structInfo = null)
            {
                this.BaseName = baseName;
                this.arrayInfo = arrayInfo;
                this.structInfo = structInfo;
            }

            internal static ArgMapping Create(string baseName) =>
                new ArgMapping(baseName);

            internal ArgMapping WithArrayInfo(ITypeRef arrayType, string arrayCountName) =>
                new ArgMapping(this.BaseName, arrayInfo: (arrayType, arrayCountName));

            internal ArgMapping WithStructInfo(ITypeRef arrayType, string arrayCountName) =>
                new ArgMapping(this.BaseName, arrayInfo: (arrayType, arrayCountName));
        }

        // TODO: why do we need both GenerateEntryPoint and GenerateInteropWrapper?

        /// <summary>
        /// Generates an interop-friendly wrapper around the Q# entry point using the configured type mapping.
        /// </summary>
        /// <param name="qualifiedName">The namespace-qualified name of the Q# entry point</param>
        public void GenerateEntryPoint(QsQualifiedName qualifiedName)
        {
            // Unfortunately this is different enough from all of the other type mapping we do to require
            // an actual different routine. Blech...
            bool MapToCType(QsArgumentTuple t, List<ITypeRef> typeList, List<string> nameList, List<ArgMapping> mappingList)
            {
                bool changed = false;

                if (t is QsArgumentTuple.QsTuple tuple)
                {
                    foreach (QsArgumentTuple inner in tuple.Item)
                    {
                        changed |= MapToCType(inner, typeList, nameList, mappingList);
                    }
                }
                else if (t is QsArgumentTuple.QsTupleItem item && item.Item.VariableName is QsLocalSymbol.ValidName varName)
                {
                    var baseName = varName.Item;
                    var map = ArgMapping.Create(baseName);
                    switch (item.Item.Type.Resolution)
                    {
                        case QsResolvedTypeKind.ArrayType array:
                            // TODO: Handle multidimensional arrays
                            // TODO: Handle arrays of structs
                            typeList.Add(this.Context.Int64Type);
                            var elementTypeRef = this.LlvmTypeFromQsharpType(array.Item);
                            typeList.Add(elementTypeRef.CreatePointerType());
                            var arrayCountName = $"{baseName}__count";
                            nameList.Add(arrayCountName);
                            nameList.Add(baseName);
                            map = map.WithArrayInfo(elementTypeRef, arrayCountName);
                            changed = true;
                            mappingList.Add(map);
                            break;
                        case QsResolvedTypeKind.TupleType _:
                            // TODO: Handle structs
                            break;
                        default:
                            typeList.Add(this.LlvmTypeFromQsharpType(item.Item.Type));
                            nameList.Add(baseName);
                            mappingList.Add(map);
                            break;
                    }
                }
                return changed;
            }

            if (this.TryGetFunction(qualifiedName.Namespace, qualifiedName.Name, QsSpecializationKind.QsBody, out IrFunction? func)
                && this.TryFindGlobalCallable(qualifiedName.Namespace, qualifiedName.Name, out QsCallable? callable))
            {
                var epName = $"{qualifiedName.Namespace.Replace('.', '_')}_{qualifiedName.Name}";

                // Check to see if the arg list needs mapping to more C-friendly types
                // TODO: handle complicated return types
                var mappedArgList = new List<ITypeRef>();
                var mappedNameList = new List<string>();
                var mappingList = new List<ArgMapping>();
                var arraysToReleaseList = new List<Value>();
                var mappedResultType = this.MapToInteropType(func.ReturnType);
                if (MapToCType(callable.ArgumentTuple, mappedArgList, mappedNameList, mappingList) ||
                    (mappedResultType != func.ReturnType))
                {
                    var epFunc = this.Module.CreateFunction(epName, this.Context.GetFunctionType(mappedResultType, mappedArgList));
                    var namedValues = new Dictionary<string, Value>();
                    for (var i = 0; i < mappedNameList.Count; i++)
                    {
                        epFunc.Parameters[i].Name = mappedNameList[i];
                        namedValues[epFunc.Parameters[i].Name] = epFunc.Parameters[i];
                    }
                    var entryBlock = epFunc.AppendBasicBlock("entry");
                    var builder = new InstructionBuilder(entryBlock);
                    // Build the argument list for the inner function
                    var argValueList = new List<Value>();
                    foreach (var mapping in mappingList)
                    {
                        if (mapping.IsArray)
                        {
                            var elementSize64 = this.ComputeSizeForType(mapping.ArrayType, builder);
                            var elementSize = builder.IntCast(elementSize64, this.Context.Int32Type, false);
                            var length = namedValues[mapping.ArrayCountName];
                            var array = builder.Call(this.GetRuntimeFunction("array_create_1d"), elementSize, length);
                            argValueList.Add(array);
                            arraysToReleaseList.Add(array);
                            // Fill in the array if the length is >0. Since the QIR array is new, we assume we can use memcpy.
                            var copyBlock = epFunc.AppendBasicBlock("copy");
                            var nextBlock = epFunc.AppendBasicBlock("next");
                            var cond = builder.Compare(IntPredicate.SignedGreaterThan, length, this.Context.CreateConstant(0L));
                            builder.Branch(cond, copyBlock, nextBlock);
                            builder = new InstructionBuilder(copyBlock);
                            var destBase = builder.Call(this.GetRuntimeFunction("array_get_element_ptr_1d"), array, this.Context.CreateConstant(0L));
                            builder.MemCpy(
                                destBase,
                                namedValues[mapping.BaseName],
                                builder.Mul(length, builder.IntCast(elementSize, this.Context.Int64Type, true)),
                                false);
                            builder.Branch(nextBlock);
                            builder = new InstructionBuilder(nextBlock);
                        }
                        else if (mapping.IsStruct)
                        {
                            // TODO: map structures
                        }
                        else
                        {
                            argValueList.Add(namedValues[mapping.BaseName]);
                        }
                    }
                    if (func.ReturnType.IsVoid)
                    {
                        // A void entry point would be odd, but it isn't illegal
                        builder.Call(func, argValueList);
                        foreach (var arrayToRelease in arraysToReleaseList)
                        {
                            builder.Call(this.GetRuntimeFunction("array_unreference"), arrayToRelease);
                        }
                        builder.Return();
                    }
                    else
                    {
                        Value result = builder.Call(func, argValueList);
                        foreach (var arrayToRelease in arraysToReleaseList)
                        {
                            builder.Call(this.GetRuntimeFunction("array_unreference"), arrayToRelease);
                        }

                        if (mappedResultType != func.ReturnType)
                        {
                            result = builder.BitCast(result, mappedResultType);
                        }
                        builder.Return(result);
                    }
                    // Mark the function as an entry point
                    epFunc.AddAttributeAtIndex(
                        FunctionAttributeIndex.Function,
                        this.Context.CreateAttribute("EntryPoint"));
                }
                else
                {
                    this.Module.AddAlias(func, epName).Linkage = Linkage.External;
                    // Mark the function as an entry point
                    func.AddAttributeAtIndex(
                        FunctionAttributeIndex.Function,
                        this.Context.CreateAttribute("EntryPoint"));
                }
            }
        }

        /// <summary>
        /// Generates a stub implementation for a runtime function or quantum instruction using the specified type mappings for interoperability.
        /// Note that wrappers go into a separate module from the other QIR code.
        /// </summary>
        /// <param name="func">The function to generate a stub for</param>
        /// <param name="baseName">The function that the stub should call</param>
        /// <param name="m">(optional) The LLVM module in which the stub should be generated</param>
        private void GenerateInteropWrapper(IrFunction func, string baseName)
        {
            func = this.InteropModule.CreateFunction(func.Name, func.Signature);

            var mappedResultType = this.MapToInteropType(func.ReturnType);
            var argTypes = func.Parameters.Select(p => p.NativeType).ToArray();
            var mappedArgTypes = argTypes.Select(this.MapToInteropType).ToArray();

            var interopFunction = this.InteropModule.CreateFunction(
                baseName,
                this.Context.GetFunctionType(mappedResultType, mappedArgTypes));

            for (var i = 0; i < func.Parameters.Count; i++)
            {
                func.Parameters[i].Name = $"arg{i + 1}";
            }

            var builder = new InstructionBuilder(func.AppendBasicBlock("entry"));
            var implArgs = Enumerable.Range(0, argTypes.Length)
                .Select(index => argTypes[index] == mappedArgTypes[index]
                    ? func.Parameters[index]
                    : builder.BitCast(func.Parameters[index], mappedArgTypes[index]))
                .ToArray();
            var interopReturnValue = builder.Call(interopFunction, implArgs);
            if (func.ReturnType == this.Context.VoidType)
            {
                builder.Return();
            }
            else if (func.ReturnType == mappedResultType)
            {
                builder.Return(interopReturnValue);
            }
            else
            {
                var returnValue = builder.BitCast(interopReturnValue, func.ReturnType);
                builder.Return(returnValue);
            }
        }

        /// <summary>
        /// Maps a QIR type to a more interop-friendly type using the specified type mapping for interoperability.
        /// </summary>
        /// <param name="t">The type to map</param>
        /// <returns>The mapped type</returns>
        private ITypeRef MapToInteropType(ITypeRef t)
        {
            string typeName = "";
            if (t == this.Types.Result)
            {
                typeName = "Result";
            }
            else if (t == this.Types.Array)
            {
                typeName = "Array";
            }
            else if (t == this.Types.Pauli)
            {
                typeName = "Pauli";
            }
            else if (t == this.Types.BigInt)
            {
                typeName = "BigInt";
            }
            else if (t == this.Types.String)
            {
                typeName = "String";
            }
            else if (t == this.Types.Qubit)
            {
                typeName = "Qubit";
            }
            else if (t == this.Types.Callable)
            {
                typeName = "Callable";
            }
            else if (t == this.Types.Tuple)
            {
                typeName = "TuplePointer";
            }

            if ((typeName != "") && this.Config.InteropTypeMapping.TryGetValue(typeName, out string replacementName))
            {
                if (this.interopType.TryGetValue(typeName, out ITypeRef interopType))
                {
                    return interopType;
                }
                else
                {
                    var newType = this.Context.CreateStructType(replacementName).CreatePointerType();
                    this.interopType[typeName] = newType;
                    return newType;
                }
            }
            else
            {
                return t;
            }
        }

        #endregion

        #region Miscellaneous

        internal void AddRef(Value v)
        {
            string? s = null;
            var t = v.NativeType;
            Value valToAddref = v;
            if (t.IsPointer)
            {
                if (t == this.Types.Array)
                {
                    s = "array_reference";
                }
                else if (t == this.Types.Result)
                {
                    s = "result_reference";
                }
                else if (t == this.Types.String)
                {
                    s = "string_reference";
                }
                else if (t == this.Types.BigInt)
                {
                    s = "bigint_reference";
                }
                else if (this.IsTupleType(t))
                {
                    s = "tuple_reference";
                    valToAddref = this.CurrentBuilder.BitCast(v, this.Types.Tuple);
                }
                else if (t == this.Types.Callable)
                {
                    s = "callable_reference";
                }
            }
            if (s != null)
            {
                var func = this.GetRuntimeFunction(s);
                this.CurrentBuilder.Call(func, valToAddref);
            }
        }

        #endregion

        #region IDisposable Support

        private bool disposedValue = false; // To detect redundant calls

        /// <inheritdoc cref="IDisposable"/>
        public void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    this.Context.Dispose();
                }

                this.disposedValue = true;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            this.Dispose(true);
        }

        #endregion
    }
}