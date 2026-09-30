using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Mroczware.PowerFullTool.Commands;
using Mroczware.PowerFullTool.Helpers;
using MroczwareTools;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using Document = EnvDTE.Document;

namespace Mroczware.PowerFullTool
{
    public enum CreateMappingType
    {
        CreateDto,
        CreateModel,
        CreateResponseDto
    }

    [Command(PackageIds.GenerateDto)]
    internal sealed class GenerateDto : BaseCommand<GenerateDto>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();

                if (docView is null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Brak aktywnego dokumentu.");
                    return;
                }

                Microsoft.VisualStudio.Text.SnapshotSpan selection = docView.TextView.Selection.SelectedSpans.FirstOrDefault();

                if (selection == null || string.IsNullOrWhiteSpace(selection.GetText()))
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Brak zaznaczenia nazwy klasy.");
                    return;
                }

                IVsTextManager editor = await ServiceProvider.GetGlobalServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
                if (editor is null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Nie udało się pobrać edytora.");
                    return;
                }

                DTE2 dte = await ServiceProvider.GetGlobalServiceAsync(typeof(DTE)) as DTE2;

                if (dte.Solution == null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Brak solucji.");
                    return;
                }

                Document activeDocument = dte.ActiveDocument;

                if (activeDocument == null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Brak aktywnego dokumentu.");
                    return;
                }

                if (!activeDocument.Saved)
                {
                    QuickAcationsHelper.ShowStatusBarMessage($"Plik {activeDocument.Name} zawiera niezapisane zmiany. Zapisz go przed kontynuacją.");
                    return;
                }

                if (activeDocument.Language != "CSharp")
                {
                    QuickAcationsHelper.ShowStatusBarMessage($"Dokument {activeDocument.Name} nie jest w języku C#");
                    return;
                }

                List<string> folders = GetFoldersStructure(activeDocument);

                EnvDTE.Project activeDocumentProject = QuickAcationsHelper.GetContainingProjectName(dte.ActiveDocument);

                if (activeDocumentProject is null || string.IsNullOrWhiteSpace(activeDocumentProject.FullName))
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Nie znaleziono projektu dla otwartego pliku");
                    return;
                }

                var projectsAndConnectionString = QuickAcationsHelper.GetProjectsAndConnectionString(dte, activeDocumentProject);

                if (projectsAndConnectionString is null) return;

                CreateMappingType mappingType;

                EnvDTE.Project targetProject;
                string activeProjectNameWithoutSuffix = TrimSuffix(activeDocumentProject.Name);
                string targetFolder;

                if (activeDocumentProject.Name.EndsWith(".Api", StringComparison.OrdinalIgnoreCase))
                {
                    targetProject = projectsAndConnectionString.ProjectCore;
                    targetFolder = projectsAndConnectionString.DtoFolder;
                    mappingType = CreateMappingType.CreateDto;
                }
                else if (activeDocumentProject.Name.EndsWith(".Core", StringComparison.OrdinalIgnoreCase))
                {
                    targetProject = projectsAndConnectionString.ProjectData;
                    targetFolder = projectsAndConnectionString.ModelsFolder;
                    mappingType = CreateMappingType.CreateModel;
                }
                else if (activeDocumentProject.Name.EndsWith(".Data", StringComparison.OrdinalIgnoreCase))
                {
                    targetProject = projectsAndConnectionString.ProjectCore;
                    targetFolder = projectsAndConnectionString.DtoFolder;
                    mappingType = CreateMappingType.CreateResponseDto;
                }
                else
                {
                    QuickAcationsHelper.ShowStatusBarMessage($"Zanaczenie musi zostać dokonane w projekcie z suffixem .Api / .Core / .Data");
                    return;
                }

                if (targetProject is null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Nie znaleziono projektu, w którym zostanie stworzona klasa.");
                    return;
                }

                editor.GetActiveView(1, null, out IVsTextView textView);

                Microsoft.VisualStudio.Text.ITextBuffer textBuffer = docView.TextBuffer;

                if (textView != null)
                {
                    string code = textBuffer.CurrentSnapshot.GetText();

                    Microsoft.CodeAnalysis.Document document = textBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();

                    if (document == null)
                    {
                        QuickAcationsHelper.ShowStatusBarMessage($"Nie znaleziono w pliku metody o nazwie {selection.GetText()}.");
                        return;
                    }

                    DocumentId documentId = document.Id;
                    Compilation compilation = await document.Project.GetCompilationAsync();
                    SyntaxTree syntaxTree = await document.GetSyntaxTreeAsync();
                    SemanticModel model = compilation.GetSemanticModel(syntaxTree);
                    CompilationUnitSyntax root = await syntaxTree.GetRootAsync() as CompilationUnitSyntax;
                    IEnumerable<ClassDeclarationSyntax> classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
                    ClassDeclarationSyntax classDeclaration = classDeclarations.FirstOrDefault(c => c.Identifier.Text.Equals(selection.GetText()));

                    if (classDeclaration is null)
                    {
                        QuickAcationsHelper.ShowStatusBarMessage($"Nie znaleziono w pliku klasy o nazwie {selection.GetText()}.");
                        return;
                    }

                    NamespaceDeclarationSyntax namespaceDeclaration = classDeclaration
                            .Ancestors()
                            .OfType<NamespaceDeclarationSyntax>()
                            .FirstOrDefault();

                    if (namespaceDeclaration == null)
                    {
                        QuickAcationsHelper.ShowStatusBarMessage($"Klasa musi znajdować się w namespace");
                        return;
                    }

                    List<PropertyDeclarationSyntax> properties = [];

                    INamedTypeSymbol classSymbol = model.GetDeclaredSymbol(classDeclaration);

                    EnvDTE.Project project = activeDocument.ProjectItem.ContainingProject;

                    bool isNullableSet = IsNullableSet(project);

                    if (classSymbol != null && classSymbol.TypeKind == TypeKind.Class)
                    {
                        List<IPropertySymbol> allProps = GetAllPublicProperties(classSymbol);

                        foreach (IPropertySymbol prop in allProps)
                        {
                            string typeName = prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            TypeSyntax typeSyntax = SyntaxFactory.ParseTypeName(typeName);

                            string initializerExpr = GetInitializerExpression(prop.Type, isNullableSet);

                            PropertyDeclarationSyntax propertyDeclaration = SyntaxFactory.PropertyDeclaration(typeSyntax, prop.Name)
                                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                                .AddAccessorListAccessors(
                                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                                    SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                                );

                            if (initializerExpr != null)
                            {
                                propertyDeclaration = propertyDeclaration
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(initializerExpr)))
                                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                            }

                            properties.Add(propertyDeclaration);
                        }
                    }

                    if (properties is null || !properties.Any())
                    {
                        QuickAcationsHelper.ShowStatusBarMessage($"Klasa nie zawiera publicznych właściwości");
                        return;
                    }

                    List<AssignmentExpressionSyntax> assigments2 = properties.Select(p =>
                    {
                        return SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(p.Identifier.Text),
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("model"),
                                SyntaxFactory.IdentifierName(p.Identifier.Text)
                            )
                        );
                    }).ToList();

                    List<AssignmentExpressionSyntax> assigments = properties.Select(p =>
                    {
                        return SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(p.Identifier.Text),
                            SyntaxFactory.IdentifierName(p.Identifier.Text));
                    }).ToList();

                    string className = mappingType switch
                    {
                        CreateMappingType.CreateDto => $"{Regex.Replace(selection.GetText(), "Request$", "")}Dto",
                        CreateMappingType.CreateModel => $"{Regex.Replace(selection.GetText(), "Dto$", "")}Model",
                        CreateMappingType.CreateResponseDto => $"{selection.GetText()}Dto",
                        _ => throw new Exception("Nie udało się wywnioskować nazwy klasy")
                    };

                    string methodName = mappingType switch
                    {
                        CreateMappingType.CreateDto => "ToDto",
                        CreateMappingType.CreateModel => "ToModel",
                        CreateMappingType.CreateResponseDto => "ToDto",
                        _ => throw new Exception("Nie udało się wywnioskować nazwy metody")
                    };

                    if (mappingType == CreateMappingType.CreateDto || mappingType == CreateMappingType.CreateModel)
                    {
                        if (mappingType == CreateMappingType.CreateDto)
                            assigments.Add(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                                                                SyntaxFactory.IdentifierName("AuthorizedUserId"),
                                                                                SyntaxFactory.IdentifierName("authorizedUserId")));

                        MethodDeclarationSyntax toDtoMethod = SyntaxFactory.MethodDeclaration(
                           SyntaxFactory.ParseTypeName(className), methodName)
                               .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                               .WithBody(
                                   SyntaxFactory.Block(
                                       SyntaxFactory.SingletonList<StatementSyntax>(
                                           SyntaxFactory.ReturnStatement(
                                               SyntaxFactory.ObjectCreationExpression(
                                                   SyntaxFactory.ParseTypeName(className))
                                                   .WithArgumentList(
                                                       SyntaxFactory.ArgumentList()
                                                   )
                                                   .WithInitializer(
                                                       SyntaxFactory.InitializerExpression(SyntaxKind.ObjectInitializerExpression,
                                                           SyntaxFactory.SeparatedList<ExpressionSyntax>(assigments))
                                                   )
                                           )
                                       )
                                   )
                               );

                        if (mappingType == CreateMappingType.CreateDto)
                        {
                            toDtoMethod = toDtoMethod.AddParameterListParameters(
                                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("authorizedUserId"))
                                        .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)))
                                );
                        }

                        ClassDeclarationSyntax newClassDeclaration = classDeclaration.AddMembers(toDtoMethod);

                        CompilationUnitSyntax newRoot = root.ReplaceNode(classDeclaration, newClassDeclaration);
                        string newCode = newRoot.NormalizeWhitespace().ToFullString();

                        using (Microsoft.VisualStudio.Text.ITextEdit edit = textBuffer.CreateEdit())
                        {
                            edit.Replace(0, textBuffer.CurrentSnapshot.Length, newCode);
                            edit.Apply();
                        }
                    }

                    SyntaxTree syntaxTree2 = CSharpSyntaxTree.ParseText(textBuffer.CurrentSnapshot.GetText());
                    CompilationUnitSyntax root2 = await syntaxTree2.GetRootAsync() as CompilationUnitSyntax;

                    StringBuilder newUsingBuilder = new($"{targetProject.Name}.{targetFolder}");
                    if (folders != null && folders.Any())
                        newUsingBuilder.Append($".{string.Join(".", folders)}");

                    string newUsing = newUsingBuilder.ToString();

                    bool usingExists = root2.Usings
                    .Any(u =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        return u.Name.ToString() == newUsing;
                    });

                    if (!usingExists && mappingType != CreateMappingType.CreateResponseDto)
                    {
                        List<UsingDirectiveSyntax> usings = root2.DescendantNodes().OfType<UsingDirectiveSyntax>().ToList();

                        UsingDirectiveSyntax newUsingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(newUsing))
                            .NormalizeWhitespace();

                        SyntaxList<UsingDirectiveSyntax> newUsings = SyntaxFactory.List(new[] { newUsingDirective }.Concat(usings));

                        root2 = root2.WithUsings(newUsings);
                    }

                    string newCode2 = root2.NormalizeWhitespace().ToFullString();

                    using (Microsoft.VisualStudio.Text.ITextEdit edit = textBuffer.CreateEdit())
                    {
                        edit.Replace(0, textBuffer.CurrentSnapshot.Length, newCode2);
                        edit.Apply();
                    }

                    string folderPath;
                    if (mappingType == CreateMappingType.CreateDto || mappingType == CreateMappingType.CreateResponseDto)
                    {
                        folderPath = Path.Combine(new[] { $"{Path.GetDirectoryName(projectsAndConnectionString.ProjectCore.FullName)}\\{projectsAndConnectionString.DtoFolder}" }.Concat(folders).ToArray());
                    }
                    else
                    {
                        folderPath = Path.Combine(new[] { $"{Path.GetDirectoryName(projectsAndConnectionString.ProjectData.FullName)}\\{projectsAndConnectionString.ModelsFolder}" }.Concat(folders).ToArray());
                    }

                    string filePath = Path.Combine(folderPath, $"{className}.cs");

                    Directory.CreateDirectory(folderPath);

                    string classCode = GenerateClassCode(projectsAndConnectionString, selection.GetText(), className, newUsing, properties.ToList(), mappingType, methodName, assigments2, folders, activeDocumentProject.Name);

                    SaveFile($"{className}.cs", filePath, classCode, targetProject);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Wystąpił błąd podczas wykonywania polecenia 😞 : {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        public bool IsNullableSet(EnvDTE.Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string csprojPath = project.FileName;
            XmlDocument xmlDoc = new();
            xmlDoc.Load(csprojPath);

            XDocument doc = XDocument.Load(csprojPath);
            string nullableValue = doc.Descendants("Nullable").FirstOrDefault()?.Value?.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(nullableValue)) return false;

            if (nullableValue == "enable")
                return true;
            else
                return false;
        }

        string GetInitializerExpression(ITypeSymbol type, bool isNullableSet)
        {
            if (type == null)
                return "null";

            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                string name = named.ConstructedFrom.ToDisplayString();

                if (name == "IEnumerable<>" ||
                    name == "ICollection<>" ||
                    name == "IList<>" ||
                    name == "List<>")
                {
                    string arg = named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    return $"new List<{arg}>()";
                }
            }

            if (isNullableSet && type.SpecialType == SpecialType.System_String && (type.NullableAnnotation == NullableAnnotation.None || type.NullableAnnotation == NullableAnnotation.NotAnnotated))
                return "null!";

            return null;
        }

        List<IPropertySymbol> GetAllPublicProperties(INamedTypeSymbol typeSymbol)
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

        private string TrimSuffix(string projectName)
        {
            string[] suffixes = [".Api", ".Core", ".Data"];

            foreach (string suffix in suffixes)
                if (projectName.EndsWith(suffix))
                    return projectName.Substring(0, projectName.Length - suffix.Length);

            throw new InvalidOperationException($"Nie prawidłowa nazwa projektu: {projectName}");

        }

        public static void SaveFile(string className, string filePath, string classCode, EnvDTE.Project targetProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (File.Exists(filePath))
            {
                if (IsFileDirty(filePath, targetProject.DTE))
                {
                    QuickAcationsHelper.ShowStatusBarMessage($"Plik {filePath} jest już edytowany - zapisz go przed wykonaniem operacji.");
                    return;
                }

                DialogResult result = System.Windows.Forms.MessageBox.Show(
                    "Plik już istnieje. Czy chcesz go nadpisać?",
                    "Potwierdzenie",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                );

                if (result == DialogResult.Yes)
                {
                    File.WriteAllText(filePath, classCode, Encoding.UTF8);
                    targetProject.ProjectItems.AddFromFile(filePath);
                    QuickAcationsHelper.ShowStatusBarMessage($"Zaktualizowano plik {className}");
                }
                else
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Zapis pliku został anulowany.");
                }
            }
            else
            {
                File.WriteAllText(filePath, classCode, Encoding.UTF8);
                targetProject.ProjectItems.AddFromFile(filePath);
                QuickAcationsHelper.ShowStatusBarMessage($"Utworzono plik {className}");
            }
        }

        public static bool IsFileDirty(string filePath, DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (Document doc in dte.Documents)
                if (doc.FullName.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                    return doc.Saved == false;

            return false;
        }

        private static string GenerateClassCode(ProjectsFromSettings projectsAndConnectionString, string baseClaseName, string className, string namespaceName, List<PropertyDeclarationSyntax> properties, CreateMappingType createMappingType, string methodName, List<AssignmentExpressionSyntax> assigments, List<string> folders, string acativeProjectName)
        {
            ClassDeclarationSyntax classDeclaration = SyntaxFactory.ClassDeclaration(className)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

            if (createMappingType == CreateMappingType.CreateDto)
            {
                PropertyDeclarationSyntax property = SyntaxFactory.ParseSyntaxTree("public int AuthorizedUserId { get; set; }").GetRoot()
                              .DescendantNodes().OfType<PropertyDeclarationSyntax>().FirstOrDefault();

                if (property != null)
                    properties.Add(property);
            }

            classDeclaration = classDeclaration.AddMembers(properties.ToArray());

            if (createMappingType == CreateMappingType.CreateResponseDto)
            {
                ConstructorDeclarationSyntax constructor = SyntaxFactory.ConstructorDeclaration(className)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                    .AddParameterListParameters(
                        SyntaxFactory.Parameter(SyntaxFactory.Identifier("model"))
                            .WithType(SyntaxFactory.ParseTypeName(baseClaseName))
                    )
                    .WithBody(
                        SyntaxFactory.Block(
                            assigments.Select(assign =>
                                SyntaxFactory.ExpressionStatement(assign)
                            )
                        )
                    );

                classDeclaration = classDeclaration.AddMembers(constructor);
            }

            NamespaceDeclarationSyntax namespaceDeclaration = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName))
                .AddMembers(classDeclaration);

            CompilationUnitSyntax compilationUnit = SyntaxFactory.CompilationUnit();

            if (createMappingType == CreateMappingType.CreateResponseDto)
            {
                UsingDirectiveSyntax usingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName($"{acativeProjectName}.{projectsAndConnectionString.ModelsFolder}.{string.Join(".", folders)}"));
                compilationUnit = compilationUnit.AddUsings(usingDirective);
            }

            SyntaxTree syntaxTree = SyntaxFactory.SyntaxTree(
            compilationUnit
                .AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System")))
                .AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Collections.Generic")))
                .AddMembers(namespaceDeclaration)
                .NormalizeWhitespace()
        );

            return syntaxTree.ToString();
        }

        public string SearchBaseClassInProjects(EnvDTE.Solution solution, string className)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (EnvDTE.Project project in solution.Projects)
            {
                if (string.IsNullOrWhiteSpace(project.FullName)) continue;
                foreach (ProjectItem projectItem in project.ProjectItems)
                {
                    string searchedClass = SearchBaseClassInProjectItems(projectItem, className);
                    if (!string.IsNullOrWhiteSpace(searchedClass)) return searchedClass;
                }

            }
            return null;
        }

        private string SearchBaseClassInProjectItems(ProjectItem item, string className)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (item.FileCodeModel != null)
                foreach (CodeElement element in item.FileCodeModel.CodeElements)
                    if (element is CodeNamespace ns)
                    {
                        foreach (CodeElement child in ns.Members)
                            if (child is CodeClass codeClass)
                                if (codeClass.Name.Equals(className, StringComparison.OrdinalIgnoreCase))
                                    return item.FileNames[1];
                    }

            if (item.ProjectItems != null)
                foreach (ProjectItem subItem in item.ProjectItems)
                {
                    string path = SearchBaseClassInProjectItems(subItem, className);
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }

            return null;
        }

        /// <summary>
        /// Pobranie nadrzędnych folderów z włączeniem pierwszego
        /// </summary>
        /// <param name="activeDocument"></param>
        /// <returns></returns>
        private static List<string> GetFoldersStructure(Document activeDocument)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            List<string> parentFolders = new();
            ProjectItem item = activeDocument?.ProjectItem;
            if (item != null)
            {
                ProjectItem parent = item.Collection?.Parent as ProjectItem;
                while (parent != null)
                {
                    parentFolders.Add(parent.Name);
                    parent = parent.Collection?.Parent as ProjectItem;
                }
            }

            List<string> subFolders = new(parentFolders);
            if (subFolders.Count > 0)
                subFolders.RemoveAt(subFolders.Count - 1);

            return subFolders.AsEnumerable().Reverse().ToList();
        }
    }
}
