using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Mroczware.PowerFullTool.Commands;
using Mroczware.PowerFullTool.Helpers;
using Mroczware.PowerFullTool.UI.View;
using Document = EnvDTE.Document;
using MroczwareTools;


namespace Mroczware.PowerFullTool
{
    public class ColumnInfo
    {
        public string ColumnName { get; set; }
        public string ReplacedName => ColumnName.Replace("_", "");
        public string DataType { get; set; }
        public bool IsNullable { get; set; }


        public ColumnInfo(string columnName, string dataType, bool isNullable)
        {
            ColumnName = columnName;
            DataType = dataType;
            IsNullable = isNullable;
        }
    }

    [Command(PackageIds.GenerateRepoFromSql)]
    internal sealed class GenerateRepoFromSql : BaseCommand<GenerateRepoFromSql>
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

                if (activeDocument.Language != "SQL Server Tools")
                {
                    QuickAcationsHelper.ShowStatusBarMessage($"Narzędzie działa tylko na plikach z rozszerzeniem .sql");
                    return;
                }

                if (!activeDocument.Saved)
                {
                    QuickAcationsHelper.ShowStatusBarMessage($"Plik {activeDocument.Name} zawiera niezapisane zmiany. Zapisz go przed kontynuacją.");
                    return;
                }

                string documentName = activeDocument.Name;

                string pattern = @"^[a-zA-Z0-9]+(_[a-zA-Z0-9]+){2,}\.sql$";

                Match match = Regex.Match(documentName, pattern, RegexOptions.IgnoreCase);

                if (!match.Success)
                {
                    QuickAcationsHelper.ShowStatusBarMessage($"Dokument {activeDocument.Name} nie pasuje do wzorca - SCHEMA_TABLE_ACTION.sql");
                    return;
                }

                string nameWithoutExtension = Path.GetFileNameWithoutExtension(documentName);
                string[] parts = nameWithoutExtension.Split('_');

                string schema = parts[0];
                string action = parts[parts.Length - 1];


                string[] tableParts = new string[parts.Length - 2];
                for (int i = 1; i < parts.Length - 1; i++)
                {
                    tableParts[i - 1] = parts[i];
                }

                string table = string.Join("_", tableParts);

                //Table CamelCase
                string tableCamelCase = "";
                for (int i = 1; i < parts.Length - 1; i++)
                {
                    if (parts[i].Length > 0)
                    {
                        string word = char.ToUpper(parts[i][0]) + parts[i].Substring(1);
                        tableCamelCase += word;
                    }
                }

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

                    string originalCode = code;

                    code = code.Replace("'", "''");

                    code = RemoveComments(code);

                    code = code.Normalize(NormalizationForm.FormC);

                    IList<ParseError> errors = [];

                    TSql160Parser parser = new(false);

                    TextReader rdr = new StreamReader(activeDocument.FullName);

                    TSqlFragment tree = parser.Parse(rdr, out errors);

