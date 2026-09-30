using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mroczware.PowerFullTool.Refactoring
{
    public class PropertyOutputItem
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string EntireLine { get; set; }
        public string MatchedObjectParamName { get; set; }
        public string MatchedParamName { get; set; }
    };

    public class PropertyInputItem
    {
        public string Name { get; set; }
        public string ObjectName { get; set; }
        public string Type { get; set; }
    };

    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(MapperRefactoring)), Shared]
    internal class MapperRefactoring : CodeRefactoringProvider
    {

        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            SyntaxNode root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            SyntaxNode node = root.FindNode(context.Span);

            ClassDeclarationSyntax classDecl = node as ClassDeclarationSyntax
                    ?? node.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();

            MethodDeclarationSyntax methodDecl = node as MethodDeclarationSyntax
                    ?? node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();

            if (methodDecl is not null)
            {
                Compilation compilation = await context.Document.Project.GetCompilationAsync();
                SyntaxTree syntaxTree = await context.Document.GetSyntaxTreeAsync();
                SemanticModel model = compilation.GetSemanticModel(syntaxTree);
                IMethodSymbol methodSymbol = model.GetDeclaredSymbol(methodDecl);
                ITypeSymbol returnType = methodSymbol.ReturnType;

                bool isTask = returnType?.Name == "Task"
                    && returnType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";

                bool isGenericTask = isTask && returnType is INamedTypeSymbol namedType && namedType.IsGenericType;

                bool isComplexObject = returnType.TypeKind == TypeKind.Class;

                if (isComplexObject && !isGenericTask && !isTask && methodDecl.ParameterList.Parameters.Any())
                {
                    var action = CodeAction.Create(
                   $"🚀 Dodaj mapowanie na {methodSymbol.ReturnType.Name} w metodzie {methodDecl.Identifier.Text}",
                   async c => await CreateMappingAsync(context, methodDecl, c));

                    context.RegisterRefactoring(action);
                }
            }
        }

        /// <summary>
        /// CreateMapping
        /// </summary>
        /// <param name="context"></param>
        /// <param name="methodDecl"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<Document> CreateMappingAsync(CodeRefactoringContext context, MethodDeclarationSyntax methodDecl, CancellationToken cancellationToken)
        {
            Compilation compilation = await context.Document.Project.GetCompilationAsync();
            SyntaxTree syntaxTree = await context.Document.GetSyntaxTreeAsync();
            SemanticModel model = compilation.GetSemanticModel(syntaxTree);
            IMethodSymbol methodSymbol = model.GetDeclaredSymbol(methodDecl);
            ITypeSymbol returnType = methodSymbol.ReturnType;

            bool isComplexObject = returnType.TypeKind == TypeKind.Class
                                   && returnType.SpecialType == SpecialType.None
                                   && returnType.Name != "String";

            List<PropertyOutputItem> outputProperties = [];

            if (isComplexObject)
            {
                INamedTypeSymbol typeSymbol = returnType as INamedTypeSymbol;
                List<IPropertySymbol> props = GetAllPublicProperties(typeSymbol);

                IEnumerable<PropertyOutputItem> publicProperties = props
                                  .Where(p => p.DeclaredAccessibility == Accessibility.Public &&
                                        p.SetMethod != null &&
                                        p.SetMethod.DeclaredAccessibility == Accessibility.Public)
                                .Select(p => new PropertyOutputItem()
                                {
                                    Name = p.Name,
                                    Type = p.Type.ToDisplayString(),
                                })
                                .ToList();

                foreach (var prop in publicProperties)
                {
                    outputProperties.Add(new PropertyOutputItem()
                    {
                        Name = prop.Name,
                        Type = prop.Type,
                    });
                }
            }

            List<PropertyInputItem> inputProperties = [];

            foreach (ParameterSyntax param in methodDecl.ParameterList.Parameters)
            {
                ITypeSymbol paramTypeSymbol = model.GetTypeInfo(param.Type).Type;

                if (paramTypeSymbol?.TypeKind == TypeKind.Class && paramTypeSymbol.Name != "String")
                {
                    IParameterSymbol parameterSymbol = model.GetDeclaredSymbol(param);
                    INamedTypeSymbol actualTypeSymbol = parameterSymbol?.Type as INamedTypeSymbol;
                    if (actualTypeSymbol is null) continue;
                    List<IPropertySymbol> props = GetAllProperties(actualTypeSymbol);
                    IEnumerable<PropertyInputItem> objectProperties = props
                        .Select(p => new PropertyInputItem()
                        {
                            Name = CapitalizeFirstLetter(p.Name),
                            Type = p.Type.ToString(),
                            ObjectName = param.Identifier.Text,
                        })
                        .ToList();

                    inputProperties.AddRange(objectProperties);
                }
                else
                {
                    inputProperties.Add(new PropertyInputItem()
                    {
                        Name = param.Identifier.Text,
                        Type = paramTypeSymbol.ToString(),
                        ObjectName = string.Empty,
                    });
                }
            }

            foreach (PropertyOutputItem propertyOutput in outputProperties)
            {
                PropertyInputItem inputProperty = inputProperties.FirstOrDefault(i => i.Name.Equals(propertyOutput.Name, StringComparison.OrdinalIgnoreCase) && (i.Type == propertyOutput.Type || i.Type == propertyOutput.Type.Replace("?", "")));

                if (inputProperty != null)
                {
                    propertyOutput.MatchedParamName = inputProperty.Name;
                    propertyOutput.MatchedObjectParamName = inputProperty.ObjectName;
                }
            }

            AssignmentExpressionSyntax[] assignments = outputProperties.Where(p => !string.IsNullOrWhiteSpace(p.MatchedParamName)).Select(p =>
            {
                StringBuilder sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(p.MatchedObjectParamName))
                    sb.Append($"{p.MatchedObjectParamName}.");

                sb.Append(p.MatchedParamName);

                return SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(p.Name),
                    SyntaxFactory.ParseExpression(sb.ToString())
                );
            }
            ).ToArray();

            var unmappedProps = outputProperties
                .Where(p => string.IsNullOrWhiteSpace(p.MatchedParamName))
                .Select(p => p.Name)
                .ToList();

            SyntaxTriviaList leadingTrivia = SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed);

            if (unmappedProps.Any())
            {
                string commentText = "// Niezmapowane właściwości: " + string.Join(", ", unmappedProps);
                leadingTrivia = SyntaxFactory.TriviaList(SyntaxFactory.Comment(commentText), SyntaxFactory.ElasticCarriageReturnLineFeed);
            }

            List<SyntaxNodeOrToken> expressionsWithCommas = new List<SyntaxNodeOrToken>();
            for (int i = 0; i < assignments.Length; i++)
            {
                var assignment = assignments[i]
                    .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

                expressionsWithCommas.Add(assignment);

                if (i < assignments.Length - 1)
                    expressionsWithCommas.Add(SyntaxFactory.Token(SyntaxKind.CommaToken));
            }

            var initializer = SyntaxFactory.InitializerExpression(
                    SyntaxKind.ObjectInitializerExpression,
                    SyntaxFactory.SeparatedList<ExpressionSyntax>(expressionsWithCommas)
                )
                .WithOpenBraceToken(
                    SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
                        .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
                )
                .WithCloseBraceToken(
                    SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
                        .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
                );

            ReturnStatementSyntax returnStatement = SyntaxFactory.ReturnStatement(
                 SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(returnType.Name))
                     .WithInitializer(initializer)
             )
             .WithLeadingTrivia(leadingTrivia);

            BlockSyntax originalBody = methodDecl.Body;
            BlockSyntax newBody = originalBody.AddStatements(returnStatement);
            MethodDeclarationSyntax newMethod = methodDecl.WithBody(newBody);
            SyntaxNode root = await context.Document.GetSyntaxRootAsync(cancellationToken);
            SyntaxNode newRoot = root.ReplaceNode(methodDecl, newMethod);
            return context.Document.WithSyntaxRoot(newRoot);
        }


        private static string CapitalizeFirstLetter(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            for (int i = 0; i < input.Length; i++)
                if (char.IsLetter(input[i]))
                    return input.Substring(0, i) +
                               char.ToUpper(input[i]) +
                               input.Substring(i + 1);

            return input;
        }
        private static List<IPropertySymbol> GetAllPublicProperties(INamedTypeSymbol typeSymbol)
        {
            List<IPropertySymbol> result = new List<IPropertySymbol>();

            void Collect(INamedTypeSymbol symbol)
            {
                if (symbol == null || symbol.Name == "Object") return;

                IEnumerable<IPropertySymbol> properties = symbol.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !result.Any(r => r.Name == p.Name));

                result.AddRange(properties);

                Collect(symbol.BaseType);
            }

            Collect(typeSymbol);
            return result;
        }

        private static List<IPropertySymbol> GetAllProperties(INamedTypeSymbol typeSymbol)
        {
            List<IPropertySymbol> result = new List<IPropertySymbol>();

            void Collect(INamedTypeSymbol symbol)
            {
                if (symbol == null || symbol.Name == "Object") return;

                IEnumerable<IPropertySymbol> properties = symbol.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(p => !p.IsStatic && !result.Any(r => r.Name == p.Name));

                result.AddRange(properties);

                Collect(symbol.BaseType);
            }

            Collect(typeSymbol);
            return result;
        }
    }
}
