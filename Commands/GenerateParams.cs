using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Mroczware.PowerFullTool.Helpers;
using Document = EnvDTE.Document;
using MroczwareTools;

namespace Mroczware.PowerFullTool
{
    [Command(PackageIds.GenerateParams)]
    internal sealed class GenerateParams : BaseCommand<GenerateParams>
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
                    QuickAcationsHelper.ShowStatusBarMessage("Brak zaznaczenia nazwy metody.");
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

                if (!(activeDocument.Selection is TextSelection selectioAct) || string.IsNullOrWhiteSpace(selectioAct.Text) || selectioAct.IsEmpty)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Brak zaznaczenia nazwy metody.");
                    return;
                }

                if (!(selectioAct.ActivePoint.get_CodeElement(vsCMElement.vsCMElementFunction) is CodeFunction codeFunction))
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Zaznacz nazwę metody w pliku z jej deklaracją.");
                    return;
                }

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
                    SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree);
                    CompilationUnitSyntax root = await syntaxTree.GetRootAsync() as CompilationUnitSyntax;
                    MethodDeclarationSyntax methodDeclaration = root
                        .DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(m => m.Identifier.Text.Equals(selection.GetText()));

                    if (methodDeclaration is null)
                    {
                        QuickAcationsHelper.ShowStatusBarMessage($"Nie znaleziono w pliku metody o nazwie {selection.GetText()}.");
                        return;
                    }

                    string methodName = methodDeclaration.Identifier.Text;
                    string returnType = methodDeclaration.ReturnType.ToString();
                    SymbolInfo returnTypeSymbolInfo = semanticModel.GetSymbolInfo(methodDeclaration.ReturnType);
                    INamedTypeSymbol returnTypeSymbol = returnTypeSymbolInfo.Symbol as INamedTypeSymbol;
                    string unwrappedType = ExtractInnermostType(returnTypeSymbol);

                    List<(string propName, string propType)> extractedProps = new();

                    List<PropertyInfo> result = [];

                    foreach (ParameterSyntax param in methodDeclaration.ParameterList.Parameters)
                    {
                    
                        TypeSyntax typeSyntax = param.Type;
                        if (typeSyntax == null) continue;

                        Microsoft.CodeAnalysis.TypeInfo typeInfo = semanticModel.GetTypeInfo(typeSyntax);
                        INamedTypeSymbol typeSymbol = typeInfo.Type as INamedTypeSymbol;

                        string? typeString = typeInfo.Type?.ToString().Replace("?", "");

                        if (!string.IsNullOrWhiteSpace(typeString) && IsKnownDotNetType(typeString))
                        {
                            result.Add(new PropertyInfo()
                            {
                                Name = CapitalizeFirstLetter(param.Identifier.Text),
                                Value = param.Identifier.Text,
                            });

                        }
                        else if (!string.IsNullOrWhiteSpace(typeString) && typeString.Contains("System.Collections.Generic"))
                        {
                            result.Add(new PropertyInfo()
                            {
                                Name = $"{CapitalizeFirstLetter(param.Identifier.Text)}Json",
                                Value = $"System.Text.Json.JsonSerializer.Serialize({param.Identifier.Text})",
                            });
                        }
                        else if (!string.IsNullOrWhiteSpace(typeString) && IsKnownDotNetArrayType(typeString))
                        {
                            result.Add(new PropertyInfo()
                            {
                                Name = $"{CapitalizeFirstLetter(param.Identifier.Text)}",
                                Value = $"({param.Identifier.Text} != null ? string.Join(\";\", {param.Identifier.Text}.Where(x => x != null).Select(x => x.ToString())) : string.Empty)",
                            });
                        }
                        else if (typeSymbol != null && !string.IsNullOrWhiteSpace(typeString))
                        {
                            IParameterSymbol parameterSymbol = semanticModel.GetDeclaredSymbol(param);
                            INamedTypeSymbol actualTypeSymbol = parameterSymbol?.Type as INamedTypeSymbol;
                            if (actualTypeSymbol is null) continue;
                            List<IPropertySymbol> props = GetAllPublicProperties(actualTypeSymbol);
                            IEnumerable<PropertyInfo> properties = props
                                .Where(p => p.DeclaredAccessibility == Accessibility.Public)
                                .Select(p => new PropertyInfo()
                                {
                                    Name = CapitalizeFirstLetter(p.Name),
                                    Value = ContainsGenericCollectionType(p.Type.ToString()) ? $"System.Text.Json.JsonSerializer.Serialize({param.Identifier.Text})" : $"{param.Identifier.Text}.{p.Name}",
                                })
                                .ToList();

                            result.AddRange(properties);
                        }
                    }

                    StringBuilder exampleInitialization = new();

                    if (result.Any())
                    {
                        exampleInitialization.AppendLine("             var _params = new DynamicParameters();");

                        foreach (PropertyInfo prop in result)
                            exampleInitialization.AppendLine($"            _params.Add(\"{prop.Name}\",{prop.Value});");
                    }

                    EditPoint startBodyPoint = codeFunction.GetStartPoint(vsCMPart.vsCMPartBody).CreateEditPoint();
                    EditPoint endBodyPoint = codeFunction.GetEndPoint(vsCMPart.vsCMPartBody).CreateEditPoint();
                    string bodyText = startBodyPoint.GetText(endBodyPoint).Trim();

                    EditPoint editPoint = codeFunction.StartPoint.CreateEditPoint();
                    editPoint.LineDown(2);

                    if (string.IsNullOrWhiteSpace(bodyText) || bodyText == "{}")
                    {
                        string functionName = codeFunction.Name;

                        Match match = Regex.Match(functionName, @"\b(Create|Update|Delete|Search|Get|GetList|GetById|GetByIdAsync|CreateAsync|UpdateAsync|DeleteAsync|SearchAsync|GetAsync|GetListAsync)\b", RegexOptions.IgnoreCase);

                        if (match.Success)
                        {
                            exampleInitialization.AppendLine($"");
                            var suffix = result.Any() ? ",_params" : "";
                            string matchedWord = match.Value.ToLower();
                            string resourceName = $"Sql.{string.Join("_", folders)}_{methodName}";
                            switch (matchedWord)
                            {
                                case "create":
                                case "createasync":
                                    exampleInitialization.AppendLine($"            var context = new DatabaseContext({resourceName}, System.Transactions.TransactionScopeOption.Required, System.Data.CommandType.Text{suffix});");
                                    exampleInitialization.AppendLine($"            return await _databaseService.GetValueAsync<int>(context);");
                                    break;
                                case "delete":
                                case "deleteasync":
                                case "update":
                                case "updateasync":
                                    exampleInitialization.AppendLine($"            var context = new DatabaseContext({resourceName}, System.Transactions.TransactionScopeOption.Required, System.Data.CommandType.Text{suffix});");
                                    exampleInitialization.AppendLine($"            await _databaseService.ExecuteNoResultAsync(context);");
                                    break;
                                case "get":
                                case "getasync":
                                case "getbyid":
                                case "getbyidasync":
                                    exampleInitialization.AppendLine($"            var context = new DatabaseContext({resourceName}, System.Transactions.TransactionScopeOption.Required, System.Data.CommandType.Text{suffix});");
                                    exampleInitialization.AppendLine($"            return (await _databaseService.QueryAsync<{unwrappedType}>(context)).FirstOrDefault();");
                                    break;
                                case "search":
                                case "searchasync":
                                case "getlist":
                                case "getlistasync":
                                    exampleInitialization.AppendLine($"            var context = new DatabaseContext({resourceName}, System.Transactions.TransactionScopeOption.Required, System.Data.CommandType.Text{suffix});");
                                    exampleInitialization.AppendLine($"            return await _databaseService.QueryAsync<{unwrappedType}>(context);");
                                    break;
                                default:
                                    break;
                            }
                        }
                    }

                    editPoint.Insert(exampleInitialization.ToString());
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Wystąpił błąd podczas wykonywania polecenia 😞 : {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

        public static bool IsKnownDotNetType(string typeName)
        {
            HashSet<string> knownTypes =
            [
                "System.TimeSpan",
                "int",
                "string",
                "bool",
                "byte",
                "sByte",
                "short",
                "uint",
                "long",
                "float",
                "double",
                "decimal",
                "char",
                "object",
                "System.DateTime",
                "System.Guid"
            ];

            return knownTypes.Contains(typeName);
        }

        public static bool IsKnownDotNetArrayType(string typeName)
        {
            HashSet<string> knownTypes =
            [
                "string[]",
                "int[]",
                "MyClass[]",
                "DateTime?[]",
                "bool[]",
                "byte[]",
                "sbyte[]",
                "short[]",
                "ushort[]",
                "long[]",
                "ulong[]",
                "float[]",
                "double[]",
                "decimal[]",
                "char[]",
                "System.Guid[]",
                "System.DateTime[]",
            ];

            return knownTypes.Contains(typeName);
        }

        public static bool ContainsGenericCollectionType(string input)
        {
            if (string.IsNullOrEmpty(input))
                return false;

            string[] genericTypes = new[]
            {
                "IEnumerable<",
                "ICollection<",
                "IList<",
                "List<",
                "ObservableCollection<",
                "Collection<",
                "HashSet<",
                "LinkedList<",
                "Queue<",
                "Stack<"
            };

            return genericTypes.Any(type => input.Contains(type));
        }

        public static string CapitalizeFirstLetter(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            for (int i = 0; i < input.Length; i++)
            {
                if (char.IsLetter(input[i]))
                {
                    return input.Substring(0, i) +
                           char.ToUpper(input[i]) +
                           input.Substring(i + 1);
                }
            }

            return input; // brak liter
        }
        string ExtractInnermostType(ITypeSymbol typeSymbol)
        {
            while (typeSymbol is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
                typeSymbol = namedType.TypeArguments[0];

            return typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }

        public class PropertyInfo
        {
            public string Name { get; set; }
            public string Value { get; set; }
            // public bool IsNullable { get; set; }
        }

        private Dictionary<string, string> GetAssembliesForDomain(string solutionFolderPath)
        {
            Dictionary<string, string> dllMap = new(StringComparer.OrdinalIgnoreCase);

            List<string> orderedFiles = Directory
                .EnumerateFiles(solutionFolderPath, "*.dll", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("\\sni.dll", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetDirectoryName(f)?.Length)
                .ToList();

            foreach (string path in orderedFiles)
            {
                string dllName = Path.GetFileName(path);
                if (!dllMap.ContainsKey(dllName))
                    dllMap[dllName] = path;
            }

            return dllMap;
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

        ///// <summary>
        ///// Zwraca projekt, w którym znajduje się otwarty dokument
        ///// </summary>
        ///// <param name="dte"></param>
        ///// <returns></returns>
        //public EnvDTE.Project GetContainingProjectName(Document activeDocument)
        //{
        //    ThreadHelper.ThrowIfNotOnUIThread();
        //    ProjectItem projectItem = activeDocument.ProjectItem;
        //    if (projectItem is null) return null;
        //    return projectItem.ContainingProject;
        //}

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

        //private static void QuickAcationsHelper.ShowStatusBarMessage(string message)
        //{
        //    ThreadHelper.ThrowIfNotOnUIThread();

        //    IVsStatusbar statusBar = (IVsStatusbar)ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar));
        //    if (statusBar == null) return;

        //    statusBar.IsFrozen(out int frozen);
        //    if (frozen != 0)
        //        statusBar.FreezeOutput(0);

        //    statusBar.SetText($"Mroczware TOOL INFO: {message}");
        //}
    }
}
