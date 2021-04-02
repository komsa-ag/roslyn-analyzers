﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;

namespace Microsoft.CodeAnalysis.ResxSourceGenerator.Test
{
    public static partial class CSharpSourceGeneratorVerifier<TSourceGenerator>
        where TSourceGenerator : ISourceGenerator, new()
    {
        public class Test : CSharpSourceGeneratorTest<TSourceGenerator, XUnitVerifier>
        {
            public Test()
            {
                SolutionTransforms.Add((solution, projectId) =>
                {
                    var project = solution.GetProject(projectId)!;
                    var parseOptions = (CSharpParseOptions)project.ParseOptions!;
                    solution = solution.WithProjectParseOptions(projectId, parseOptions.WithLanguageVersion(LanguageVersion));

                    var compilationOptions = solution.GetProject(projectId).CompilationOptions;
                    compilationOptions = compilationOptions.WithSpecificDiagnosticOptions(
                        compilationOptions.SpecificDiagnosticOptions.SetItems(CSharpVerifierHelper.NullableWarnings));
                    solution = solution.WithProjectCompilationOptions(projectId, compilationOptions);

                    return solution;
                });
            }

            public LanguageVersion LanguageVersion { get; set; } = LanguageVersion.Default;
        }
    }
}