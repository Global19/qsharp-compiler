﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

namespace Microsoft.Quantum.QsCompiler.BondSchemas
{
    using BondQsCompilation = V1.QsCompilation;

    internal static class Translators
    {
        public static SyntaxTree.QsCompilation FromBondSchemaToSyntaxTree<TBond>(
            TBond bondCompilation)
        {
            switch (bondCompilation)
            {
#pragma warning disable IDE0001 // Simplify Names
                case V1.QsCompilation bondCompilationV1:
                    return V1.CompilerObjectTranslator.CreateQsCompilation(bondCompilationV1);

                case V2.QsCompilation bondCompilationV2:
                    return V2.CompilerObjectTranslator.CreateQsCompilation(bondCompilationV2);

                default:
                    // TODO: Use a more meaningful message.
                    throw new ArgumentException();
#pragma warning restore IDE0001 // Simplify Names
            }
        }

        public static BondQsCompilation FromSyntaxTreeToBondSchema(SyntaxTree.QsCompilation qsCompilation) =>
            V1.BondSchemaTranslator.CreateBondCompilation(qsCompilation);
    }
}
