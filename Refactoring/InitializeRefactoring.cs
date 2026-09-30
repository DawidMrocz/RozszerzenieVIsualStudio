using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mroczware.PowerFullTool.Refactoring
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(AddIDependencyRefactoring)), Shared]
    internal class InitializeRefactoring : CodeRefactoringProvider
    {
        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var node = root.FindNode(context.Span);

            var creation = node.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>();
            if (creation == null)
                return;

            var action = CodeAction.Create(
               $"🚀 Zainicjalizuj właściwości",
               c => AddPropertyInitializersAsync(context, creation, c),
               nameof(InitializeRefactoring)
           );

            context.RegisterRefactoring(action);
        }

        private async Task<Document> AddPropertyInitializersAsync(CodeRefactoringContext context, ObjectCreationExpressionSyntax creation, CancellationToken cancellationToken)
        {
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken);
            var typeInfo = semanticModel.GetTypeInfo(creation, context.CancellationToken);
            var typeSymbol = typeInfo.Type as INamedTypeSymbol;

            List<IPropertySymbol> properties = GetAllPublicProperties(typeSymbol);

            var assignments = properties.Select(p =>
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(p.Name),
                    GetDefaultValue(p.Type)
                )
                .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
            ).ToList();

            var expressionsWithCommas = new List<SyntaxNodeOrToken>();
            for (int i = 0; i < assignments.Count; i++)
            {
                expressionsWithCommas.Add(assignments[i]);
                if (i < assignments.Count - 1)
                {
                    expressionsWithCommas.Add(SyntaxFactory.Token(SyntaxKind.CommaToken));
                }
            }

            var initializer = SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SeparatedList<ExpressionSyntax>(expressionsWithCommas)
            );

            var newCreation = creation.WithInitializer(initializer);

            SyntaxNode root = await context.Document.GetSyntaxRootAsync(cancellationToken);

            var newRoot = root.ReplaceNode(creation, newCreation);
            return context.Document.WithSyntaxRoot(newRoot);
        }

        private ExpressionSyntax GetDefaultValue(ITypeSymbol type)
        {
            if (type == null)
                return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

            if (type.NullableAnnotation == NullableAnnotation.Annotated && type is INamedTypeSymbol namedType && namedType.TypeArguments.Length == 1)
                return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

            if (IsGuidType(type))
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("Guid"),
                    SyntaxFactory.IdentifierName("Empty")
                );
            }

            if (type.TypeKind == TypeKind.Class && type.SpecialType != SpecialType.System_String && type is INamedTypeSymbol namedClass)
            {
                var hasParameterlessCtor = namedClass.InstanceConstructors.Any(ctor =>
                    ctor.DeclaredAccessibility == Accessibility.Public &&
                    ctor.Parameters.Length == 0);

                if (hasParameterlessCtor)
                {
                    return SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName(type.ToDisplayString().Replace("?", "")))
                        .WithArgumentList(SyntaxFactory.ArgumentList());
                }
                else
                {
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
                }
            }

            switch (type.SpecialType)
            {
                case SpecialType.System_String:
                    return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(""));
                case SpecialType.System_Char:
                    return SyntaxFactory.LiteralExpression(SyntaxKind.CharacterLiteralExpression, SyntaxFactory.Literal('\0'));
                case SpecialType.System_Boolean:
                    return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression);
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0));
                case SpecialType.System_Single:
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0f));
                case SpecialType.System_Double:
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0.0));
                case SpecialType.System_Decimal:
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0m));
                case SpecialType.System_DateTime:
                    return SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("DateTime"),
                        SyntaxFactory.IdentifierName("Now")
                    );
                case SpecialType.System_Object:
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
                default:
                    return SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(type.ToDisplayString()));
            }
        }

        bool IsGuidType(ITypeSymbol type)
        {
            return type.ContainingNamespace?.ToDisplayString() == "System" &&
                   type.Name == "Guid";
        }

        private static List<IPropertySymbol> GetAllPublicProperties(INamedTypeSymbol typeSymbol)
        {
            List<IPropertySymbol> result = [];

            void Collect(INamedTypeSymbol symbol)
            {
                if (symbol == null || symbol.Name == "Object") return;

                IEnumerable<IPropertySymbol> properties = symbol.GetMembers()
                   .OfType<IPropertySymbol>()
                   .Where(p =>
                   p.DeclaredAccessibility == Accessibility.Public
                   && p.SetMethod != null
                   && p.SetMethod.DeclaredAccessibility == Accessibility.Public
                   && !p.IsStatic && !result.Any(r => r.Name == p.Name));

                result.AddRange(properties);

                Collect(symbol.BaseType);
            }

            Collect(typeSymbol);
            return result;
        }
    }
}