                    //Wyszukuje parametry, dla których nie da się określić typu
                    ParamUsageAnalyzer visitor3 = new();
                    tree.Accept(visitor3);
                    var notToDeclareVariables = visitor3.GetSingleIsNullParams();
                    if (notToDeclareVariables is not null && notToDeclareVariables.Any())
                    {
                        string paramsList = string.Join(", ", notToDeclareVariables);
                        System.Windows.MessageBox.Show(
                            $"Nie można w żaden sposób określić typu dla danych parametrów.\nParametry: {paramsList}",
                            "Informacja",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                        return;
                    }

                    //Podmienia parametry w wyrażeniach BooleanIsNullExpression na NULL, nic nie wnosza, a nie można przeprowadzić analizy poniższa procedura
                    ParamToNullIfBooleanIsNullExpression visitor2 = new();
                    tree.Accept(visitor2);
                    var generator = new Sql150ScriptGenerator();
                    string sqlWithoutBooleanNullExpressions;
                    generator.GenerateScript(tree, out sqlWithoutBooleanNullExpressions);
                    string code2 = sqlWithoutBooleanNullExpressions.Replace("'", "''");
                    code2 = RemoveComments(code2);
                    code2 = code2.Normalize(NormalizationForm.FormC);

                    VariableVisitor visitor = new();
                    tree.Accept(visitor);
                
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Regex regex = new Regex(@"@([A-Za-z_][A-Za-z0-9_]*)");
                    string varaiblesReplacedWithNUlls = regex.Replace(code2, match =>
                    {
                        var name = match.Groups[1].Value;

                        if (seen.Contains(name))
                        {
                            if (name == "PageSize" || name == "Page" || name == "StartIndex")
                                return "1";
                            else if (name != "SortExpression")
                                return "NULL";
                        }

                        seen.Add(name);
                        return match.Value;
                    });

                    code2 = varaiblesReplacedWithNUlls;

                    List<SelectColumnInfo> selectColumnInfoList = [];
                    List<ParametersInfo> parametersInfoList = [];

                    StringBuilder sb = new();

                    if (code2.Contains("@Page") && !visitor.DeclaredVariables.Contains("@Page"))
                    {
                        sb.AppendLine("Declare @Page INT");
                        parametersInfoList.Add(new ParametersInfo("@Page", "int", true));
                    }

                    if (code2.Contains("@PageSize") && !visitor.DeclaredVariables.Contains("@PageSize"))
                    {
                        sb.AppendLine("Declare @PageSize INT");
                        parametersInfoList.Add(new ParametersInfo("@PageSize", "int", true));
                    }

                    if (code2.Contains("@OrderDir") && !visitor.DeclaredVariables.Contains("@OrderDir"))
                    {
                        sb.AppendLine("Declare @OrderDir INT");
                        parametersInfoList.Add(new ParametersInfo("@OrderDir", "int", true));
                    }

                    if (code2.Contains("@OrderBy") && !visitor.DeclaredVariables.Contains("@OrderBy"))
                    {
                        sb.AppendLine("Declare @OrderBy INT");
                        parametersInfoList.Add(new ParametersInfo("@OrderBy", "NVARCHAR(MAX)"));
                    }

                    if (code2.Contains("@StartIndex") && !visitor.DeclaredVariables.Contains("@StartIndex"))
                    {
                        sb.AppendLine("Declare @StartIndex INT");
                        parametersInfoList.Add(new ParametersInfo("@StartIndex", "int"));
                    }

                    if (code2.Contains("@SortExpression") && !visitor.DeclaredVariables.Contains("@SortExpression"))
                    {
                        sb.AppendLine("Declare @SortExpression NVARCHAR(MAX)");
                        parametersInfoList.Add(new ParametersInfo("@SortExpression", "NVARCHAR(MAX)"));
                    }

                    foreach (ParseError error in errors)
                        QuickAcationsHelper.ShowStatusBarMessage($"{error.Message}");

                    if (tree is TSqlScript script)
                    {
                        RootLevelStatementTypeAnalyzer analyzer = new();
                        analyzer.Analyze(script);

                        if (analyzer.FirstStatementType.Value == StatementTypeEnum.Merge)
                        {
                            System.Windows.MessageBox.Show(
                              "Brak wsparcia dla operacji MERGE.",
                              "Informacja",
                              MessageBoxButton.OK,
                              MessageBoxImage.Information
                            );
                            return;
                        }
               
                        if (analyzer.FirstStatementType is null)
                        {
                            System.Windows.MessageBox.Show(
                               "Nie znaleziono żadnego zapytania typu SELECT / UPDATE / INSERT.",
                               "Informacja",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information
                           );
                            return;
                        }                       

                        if (analyzer.FirstStatementType.Value == StatementTypeEnum.Delete)
                        {
                            System.Windows.MessageBox.Show(
                             "Brak wsparcia dla operacji DELETE.",
                             "Informacja",
                             MessageBoxButton.OK,
                             MessageBoxImage.Information
                           );
                            return;
                        }

                        if (analyzer.FirstStatementType.Value == StatementTypeEnum.Unknown)
                        {
                            System.Windows.MessageBox.Show(
                               "Nie rozpoznano typu zapytania SQL.",
                               "Informacja",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information
                             );
                            return;
                        }

                        if (!analyzer.IsSingleType)
                        {
                            System.Windows.MessageBox.Show(
                                "Wykryto więcej niż 1 typ zapytania w pliku sql.",
                                "Informacja",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information
                            );
                            return;
                        }

                        ProjectsFromSettings projectsAndConnectionString = QuickAcationsHelper.GetProjectsAndConnectionString(dte, activeDocumentProject);

                        if (projectsAndConnectionString is null) return;

                        List<ColumnInfo> columnsFromDb = QuickAcationsHelper.GetColumnsFromDb(table, schema, projectsAndConnectionString.ConnectionString);

                        if (columnsFromDb is null || !columnsFromDb.Any())
                        {
                            QuickAcationsHelper.ShowStatusBarMessage($"Nie znaleziono obiektu w bazie: [{schema}].[{table}]");
                            return;
                        }

                        string finalSql = sb.ToString() + "\r\n" + code2;

                        if (analyzer.FirstStatementType == StatementTypeEnum.Select)
                            using (SqlConnection connection = new(projectsAndConnectionString.ConnectionString))
                            {
                                string sqlQuery = $"EXEC sp_describe_first_result_set @tsql = N'{finalSql}'";
                                SqlCommand command = new(sqlQuery, connection);
                                await connection.OpenAsync();
                                SqlDataReader reader = await command.ExecuteReaderAsync();
                                while (await reader.ReadAsync())
                                {
                                    string columnName = reader["name"].ToString().Replace("_", "");
                                    string columnType = reader["system_type_name"].ToString();
                                    bool isNullable = (bool)reader["is_nullable"];
                                    selectColumnInfoList.Add(new SelectColumnInfo(columnName, columnType, isNullable));
                                }
                            }

                        using (SqlConnection connection = new(projectsAndConnectionString.ConnectionString))
                        {
                            string sqlDescribeParametersQuery = $"EXEC sp_describe_undeclared_parameters @tsql = N'{finalSql}'";
                            SqlCommand sqlDescribeParametersCommand = new(sqlDescribeParametersQuery, connection);
                            await connection.OpenAsync();
                            SqlDataReader describeParametersReader = await sqlDescribeParametersCommand.ExecuteReaderAsync();
                            while (await describeParametersReader.ReadAsync())
                            {
                                string parameterName = describeParametersReader["name"].ToString();
                                string parameterType = describeParametersReader["suggested_system_type_name"].ToString();
                                bool mayBeNull = false;

                                if (analyzer.FirstStatementType.Value == StatementTypeEnum.Insert || analyzer.FirstStatementType.Value == StatementTypeEnum.Update)
                                {
                                    ColumnInfo matchedColumn = columnsFromDb.FirstOrDefault(c => c.ColumnName.Equals(parameterName.Replace("@", ""), StringComparison.OrdinalIgnoreCase));

                                    if (matchedColumn is not null)
                                        mayBeNull = matchedColumn.IsNullable;
                                }

                                parametersInfoList.Add(new ParametersInfo(parameterName, parameterType, mayBeNull));
                            }
                        }                     

                        bool returnsMultipleResults = false;

                        if (analyzer.FirstStatementType == StatementTypeEnum.Select)
                        {

                            MessageBoxResult result = System.Windows.MessageBox.Show(
                            "Czy zapytanie będzię zwracało wiele rekordów?",
                            "Typ zwracanych danych",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question
                            );

                            returnsMultipleResults = (result == MessageBoxResult.Yes);
                        }

                        if (analyzer.FirstStatementType == StatementTypeEnum.Update && (originalCode.Replace(" ", "").ToLower().Contains("[isactive]=0") || originalCode.Replace(" ", "").ToLower().Contains("isactive=0")))
                        {
                            MessageBoxResult result = System.Windows.MessageBox.Show(
                            "Czy te zapytanie usuwa rekord?",
                            "Typ zwracanych danych",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question
                            );

                            if (result == MessageBoxResult.Yes)
                                analyzer.FirstStatementType = StatementTypeEnum.Delete;
                        }

                        string[] searchParameters = ["PageSize", "Page", "OrderBy", "OrderDir"];

                        bool isSearch = searchParameters.All(required =>
                            parametersInfoList.Any(col => string.Equals(col.Name.Replace("@", ""), required, StringComparison.OrdinalIgnoreCase))
                        );

                        var selectLayerWindow = new SelectLayerWindow();
                        selectLayerWindow.ShowDialog();

                        if (selectLayerWindow.Layer is null)
                        {
                            QuickAcationsHelper.ShowStatusBarMessage($"Nie wybrano żadnej opcji.");
                            return;
                        }

                        bool existsParamsInObject = ExistParamsForObject(table, parametersInfoList);

                        string repoFolder = QuickAcationsHelper.FindFolder(activeDocumentProject.ProjectItems, "Repositories")?.Name
                            ?? QuickAcationsHelper.FindFolder(activeDocumentProject.ProjectItems, "Repository")?.Name ?? "Repositories";

                        GenerateRepositoryInterfaceFile(selectLayerWindow.AsynchronousGenerate, projectsAndConnectionString, repoFolder, schema, tableCamelCase, analyzer.FirstStatementType.Value, parametersInfoList, activeDocumentProject, returnsMultipleResults, action, dte);
                        if (parametersInfoList.Any() && existsParamsInObject)
                            GenerateModelFile(projectsAndConnectionString, schema, tableCamelCase, parametersInfoList, activeDocumentProject, action, originalCode);
                        GenerateRepositoryFile(selectLayerWindow.AsynchronousGenerate, projectsAndConnectionString, repoFolder, schema, table, tableCamelCase, parametersInfoList, analyzer.FirstStatementType.Value, activeDocumentProject, returnsMultipleResults, action, Path.GetFileNameWithoutExtension(dte.ActiveDocument.Name));
                        GenerateRepositoryMainFile(repoFolder, schema, tableCamelCase, activeDocumentProject);
                        if (analyzer.FirstStatementType == StatementTypeEnum.Select)
                            GenerateResponseModelFile(projectsAndConnectionString, schema, tableCamelCase, selectColumnInfoList, activeDocumentProject, action);

                        if (selectLayerWindow.Layer == LayerTypeEnum.Services_Repositories || selectLayerWindow.Layer == LayerTypeEnum.Controllers_Services_Repositories)
                        {
                            GenerateServiceInterfaceFile(selectLayerWindow.AsynchronousGenerate, projectsAndConnectionString, schema, tableCamelCase, analyzer.FirstStatementType.Value, parametersInfoList, projectsAndConnectionString.ProjectCore, returnsMultipleResults, action, projectsAndConnectionString.ProjectData, isSearch);
                            if (parametersInfoList.Any() && existsParamsInObject)
                                GenerateDtoFile(projectsAndConnectionString, schema, tableCamelCase, parametersInfoList, action, originalCode, projectsAndConnectionString.ProjectCore, projectsAndConnectionString.ProjectData, isSearch);
                            GenerateServiceFile(selectLayerWindow.AsynchronousGenerate, projectsAndConnectionString, schema, tableCamelCase, analyzer.FirstStatementType.Value, parametersInfoList, returnsMultipleResults, action, isSearch, projectsAndConnectionString.ProjectCore, projectsAndConnectionString.ProjectData);
                            GenerateServiceMainFile(repoFolder, schema, tableCamelCase, projectsAndConnectionString.ProjectCore, projectsAndConnectionString.ProjectData);
                        }

                        if (selectLayerWindow.Layer == LayerTypeEnum.Controllers_Services_Repositories)
                        {
                            if (parametersInfoList.Any() && existsParamsInObject)
                                GenerateRequestFile(projectsAndConnectionString, schema, tableCamelCase, parametersInfoList, projectsAndConnectionString.ProjectApi, projectsAndConnectionString.ProjectCore, action, isSearch, originalCode);
                            GenerateControllerFile(projectsAndConnectionString, selectLayerWindow.AsynchronousGenerate, schema, tableCamelCase, action, analyzer.FirstStatementType.Value, projectsAndConnectionString.ProjectApi, returnsMultipleResults, isSearch, parametersInfoList);
                            GenerateMainControllerFile(schema, tableCamelCase, projectsAndConnectionString.ProjectApi, projectsAndConnectionString.ProjectCore);
                        }
                    }

                    rdr.Dispose();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Wystąpił błąd podczas wykonywania polecenia 😞 : {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static string ConvertToPlural(string word)
        {
            if (Regex.IsMatch(word, "[^aeiou]y$"))
                return Regex.Replace(word, "y$", "ies");

            if (Regex.IsMatch(word, "(s|ss|sh|ch|x|z)$"))
            {
                if (word.EndsWith("z"))
                    return word + "zes";
                else
                    return word + "es";
            }

            if (Regex.IsMatch(word, "[^aeiou]o$"))
                return word + "es";

            return word + "s";
        }

        public static string ConvertToKebabCase(string tekst)
        {
            var wynik = new StringBuilder();

            foreach (char znak in tekst)
            {
                if (char.IsUpper(znak))
                {
                    wynik.Append('-');
                    wynik.Append(char.ToLower(znak));
                }
                else
                {
                    wynik.Append(znak);
                }
            }

            return wynik.ToString();
        }

        private void GenerateRequestFile(ProjectsFromSettings projectsAndConnectionString, string schema, string table, List<ParametersInfo> parameters, EnvDTE.Project apiProject, EnvDTE.Project coreProject, string actionName, bool isSearch, string code)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string className = $"{table}{actionName}Request";
            string fileName = $"{className}.cs";
            string projectDir = Path.GetDirectoryName(apiProject.FullName);
            string directoryPath = Path.Combine(projectDir, projectsAndConnectionString.ApiModelsFolder, schema, table);
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);
            StringBuilder sb = new();

            string[] systemTypes = ["date", "datetime", "datetime2", "smalldatetime", "time", "uniqueidentifier"];
            if (parameters.Any(p => systemTypes.Contains(p.Type.ToLower())))
                sb.AppendLine("using System;");

            string[] searchParameters = ["PageSize", "Page", "OrderBy", "OrderDir"];

            var publicParameters = parameters.Where(p =>
            p.ReplacedName.ToLower() != "authorizeduserid"
            && p.ReplacedName.ToLower() != "userid"
            && p.ReplacedName.ToLower() != "id"
            && !searchParameters.Any(sp => sp.Equals(p.ReplacedName, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            if (isSearch)
            {
                sb.AppendLine("using Mroczware.Core.Common.Models.Search;");
                sb.AppendLine("using Mroczware.Framework.Api.Models.ViewModels;");
            }

            if (publicParameters.Any(p => !p.Nullable))
                sb.AppendLine("using System.ComponentModel.DataAnnotations;");

            sb.AppendLine($"using {coreProject.Name}.{projectsAndConnectionString.DtoFolder}.{schema}.{table};");
            sb.AppendLine();
            sb.AppendLine($"namespace {apiProject.Name}.{projectsAndConnectionString.ApiModelsFolder}.{schema}.{table}");
            sb.AppendLine("{");
            sb.AppendLine($"    public class {className}  {(isSearch ? ": SearchCriteria" : "")}");
            sb.AppendLine("    {");

            string codeWithoutSpaces = code.Replace(" ", "");

            foreach (ParametersInfo parameter in publicParameters)
            {
                if (codeWithoutSpaces.Contains($"{parameter.Name}ISNULL") || codeWithoutSpaces.Contains($"ISNULL({parameter.Name}") || codeWithoutSpaces.Contains($"COALESCE({parameter.Name}"))
                    parameter.Nullable = true;

                string csharpType = QuickAcationsHelper.SqlTypeToCSharp(parameter.Type, parameter.Nullable);

                if (!parameter.Nullable)
                    sb.AppendLine($"        [Required(ErrorMessage = \"{parameter.ReplacedName} is required.\")]");

                if (parameter.Type.Contains("varchar"))
                {
                    if (parameter.Nullable)
                    {
                        sb.AppendLine($"        public {csharpType} {parameter.ReplacedName} {{ get; set; }}");
                    }
                    else
                    {
                        sb.AppendLine($"        public {csharpType} {parameter.ReplacedName} {{ get; set; }} = null!;");
                    }
                }
                else
                {
                    sb.AppendLine($"        public {csharpType} {parameter.ReplacedName} {{ get; set; }}");
                }

                sb.AppendLine();
            }

            List<string> toDoArguments = new();

            if (parameters.Any(p => p.ReplacedName.ToLower() == "id"))
                toDoArguments.Add("int id");

            ParametersInfo authorizedUserIdParam = parameters.FirstOrDefault(p => p.ReplacedName.ToLower() == "authorizeduserid" || p.ReplacedName.ToLower() == "userid");

            if (authorizedUserIdParam != null)
                toDoArguments.Add($"int {ChangeFirstLetterToSmall(authorizedUserIdParam.ReplacedName)}");

            sb.AppendLine($"        internal {table}{actionName}Dto ToDto({string.Join(", ", toDoArguments)})");
            sb.AppendLine("        {");
            sb.AppendLine($"            return new {table}{actionName}Dto");
            sb.AppendLine("            {");

            foreach (ParametersInfo parameter in parameters)
            {
                if (parameter.ReplacedName.ToLower() == "authorizeduserid" || parameter.ReplacedName.ToLower() == "userid")
                {
                    sb.AppendLine($"                {parameter.ReplacedName} = {ChangeFirstLetterToSmall(parameter.ReplacedName)},");
                }
                else if (parameter.ReplacedName.ToLower() == "id")
                {
                    sb.AppendLine($"                {parameter.ReplacedName} = id,");
                }
                else
                {
                    sb.AppendLine($"                {parameter.ReplacedName} = {parameter.ReplacedName},");
                }

            }

            sb.AppendLine("            };");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(filePath, sb.ToString());
            apiProject.ProjectItems.AddFromFile(filePath);
        }

        private void GenerateControllerFile(ProjectsFromSettings projectsAndConnectionString, bool asynchronousGenerate, string schema, string table, string actionName, StatementTypeEnum resultType, EnvDTE.Project apiProject, bool manyResults, bool isSearch, List<ParametersInfo> parameters)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string className = $"{table}Controller";
            string fileName = $"{className}.{actionName}.cs";
            string projectDir = System.IO.Path.GetDirectoryName(apiProject.FullName);
            string directoryPath = Path.Combine(projectDir, "Controllers", schema, table);
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);

            bool existsParamsInObject = ExistParamsForObject(table, parameters);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("using Microsoft.AspNetCore.Mvc;");
            if(asynchronousGenerate)
                sb.AppendLine("using System.Threading.Tasks;");

            if (existsParamsInObject)
                sb.AppendLine($"using {apiProject.Name}.{projectsAndConnectionString.ApiModelsFolder}.{schema}.{table};");

            sb.AppendLine();
            sb.AppendLine($"namespace {apiProject.Name}.Controllers.{schema}.{table}");
            sb.AppendLine("{");
            sb.AppendLine($"    public partial class {className}");
            sb.AppendLine("    {");
            sb.AppendLine($"       [{HttpVerb(resultType)}(\"{ChangeFirstLetterToSmall(schema)}/{ConvertToKebabCase(BuildRoute(table, actionName, parameters))}\")]");
            sb.AppendLine($"        public {(asynchronousGenerate ? "async" : "")} {(asynchronousGenerate ? "Task<IActionResult>" : "IActionResult")} {actionName}({BuildControllerArguments(table, actionName, parameters, resultType, existsParamsInObject)})");
            sb.AppendLine("        {");
            sb.AppendLine("             if (!ModelState.IsValid)");
            sb.AppendLine("                 return BadRequest(ModelState);");
            sb.AppendLine("");


            List<string> arguments = new();

            if (parameters.Any(p => p.ReplacedName.Equals("id", StringComparison.OrdinalIgnoreCase)))
                arguments.Add("id");

            if (parameters.Any(p => p.ReplacedName.ToLower() == "authorizeduserid"))
                arguments.Add("AuthorizedUser.Id");

            if (resultType == StatementTypeEnum.Select || resultType == StatementTypeEnum.Insert)
            {
                if (existsParamsInObject)
                {
                    sb.AppendLine($"            return Ok({(asynchronousGenerate ? "await" : "")} _{ChangeFirstLetterToSmall(table)}Service.{actionName}{(asynchronousGenerate ? "Async" : "")}({(parameters.Any() ? $"request.ToDto({string.Join(", ", arguments)})" : "")}));");
                }
                else
                {
                    sb.AppendLine($"            return Ok({(asynchronousGenerate ? "await" : "")} _{ChangeFirstLetterToSmall(table)}Service.{actionName}{(asynchronousGenerate ? "Async" : "")}({(parameters.Any() ? string.Join(", ", arguments) : "")}));");
                }
            }
            else
            {
                if (existsParamsInObject)
                {
                    sb.AppendLine($"            {(asynchronousGenerate ? "await" : "")} _{ChangeFirstLetterToSmall(table)}Service.{actionName}{(asynchronousGenerate ? "Async" : "")}({(parameters.Any() ? $"request.ToDto({string.Join(", ", arguments)})" : "")});");
                }
                else
                {
                    sb.AppendLine($"            {(asynchronousGenerate ? "await" : "")} _{ChangeFirstLetterToSmall(table)}Service.{actionName}{(asynchronousGenerate ? "Async" : "")}({(parameters.Any() ? string.Join(", ", arguments) : "")});");
                }

                sb.AppendLine($"            return Ok();");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(filePath, sb.ToString());
            apiProject.ProjectItems.AddFromFile(filePath);
        }

        private static string BuildControllerArguments(string table, string actionName, List<ParametersInfo> parameters, StatementTypeEnum resultType, bool existsParamsInObject)
        {
            string attribute = resultType == StatementTypeEnum.Update || resultType == StatementTypeEnum.Insert || resultType == StatementTypeEnum.Delete ? "[FromBody]" : "[FromQuery]";

            List<string> argumentFragments = new();

            if (parameters.Any(p => p.ReplacedName.ToLower() == "id"))
                argumentFragments.Add("[FromRoute] int id");

            if (parameters.Any() && existsParamsInObject)
                argumentFragments.Add($"{attribute} {table}{actionName}Request request");

            return string.Join(", ", argumentFragments);
        }

        private static string BuildRoute(string table, string actionName, List<ParametersInfo> parameters)
        {
            List<string> routeFragments = [ChangeFirstLetterToSmall(ConvertToPlural(table))];

            if (parameters.Any(p => p.ReplacedName.Equals("id", StringComparison.OrdinalIgnoreCase)))
                routeFragments.Add("{id}");
            else if (parameters.Any(p => p.ReplacedName.Equals(table + "Id", StringComparison.OrdinalIgnoreCase)))
                routeFragments.Add($"{table + "Id"}");

            switch (actionName.ToLower())
            {
                case "get":
                case "update":
                case "delete":
                case "create":
                case "getlist":
                    break;
                default:
                    routeFragments.Add(ChangeFirstLetterToSmall(actionName));
                    break;
            }

            return string.Join("/", routeFragments);
        }

        private static string HttpVerb(StatementTypeEnum resultType)
        {
            string csharpType;

            switch (resultType)
            {
                case StatementTypeEnum.Select:
                    csharpType = $"HttpGet";
                    break;
                case StatementTypeEnum.Update:
                    csharpType = $"HttpPut";
                    break;
                case StatementTypeEnum.Insert:
                    csharpType = $"HttpPost";
                    break;
                case StatementTypeEnum.Delete:
                    csharpType = $"HttpDelete";
                    break;
                default:
                    throw new Exception("Nie można określić czasownika HTTP");
            }

            return csharpType;
        }

        private void GenerateMainControllerFile(string schema, string table, EnvDTE.Project apiProject, EnvDTE.Project coreProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string className = $"{table}Controller";
            string fileName = $"{className}.cs";
            string projectDir = Path.GetDirectoryName(apiProject.FullName);
            string directoryPath = Path.Combine(projectDir, "Controllers", schema, table);
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);
            if (File.Exists(filePath))
                return;

            StringBuilder sb = new();
            sb.AppendLine("using Mroczware.Framework.Authentication.Controllers;");
            sb.AppendLine("using Mroczware.Framework.Authentication.Models;");
            sb.AppendLine($"using {coreProject.Name}.Services.{schema}.{table};");
            sb.AppendLine();
            sb.AppendLine($"namespace {apiProject.Name}.Controllers.{schema}.{table}");
            sb.AppendLine("{");
            sb.AppendLine($"    public partial class {className} : AuthenticatedController<ApiUser>");
            sb.AppendLine("    {");
            sb.AppendLine($"        private readonly I{table}Service _{ChangeFirstLetterToSmall(table)}Service;");
            sb.AppendLine();
            sb.AppendLine($"        public {className}(I{table}Service {ChangeFirstLetterToSmall(table)}Service)");
            sb.AppendLine("        {");
            sb.AppendLine($"            _{ChangeFirstLetterToSmall(table)}Service = {ChangeFirstLetterToSmall(table)}Service;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(filePath, sb.ToString());

            apiProject.ProjectItems.AddFromFile(filePath);
        }

        string RemoveComments(string sql)
        {
            // usuwa komentarze blokowe /* ... */
            sql = Regex.Replace(sql, @"/\*.*?\*/", "", RegexOptions.Singleline);

            // usuwa komentarze liniowe --
            sql = Regex.Replace(sql, @"--.*?$", "", RegexOptions.Multiline);

            return sql;
        }

        class RootLevelStatementTypeAnalyzer
        {
            private readonly List<StatementTypeEnum> _types = [];

            public StatementTypeEnum? FirstStatementType { get; set; }

            public bool IsSingleType => _types.Count == 1;

            public void Analyze(TSqlScript script)
            {
                foreach (TSqlBatch batch in script.Batches)
                {
                    foreach (TSqlStatement statement in batch.Statements)
                    {
                        StatementTypeEnum type = GetStatementType(statement);

                        if (type != StatementTypeEnum.Unknown)
                        {
                            if (!_types.Any()) FirstStatementType = type;

                            _types.Add(type);
                        }
                    }
                }
            }

            private StatementTypeEnum GetStatementType(TSqlStatement stmt) => stmt switch
            {
                SelectStatement => StatementTypeEnum.Select,
                InsertStatement => StatementTypeEnum.Insert,
                UpdateStatement => StatementTypeEnum.Update,
                DeleteStatement => StatementTypeEnum.Delete,
                MergeStatement => StatementTypeEnum.Merge,
                _ => StatementTypeEnum.Unknown
            };
        }

        private void GenerateResponseModelFile(ProjectsFromSettings projectsAndConnectionString, string schema, string table, List<SelectColumnInfo> selectColumnInfoList, EnvDTE.Project activeDocumentProject, string actionName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string className = $"{table}{actionName}ResponseModel";
            string fileName = $"{className}.cs";
            string projectDir = Path.GetDirectoryName(activeDocumentProject.FullName);
            string directoryPath = Path.Combine(projectDir, projectsAndConnectionString.ModelsFolder, schema, table);
            Directory.CreateDirectory(directoryPath);
            string filePath = Path.Combine(directoryPath, fileName);

            StringBuilder sb = new StringBuilder();

            string[] systemTypes = ["date", "datetime", "datetime2", "smalldatetime", "time", "uniqueidentifier"];
            if (selectColumnInfoList.Any(p => systemTypes.Contains(p.Type.ToLower())))
                sb.AppendLine("using System;");

            if (selectColumnInfoList.Select(c => c.Name).Contains("TotalRows"))
            {
                sb.AppendLine("using Mroczware.Core.Common.Models.Search;");
                sb.AppendLine($"using Mroczware.Framework.Api.Models.ViewModels;");
            }

            sb.AppendLine();
            sb.AppendLine($"namespace {activeDocumentProject.Name}.{projectsAndConnectionString.ModelsFolder}.{schema}.{table}");
            sb.AppendLine("{");
            sb.AppendLine($"    public class {className}  {(selectColumnInfoList.Select(c => c.Name).Contains("TotalRows") ? ": SearchItem" : "")}");
            sb.AppendLine("    {");

            foreach (var column in selectColumnInfoList.Where(c => c.Name != "TotalRows"))
            {
                string csharpType = QuickAcationsHelper.SqlTypeToCSharp(column.Type, column.Nullable);
                sb.AppendLine($"        public {csharpType} {column.Name} {{ get; set; }} {(!column.Nullable && csharpType == "string" ? "= null!;" : "")}");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(filePath, sb.ToString());
            activeDocumentProject.ProjectItems.AddFromFile(filePath);
        }
        private void GenerateModelFile(ProjectsFromSettings projectsAndConnectionString, string schema, string table, List<ParametersInfo> parameters, EnvDTE.Project activeDocumentProject, string actionName, string code)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string className = $"{table}{actionName}Model";
            string fileName = $"{className}.cs";
            string projectDir = Path.GetDirectoryName(activeDocumentProject.FullName);
            string directoryPath = Path.Combine(projectDir, projectsAndConnectionString.ModelsFolder, schema, table);
            Directory.CreateDirectory(directoryPath);
            string filePath = Path.Combine(directoryPath, fileName);

            string[] searchParameters = ["PageSize", "Page", "OrderBy", "OrderDir"];

            bool containsAll = searchParameters.All(required =>
                parameters.Any(col => string.Equals(col.Name, required, StringComparison.OrdinalIgnoreCase))
            );

            StringBuilder sb = new();

            if (containsAll)
            {
                sb.AppendLine("using Mroczware.Core.Common.Models.Search;");
                sb.AppendLine("using Mroczware.Framework.Api.Models.ViewModels;");
            }

            string[] systemTypes = ["date", "datetime", "datetime2", "smalldatetime", "time", "uniqueidentifier"];
            if (parameters.Any(p => systemTypes.Contains(p.Type.ToLower())))
                sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine($"namespace {activeDocumentProject.Name}.{projectsAndConnectionString.ModelsFolder}.{schema}.{table}");
            sb.AppendLine("{");
            sb.AppendLine($"    public class {className}  {(containsAll ? ": SearchCriteria" : "")}");
            sb.AppendLine("    {");

            if (containsAll)
                parameters = parameters.Where(p => !searchParameters.Contains(p.ReplacedName)).ToList();

            string codeWithoutSpaces = code.Replace(" ", "");

            foreach (ParametersInfo parameter in parameters)
            {
                if (codeWithoutSpaces.Contains($"{parameter.Name}ISNULL") || codeWithoutSpaces.Contains($"ISNULL({parameter.Name}") || codeWithoutSpaces.Contains($"COALESCE({parameter.Name}"))
                    parameter.Nullable = true;

                string csharpType = QuickAcationsHelper.SqlTypeToCSharp(parameter.Type, parameter.Nullable);

                if (parameter.Type.Contains("varchar"))
                {
                    if (parameter.Nullable)
                    {
                        sb.AppendLine($"        public {csharpType} {parameter.ReplacedName} {{ get; set; }}");
                    }
                    else
                    {
                        sb.AppendLine($"        public {csharpType} {parameter.ReplacedName} {{ get; set; }} = null!;");
                    }
                }
                else
                {
                    sb.AppendLine($"        public {csharpType} {parameter.ReplacedName} {{ get; set; }}");
                }
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(filePath, sb.ToString());
            activeDocumentProject.ProjectItems.AddFromFile(filePath);
        }

        private void GenerateRepositoryInterfaceFile(bool asynchronousGenerate, ProjectsFromSettings projectsAndConnectionString, string repoFolder, string schema, string table, StatementTypeEnum resultType, List<ParametersInfo> parameters, EnvDTE.Project activeDocumentProject, bool manyResults, string actionName, DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string interfaceName = $"I{table}Repository";
            string fileName = $"{interfaceName}.cs";
            string projectDir = System.IO.Path.GetDirectoryName(activeDocumentProject.FullName);
            string directoryPath = Path.Combine(projectDir, repoFolder, schema, table);
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);

            if (!File.Exists(filePath))
            {
                StringBuilder sb = new();
                if(asynchronousGenerate)
                    sb.AppendLine("using System.Threading.Tasks;");
                sb.AppendLine($"using {QuickAcationsHelper.ReturnIDependencyUsing(activeDocumentProject)};");
                sb.AppendLine();
                sb.AppendLine($"namespace {activeDocumentProject.Name}.{repoFolder}.{schema}.{table}");
                sb.AppendLine("{");
                sb.AppendLine($"    public interface {interfaceName} : IDependency");
                sb.AppendLine("    {");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                File.WriteAllText(filePath, sb.ToString());
                activeDocumentProject.ProjectItems.AddFromFile(filePath);
            }
            ModifyInterface(asynchronousGenerate, projectsAndConnectionString, filePath, table, schema, resultType, dte, manyResults, actionName, parameters, activeDocumentProject);
        }

        private void GenerateServiceInterfaceFile(bool asynchronousGenerate, ProjectsFromSettings projectsAndConnectionString, string schema, string table, StatementTypeEnum resultType, List<ParametersInfo> parameters, EnvDTE.Project coreProject, bool manyResults, string actionName, EnvDTE.Project dataProject, bool isSearch)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string interfaceName = $"I{table}Service";
            string fileName = $"{interfaceName}.cs";
            string projectDir = Path.GetDirectoryName(coreProject.FullName);
            string directoryPath = Path.Combine(projectDir, "Services", schema, table);
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);

            if (!File.Exists(filePath))
            {
                StringBuilder sb = new();
                if(asynchronousGenerate)
                    sb.AppendLine("using System.Threading.Tasks;");
                sb.AppendLine($"using {QuickAcationsHelper.ReturnIDependencyUsing(coreProject)};");
                sb.AppendLine();
                sb.AppendLine($"namespace {Path.GetFileNameWithoutExtension(coreProject.FullName)}.Services.{schema}.{table}");
                sb.AppendLine("{");
                sb.AppendLine($"    public interface {interfaceName} : IDependency");
                sb.AppendLine("    {");
                sb.AppendLine("    }");
                sb.AppendLine("}");

                File.WriteAllText(filePath, sb.ToString());
                coreProject.ProjectItems.AddFromFile(filePath);
            }

            ModifyServiceInterface(asynchronousGenerate, projectsAndConnectionString, filePath, table, schema, resultType, manyResults, actionName, parameters, coreProject, dataProject, isSearch);
        }

