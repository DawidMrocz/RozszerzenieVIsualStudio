using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Threading;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using Mroczware.PowerFullTool.Helpers;
using Mroczware.PowerFullTool.Models;
using Mroczware.PowerFullTool.UI.View;
using static Mroczware.PowerFullTool.GenerateRepoFromSql;
using MroczwareTools;


namespace Mroczware.PowerFullTool
{
    public class GenerateResponseFromSqlResponse
    {
        public int TabIndex { get; set; }
        public string SqlQuery { get; set; }
        public string Schema { get; set; }
        public string Name { get; set; }
        public string ClassName { get; set; }
        public string Type { get; set; }
        
    }

    [Command(PackageIds.GenerateResponseFromSql)]
    internal sealed class GenerateResponseFromSql : BaseCommand<GenerateResponseFromSql>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                DTE2 dte = await ServiceProvider.GetGlobalServiceAsync(typeof(DTE)) as DTE2;

                UIHierarchy hierarchy = dte.ToolWindows.SolutionExplorer;

                Array selectedItems = hierarchy.SelectedItems as Array;

                if (selectedItems is null)
                    System.Windows.MessageBox.Show("Nie zaznaczono projektu.");

                if (selectedItems.Length != 1)
                    System.Windows.MessageBox.Show("Można zanzaczyć tylko jeden projekt.");

                UIHierarchyItem selectedItem = (UIHierarchyItem)selectedItems.GetValue(0);
                ProjectItem projectItem = selectedItem.Object as ProjectItem;

                if (projectItem is null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage($"Zaznaczony element nie jest folderem.");
                    return;
                }

                if (projectItem.Kind != EnvDTE.Constants.vsProjectItemKindPhysicalFolder)
                {
                    QuickAcationsHelper.ShowStatusBarMessage($"Zaznaczony element nie jest folderem.");
                    return;
                }

                string folderPath = projectItem.FileNames[1];
                string folderName = projectItem.Name;
                EnvDTE.Project containingProject = projectItem.ContainingProject;

                if (containingProject is null)
                    System.Windows.MessageBox.Show("Zaznaczony obiekt nie ma rozszerzenia .csproj.");

                string projectName = containingProject.Name;

                var connectionString = QuickAcationsHelper.GetConnectionString(dte, containingProject);

                if (string.IsNullOrWhiteSpace(connectionString)) return;

                if (dte.Solution == null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Brak solucji.");
                    return;
                }

                QuickAcationsHelper.ShowStatusBarMessage($"Próba połączenia z bazą danych... (Sprawdź VPN lub czy został ustawiony ConnectionString)");

                List<SelectItem> schemesAndObjects = QuickAcationsHelper.GetItemsToGenerateResponseFromSql(connectionString);

                var generateResponseFromSqlWindow = new GenerateResponseFromSqlWindow(schemesAndObjects);
                generateResponseFromSqlWindow.ShowDialog();

                if (
                       generateResponseFromSqlWindow is null || generateResponseFromSqlWindow.Response is null || string.IsNullOrWhiteSpace(generateResponseFromSqlWindow.Response.ClassName)
                       || (generateResponseFromSqlWindow.Response.TabIndex == 0 && string.IsNullOrWhiteSpace(generateResponseFromSqlWindow.Response.SqlQuery))
                       || (generateResponseFromSqlWindow.Response.TabIndex == 1 && (string.IsNullOrWhiteSpace(generateResponseFromSqlWindow.Response.Schema) || string.IsNullOrWhiteSpace(generateResponseFromSqlWindow.Response.Name) || string.IsNullOrWhiteSpace(generateResponseFromSqlWindow.Response.Type))))
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Uzupełnij dane");
                    return;
                }

                List<SelectColumnInfo> selectColumnInfoList = [];

                if (generateResponseFromSqlWindow.Response.TabIndex == 0)
                {
                    string originalQuery = generateResponseFromSqlWindow.Response.SqlQuery;

                    generateResponseFromSqlWindow.Response.SqlQuery = generateResponseFromSqlWindow.Response.SqlQuery.Replace("'", "''");
                    generateResponseFromSqlWindow.Response.SqlQuery = RemoveComments(generateResponseFromSqlWindow.Response.SqlQuery);
                    generateResponseFromSqlWindow.Response.SqlQuery = generateResponseFromSqlWindow.Response.SqlQuery.Normalize(NormalizationForm.FormC);

                    using (SqlConnection connection = new(connectionString))
                    {
                        string sqlQuery = $"EXEC sp_describe_first_result_set @tsql = N'{generateResponseFromSqlWindow.Response.SqlQuery}'";
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
                }
                else
                {
                    using (SqlConnection connection = new(connectionString))
                    {
                        string sqlQuery;

                        if (generateResponseFromSqlWindow.Response.Type == "Funkcja tabelaryczna")
                        {
                            var parameters = QuickAcationsHelper.GetTableFunctionParameters(connectionString, generateResponseFromSqlWindow.Response.Schema, generateResponseFromSqlWindow.Response.Name);

                            StringBuilder sb = new();
                            foreach (var paramter in parameters)
                                sb.Append($",{paramter.Name}");

                            string result = sb.ToString().TrimStart(',');

                            sqlQuery = $"EXEC sp_describe_first_result_set @tsql = N'SELECT * FROM [{generateResponseFromSqlWindow.Response.Schema}].[{generateResponseFromSqlWindow.Response.Name}]({result})'";

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
                        else if (generateResponseFromSqlWindow.Response.Type == "Procedura")
                        {
                            sqlQuery = $"EXEC sp_describe_first_result_set @tsql = N'EXEC [{generateResponseFromSqlWindow.Response.Schema}].[{generateResponseFromSqlWindow.Response.Name}]'";

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
                        else
                        {
                            var columns = QuickAcationsHelper.GetColumnsFromDb(generateResponseFromSqlWindow.Response.Name, generateResponseFromSqlWindow.Response.Schema, connectionString);

                            selectColumnInfoList = columns.Select(c => new SelectColumnInfo(c.ColumnName, c.DataType, c.IsNullable)).ToList();
                        }              
                    }            
                }

                GenerateResponseModelFile(folderPath, folderName, selectColumnInfoList, containingProject, generateResponseFromSqlWindow.Response.ClassName);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Wystąpił błąd podczas wykonywania polecenia 😞 : {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GenerateResponseModelFile(string folderPath, string folderName, List<SelectColumnInfo> selectColumnInfoList, EnvDTE.Project activeDocumentProject, string inputClassName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string className = $"{inputClassName}";
            string fileName = $"{className}.cs";
            string filePath = Path.Combine(folderPath, fileName);

            StringBuilder sb = new StringBuilder();

            string[] systemTypes = ["date", "datetime", "datetime2", "smalldatetime", "time", "uniqueidentifier"];
            if (selectColumnInfoList.Any(p => systemTypes.Contains(p.Type.ToLower())))
                sb.AppendLine("using System;");

            if (selectColumnInfoList.Select(c => c.Name).Contains("TotalRows"))
                sb.AppendLine("using Mroczware.Core.Common.Models.Search;");

            sb.AppendLine();
            sb.AppendLine($"namespace {activeDocumentProject.Name}.{folderName}.{inputClassName}");
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

        string RemoveComments(string sql)
        {
            sql = Regex.Replace(sql, @"/\*.*?\*/", "", RegexOptions.Singleline);
            sql = Regex.Replace(sql, @"--.*?$", "", RegexOptions.Multiline);
            return sql;
        }

    }
}
