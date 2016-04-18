﻿namespace PCLMock.CodeGeneration
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Editing;
    using Microsoft.CodeAnalysis.MSBuild;
    using VB = Microsoft.CodeAnalysis.VisualBasic.Syntax;

    public static class Generator
    {
        private const string headerComment =
@"-----------------------------------------------------------------------
<autogenerated>
   This code was generated from a template.

   Changes to this file may cause incorrect behaviour and will be lost
   if the code is regenerated.
</autogenerated>
------------------------------------------------------------------------";

        public async static Task<IImmutableList<SyntaxNode>> GenerateMocksAsync(
            Language language,
            string solutionPath,
            Func<INamedTypeSymbol, bool> interfacePredicate,
            Func<INamedTypeSymbol, string> mockNamespaceSelector,
            Func<INamedTypeSymbol, string> mockNameSelector)
        {
            var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(solutionPath);

            return await GenerateMocksAsync(
                language,
                solution,
                interfacePredicate,
                mockNamespaceSelector,
                mockNameSelector);
        }

        public async static Task<IImmutableList<SyntaxNode>> GenerateMocksAsync(
            Language language,
            Solution solution,
            Func<INamedTypeSymbol, bool> interfacePredicate,
            Func<INamedTypeSymbol, string> mockNamespaceSelector,
            Func<INamedTypeSymbol, string> mockNameSelector)
        {
            var syntaxGenerator = SyntaxGenerator.GetGenerator(solution.Workspace, language.ToSyntaxGeneratorLanguageName());
            var compilations = await Task.WhenAll(
                solution
                    .Projects
                    .Select(async x =>
                        {
                            var compilation = await x.GetCompilationAsync();
                            // make sure the compilation has a reference to PCLMock
                            return compilation.AddReferences(MetadataReference.CreateFromFile(typeof(MockBase<>).Assembly.Location));
                        }));

            return compilations
                .SelectMany(x =>
                    x
                        .SyntaxTrees
                        .Select(y =>
                            new
                            {
                                Compilation = x,
                                SyntaxTree = y,
                                SemanticModel = x.GetSemanticModel(y)
                            }))
                .SelectMany(
                    x => x
                        .SyntaxTree
                        .GetRoot()
                        .DescendantNodes()
                        .Where(y => y is InterfaceDeclarationSyntax || y is VB.InterfaceBlockSyntax)
                        .Select(y =>
                            new
                            {
                                Compilation = x.Compilation,
                                SyntaxTree = x.SyntaxTree,
                                SemanticModel = x.SemanticModel,
                                InterfaceSymbol = (INamedTypeSymbol)x.SemanticModel.GetDeclaredSymbol(y)
                            }))
                .Where(x => interfacePredicate == null || interfacePredicate(x.InterfaceSymbol))
                .Distinct()
                .Select(x => GenerateMock(language, syntaxGenerator, x.SemanticModel, x.InterfaceSymbol, mockNamespaceSelector(x.InterfaceSymbol), mockNameSelector(x.InterfaceSymbol)))
                .Select((x, i) => i == 0 ? syntaxGenerator.WithLeadingComments(x, headerComment, language) : x)
                .ToImmutableList();
        }

        private static SyntaxNode GenerateMock(
            Language language,
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel,
            INamedTypeSymbol interfaceSymbol,
            string mockNamespace,
            string mockName)
        {
            var namespaceSyntax = GetNamespaceDeclarationSyntax(syntaxGenerator, semanticModel, mockNamespace, language);
            var classSyntax = GetClassDeclarationSyntax(syntaxGenerator, semanticModel, mockName, interfaceSymbol);

            classSyntax = syntaxGenerator
                .AddAttributes(classSyntax, GetClassAttributesSyntax(syntaxGenerator, semanticModel));
            classSyntax = syntaxGenerator
                .AddMembers(classSyntax, GetMemberDeclarations(syntaxGenerator, semanticModel, mockName, interfaceSymbol, language));
            namespaceSyntax = syntaxGenerator
                .AddMembers(namespaceSyntax, classSyntax);

            return syntaxGenerator
                .CompilationUnit(namespaceSyntax)
                .NormalizeWhitespace();
        }

        private static SyntaxNode GetNamespaceDeclarationSyntax(
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel,
            string @namespace,
            Language language)
        {
            return syntaxGenerator.NamespaceDeclaration(@namespace);
        }

        private static SyntaxNode GetClassDeclarationSyntax(
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel,
            string name,
            INamedTypeSymbol interfaceSymbol)
        {
            var interfaceType = syntaxGenerator.TypeExpression(interfaceSymbol);
            var mockBaseType = semanticModel
                .Compilation
                .GetTypeByMetadataName("PCLMock.MockBase`1");

            if (mockBaseType == null)
            {
                throw new InvalidOperationException("Failed to find type in PCLMock assembly. Are you sure this project has a reference to PCLMock?");
            }

            var baseType = syntaxGenerator.TypeExpression(
                mockBaseType
                    .Construct(interfaceSymbol));

            var accessibility = interfaceSymbol.DeclaredAccessibility == Accessibility.NotApplicable
                ? Accessibility.Public
                : interfaceSymbol.DeclaredAccessibility;

            var classDeclaration = syntaxGenerator.ClassDeclaration(
                name,
                accessibility: accessibility,
                modifiers: DeclarationModifiers.Partial,
                typeParameters: interfaceSymbol.TypeParameters.Select(x => x.Name),
                baseType: baseType,
                interfaceTypes: new[] { interfaceType });

            // TODO: tidy this up once this issue is rectified: https://github.com/dotnet/roslyn/issues/1658
            foreach (var typeParameter in interfaceSymbol.TypeParameters)
            {
                if (typeParameter.HasConstructorConstraint ||
                    typeParameter.HasReferenceTypeConstraint ||
                    typeParameter.HasValueTypeConstraint ||
                    typeParameter.ConstraintTypes.Length > 0)
                {
                    var kinds = (typeParameter.HasConstructorConstraint ? SpecialTypeConstraintKind.Constructor : SpecialTypeConstraintKind.None) |
                                (typeParameter.HasReferenceTypeConstraint ? SpecialTypeConstraintKind.ReferenceType : SpecialTypeConstraintKind.None) |
                                (typeParameter.HasValueTypeConstraint ? SpecialTypeConstraintKind.ValueType : SpecialTypeConstraintKind.None);

                    classDeclaration = syntaxGenerator.WithTypeConstraint(
                        classDeclaration,
                        typeParameter.Name,
                        kinds: kinds,
                        types: typeParameter.ConstraintTypes.Select(t => syntaxGenerator.TypeExpression(t)));
                }
            }

            return classDeclaration;
        }

        private static IEnumerable<SyntaxNode> GetClassAttributesSyntax(
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel)
        {
            // GENERATED CODE:
            //
            //     [System.CodeDom.Compiler.GeneratedCode("PCLMock", "[version]")]
            //     [System.Runtime.CompilerServices.CompilerGenerated)]
            yield return syntaxGenerator
                .Attribute(
                    "System.CodeDom.Compiler.GeneratedCode",
                    syntaxGenerator.LiteralExpression("PCLMock"),
                    syntaxGenerator.LiteralExpression(typeof(MockBase<>).Assembly.GetName().Version.ToString()));
            yield return syntaxGenerator
                .Attribute(
                    "System.Runtime.CompilerServices.CompilerGenerated");
        }

        private static SyntaxNode GetConstructorDeclarationSyntax(
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel,
            string name)
        {
            // GENERATED CODE:
            //
            //     public Name(MockBehavior behavior = MockBehavior.Strict)
            //         : base(behavior)
            //     {
            //         if (behavior == MockBehavior.Loose)
            //         {
            //             ConfigureLooseBehavior();
            //         }
            //     }
            var mockBehaviorType = syntaxGenerator
                .TypeExpression(
                    semanticModel
                        .Compilation
                        .GetTypeByMetadataName("PCLMock.MockBehavior"));

            return syntaxGenerator
                .ConstructorDeclaration(
                    name,
                    parameters: new[]
                    {
                        syntaxGenerator
                            .ParameterDeclaration(
                                "behavior",
                                mockBehaviorType,
                                initializer: syntaxGenerator.MemberAccessExpression(mockBehaviorType, "Strict"))
                    },
                    accessibility: Accessibility.Public,
                    baseConstructorArguments: new[] { syntaxGenerator.IdentifierName("behavior") },
                    statements: new[]
                    {
                        syntaxGenerator.IfStatement(
                            syntaxGenerator.ValueEqualsExpression(
                                syntaxGenerator.IdentifierName("behavior"),
                                syntaxGenerator.MemberAccessExpression(mockBehaviorType, "Loose")),
                                new[]
                                {
                                    syntaxGenerator.InvocationExpression(syntaxGenerator.IdentifierName("ConfigureLooseBehavior"))
                                })
                    });
        }

        private static SyntaxNode GetInitializationMethodSyntax(
            Language language,
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel)
        {
            // GENERATED CODE:
            //
            //     partial void ConfigureLooseBehavior();
            return syntaxGenerator.MethodDeclaration(
                "ConfigureLooseBehavior",
                accessibility: language == Language.VisualBasic ? Accessibility.Private : Accessibility.NotApplicable,
                modifiers: DeclarationModifiers.Partial);
        }

        private static IEnumerable<SyntaxNode> GetMemberDeclarations(
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel,
            string name,
            INamedTypeSymbol interfaceSymbol,
            Language language)
        {
            return
                new SyntaxNode[]
                {
                    GetConstructorDeclarationSyntax(syntaxGenerator, semanticModel, name),
                    GetInitializationMethodSyntax(language, syntaxGenerator, semanticModel)
                }
                .Concat(
                    GetMembersRecursive(interfaceSymbol)
                        .Select(x => GetMemberDeclarationSyntax(syntaxGenerator, semanticModel, x))
                        .Where(x => x != null)
                        .GroupBy(x => x, SyntaxNodeEqualityComparer.Instance)
                        .Where(group => group.Count() == 1)
                        .SelectMany(group => group)
                        .Select(x => syntaxGenerator.AsPublicInterfaceImplementation(x, syntaxGenerator.TypeExpression(interfaceSymbol))));
        }

        private static IEnumerable<ISymbol> GetMembersRecursive(INamedTypeSymbol interfaceSymbol)
        {
            foreach (var member in interfaceSymbol.GetMembers())
            {
                yield return member;
            }

            foreach (var implementedInterface in interfaceSymbol.Interfaces)
            {
                foreach (var member in GetMembersRecursive(implementedInterface))
                {
                    yield return member;
                }
            }
        }

        private static SyntaxNode GetMemberDeclarationSyntax(
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel,
            ISymbol symbol)
        {
            var propertySymbol = symbol as IPropertySymbol;

            if (propertySymbol != null)
            {
                return GetPropertyDeclarationSyntax(syntaxGenerator, semanticModel, propertySymbol);
            }

            var methodSymbol = symbol as IMethodSymbol;

            if (methodSymbol != null)
            {
                return GetMethodDeclarationSyntax(syntaxGenerator, semanticModel, methodSymbol);
            }

            // unsupported symbol type, but we don't error - the user can supplement our code as necessary because it's a partial class
            return null;
        }

        private static SyntaxNode GetPropertyDeclarationSyntax(
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel,
            IPropertySymbol propertySymbol)
        {
            var getAccessorStatements = GetPropertyGetAccessorsSyntax(syntaxGenerator, semanticModel, propertySymbol).ToList();
            var setAccessorStatements = GetPropertySetAccessorsSyntax(syntaxGenerator, semanticModel, propertySymbol).ToList();
            var declarationModifiers = DeclarationModifiers.None;

            if (getAccessorStatements.Count == 0)
            {
                declarationModifiers = declarationModifiers.WithIsWriteOnly(true);

                // set-only properties are not currently supported
                return null;
            }

            if (setAccessorStatements.Count == 0)
            {
                declarationModifiers = declarationModifiers.WithIsReadOnly(true);
            }

            if (!propertySymbol.IsIndexer)
            {
                return syntaxGenerator
                    .PropertyDeclaration(
                        propertySymbol.Name,
                        syntaxGenerator.TypeExpression(propertySymbol.Type),
                        accessibility: Accessibility.Public,
                        modifiers: declarationModifiers,
                        getAccessorStatements: getAccessorStatements,
                        setAccessorStatements: setAccessorStatements);
            }
            else
            {
                var parameters = propertySymbol
                    .Parameters
                    .Select(x => syntaxGenerator.ParameterDeclaration(x.Name, syntaxGenerator.TypeExpression(x.Type)))
                    .ToList();

                return syntaxGenerator
                    .IndexerDeclaration(
                        parameters,
                        syntaxGenerator.TypeExpression(propertySymbol.Type),
                        accessibility: Accessibility.Public,
                        modifiers: declarationModifiers,
                        getAccessorStatements: getAccessorStatements,
                        setAccessorStatements: setAccessorStatements);
            }
        }

        private static IEnumerable<SyntaxNode> GetPropertyGetAccessorsSyntax(
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel,
            IPropertySymbol propertySymbol)
        {
            if (propertySymbol.GetMethod == null)
            {
                yield break;
            }

            var lambdaParameterName = GetUniqueName(propertySymbol);

            if (!propertySymbol.IsIndexer)
            {
                // GENERATED CODE:
                //
                //     return this.Apply(x => x.PropertyName);
                yield return syntaxGenerator
                    .ReturnStatement(
                        syntaxGenerator.InvocationExpression(
                            syntaxGenerator.MemberAccessExpression(
                                syntaxGenerator.ThisExpression(),
                                "Apply"),
                            syntaxGenerator.ValueReturningLambdaExpression(
                                lambdaParameterName,
                                syntaxGenerator.MemberAccessExpression(
                                    syntaxGenerator.IdentifierName(lambdaParameterName),
                                    syntaxGenerator.IdentifierName(propertySymbol.Name)))));
            }
            else
            {
                // GENERATED CODE:
                //
                //     return this.Apply(x => x[first, second]);
                var arguments = propertySymbol
                    .Parameters
                    .Select(x => syntaxGenerator.Argument(syntaxGenerator.IdentifierName(x.Name)))
                    .ToList();

                yield return syntaxGenerator
                    .ReturnStatement(
                        syntaxGenerator.InvocationExpression(
                            syntaxGenerator.MemberAccessExpression(
                                syntaxGenerator.ThisExpression(),
                                "Apply"),
                            syntaxGenerator.ValueReturningLambdaExpression(
                                lambdaParameterName,
                                syntaxGenerator.ElementAccessExpression(
                                    syntaxGenerator.IdentifierName(lambdaParameterName),
                                    arguments))));
            }
        }

        private static IEnumerable<SyntaxNode> GetPropertySetAccessorsSyntax(
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel,
            IPropertySymbol propertySymbol)
        {
            if (propertySymbol.SetMethod == null)
            {
                yield break;
            }

            var lambdaParameterName = GetUniqueName(propertySymbol);

            if (!propertySymbol.IsIndexer)
            {
                // GENERATED CODE:
                //
                //     this.ApplyPropertySet(x => x.PropertyName, value);
                yield return syntaxGenerator
                    .InvocationExpression(
                        syntaxGenerator.MemberAccessExpression(
                            syntaxGenerator.ThisExpression(),
                            "ApplyPropertySet"),
                        syntaxGenerator.ValueReturningLambdaExpression(
                            lambdaParameterName,
                            syntaxGenerator.MemberAccessExpression(
                                syntaxGenerator.IdentifierName(lambdaParameterName),
                                syntaxGenerator.IdentifierName(propertySymbol.Name))),
                        syntaxGenerator.IdentifierName("value"));
            }
            else
            {
                // GENERATED CODE:
                //
                //     this.ApplyPropertySet(x => x[first, second], value);
                var arguments = propertySymbol
                    .Parameters
                    .Select(x => syntaxGenerator.Argument(syntaxGenerator.IdentifierName(x.Name)))
                    .ToList();

                yield return syntaxGenerator
                    .InvocationExpression(
                        syntaxGenerator.MemberAccessExpression(
                            syntaxGenerator.ThisExpression(),
                            "ApplyPropertySet"),
                        syntaxGenerator.ValueReturningLambdaExpression(
                            lambdaParameterName,
                            syntaxGenerator.ElementAccessExpression(
                                syntaxGenerator.IdentifierName(lambdaParameterName),
                                arguments)),
                        syntaxGenerator.IdentifierName("value"));
            }
        }

        private static SyntaxNode GetMethodDeclarationSyntax(
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel,
            IMethodSymbol methodSymbol)
        {
            if (methodSymbol.MethodKind != MethodKind.Ordinary)
            {
                return null;
            }

            var methodDeclaration = syntaxGenerator
                .MethodDeclaration(methodSymbol);
            methodDeclaration = syntaxGenerator
                .WithModifiers(
                    methodDeclaration,
                    syntaxGenerator
                        .GetModifiers(methodDeclaration)
                        .WithIsAbstract(false));
            methodDeclaration = syntaxGenerator
                .WithStatements(
                    methodDeclaration,
                    GetMethodStatementsSyntax(syntaxGenerator, semanticModel, methodSymbol));

            var csharpMethodDeclaration = methodDeclaration as MethodDeclarationSyntax;

            if (csharpMethodDeclaration != null)
            {
                // remove trailing semi-colon from the declaration
                methodDeclaration = csharpMethodDeclaration.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
            }

            return methodDeclaration;
        }

        private static IEnumerable<SyntaxNode> GetMethodStatementsSyntax(
            SyntaxGenerator syntaxGenerator,
            SemanticModel semanticModel,
            IMethodSymbol methodSymbol)
        {
            // GENERATED CODE (for every ref or out parameter):
            //
            //     string someOutParameter;
            //     var someRefParameter = default(int);
            for (var i = 0; i < methodSymbol.Parameters.Length; ++i)
            {
                var parameter = methodSymbol.Parameters[i];

                if (parameter.RefKind == RefKind.Out)
                {
                    yield return syntaxGenerator
                        .LocalDeclarationStatement(
                            syntaxGenerator.TypeExpression(parameter.Type),
                            GetNameForParameter(methodSymbol, parameter));
                }
                else if (parameter.RefKind == RefKind.Ref)
                {
                    yield return syntaxGenerator
                        .LocalDeclarationStatement(
                            GetNameForParameter(methodSymbol, parameter),
                            initializer: syntaxGenerator.DefaultExpression(syntaxGenerator.TypeExpression(parameter.Type)));
                }
            }

            var arguments = methodSymbol
                .Parameters
                .Select(x =>
                    syntaxGenerator
                        .Argument(
                            x.RefKind,
                            syntaxGenerator.IdentifierName(GetNameForParameter(methodSymbol, x))))
                .ToList();

            var typeArguments = methodSymbol
                .TypeArguments
                .Select(x => syntaxGenerator.TypeExpression(x))
                .ToList();

            var lambdaParameterName = GetUniqueName(methodSymbol);

            var lambdaInvocation = syntaxGenerator
                .MemberAccessExpression(
                    syntaxGenerator.IdentifierName(lambdaParameterName),
                    methodSymbol.Name);

            if (typeArguments.Count > 0)
            {
                lambdaInvocation = syntaxGenerator
                    .WithTypeArguments(
                        lambdaInvocation,
                        typeArguments);
            }

            // GENERATED CODE (for every ref or out parameter):
            //
            //     someOutParameter = this.GetOutParameterValue<string>(x => x.TheMethod(out someOutParameter), parameterIndex: 0);
            //     someRefParameter = this.GetRefParameterValue<int>(x => x.TheMethod(ref someRefParameter), parameterIndex: 0);
            for (var i = 0; i < methodSymbol.Parameters.Length; ++i)
            {
                var parameter = methodSymbol.Parameters[i];

                if (parameter.RefKind == RefKind.Out || parameter.RefKind == RefKind.Ref)
                {
                    var nameOfMethodToCall = parameter.RefKind == RefKind.Out ? "GetOutParameterValue" : "GetRefParameterValue";

                    yield return syntaxGenerator
                        .AssignmentStatement(
                            syntaxGenerator.IdentifierName(parameter.Name),
                            syntaxGenerator
                            .InvocationExpression(
                                syntaxGenerator.MemberAccessExpression(
                                    syntaxGenerator.ThisExpression(),
                                    syntaxGenerator.GenericName(
                                        nameOfMethodToCall,
                                        typeArguments: syntaxGenerator.TypeExpression(parameter.Type))),
                                        arguments: new[]
                                        {
                                            syntaxGenerator.ValueReturningLambdaExpression(
                                                lambdaParameterName,
                                                syntaxGenerator.InvocationExpression(
                                                    lambdaInvocation,
                                                    arguments: arguments)),
                                                syntaxGenerator.LiteralExpression(i)
                                        }));
                }
            }

            // GENERATED CODE:
            //
            //     [return] this.Apply(x => x.SomeMethod(param1, param2));
            var applyInvocation = syntaxGenerator
                .InvocationExpression(
                    syntaxGenerator.MemberAccessExpression(
                        syntaxGenerator.ThisExpression(),
                        "Apply"),
                    syntaxGenerator.ValueReturningLambdaExpression(
                        lambdaParameterName,
                        syntaxGenerator.InvocationExpression(
                            lambdaInvocation,
                            arguments: arguments)));

            if (!methodSymbol.ReturnsVoid)
            {
                applyInvocation = syntaxGenerator.ReturnStatement(applyInvocation);
            }

            yield return applyInvocation;
        }

        private static string GetUniqueName(IPropertySymbol within, string proposed = "x")
        {
            while (within.Parameters.Any(x => x.Name == proposed))
            {
                proposed = "_" + proposed;
            }

            return proposed;
        }

        private static string GetUniqueName(IMethodSymbol within, string proposed = "x")
        {
            while (within.Parameters.Any(x => x.Name == proposed))
            {
                proposed = "_" + proposed;
            }

            return proposed;
        }

        private static string GetNameForParameter(IMethodSymbol within, IParameterSymbol parameterSymbol)
        {
            switch (parameterSymbol.RefKind)
            {
                case RefKind.None:
                    return parameterSymbol.Name;
                case RefKind.Ref:
                    return GetUniqueName(within, parameterSymbol.Name);
                case RefKind.Out:
                    return GetUniqueName(within, parameterSymbol.Name);
                default:
                    throw new NotSupportedException("Unknown parameter ref kind: " + parameterSymbol.RefKind);
            }
        }

        private sealed class SyntaxNodeEqualityComparer : IEqualityComparer<SyntaxNode>
        {
            public static readonly SyntaxNodeEqualityComparer Instance = new SyntaxNodeEqualityComparer();

            private SyntaxNodeEqualityComparer()
            {
            }

            public bool Equals(SyntaxNode x, SyntaxNode y) =>
                x.IsEquivalentTo(y, topLevel: true);

            // We have to ensure like syntax nodes have the same hash code in order for Equals to even be called
            // Unfortunately, Roslyn does not implement GetHashCode, so we can't use that. We also don't want to
            // use ToString because then we may as well have just grouped by it and because it includes the
            // implementation, not just the declaration. To do this "properly", we'd have to write a recursive
            // hash code calculator, using similar logic to what IsEquivalentTo gives us.
            public int GetHashCode(SyntaxNode obj) =>
                0;
        }
    }
}