        private void GenerateRepositoryFile(bool asynchronousGenerate, ProjectsFromSettings projectsAndConnectionString, string repoFolder, string schema, string table,string tableCamelCase, List<ParametersInfo> parameters, StatementTypeEnum resultType, EnvDTE.Project activeDocumentProject, bool manyResults, string actionName, string fileNameInExplorer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string className = $"{tableCamelCase}Repository";
            string fileName = $"{className}.{actionName}{(asynchronousGenerate ? "Async" : "")}.cs";
            string projectDir = Path.GetDirectoryName(activeDocumentProject.FullName);
            string directoryPath = Path.Combine(projectDir, repoFolder, schema, tableCamelCase);
            Directory.CreateDirectory(directoryPath);
            string filePath = Path.Combine(directoryPath, fileName);

            StringBuilder sb = new();
            sb.AppendLine("using Dapper;");
            sb.AppendLine("using Mroczware.DataBase.Models;");
            if (ExistParamsForObject(table, parameters) || resultType == StatementTypeEnum.Select)
                sb.AppendLine($"using {activeDocumentProject.Name}.{projectsAndConnectionString.ModelsFolder}.{schema}.{tableCamelCase};");
            if (resultType == StatementTypeEnum.Select && manyResults)
                sb.AppendLine("using System.Collections.Generic;");
            if(asynchronousGenerate)
                sb.AppendLine("using System.Threading.Tasks;");
            if (resultType == StatementTypeEnum.Select && !manyResults)
                sb.AppendLine("using System.Linq;");
            sb.AppendLine($"using {activeDocumentProject.Name}.Resources.{schema}.{tableCamelCase};");
            sb.AppendLine();
            sb.AppendLine($"namespace {activeDocumentProject.Name}.{repoFolder}.{schema}.{tableCamelCase}");
            sb.AppendLine("{");
            sb.AppendLine($"    internal partial class {className}");
            sb.AppendLine("    {");

            bool existsParamsInObject = ExistParamsForObject(table, parameters);

            if (parameters.Any())
            {
                if (existsParamsInObject)
                {
                    sb.AppendLine($"        public {(asynchronousGenerate ? "async" : "")} {BuildRepositoryResponse(asynchronousGenerate, tableCamelCase, resultType, manyResults, actionName)} {actionName}{(asynchronousGenerate ? "Async" : "")}({tableCamelCase}{actionName}Model model)");
                }
                else
                {
                    sb.AppendLine($"        public {(asynchronousGenerate ? "async" : "")} {BuildRepositoryResponse(asynchronousGenerate, tableCamelCase, resultType, manyResults, actionName)} {actionName}{(asynchronousGenerate ? "Async" : "")}({string.Join(",", parameters.Select(p => $"int {ChangeFirstLetterToSmall(p.ReplacedName)}"))})");
                }
            }
            else
            {
                sb.AppendLine($"        public {(asynchronousGenerate ? "async" : "")} {BuildRepositoryResponse(asynchronousGenerate, tableCamelCase, resultType, manyResults, actionName)} {actionName}{(asynchronousGenerate ? "Async" : "")}()");
            }


            sb.AppendLine("        {");

            if (parameters.Any())
                sb.AppendLine("            var _params = new DynamicParameters();");

            foreach (ParametersInfo parameter in parameters)
            {
                if (existsParamsInObject)
                {
                    sb.AppendLine($"                _params.Add(\"{parameter.Name.Replace("@", "")}\", model.{parameter.ReplacedName});");
                }
                else
                {
                    sb.AppendLine($"                _params.Add(\"{parameter.Name.Replace("@", "")}\", {ChangeFirstLetterToSmall(parameter.ReplacedName)});");
                }
            }


            sb.AppendLine();
            sb.AppendLine("            var context = new DatabaseContext(");
            sb.AppendLine($"                Sql.{fileNameInExplorer},");
            sb.AppendLine("                System.Transactions.TransactionScopeOption.Required,");
            if (parameters.Any())
            {
                sb.AppendLine($"                System.Data.CommandType.Text, _params");
            }
            else
            {
                sb.AppendLine($"                System.Data.CommandType.Text");
            }

            sb.AppendLine("            );");
            sb.AppendLine();
            sb.AppendLine($"            {BuildRepoMethod(asynchronousGenerate, table, tableCamelCase, resultType, manyResults, actionName)}");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(filePath, sb.ToString());
            activeDocumentProject.ProjectItems.AddFromFile(filePath);
        }

