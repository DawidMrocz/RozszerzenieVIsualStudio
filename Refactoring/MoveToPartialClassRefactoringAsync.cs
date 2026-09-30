using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil.Cil;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mroczware.PowerFullTool.Refactoring
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(AddIDependecyRefactoring)), Shared]
    internal class AddIDependecyRefactoring : CodeRefactoringProvider
    {

        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            SyntaxNode root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            SyntaxNode node = root.FindNode(context.Span);

            MethodDeclarationSyntax methodDecl = node as MethodDeclarationSyntax
                    ?? node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();

            if (methodDecl != null)
            {
                ClassDeclarationSyntax classDecl = methodDecl.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();

                if (
                    classDecl != null
                    //&& classDecl.BaseList != null
                    //&& classDecl.BaseList.Types.Any(t => t.Type is IdentifierNameSyntax id && id.Identifier.Text.Equals($"I{classDecl.Identifier.Text}", StringComparison.OrdinalIgnoreCase))
                    )
                {
                    NamespaceDeclarationSyntax namespaceDecl = classDecl.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
                    if (namespaceDecl != null)
                    {
                        var action = CustomCodeAction.Create(
                         $"🚀 Przenieś metodę do pliku {classDecl.Identifier.Text}.{methodDecl.Identifier.Text}.cs",
                         async (c, isPreview) => await MoveToPartialClassRefactoringAsync(context, methodDecl, c, isPreview),
                         equivalenceKey: "MoveToPartialClass");

                        context.RegisterRefactoring(action);
                    }
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
        private static async Task<Microsoft.CodeAnalysis.Solution> MoveToPartialClassRefactoringAsync(CodeRefactoringContext context, MethodDeclarationSyntax methodDecl, CancellationToken cancellationToken, bool isPreview)
        {

            ClassDeclarationSyntax classDecl = methodDecl.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            NamespaceDeclarationSyntax namespaceDecl = classDecl.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();

            string newFileName = $"{classDecl.Identifier.Text}.{methodDecl.Identifier.Text}.cs";

            ClassDeclarationSyntax classDeclaration = SyntaxFactory.ClassDeclaration(classDecl.Identifier.Text)
                  .WithModifiers(classDecl.Modifiers)
                  .AddMembers(methodDecl.WithLeadingTrivia())
                  .NormalizeWhitespace();

            NamespaceDeclarationSyntax namespaceDeclaration = SyntaxFactory.NamespaceDeclaration(
                    SyntaxFactory.ParseName(namespaceDecl.Name.ToString()))
                .AddMembers(classDeclaration);

            CompilationUnitSyntax root = await context.Document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;

            CompilationUnitSyntax compilationUnit = SyntaxFactory.CompilationUnit()
                .WithUsings(root.Usings)
                .AddMembers(namespaceDeclaration)
                .NormalizeWhitespace();

            string newFileText = compilationUnit.ToFullString();

            ClassDeclarationSyntax updatedClass = classDecl.WithMembers(
                new SyntaxList<MemberDeclarationSyntax>(
                    classDecl.Members.Where(m => m != methodDecl)
                )
            );

            CompilationUnitSyntax newRoot = root.ReplaceNode(classDecl, updatedClass);
            Microsoft.CodeAnalysis.Document updatedDocument = context.Document.WithSyntaxRoot(newRoot);
            Microsoft.CodeAnalysis.Solution currentSolution = updatedDocument.Project.Solution;
            Microsoft.CodeAnalysis.Project updatedProject = currentSolution.GetProject(context.Document.Project.Id);
            Microsoft.CodeAnalysis.Document newDocument = updatedProject.AddDocument(newFileName, newFileText);

            if (!isPreview)
            {
                string filePath = Path.Combine(Path.GetDirectoryName(context.Document.FilePath), newFileName);
                File.WriteAllText(filePath, newFileText, Encoding.UTF8);
                await AddFileToProjectAsync(filePath, context.Document.Project.FilePath);
            }

            Microsoft.CodeAnalysis.Solution finalSolution = updatedDocument.Project.Solution;
            return finalSolution;
        }

        private static async Task AddFileToProjectAsync(string filePath, string projectPath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            var solution = dte.Solution;

            EnvDTE.Project project = solution.Projects
                .OfType<EnvDTE.Project>()
                .FirstOrDefault(i =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return i.Name == Path.GetFileNameWithoutExtension(projectPath);
                });

            if (project != null)
                project.ProjectItems.AddFromFile(filePath);
        }
    }

    public class CustomCodeAction : CodeAction
    {
        private readonly Func<CancellationToken, bool, Task<Microsoft.CodeAnalysis.Solution>> _createChangedSolution;

        public override string EquivalenceKey { get; }
        public override string Title { get; }

        protected CustomCodeAction(string title, Func<CancellationToken, bool, Task<Microsoft.CodeAnalysis.Solution>> createChangedSolution, string equivalenceKey = null)
        {
            _createChangedSolution = createChangedSolution;

            Title = title;
            EquivalenceKey = equivalenceKey;
        }

        /// <summary>
        ///     Creates a <see cref="CustomCodeAction" /> for a change to more than one <see cref="Document" /> within a <see cref="Solution" />.
        ///     Use this factory when the change is expensive to compute and should be deferred until requested.
        /// </summary>
        /// <param name="title">Title of the <see cref="CustomCodeAction" />.</param>
        /// <param name="createChangedSolution">Function to create the <see cref="Solution" />.</param>
        /// <param name="equivalenceKey">Optional value used to determine the equivalence of the <see cref="CustomCodeAction" /> with other <see cref="CustomCodeAction" />s. See <see cref="CustomCodeAction.EquivalenceKey" />.</param>
        public static CustomCodeAction Create(string title, Func<CancellationToken, bool, Task<Microsoft.CodeAnalysis.Solution>> createChangedSolution, string equivalenceKey = null)
        {
            if (title == null)
                throw new ArgumentNullException(nameof(title));

            if (createChangedSolution == null)
                throw new ArgumentNullException(nameof(createChangedSolution));

            return new CustomCodeAction(title, createChangedSolution, equivalenceKey);
        }

        protected override async Task<IEnumerable<CodeActionOperation>> ComputePreviewOperationsAsync(CancellationToken cancellationToken)
        {
            const bool isPreview = true;
            // Content copied from http://source.roslyn.io/#Microsoft.CodeAnalysis.Workspaces/CodeActions/CodeAction.cs,81b0a0866b894b0e,references
            var changedSolution = await GetChangedSolutionWithPreviewAsync(cancellationToken, isPreview).ConfigureAwait(false);
            if (changedSolution == null)
                return null;

            return new CodeActionOperation[] { new ApplyChangesOperation(changedSolution) };
        }

        protected override Task<Microsoft.CodeAnalysis.Solution> GetChangedSolutionAsync(CancellationToken cancellationToken)
        {
            const bool isPreview = false;
            return GetChangedSolutionWithPreviewAsync(cancellationToken, isPreview);
        }

        protected virtual Task<Microsoft.CodeAnalysis.Solution> GetChangedSolutionWithPreviewAsync(CancellationToken cancellationToken, bool isPreview)
        {
            return _createChangedSolution(cancellationToken, isPreview);
        }
    }
}
