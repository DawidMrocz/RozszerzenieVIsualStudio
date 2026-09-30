using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Xml.Linq;
using Mroczware.PowerFullTool.Helpers;
using MroczwareTools;

namespace Mroczware.PowerFullTool
{
    [Command(PackageIds.GenerateIntegrationTest)]
    internal sealed class GenerateIntegrationTest : BaseCommand<GenerateIntegrationTest>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();

                if (docView is null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Nie udało się pobrać Document View.");
                    return;
                }

                IWpfTextView textView = docView.TextView as IWpfTextView;
                if (textView is null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Nie udało się pobrać właściwości Text View.");
                    return;
                }

                ITextCaret caret = textView.Caret;
                SnapshotPoint caretPosition = caret.Position.BufferPosition;

                DTE2 dte = await ServiceProvider.GetGlobalServiceAsync(typeof(DTE)) as DTE2;

                if (dte.Solution == null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Brak solucji.");
                    return;
                }

                EnvDTE.Document activeDocument = dte.ActiveDocument;

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

                EnvDTE.Project activeDocumentProject = QuickAcationsHelper.GetContainingProjectName(dte.ActiveDocument);

                if (!CheckWebApiVersion(activeDocumentProject))
                {
                    QuickAcationsHelper.ShowStatusBarMessage($"Funkcja dostępna tylko dla WebApi z .NET8 lub .NET10");
                    return;
                }

                EnvDTE.Project activeProjectName = dte.ActiveDocument.ProjectItem.ContainingProject;

                var testProject = FindTestProject(dte, activeProjectName.Name);

                SnapshotSpan selection = docView.TextView.Selection.SelectedSpans.FirstOrDefault();

                IVsTextManager editor = await ServiceProvider.GetGlobalServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
                if (editor is null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Nie udało się pobrać edytora.");
                    return;
                }

                ITextBuffer textBuffer = docView.TextBuffer;

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
                CompilationUnitSyntax root = await syntaxTree.GetRootAsync() as CompilationUnitSyntax;