        private static bool ExistParamsForObject(string table, List<ParametersInfo> parameters)
        {
            return parameters.Any(p =>
                                     !p.ReplacedName.Equals("id", StringComparison.OrdinalIgnoreCase)
                                     && !p.ReplacedName.Equals("AuthorizedUserId", StringComparison.OrdinalIgnoreCase)
                                 );
        }

        private void ModifyInterface(bool asynchronousGenerate, ProjectsFromSettings projectsAndConnectionString, string filePath, string table, string schema, StatementTypeEnum resultType, DTE2 dte, bool manyResults, string actionName, List<ParametersInfo> parameters, EnvDTE.Project dataProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string methodName = $"{actionName}{(asynchronousGenerate ? "Async" : "")}";

            string fileContent = File.ReadAllText(filePath);
            SyntaxTree syntaxTreeMainClass = CSharpSyntaxTree.ParseText(fileContent);
            CompilationUnitSyntax rootMainClass = syntaxTreeMainClass.GetCompilationUnitRoot();

            bool argumentUsingExists = rootMainClass.Usings
           .Any(u => u.Name.ToString() == $"{Path.GetFileNameWithoutExtension(dataProject.FullName)}.{projectsAndConnectionString.ModelsFolder}.{schema}.{table}");

            if (!argumentUsingExists && (parameters.Any(p => p.ReplacedName.ToLower() != "id" && p.ReplacedName.ToLower() != "authorizeduserid") || resultType == StatementTypeEnum.Select))
            {
                UsingDirectiveSyntax newUsingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName($"{Path.GetFileNameWithoutExtension(dataProject.FullName)}.{projectsAndConnectionString.ModelsFolder}.{schema}.{table}"));

                rootMainClass = rootMainClass.AddUsings(newUsingDirective);
            }

