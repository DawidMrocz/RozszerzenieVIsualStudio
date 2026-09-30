using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Mroczware.PowerFullTool.Refactoring
{


    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(AddIDependencyRefactoring)), Shared]
    internal class AddIDependencyRefactoring : CodeRefactoringProvider
    {
        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            SyntaxNode root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            SyntaxNode node = root.FindNode(context.Span);

            InterfaceDeclarationSyntax interfaceDecl = node as InterfaceDeclarationSyntax
                ?? node.AncestorsAndSelf().OfType<InterfaceDeclarationSyntax>().FirstOrDefault();

            if (interfaceDecl is not null && (interfaceDecl.BaseList is null || !interfaceDecl.BaseList.Types.Any(t => t.Type is IdentifierNameSyntax id && id.Identifier.Text.Equals($"IDependency", StringComparison.OrdinalIgnoreCase))))
            {
                var action = CodeAction.Create(
                         $"🚀 Dodaj IDependency do pliku",
                         async c => await AddIDependencyAsync(context, interfaceDecl, c),
                         equivalenceKey: "MoveToPartialClass");

                context.RegisterRefactoring(action);
            }
        }

        /// <summary>
        /// CreateMapping
        /// </summary>
        /// <param name="context"></param>
        /// <param name="methodDecl"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<Document> AddIDependencyAsync(CodeRefactoringContext context, InterfaceDeclarationSyntax interfaceDecl, CancellationToken cancellationToken)
        {
            Microsoft.CodeAnalysis.Project project = context.Document.Project;
            var projectPath = project.FilePath;

            XDocument xmlDoc = XDocument.Load(projectPath);
            XElement targetFrameworkElement = xmlDoc.Descendants("TargetFramework").FirstOrDefault();
            string targetFramework = targetFrameworkElement?.Value;

            string usingToAdd;

            // Wybór przestrzeni nazw na podstawie wersji frameworka
            switch (targetFramework)
            {
                case "net8.0":
                case "net8.0-windows":
                    usingToAdd = "Mroczware.Framework.Infrastructure.Interfaces.DependencyInjection";
                    break;

                case "net6.0":
                case "net6.0-windows":
                    usingToAdd = "Mroczware.Framework.Infrastructure.Interfaces";
                    break;

                default:
                    usingToAdd = "Mroczware.Framework";
                    break;
            }

            SimpleBaseTypeSyntax newBase = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("IDependency"));
            InterfaceDeclarationSyntax updatedInterface;
            if (interfaceDecl.BaseList == null)
            {
                updatedInterface = interfaceDecl.WithBaseList(
                    SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(newBase)));
            }
            else
            {
                BaseListSyntax newBaseList = interfaceDecl.BaseList.AddTypes(newBase);
                updatedInterface = interfaceDecl.WithBaseList(newBaseList);
            }

            SyntaxNode root = await context.Document.GetSyntaxRootAsync(cancellationToken);
            SyntaxNode newRoot = root.ReplaceNode(interfaceDecl, updatedInterface);
            CompilationUnitSyntax compilationUnit = (CompilationUnitSyntax)newRoot;
            SyntaxList<UsingDirectiveSyntax> usings = compilationUnit.Usings;
            UsingDirectiveSyntax existingUsing = usings.FirstOrDefault(u => u.Name.ToString() == usingToAdd);

            if (existingUsing == null)
            {
                UsingDirectiveSyntax newUsing = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(usingToAdd));
                SyntaxList<UsingDirectiveSyntax> updatedUsings = usings.Add(newUsing);
                compilationUnit = compilationUnit.WithUsings(updatedUsings);
            }

            string formattedCode = compilationUnit.NormalizeWhitespace(indentation: "    ", eol: "\n").ToFullString();
            return context.Document.WithSyntaxRoot(await SyntaxFactory.ParseSyntaxTree(formattedCode).GetRootAsync());
        }
    }
}
