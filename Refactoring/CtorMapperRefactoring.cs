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
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(CtorMapperRefactoring)), Shared]
    internal class CtorMapperRefactoring : CodeRefactoringProvider
    {

        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            SyntaxNode root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            SyntaxNode node = root.FindNode(context.Span);

            ConstructorDeclarationSyntax ctorDecl = node as ConstructorDeclarationSyntax
                    ?? node.AncestorsAndSelf().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();

            if (ctorDecl is not null)
            {
                Compilation compilation = await context.Document.Project.GetCompilationAsync();
                SyntaxTree syntaxTree = await context.Document.GetSyntaxTreeAsync();
                SemanticModel model = compilation.GetSemanticModel(syntaxTree);

                if (ctorDecl.ParameterList.Parameters.Any())
                {
                    var action = CodeAction.Create(
                   $"🚀 Dodaj mapowanie w kontruktorze {ctorDecl.Identifier.Text}",
                   async c => await CreateCtorMappingAsync(context, ctorDecl, c));

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
        private static async Task<Document> CreateCtorMappingAsync(CodeRefactoringContext context, ConstructorDeclarationSyntax ctorDecl, CancellationToken cancellationToken)
        {
            Compilation compilation = await context.Document.Project.GetCompilationAsync();
            SyntaxTree syntaxTree = await context.Document.GetSyntaxTreeAsync();
            SemanticModel model = compilation.GetSemanticModel(syntaxTree);
            IMethodSymbol ctorSymbol = model.GetDeclaredSymbol(ctorDecl);

            //Właściwości wynikowe
            List<PropertyOutputItem> outputProperties = [];
            List<IPropertySymbol> props = GetAllProperties(ctorSymbol.ContainingType);

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

            //========================================================================================================

            //Właściwości werjsciowe
            List<PropertyInputItem> inputProperties = [];

            foreach (ParameterSyntax param in ctorDecl.ParameterList.Parameters)
            {
                ITypeSymbol paramTypeSymbol = model.GetTypeInfo(param.Type).Type;

                if (paramTypeSymbol?.TypeKind == TypeKind.Class && paramTypeSymbol.Name != "String")
                {
                    IParameterSymbol parameterSymbol = model.GetDeclaredSymbol(param);
                    INamedTypeSymbol actualTypeSymbol = parameterSymbol?.Type as INamedTypeSymbol;
                    if (actualTypeSymbol is null) continue;
                    List<IPropertySymbol> parameterProps = GetAllProperties(actualTypeSymbol);
                    IEnumerable<PropertyInputItem> objectProperties = parameterProps
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

            ExpressionStatementSyntax[] assignments = outputProperties.Where(p => !string.IsNullOrWhiteSpace(p.MatchedParamName)).Select(p =>
            {
                StringBuilder sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(p.MatchedObjectParamName))
                    sb.Append($"{p.MatchedObjectParamName}.");

                sb.Append(p.MatchedParamName);

                return SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(p.Name),
                    SyntaxFactory.ParseExpression(sb.ToString())).WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
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

                if (assignments.Length > 0)
                    assignments[0] = assignments[0].WithLeadingTrivia(leadingTrivia);
            }

            BlockSyntax originalBody = ctorDecl.Body ?? SyntaxFactory.Block();
            BlockSyntax newBody = originalBody.AddStatements(assignments.ToArray());
            ConstructorDeclarationSyntax newCtor = ctorDecl.WithBody(newBody);
            SyntaxNode root = await context.Document.GetSyntaxRootAsync(cancellationToken);
            SyntaxNode newRoot = root.ReplaceNode(ctorDecl, newCtor);
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