            //IEnumerable
            bool iEnumerableUsingExists = rootMainClass.Usings
           .Any(u => u.Name.ToString() == $"System.Collections.Generic");

            if (!iEnumerableUsingExists && resultType == StatementTypeEnum.Select && manyResults)
            {
                UsingDirectiveSyntax newUsingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName($"System.Collections.Generic"));

                rootMainClass = rootMainClass.AddUsings(newUsingDirective);
            }


            InterfaceDeclarationSyntax interfaceNode = rootMainClass.DescendantNodes()
           .OfType<InterfaceDeclarationSyntax>()
           .FirstOrDefault();

            if (interfaceNode != null)
            {
                // Znajdź metodę o danej nazwie
                MethodDeclarationSyntax methodToRemove = interfaceNode.Members
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == methodName);

                InterfaceDeclarationSyntax updatedInterface = interfaceNode;

                if (methodToRemove != null)
                {
                    // Usuń metodę z interfejsu
                    updatedInterface = interfaceNode.RemoveNode(methodToRemove, SyntaxRemoveOptions.KeepNoTrivia);
                }
                TypeSyntax returnType = SyntaxFactory.ParseTypeName(BuildRepositoryResponse(asynchronousGenerate, table, resultType, manyResults, actionName));
                ParameterListSyntax interfaceParameters = SyntaxFactory.ParameterList();

                bool existsParamsInObject = ExistParamsForObject(table, parameters);

                if (parameters.Any() && existsParamsInObject)
                {
                    interfaceParameters = SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Parameter(SyntaxFactory.Identifier("model"))
                                .WithType(SyntaxFactory.ParseTypeName($"{table}{actionName}Model"))
                        )
                    );
                }
                else if (parameters.Any() && !existsParamsInObject)
                {
                    var parameterList = new List<ParameterSyntax>();

                    foreach (ParametersInfo parameter in parameters)
                    {
                        var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(ChangeFirstLetterToSmall(parameter.ReplacedName)))
                            .WithType(SyntaxFactory.ParseTypeName("int"));

                        parameterList.Add(param);
                    }

                    interfaceParameters = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameterList));
                }

                MethodDeclarationSyntax newMethod = SyntaxFactory.MethodDeclaration(
                        returnType,
                        methodName)
                    .WithParameterList(interfaceParameters)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

                updatedInterface = updatedInterface.AddMembers(newMethod);

                CompilationUnitSyntax newRoot = rootMainClass.ReplaceNode(interfaceNode, updatedInterface);

                string newCode = newRoot.NormalizeWhitespace().ToFullString();

                File.WriteAllText(filePath, newCode);

            }
        }

        private void ModifyServiceInterface(bool asynchronousGenerate, ProjectsFromSettings projectsAndConnectionString, string filePath, string table, string schema, StatementTypeEnum resultType, bool manyResults, string actionName, List<ParametersInfo> parameters, EnvDTE.Project coreProject, EnvDTE.Project dataProject, bool isSearch)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string methodName = $"{actionName}{(asynchronousGenerate ? "Async" : "")}";

            string fileContent = File.ReadAllText(filePath);
            SyntaxTree syntaxTreeMainClass = CSharpSyntaxTree.ParseText(fileContent);
            CompilationUnitSyntax rootMainClass = syntaxTreeMainClass.GetCompilationUnitRoot();

            bool argumentUsingExists = rootMainClass.Usings
           .Any(u => u.Name.ToString() == $"{Path.GetFileNameWithoutExtension(coreProject.FullName)}.{projectsAndConnectionString.DtoFolder}.{schema}.{table}");

            bool existsParamsInObject = ExistParamsForObject(table, parameters);

            if (!argumentUsingExists && parameters.Any() && existsParamsInObject)
            {
                UsingDirectiveSyntax newUsingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName($"{Path.GetFileNameWithoutExtension(coreProject.FullName)}.{projectsAndConnectionString.DtoFolder}.{schema}.{table}"));

                rootMainClass = rootMainClass.AddUsings(newUsingDirective);
            }

            bool responseUsingExists = rootMainClass.Usings
            .Any(u => u.Name.ToString() == $"{Path.GetFileNameWithoutExtension(dataProject.FullName)}.{projectsAndConnectionString.ModelsFolder}.{schema}.{table}");

            if (!responseUsingExists && resultType == StatementTypeEnum.Select)
            {
                UsingDirectiveSyntax newUsingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName($"{Path.GetFileNameWithoutExtension(dataProject.FullName)}.{projectsAndConnectionString.ModelsFolder}.{schema}.{table}"));

                rootMainClass = rootMainClass.AddUsings(newUsingDirective);
            }

            //IEnumerable
            bool iEnumerableUsingExists = rootMainClass.Usings
           .Any(u => u.Name.ToString() == $"System.Collections.Generic");

            if (!iEnumerableUsingExists && resultType == StatementTypeEnum.Select && manyResults && !isSearch)
            {
                UsingDirectiveSyntax newUsingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName($"System.Collections.Generic"));

                rootMainClass = rootMainClass.AddUsings(newUsingDirective);
            }

            //IEnumerable
            bool iSearchResultUsingExists = rootMainClass.Usings
           .Any(u => u.Name.ToString() == $"Mroczware.Core.Common.Models.Search");

            if (!iSearchResultUsingExists && resultType == StatementTypeEnum.Select && manyResults && isSearch)
            {
                UsingDirectiveSyntax newUsingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName($"Mroczware.Core.Common.Models.Search"));

                rootMainClass = rootMainClass.AddUsings(newUsingDirective);
            }

            InterfaceDeclarationSyntax interfaceNode = rootMainClass.DescendantNodes()
           .OfType<InterfaceDeclarationSyntax>()
           .FirstOrDefault();

            InterfaceDeclarationSyntax updatedInterface = interfaceNode;

            if (interfaceNode != null)
            {
                // Znajdź metodę o danej nazwie
                MethodDeclarationSyntax methodToRemove = interfaceNode.Members
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == methodName);

                if (methodToRemove != null)
                {
                    // Usuń metodę z interfejsu
                    updatedInterface = interfaceNode.RemoveNode(methodToRemove, SyntaxRemoveOptions.KeepNoTrivia);
                }

                TypeSyntax returnType = SyntaxFactory.ParseTypeName(BuildResponse(asynchronousGenerate, table, resultType, manyResults, actionName, isSearch));
                ParameterListSyntax interfaceParameters = SyntaxFactory.ParameterList(); // pusty



                if (parameters.Any() && existsParamsInObject)
                {
                    interfaceParameters = SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Parameter(SyntaxFactory.Identifier("dto"))
                                .WithType(SyntaxFactory.ParseTypeName($"{table}{actionName}Dto"))
                        )
                    );
                }
                else if (parameters.Any() && !existsParamsInObject)
                {
                    var parameterList = new List<ParameterSyntax>();

                    foreach (ParametersInfo parameter in parameters)
                    {
                        var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(ChangeFirstLetterToSmall(parameter.ReplacedName)))
                            .WithType(SyntaxFactory.ParseTypeName("int"));

                        parameterList.Add(param);
                    }

                    interfaceParameters = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameterList));
                }

                MethodDeclarationSyntax newMethod = SyntaxFactory.MethodDeclaration(
                        returnType,
                        methodName)
                    .WithParameterList(interfaceParameters)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

                updatedInterface = updatedInterface.AddMembers(newMethod);

                CompilationUnitSyntax newRoot = rootMainClass.ReplaceNode(interfaceNode, updatedInterface);

                string newCode = newRoot.NormalizeWhitespace().ToFullString();

                File.WriteAllText(filePath, newCode);

            }
        }

        private string BuildResponse(bool asynchronousGenerate, string table, StatementTypeEnum resultType, bool manyResults, string actionName, bool isSearch)
        {
            switch (resultType)
            {
                case StatementTypeEnum.Select:
                    {
                        if (manyResults)
                        {
                            if (isSearch)
                            {
                                
                                if (asynchronousGenerate)
                                {
                                    return $"Task<SearchResult<{table}{actionName}ResponseModel>>";
                                }
                                else
                                {
                                    return $"SearchResult<{table}{actionName}ResponseModel>";
                                }

                            }
                            else
                            {
                                
                                if (asynchronousGenerate)
                                {
                                    return $"Task<IEnumerable<{table}{actionName}ResponseModel>>";
                                }
                                else
                                {
                                    return $"IEnumerable<{table}{actionName}ResponseModel>";
                                }
                            }
                        }
                        else
                        {
                            if (asynchronousGenerate)
                            {
                                return $"Task<{table}{actionName}ResponseModel?>";
                            }
                            else
                            {
                                return $"{table}{actionName}ResponseModel?";
                            }
                            
                        }
                    }
                case StatementTypeEnum.Update:
                case StatementTypeEnum.Delete:
                    {
                        if (asynchronousGenerate)
                        {
                            return "Task";
                        }
                        else
                        {
                            return "void";
                        }
                    }
                   
                case StatementTypeEnum.Insert:
                    {
                        if (asynchronousGenerate)
                        {
                            return "Task<int>";
                        }
                        else
                        {
                            return "int";
                        }
                    }
                   
                default:
                    throw new ArgumentException($"Unknown action: {actionName}");
            }
        }

        class VariableVisitor : TSqlFragmentVisitor
        {
            public readonly List<string> DeclaredVariables = [];

            public override void Visit(DeclareVariableStatement node)
            {
                foreach (DeclareVariableElement variable in node.Declarations)
                    DeclaredVariables.Add(variable.VariableName.Value);
            }
        }

        class ParamToNullIfBooleanIsNullExpression : TSqlFragmentVisitor
        {
            public override void Visit(BooleanIsNullExpression node)
            {
                if (node.Expression is VariableReference varRef)
                    node.Expression = new NullLiteral();

                base.Visit(node);
            }
        }

        class ParamUsageAnalyzer : TSqlFragmentVisitor
        {
            private readonly Dictionary<string, int> _paramUsageCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _paramsUsedInIsNull = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public override void Visit(VariableReference node)
            {
                if (!_paramUsageCount.ContainsKey(node.Name))
                    _paramUsageCount[node.Name] = 0;
                _paramUsageCount[node.Name]++;
                base.Visit(node);
            }

            public override void Visit(BooleanIsNullExpression node)
            {
                if (node.Expression is VariableReference varRef)
                    _paramsUsedInIsNull.Add(varRef.Name);

                base.Visit(node);
            }

            public List<string> GetSingleIsNullParams()
            {
                var result = new List<string>();
                foreach (var param in _paramsUsedInIsNull)
                {
                    if (_paramUsageCount.TryGetValue(param, out int count) && count == 1)
                    {
                        result.Add(param);
                    }
                }
                return result;
            }
        }

        private void GenerateDtoFile(ProjectsFromSettings projectsAndConnectionString, string schema, string table, List<ParametersInfo> parameters, string actionName, string code, EnvDTE.Project coreProject, EnvDTE.Project dataProject, bool isSearch)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string className = $"{table}{actionName}Dto";
            string fileName = $"{className}.cs";
            string projectDir = Path.GetDirectoryName(coreProject.FullName);
            string directoryPath = Path.Combine(projectDir, projectsAndConnectionString.DtoFolder, schema, table);
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);

            StringBuilder sb = new();
            string[] systemTypes = ["date", "datetime", "datetime2", "smalldatetime", "time", "uniqueidentifier"];
            if (parameters.Any(p => systemTypes.Contains(p.Type.ToLower())))
                sb.AppendLine("using System;");
            sb.AppendLine($"using {Path.GetFileNameWithoutExtension(dataProject.FullName)}.{projectsAndConnectionString.ModelsFolder}.{schema}.{table};");
            sb.AppendLine();
            sb.AppendLine($"namespace {Path.GetFileNameWithoutExtension(coreProject.FullName)}.{projectsAndConnectionString.DtoFolder}.{schema}.{table}");
            sb.AppendLine("{");
            sb.AppendLine($"    public class {className}");
            sb.AppendLine("    {");

            string codeWithoutSpaces = code.Replace(" ", "");

            foreach (ParametersInfo parameter in parameters)
            {
                if (codeWithoutSpaces.Contains($"{parameter.Name}ISNOTNULL") || codeWithoutSpaces.Contains($"{parameter.Name}ISNULL") || codeWithoutSpaces.Contains($"ISNULL({parameter.Name}") || codeWithoutSpaces.Contains($"COALESCE({parameter.Name}"))
                    parameter.Nullable = true;

                string csharpType = QuickAcationsHelper.SqlTypeToCSharp(parameter.Type, parameter.Nullable);


                if (parameter.Type.Contains("varchar"))
                {
                    if (parameter.Nullable)
                    {
                        sb.AppendLine($"        public {csharpType} {parameter.ReplacedName} {{ get; set; }}");
                    }
                    else
                    {
                        sb.AppendLine($"        public {csharpType} {parameter.ReplacedName} {{ get; set; }} = null!;");
                    }
                }
                else
                {
                    sb.AppendLine($"        public {csharpType} {parameter.ReplacedName} {{ get; set; }}");
                }
            }

            sb.AppendLine();
            sb.AppendLine($"        internal {table}{actionName}Model ToModel()");
            sb.AppendLine("        {");
            sb.AppendLine($"            return new {table}{actionName}Model");
            sb.AppendLine("            {");

            foreach (ParametersInfo parameter in parameters)
            {
                sb.AppendLine($"                {parameter.ReplacedName} = {parameter.ReplacedName},");
            }

            sb.AppendLine("            };");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(filePath, sb.ToString());
            coreProject.ProjectItems.AddFromFile(filePath);
        }

        private void GenerateServiceFile(bool asynchronousGenerate, ProjectsFromSettings projectsAndConnectionString, string schema, string table, StatementTypeEnum resultType, List<ParametersInfo> parameters, bool manyResults, string actionName, bool isSearch, EnvDTE.Project coreProject, EnvDTE.Project dataProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string className = $"{table}Service";
            string fileName = $"{className}.{actionName}.cs";
            string projectDir = System.IO.Path.GetDirectoryName(coreProject.FullName);
            string directoryPath = Path.Combine(projectDir, "Services", schema, table);
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);

            bool existsParamsInObject = ExistParamsForObject(table, parameters);

            StringBuilder sb = new StringBuilder();
            if (resultType == StatementTypeEnum.Select && manyResults)
            {
                if (isSearch)
                {
                    sb.AppendLine("using Mroczware.Core.Common.Models.Search;");
                }
                else
                {
                    sb.AppendLine("using System.Collections.Generic;");
                }
            }
            if(asynchronousGenerate)
                sb.AppendLine("using System.Threading.Tasks;");
            if (resultType == StatementTypeEnum.Select && !manyResults && !isSearch)
                sb.AppendLine("using Mroczware.Framework.Infrastructure.Exceptions;");
            if (existsParamsInObject)
                sb.AppendLine($"using {Path.GetFileNameWithoutExtension(coreProject.FullName)}.{projectsAndConnectionString.DtoFolder}.{schema}.{table};");
            if (resultType == StatementTypeEnum.Select)
                sb.AppendLine($"using {Path.GetFileNameWithoutExtension(dataProject.FullName)}.{projectsAndConnectionString.ModelsFolder}.{schema}.{table};");
            sb.AppendLine();
            sb.AppendLine($"namespace {Path.GetFileNameWithoutExtension(coreProject.FullName)}.Services.{schema}.{table}");
            sb.AppendLine("{");
            sb.AppendLine($"    internal partial class {className}");
            sb.AppendLine("    {");



            string methodParamFragment = "";

            if (parameters.Any())
            {
                if (existsParamsInObject)
                {
                    methodParamFragment = $"{table}{actionName}Dto dto";
                }
                else
                {
                    methodParamFragment = string.Join(",", parameters.Select(p => $"int {ChangeFirstLetterToSmall(p.ReplacedName)}"));
                }
            }

            sb.AppendLine($"        public {(asynchronousGenerate ? "async" : "")} {BuildResponse(asynchronousGenerate, table, resultType, manyResults, actionName, isSearch)} {actionName}Async({methodParamFragment})");

            sb.AppendLine("        {");

            string paramFragment = "";

            if (parameters.Any())
            {
                if (existsParamsInObject)
                {
                    paramFragment = "dto.ToModel()";
                }
                else
                {
                    paramFragment = string.Join(",", parameters.Select(p => ChangeFirstLetterToSmall(p.ReplacedName)));
                }
            }

            if (resultType == StatementTypeEnum.Insert || (resultType == StatementTypeEnum.Select && manyResults && !isSearch))
            {
                sb.AppendLine($"           return {(asynchronousGenerate ? "await" : "")} _{ChangeFirstLetterToSmall(table)}Repository.{actionName}{(asynchronousGenerate ? "Async" : "")}({paramFragment});");
            }
            else if (resultType == StatementTypeEnum.Update)
            {
                sb.AppendLine($"           {(asynchronousGenerate ? "await" : "")} _{ChangeFirstLetterToSmall(table)}Repository.{actionName}{(asynchronousGenerate ? "Async" : "")}({paramFragment});");
            }
            else if (resultType == StatementTypeEnum.Select && !manyResults && !isSearch)
            {
                sb.AppendLine($"           return {(asynchronousGenerate ? "await" : "")} _{ChangeFirstLetterToSmall(table)}Repository.{actionName}{(asynchronousGenerate ? "Async" : "")}({paramFragment}) ?? throw new MroczwareBusinessException($\"Nie znaleziono {table}\");");
            }
            else if (resultType == StatementTypeEnum.Select && manyResults && isSearch)
            {
                sb.AppendLine($"           var result = {(asynchronousGenerate ? "await" : "")} _{ChangeFirstLetterToSmall(table)}Repository.{actionName}{(asynchronousGenerate ? "Async" : "")}({paramFragment});");
                sb.AppendLine($"           return new SearchResult<{table}{actionName}ResponseModel>(result);");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(filePath, sb.ToString());

            coreProject.ProjectItems.AddFromFile(filePath);
        }

        private void GenerateServiceMainFile(string repoFolder, string schema, string table, EnvDTE.Project coreProject, EnvDTE.Project dataProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string className = $"{table}Service";
            string fileName = $"{className}.cs";
            string projectDir = Path.GetDirectoryName(coreProject.FullName);
            string directoryPath = Path.Combine(projectDir, "Services", schema, table);
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);
            if (File.Exists(filePath))
                return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine($"using {Path.GetFileNameWithoutExtension(dataProject.FullName)}.{repoFolder}.{schema}.{table};");
            sb.AppendLine();
            sb.AppendLine($"namespace {Path.GetFileNameWithoutExtension(coreProject.FullName)}.Services.{schema}.{table}");
            sb.AppendLine("{");
            sb.AppendLine($"    internal partial class {className} : I{className}");
            sb.AppendLine("    {");
            sb.AppendLine($"        private readonly I{table}Repository _{ChangeFirstLetterToSmall(table)}Repository;");
            sb.AppendLine();
            sb.AppendLine($"        public {className}(I{table}Repository {ChangeFirstLetterToSmall(table)}Repository)");
            sb.AppendLine("        {");
            sb.AppendLine($"            _{ChangeFirstLetterToSmall(table)}Repository = {ChangeFirstLetterToSmall(table)}Repository;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(filePath, sb.ToString());

            coreProject.ProjectItems.AddFromFile(filePath);
        }

        private static string ChangeFirstLetterToSmall(string value)
        {
            return char.ToLower(value[0]) + value.Substring(1);
        }

        private string BuildRepositoryResponse(bool asynchronousGenerate, string table, StatementTypeEnum resultType, bool manyResults, string actionName)
        {
            switch (resultType)
            {
                case StatementTypeEnum.Select:
                    {
                        if (manyResults)
                        {
                            if (asynchronousGenerate)
                            {
                                return $"Task<IEnumerable<{table}{actionName}ResponseModel>>";
                            }
                            else
                            {
                                return $"IEnumerable<{table}{actionName}ResponseModel>";
                            }
                            
                        }
                        else
                        {
                            if (asynchronousGenerate)
                            {
                                return $"Task<{table}{actionName}ResponseModel?>";
                            }
                            else
                            {
                                return $"{table}{actionName}ResponseModel?";
                            }
                            
                        }
                    }
                case StatementTypeEnum.Delete:
                case StatementTypeEnum.Update:
                    {
                        if (asynchronousGenerate)
                        {
                            return "Task";
                        }
                        else
                        {
                            return "void";
                        }
                    }
                    
                case StatementTypeEnum.Insert:
                    {
                        if (asynchronousGenerate)
                        {
                            return "Task<int>";
                        }
                        else
                        {
                            return "int";
                        }
                    }
                    

                default:
                    throw new ArgumentException($"Unknown action: {actionName}");
            }
        }
        private string BuildRepoMethod(bool asynchronousGenerate, string table, string tableCamelCase,  StatementTypeEnum resultType, bool manyResults, string actionName)
        {
            switch (resultType)
            {
                case StatementTypeEnum.Select:
                    {
                        if (manyResults)
                        {
                            return $"return {(asynchronousGenerate ? "await" : "")} _databaseService.Query{(asynchronousGenerate ? "Async" : "")}<{tableCamelCase}{actionName}ResponseModel>(context);";
                        }
                        else
                        {
                            return $"return ({(asynchronousGenerate ? "await" : "")} _databaseService.Query{(asynchronousGenerate ? "Async" : "")}<{tableCamelCase}{actionName}ResponseModel>(context)).FirstOrDefault();";
                        }
                    }
                case StatementTypeEnum.Update:
                    return $"{(asynchronousGenerate ? "await" : "")} _databaseService.ExecuteNoResult{(asynchronousGenerate ? "Async" : "")}(context);";
                case StatementTypeEnum.Delete:
                    return $"{(asynchronousGenerate ? "await" : "")} _databaseService.ExecuteNoResult{(asynchronousGenerate ? "Async" : "")}(context);";
                case StatementTypeEnum.Insert:
                    return $"return {(asynchronousGenerate ? "await" : "")} _databaseService.GetValue{(asynchronousGenerate ? "Async" : "")}<int>(context);";
                default:
                    throw new ArgumentException($"Unknown action: {actionName}");
            }
        }

        public enum StatementTypeEnum
        {
            Select,
            Update,
            Insert,
            Delete,
            Merge,
            Unknown
        }

        private void GenerateRepositoryMainFile(string repoFolder, string schema, string table, EnvDTE.Project activeDocumentProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string className = $"{table}Repository";
            string fileName = $"{className}.cs";
            string projectDir = Path.GetDirectoryName(activeDocumentProject.FullName);
            string directoryPath = Path.Combine(projectDir, repoFolder, schema, table);
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);
            if (File.Exists(filePath))
                return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using Mroczware.DataBase.Services;");
            sb.AppendLine($"using {activeDocumentProject.Name}.{repoFolder}.{schema}.{table};");
            sb.AppendLine();
            sb.AppendLine($"namespace {activeDocumentProject.Name}.{repoFolder}.{schema}.{table}");
            sb.AppendLine("{");
            sb.AppendLine($"    internal partial class {className} : I{className}");
            sb.AppendLine("    {");
            sb.AppendLine($"        private readonly IDatabaseService _databaseService;");
            sb.AppendLine();
            sb.AppendLine($"        public {className}(IDatabaseService databaseService)");
            sb.AppendLine("        {");
            sb.AppendLine($"            _databaseService = databaseService;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(filePath, sb.ToString());

            activeDocumentProject.ProjectItems.AddFromFile(filePath);
        }

        public class SelectColumnInfo
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public bool Nullable { get; set; }

            public SelectColumnInfo(string name, string type, bool nullable)
            {
                Name = name;
                Type = type;
                Nullable = nullable;
            }
        }

        public class ParametersInfo
        {
            public string Name { get; set; }
            public string ReplacedName => Name.Replace("@", "").Replace("_", "");
            public string Type { get; set; }
            public bool Nullable { get; set; }

            public ParametersInfo(string name, string type, bool nullable = false)
            {
                Name = name;
                Type = type;
                Nullable = nullable;
            }
        }
    }
}