                MethodDeclarationSyntax method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.FullSpan.Start <= caretPosition.Position && caretPosition.Position <= m.FullSpan.End);

                var classNode = method?.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                string controllerName = classNode?.Identifier.Text;

                if (method != null && controllerName != null)
                {
                    var semanticModel = compilation.GetSemanticModel(syntaxTree);
                    IMethodSymbol methodSymbol = semanticModel.GetDeclaredSymbol(method);

                    var httpAttributes = new HashSet<string>
                    {
                        "Microsoft.AspNetCore.Mvc.HttpGetAttribute",
                        "Microsoft.AspNetCore.Mvc.HttpPostAttribute",
                        "Microsoft.AspNetCore.Mvc.HttpPutAttribute",
                        "Microsoft.AspNetCore.Mvc.HttpDeleteAttribute"
                    };

                    AttributeData matchedAttribute = methodSymbol
                        .GetAttributes()
                        .FirstOrDefault(a => httpAttributes.Contains(a.AttributeClass.ToDisplayString()));

                    if (matchedAttribute == null)
                    {
                        QuickAcationsHelper.ShowStatusBarMessage($"Wywołanie dostępne tylko na akcji kontrolera z przypisanym atrybutem HTTP");
                        return;
                    }

                    if (testProject == null)
                    {
                        QuickAcationsHelper.ShowStatusBarMessage(
                            $"Projekt testowy '{activeProjectName.Name}.Tests' nie istnieje."
                        );
                        return;
                    }

                    string name = matchedAttribute.AttributeClass.Name;
                    string normalizedHttpVerb = matchedAttribute.AttributeClass.Name.Replace("Attribute", "");
                    TypedConstant? route = matchedAttribute.ConstructorArguments.FirstOrDefault();

                    if (route == null)
                    {
                        QuickAcationsHelper.ShowStatusBarMessage(
                            $"Atrybut {normalizedHttpVerb} musi mieć zdefiniowana ścieżkę."
                        );
                        return;
                    }

                    List<string> folders = GetFoldersStructure(activeDocument);
                    string folderPath = System.IO.Path.GetDirectoryName(testProject.FullName);

                    foreach (string f in folders.Take(folders.Count - 1))
                        folderPath = System.IO.Path.Combine(folderPath, f);

                    Directory.CreateDirectory(folderPath);

                    string filePath = System.IO.Path.Combine(folderPath, $"{controllerName}Test.cs");

                    AddProjectReferenceXml(testProject, activeDocumentProject);
                    GenerateFakeDatabaseService(testProject);
                    string namespaceName = $"{testProject.Name}.{string.Join(".", folders.Take(folders.Count - 1))}";
                    GenerateApiTestFixture(testProject);
                    GenerateQueryStringHelper(testProject);
                    GenerateTestConfig(testProject);
                    GenerateTestCollection(testProject);

                    List<PackageInfo> packageInfo = new List<PackageInfo>()
                        {
                            new PackageInfo() {
                                Name = "FluentAssertions",
                                Version = "8.8.0"
                            },
                            new PackageInfo() {
                                Name = "Microsoft.AspNetCore.Mvc.Testing",
                                Version = "8.0.22"
                            }
                        };

                    AddDependenciesInProject(testProject, packageInfo);

                    List<string> parameterNamepsaces = new List<string>();
                    foreach (var parameter in method.ParameterList.Parameters)
                    {
                        var typeSyntax = parameter.Type;
                        var typeSymbol = semanticModel.GetTypeInfo(typeSyntax).Type;
                        var ns = typeSymbol?.ContainingNamespace?.ToDisplayString();
                        parameterNamepsaces.Add(ns);
                    }

                    CreateEmptyTestClassFile(testProject, $"{controllerName}Test", namespaceName, filePath);

                    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath));
                    var rootTest = tree.GetCompilationUnitRoot();

                    var allUsings = rootTest.Usings
                        .Select(u => u.Name.ToString())
                        .Concat(parameterNamepsaces.Where(ns => !string.IsNullOrWhiteSpace(ns)))
                        .Distinct()
                        .ToList();

                    var newUsings = allUsings
                    .Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns)))
                    .ToArray();

                    var newRootWithUsings = rootTest.WithUsings(
                        SyntaxFactory.List(newUsings)
                    );

                    var createdClassNode = newRootWithUsings.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .First();

                    var methods = GenerateTestMethods(method.Identifier.Text, route.Value.Value.ToString(), method.ParameterList.Parameters, normalizedHttpVerb);

                    var newRoot = newRootWithUsings.ReplaceNode(
                        createdClassNode,
                        createdClassNode.AddMembers(methods.ToArray())
                    );

                    File.WriteAllText(filePath, newRoot.NormalizeWhitespace().ToFullString());
                }
                else
                    QuickAcationsHelper.ShowStatusBarMessage("Kursor nie znajduje się w żadnej metodzie.");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Wystąpił błąd podczas wykonywania polecenia 😞 : {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool CheckWebApiVersion(EnvDTE.Project activeProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            XDocument xmlDoc = XDocument.Load(activeProject.FullName);
            XElement targetFrameworkElement = xmlDoc.Descendants("TargetFramework").FirstOrDefault();
            string targetFramework = targetFrameworkElement?.Value;

            return targetFramework switch
            {
                var tf when tf.Contains("net8.0") => true,
                var tf when tf.Contains("net10.0") => true,
                _ => false
            };
        }

        private static void AddProjectReferenceXml(EnvDTE.Project project, EnvDTE.Project targetProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string projectPath = project.FullName;
            string xmlPath = projectPath;
            var xmlDoc = XDocument.Load(xmlPath);

            // Utworzenie lub pobranie ItemGroup dla ProjectReference
            var itemGroup = xmlDoc.Descendants("ItemGroup")
                                  .FirstOrDefault(ig => ig.Elements("ProjectReference").Any())
                              ?? new XElement("ItemGroup");

            string relativePath = GetRelativePath(projectPath, targetProject.FullName);

            // Sprawdzenie czy ProjectReference już istnieje
            bool referenceExists = itemGroup.Elements("ProjectReference")
                .Any(pr => pr.Attribute("Include")?.Value == relativePath);

            if (!referenceExists)
            {
                var projectReference = new XElement("ProjectReference",
                    new XAttribute("Include", relativePath));
                itemGroup.Add(projectReference);
            }

            if (itemGroup.Parent == null)
                xmlDoc.Root.Add(itemGroup);

            xmlDoc.Save(xmlPath);
        }

        private static string GetRelativePath(string fromPath, string toPath)
        {
            Uri fromUri = new Uri(fromPath);
            Uri toUri = new Uri(toPath);

            Uri relativeUri = fromUri.MakeRelativeUri(toUri);
            return relativeUri.ToString().Replace('/', Path.DirectorySeparatorChar);
        }

        private void CreateEmptyTestClassFile(EnvDTE.Project testProject, string className, string namespaceName, string fileTestPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (File.Exists(fileTestPath)) return;
            string classCode = GenerateEmptyTestClassCode(className, namespaceName);
            File.WriteAllText(fileTestPath, classCode);
            testProject.ProjectItems.AddFromFile(fileTestPath);
        }

        private string GenerateEmptyTestClassCode(string className, string namespaceName)
        {
            var usings = new[]
            {
                "System.Net",
                "System.Text",
                "System.Text.Json",
                "FluentAssertions",
                "Xunit.Abstractions",
                "Microsoft.AspNetCore.WebUtilities"
        };

            var clientField =
         SyntaxFactory.FieldDeclaration(
             SyntaxFactory.VariableDeclaration(
                 SyntaxFactory.ParseTypeName("HttpClient"))
             .AddVariables(
                 SyntaxFactory.VariableDeclarator("_client")))
         .AddModifiers(
             SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
             SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

            var outputField =
                SyntaxFactory.FieldDeclaration(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.ParseTypeName("ITestOutputHelper"))
                    .AddVariables(
                        SyntaxFactory.VariableDeclarator("_output")))
                .AddModifiers(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                    SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

            var ctor =
                SyntaxFactory.ConstructorDeclaration(className)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("fixture"))
                        .WithType(SyntaxFactory.ParseTypeName("ApiTestFixture")),
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("output"))
                        .WithType(SyntaxFactory.ParseTypeName("ITestOutputHelper")))
                .WithBody(
                    SyntaxFactory.Block(
                        SyntaxFactory.ParseStatement("_client = fixture.Client;"),
                        SyntaxFactory.ParseStatement("_output = output;")
                    )
                );

            var collectionAttribute =
        SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(
                    SyntaxFactory.IdentifierName("Collection"))
                .AddArgumentListArguments(
                    SyntaxFactory.AttributeArgument(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal("ApiTests")
                        ))
                )
            ));

            var classDeclaration =
                 SyntaxFactory.ClassDeclaration(className)
                 .AddAttributeLists(collectionAttribute)
                 .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                 .AddBaseListTypes(
                     SyntaxFactory.SimpleBaseType(
                         SyntaxFactory.ParseTypeName("IClassFixture<ApiTestFixture>")))
                 .AddMembers(clientField)
                 .AddMembers(outputField)
                 .AddMembers(ctor);

            var namespaceDeclaration =
                SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName))
                .AddMembers(classDeclaration);

            var compilationUnit =
                SyntaxFactory.CompilationUnit()
                .AddUsings(usings.Select(u => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u))).ToArray())
                .AddMembers(namespaceDeclaration)
                .NormalizeWhitespace();

            return compilationUnit.ToFullString();
        }

        private IEnumerable<MemberDeclarationSyntax> GenerateTestMethods(string actionName, string route, SeparatedSyntaxList<ParameterSyntax> parameters, string normalizedHttpVerb)
        {
            // Budowanie listy elementów do object[]
            var parameterLiterals = parameters
                .Select(p => SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(p.Type.ToString())))
                .ToArray();

            // Tworzenie array initializer
            var arrayInitializer = SyntaxFactory.InitializerExpression(
                SyntaxKind.ArrayInitializerExpression,
                SyntaxFactory.SeparatedList<ExpressionSyntax>(parameterLiterals)
            );

            // Tworzymy new object[] { ... }
            var objectArrayCreation = SyntaxFactory.ArrayCreationExpression(
                SyntaxFactory.ArrayType(
                    SyntaxFactory.ParseTypeName("object"))
                .AddRankSpecifiers(
                    SyntaxFactory.ArrayRankSpecifier(
                        SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                            SyntaxFactory.OmittedArraySizeExpression()))))
            .WithInitializer(arrayInitializer);

            // yield return new object[] { ... };
            var yieldStatement = SyntaxFactory.YieldStatement(
                SyntaxKind.YieldReturnStatement,
                objectArrayCreation);

            // --- metoda GetTestRequests ---
            var getTestRequests =
                SyntaxFactory.MethodDeclaration(
                        SyntaxFactory.GenericName("IEnumerable")
                            .AddTypeArgumentListArguments(SyntaxFactory.ParseTypeName("object[]")),
                        $"{actionName}_TestRequests")
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                                  SyntaxFactory.Token(SyntaxKind.StaticKeyword))
                    .WithBody(
                        SyntaxFactory.Block(yieldStatement));

            AttributeListSyntax[] attributeList;

            if (parameters != null && parameters.Any())
            {
                var theoryAttribute = SyntaxFactory.AttributeList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Attribute(
                            SyntaxFactory.IdentifierName("Theory"))));

                var memberDataAttribute = SyntaxFactory.AttributeList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Attribute(
                            SyntaxFactory.IdentifierName("MemberData"))
                        .AddArgumentListArguments(
                            SyntaxFactory.AttributeArgument(
                                SyntaxFactory.ParseExpression($"nameof({actionName}_TestRequests)")))));

                attributeList = new[] { theoryAttribute, memberDataAttribute };
            }
            else
            {
                var factAttribute = SyntaxFactory.AttributeList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Attribute(
                            SyntaxFactory.IdentifierName("Fact"))));

                attributeList = new[] { factAttribute };
            }

            var statements = new List<StatementSyntax>();
            var normalizedVerb = normalizedHttpVerb.Substring(4);

            ParameterSyntax nonRouteParam = parameters
                .FirstOrDefault(p =>
                    route.IndexOf("{" + p.Identifier.Text + "}", StringComparison.OrdinalIgnoreCase) < 0);

            if (normalizedVerb is "Post" or "Put" or "Patch")
            {

                if (nonRouteParam is null)
                {
                    statements.Add(SyntaxFactory.ParseStatement(
                                    @"var content = new StringContent(
                            JsonSerializer.Serialize(new object()),
                            Encoding.UTF8,
                            ""application/json"");"));
                }
                else
                {
                    statements.Add(SyntaxFactory.ParseStatement(
                @$"var content = new StringContent(
                            JsonSerializer.Serialize({nonRouteParam.Identifier.Text}),
                            Encoding.UTF8,
                            ""application/json"");"));
                }

                statements.Add(SyntaxFactory.ParseStatement(
            $@"var response = await _client.{normalizedVerb}Async($""{route}"", content);"));
            }
            else
            {
                if(nonRouteParam is null)
                {
                    statements.Add(SyntaxFactory.ParseStatement(
                $@"var response = await _client.{normalizedVerb}Async($""{route}"");"));
                }
                else
                {
                    statements.Add(SyntaxFactory.ParseStatement(
               $@"List<KeyValuePair<string, string>> queryParams = QueryStringHelper.GenerateQueryString({nonRouteParam.Identifier.Text});"));
                    statements.Add(SyntaxFactory.ParseStatement(
               $@"var url = QueryHelpers.AddQueryString($""{route}"", queryParams);"));
                    statements.Add(SyntaxFactory.ParseStatement(
               $@"var response = await _client.{normalizedVerb}Async(url);"));
                }                
            }

            statements.Add(SyntaxFactory.ParseStatement(@"_output.WriteLine(await response.Content.ReadAsStringAsync());"));
            statements.Add(SyntaxFactory.ParseStatement(@"response.StatusCode.Should().Be(HttpStatusCode.OK);"));

            var testParams = parameters
                .Select(p =>
                    SyntaxFactory.Parameter(
                            SyntaxFactory.Identifier(p.Identifier.Text))
                        .WithType(p.Type))
                .ToArray();

            var testMethod =
                SyntaxFactory.MethodDeclaration(
                        SyntaxFactory.IdentifierName("Task"),
                        $"{actionName}_ReturnsOkResult")
                    .AddModifiers(
                        SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                        SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                    .AddAttributeLists(attributeList)
                    .AddParameterListParameters(testParams)
                    .WithBody(SyntaxFactory.Block(statements));

            if(parameters != null && parameters.Any())
            {
                return new MemberDeclarationSyntax[] { getTestRequests, testMethod };
            }
            else
            {
                return new MemberDeclarationSyntax[] { testMethod };
            }               
        }

        private void GenerateFakeDatabaseService(EnvDTE.Project testProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string projectDir = System.IO.Path.GetDirectoryName(testProject.FullName);

            string filePath = System.IO.Path.Combine(projectDir, "TestDatabaseService.cs");

            if (File.Exists(filePath)) return;

            string fileContent = @"using Dapper;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Text;
using Mroczware.DataBase.Models;
using Mroczware.DataBase.Services;

public class TestDatabaseService : IDatabaseService
{
    private readonly SqlConnection _connection;

    public TestDatabaseService(SqlConnection connection)
    {
        _connection = connection;
    }

    private static readonly Dictionary<Type, string> _sqlTypeConversionMap = new Dictionary<Type, string>
    {
        { typeof(string), ""NVARCHAR(MAX)"" },
        { typeof(short), ""INT"" },
        { typeof(int), ""INT"" },
        { typeof(long), ""BIGINT"" },
        { typeof(decimal), ""DECIMAL(18, 2)"" },
        { typeof(DateTime), ""DATETIME"" },
        { typeof(TimeSpan), ""TIME"" },
        { typeof(bool), ""BIT"" },
        { typeof(Guid), ""UNIQUEIDENTIFIER"" }
    };

    public async Task<IEnumerable<T>> ExecProcedureAsync<T>(DatabaseContext context)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();
        IEnumerable<T> results = await _connection.QueryAsync<T>(context.SqlCommand, context.Parameters, null, commandType: CommandType.StoredProcedure, commandTimeout: context.Timeout?.Seconds ?? 30);

        return results.ToList();
    }

    public async Task<IEnumerable<T>> QueryAsync<T>(DatabaseContext context)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();
        IEnumerable<T> results = await _connection.QueryAsync<T>(context.SqlCommand, context.Parameters, null, commandType: CommandType.Text, commandTimeout: context.Timeout?.Seconds ?? 30);

        return results.ToList();
    }

    public async Task<DataTable> QueryAsync(DatabaseContext context)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();
        DbDataReader reader = await _connection.ExecuteReaderAsync(context.SqlCommand, context.Parameters, null, commandType: context.CommandType, commandTimeout: context.Timeout?.Seconds ?? 30);

        DataTable result = new DataTable();
        result.Load(reader);
        return result;
    }

    public async Task<T?> GetValueAsync<T>(DatabaseContext context)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();

        T result = await _connection.QueryFirstOrDefaultAsync<T>(context.SqlCommand, context.Parameters, null, commandType: context.CommandType, commandTimeout: context.Timeout?.Seconds ?? 30);

        return result;
    }

    public async Task<int> ExecuteNoResultAsync(DatabaseContext context)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();
        int result = await _connection.ExecuteAsync(context.SqlCommand, context.Parameters, null, commandType: context.CommandType, commandTimeout: context.Timeout?.Seconds ?? 30);

        return result;
    }

    public async Task<int> ExecuteAsync(DatabaseContext context)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();
        int results = await _connection.ExecuteAsync(context.SqlCommand, context.Parameters, null, commandType: context.CommandType, commandTimeout: context.Timeout?.Seconds ?? 30);

        return results;
    }

    public async Task<IEnumerable<T>> ExecProcedureAsync<T>(string procedureName)
    {
        return await ExecProcedureAsync<T>(procedureName, null);
    }

    public async Task<IEnumerable<T>> ExecProcedureAsync<T>(string procedureName, DynamicParameters? parameters)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();
        IEnumerable<T> results = await _connection.QueryAsync<T>(procedureName, parameters, null, null, CommandType.StoredProcedure);

        return results.ToList();
    }

    public async Task<IEnumerable<T>> QueryAsync<T>(string sqlSyntax)
    {
        return await QueryAsync<T>(sqlSyntax, null);
    }

    public async Task<IEnumerable<T>> QueryAsync<T>(string sqlSyntax, DynamicParameters? parameters)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();
        IEnumerable<T> results = await _connection.QueryAsync<T>(sqlSyntax, parameters, null, null, CommandType.Text);
        return results.ToList();
    }

    public async Task<int> ExecuteNoResultAsync(string sqlSyntax)
    {
        return await ExecuteNoResultAsync(sqlSyntax, null);
    }

    public async Task<int> ExecuteNoResultAsync(string sqlSyntax, DynamicParameters? parameters)
    {
        return await ExecuteNoResultAsync(sqlSyntax, parameters, CommandType.Text);
    }

    public async Task<int> ExecuteNoResultAsync(string sqlSyntax, DynamicParameters? parameters, CommandType commandType)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();
        int result = await _connection.ExecuteAsync(sqlSyntax, parameters, null, commandType: commandType, commandTimeout: 30);

        return result;
    }

    public async Task<T?> GetValueAsync<T>(string sqlSyntax)
    {
        return await GetValueAsync<T>(sqlSyntax, null);
    }

    public async Task<T?> GetValueAsync<T>(string sqlSyntax, DynamicParameters? parameters)
    {
        return await GetValueAsync<T>(sqlSyntax, parameters, CommandType.Text);
    }

    public async Task<T?> GetValueAsync<T>(string sqlSyntax, DynamicParameters? parameters, CommandType commandType)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();
        T? result = await _connection.QueryFirstOrDefaultAsync<T>(sqlSyntax, parameters, null, null, commandType);

        return result;
    }

    public async Task<IEnumerable<R>> BulkQueryAsync<T, R>(DatabaseContext context, string tableVariableName, IEnumerable<T> tableValues)
    {
        return await BulkQueryAsync<T, R>(context, new Dictionary<string, IEnumerable<T>> { { tableVariableName, tableValues } });
    }

    public async Task<IEnumerable<R>> BulkQueryAsync<T, R>(DatabaseContext context, Dictionary<string, IEnumerable<T>> tableNameWithTableValues)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();

        foreach (string tableName in tableNameWithTableValues.Keys)
        {
            var (data, tmpTable) = await PrepareBulkOperationAsync(_connection, context, tableName, tableNameWithTableValues[tableName]);
            using SqlBulkCopy bulkcopy = new SqlBulkCopy(_connection);
            bulkcopy.BulkCopyTimeout = context.Timeout?.Seconds ?? 30;
            bulkcopy.DestinationTableName = tmpTable;
            await bulkcopy.WriteToServerAsync(data);
            bulkcopy.Close();
        }

        IEnumerable<R> results = await _connection.QueryAsync<R>(context.SqlCommand, context.Parameters, null, commandType: CommandType.Text, commandTimeout: context.Timeout?.Seconds ?? 30);

        return results.ToList();
    }

    public async Task BulkNoResultAsync<R>(DatabaseContext context, string tableVariableName, IEnumerable<R> tableValues)
    {
        await BulkNoResultAsync(context, new Dictionary<string, IEnumerable<R>> { { tableVariableName, tableValues } });
    }

    public async Task BulkNoResultAsync<R>(DatabaseContext context, Dictionary<string, IEnumerable<R>> tableNameWithTableValues)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();

        foreach (string tableName in tableNameWithTableValues.Keys)
        {
            var (data, tmpTable) = await PrepareBulkOperationAsync(_connection, context, tableName, tableNameWithTableValues[tableName]);
            using SqlBulkCopy bulkcopy = new SqlBulkCopy(_connection);
            bulkcopy.BulkCopyTimeout = context.Timeout?.Seconds ?? 30;
            bulkcopy.DestinationTableName = tmpTable;
            await bulkcopy.WriteToServerAsync(data);
            bulkcopy.Close();
        }

        await _connection.ExecuteAsync(context.SqlCommand, context.Parameters, null, commandType: CommandType.Text, commandTimeout: context.Timeout?.Seconds ?? 30);

    }

    public async Task<T?> BulkGetValueAsync<T>(DatabaseContext context, string tableVariableName, IEnumerable<T> tableValues)
    {
        return await BulkGetValueAsync(context, new Dictionary<string, IEnumerable<T>> { { tableVariableName, tableValues } });
    }

    public async Task<T?> BulkGetValueAsync<T>(DatabaseContext context, Dictionary<string, IEnumerable<T>> tableNameWithTableValues)
    {

        if (_connection.State != ConnectionState.Open)
            _connection.Open();

        foreach (string tableName in tableNameWithTableValues.Keys)
        {
            var (data, tmpTable) = await PrepareBulkOperationAsync(_connection, context, tableName, tableNameWithTableValues[tableName]);
            using SqlBulkCopy bulkcopy = new SqlBulkCopy(_connection);
            bulkcopy.BulkCopyTimeout = context.Timeout?.Seconds ?? 30;
            bulkcopy.DestinationTableName = tmpTable;
            await bulkcopy.WriteToServerAsync(data);
            bulkcopy.Close();
        }

        T? result = await _connection.ExecuteScalarAsync<T>(context.SqlCommand, context.Parameters, null, commandType: CommandType.Text, commandTimeout: context.Timeout?.Seconds ?? 30);
        return result;
    }

    private static async Task<(DataTable Data, string tmpTableName)> PrepareBulkOperationAsync<T>(SqlConnection connection, DatabaseContext context, string tableVariableName, IEnumerable<T> tableValues)
    {
        if (context.CommandType == CommandType.StoredProcedure)
            throw new InvalidOperationException(""Operacje Bulk nie wspierają stored procedure"");

        string tmpTableName = ""#"" + Guid.NewGuid().ToString(""N"");
        DataTable data = ConvertToDataTable(tableValues);
        string createTmpTableSql = GetCreateTmpTableSql(tmpTableName, data);
        await connection.ExecuteAsync(createTmpTableSql);
        context.SqlCommand = context.SqlCommand.Replace(tableVariableName, tmpTableName);
        return (data, tmpTableName);
    }

    private static DataTable ConvertToDataTable<T>(IEnumerable<T> data)
    {
        PropertyDescriptorCollection properties = TypeDescriptor.GetProperties(typeof(T));
        DataTable table = new DataTable();

        foreach (PropertyDescriptor prop in properties)
            table.Columns.Add(prop.Name, Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType);

        foreach (T item in data)
        {
            DataRow row = table.NewRow();
            foreach (PropertyDescriptor prop in properties)
                row[prop.Name] = prop.GetValue(item) ?? DBNull.Value;

            table.Rows.Add(row);
        }

        return table;
    }

    private static string GetCreateTmpTableSql(string tmpTableName, DataTable table)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(""CREATE TABLE "").Append(tmpTableName).Append('(');

        for (int i = 0; i < table.Columns.Count; i++)
        {
            sb.Append('[').Append(table.Columns[i].ColumnName).Append(']').Append(' ')
              .Append(_sqlTypeConversionMap[table.Columns[i].DataType]);

            if (i < table.Columns.Count - 1)
                sb.Append(',');
        }

        sb.Append("");"");
        return sb.ToString();
    }
}
";
            File.WriteAllText(filePath, fileContent);
            testProject.ProjectItems.AddFromFile(filePath);
        }

        private void GenerateTestConfig(EnvDTE.Project testProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string projectDir = System.IO.Path.GetDirectoryName(testProject.FullName);
            string filePath = System.IO.Path.Combine(projectDir, "TestConfig.cs");

            if (File.Exists(filePath)) return;

            string fileContent = @"using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
";
            File.WriteAllText(filePath, fileContent);
            testProject.ProjectItems.AddFromFile(filePath);
        }

        private void GenerateTestCollection(EnvDTE.Project testProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string projectDir = System.IO.Path.GetDirectoryName(testProject.FullName);
            string filePath = System.IO.Path.Combine(projectDir, "ApiTestCollection.cs");

            if (File.Exists(filePath)) return;

            string fileContent = $@"using Xunit;

namespace {testProject.Name}
{{
    [CollectionDefinition(""ApiTests"")]
    public class ApiTestCollection : ICollectionFixture<ApiTestFixture> {{ }}
}}";
            File.WriteAllText(filePath, fileContent);
            testProject.ProjectItems.AddFromFile(filePath);
        }

        private void GenerateQueryStringHelper(EnvDTE.Project testProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string projectDir = System.IO.Path.GetDirectoryName(testProject.FullName);
            string filePath = System.IO.Path.Combine(projectDir, "QueryStringHelper.cs");

            if (File.Exists(filePath)) return;

            string fileContent = $@"using System.Reflection;

namespace {testProject.Name}
{{
    public static class QueryStringHelper
    {{
        public static List<KeyValuePair<string, string>> GenerateQueryString<T>(T criteria)
        {{
            return criteria.GetType()
                            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Where(p => p.GetValue(criteria) != null)
                            .SelectMany(p =>
                            {{
                                var value = p.GetValue(criteria);
                                if (value is IEnumerable<int> list)
                                    return list.Select(v => new KeyValuePair<string, string>(p.Name, v.ToString()));

                                return new[] {{ new KeyValuePair<string, string>(p.Name, value.ToString()!) }};
                            }})
                            .ToList();
        }}
    }}
}}";
            File.WriteAllText(filePath, fileContent);
            testProject.ProjectItems.AddFromFile(filePath);
        }

        private void GenerateApiTestFixture(EnvDTE.Project testProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string projectDir = System.IO.Path.GetDirectoryName(testProject.FullName);
            string filePath = System.IO.Path.Combine(projectDir, "ApiTestFixture.cs");

            if (File.Exists(filePath)) return;

            string fileContent = $@"using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Data;
using System.Data.SqlClient;
using System.Transactions;
using Mroczware.DataBase.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;

namespace {testProject.Name}
{{
    public class ApiTestFixture : IDisposable
    {{
        public HttpClient Client {{ get; }}
        public WebApplicationFactory<Program> Factory {{ get; }}

        public ApiTestFixture()
        {{
            Factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {{
                    builder.ConfigureTestServices(services =>
                    {{
                        services.AddSingleton<IStartupFilter>(new FakeUserStartupFilter());
                    }});
                    builder.ConfigureServices(services =>
                    {{
                        services.RemoveAll<IDatabaseService>();
                        services.AddScoped(sp =>
                        {{
                            var config = sp.GetRequiredService<IConfiguration>();
                            string conn = config.GetConnectionString(""DefaultConnection"") ?? throw new Exception(""Ustaw DefaultConnection w projekcie testowanym"");
                            return new SqlConnection(conn);
                        }});
                        services.AddScoped<IDatabaseService, TestDatabaseService>();
                        services.RemoveAll<Mroczware.File.Core.Services.IThumbMakerService>();
                        services.PostConfigure<MvcOptions>(options =>
                        {{
                            options.Filters.Add(new TestTransactionFilter());
                        }});
                    }});
                }});

            Client = Factory.CreateClient();
        }}

        public void Dispose()
        {{
            using (var scope = Factory.Services.CreateScope())
            {{
                var connection = scope.ServiceProvider.GetService<SqlConnection>();
                if (connection != null)
                {{
                    try
                    {{
                        if (connection.State != ConnectionState.Closed)
                            connection.Close();
                        connection.Dispose();
                    }}
                    catch {{ /* ignorujemy */ }}
                }}
            }}

            Client.Dispose();
            Factory.Dispose();
        }}
    }}

    public class TestTransactionFilter : IAsyncActionFilter
    {{
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {{
            using var scope = new TransactionScope(TransactionScopeOption.Required, new TransactionOptions
            {{
                IsolationLevel = System.Transactions.IsolationLevel.ReadUncommitted,
                Timeout = TransactionManager.DefaultTimeout
            }}, TransactionScopeAsyncFlowOption.Enabled);
            var result = await next();
        }}
    }}

    public class FakeUserStartupFilter : IStartupFilter
    {{
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {{
            return app =>
            {{
                app.Use(async (context, nextMiddleware) =>
                {{
                    var claims = new[]
                    {{
                        new Claim(ClaimTypes.NameIdentifier, ""1"")
                    }};

                    context.User = new ClaimsPrincipal(
                        new ClaimsIdentity(claims, ""TestAuth""));

                    await nextMiddleware();
                }});

                next(app);
            }};
        }}
    }}
}}";
            File.WriteAllText(filePath, fileContent);
            testProject.ProjectItems.AddFromFile(filePath);
        }

        private EnvDTE.Project? FindTestProject(DTE2 dte, string baseProjectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string expectedName = baseProjectName + ".Tests";

            foreach (EnvDTE.Project proj in dte.Solution.Projects)
                if (proj.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                    return proj;

            return null;
        }

        /// <summary>
        /// Pobranie nadrzędnych folderów z włączeniem pierwszego
        /// </summary>
        /// <param name="activeDocument"></param>
        /// <returns></returns>
        private static List<string> GetFoldersStructure(EnvDTE.Document activeDocument)
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

        private class PackageInfo
        {
            public string Name { get; set; }
            public string Version { get; set; }
        }

        private static void AddDependenciesInProject(EnvDTE.Project project, List<PackageInfo> packages)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var projectPath = project.FullName;
            var xmlDoc = XDocument.Load(projectPath);
            var itemGroup = xmlDoc.Descendants("ItemGroup").FirstOrDefault(ig => ig.Elements("PackageReference").Any()) ?? new XElement("ItemGroup");

            foreach (var pkg in packages)
                if (!itemGroup.Elements("PackageReference").Any(pr => pr.Attribute("Include")?.Value == pkg.Name))
                    itemGroup.Add(new XElement("PackageReference", new XAttribute("Include", pkg.Name), new XAttribute("Version", pkg.Version)));

            if (!xmlDoc.Descendants("ItemGroup").Contains(itemGroup))
                xmlDoc.Root.Add(itemGroup);

            xmlDoc.Save(projectPath);
        }
    }
}
