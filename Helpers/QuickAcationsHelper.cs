using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UI.View;
using Mroczware.PowerFullTool.Commands;
using Mroczware.PowerFullTool.Models;

namespace Mroczware.PowerFullTool.Helpers
{
    public static class EnumerableExtensions
    {
        public static bool IsLast<T>(this IEnumerable<T> source, T element)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (element == null) throw new ArgumentNullException(nameof(element));

            using var enumerator = source.GetEnumerator();
            T previous = default;

            while (enumerator.MoveNext())
            {
                if (Equals(enumerator.Current, element))
                {
                    // Sprawdź, czy jeszcze jakiś element istnieje
                    return !enumerator.MoveNext();
                }
            }

            return false;
        }
    }


    public static class QuickAcationsHelper
    {
        public static ProjectsFromSettings GetProjectsAndConnectionString(DTE2 dte, EnvDTE.Project activeDocumentProject)
        {
            VsixSettingsManager.ClearSettingsIfProjectsNamesAreDifferent(activeDocumentProject);

            ThreadHelper.ThrowIfNotOnUIThread();

            ProjectsFromSettings response = new();

            IEnumerable<EnvDTE.Project> projects = GetAllProjects(dte.Solution);

            if ((activeDocumentProject.Name.EndsWith(".Api") || activeDocumentProject.Name.EndsWith(".Core") || activeDocumentProject.Name.EndsWith(".Data")) && projects.Count() > 1)
            {
                string projectNameWithoutExtension = RemoveSuffix(activeDocumentProject);

                EnvDTE.Project apiProject = projects.FirstOrDefault(a => a.Name.Equals($"{projectNameWithoutExtension}.Api", StringComparison.OrdinalIgnoreCase));
                EnvDTE.Project coreProject = projects.FirstOrDefault(a => a.Name.Equals($"{projectNameWithoutExtension}.Core", StringComparison.OrdinalIgnoreCase));
                EnvDTE.Project dataProject = projects.FirstOrDefault(a => a.Name.Equals($"{projectNameWithoutExtension}.Data", StringComparison.OrdinalIgnoreCase));

                response.ProjectApi = apiProject;
                response.ApiModelsFolder = FindFolder(apiProject?.ProjectItems, "ApiModels")?.Name;
                response.ProjectCore = coreProject;
                response.DtoFolder = FindFolder(coreProject?.ProjectItems, "Dto")?.Name;
                response.ProjectData = dataProject;
                response.ModelsFolder = FindFolder(dataProject?.ProjectItems, "Models")?.Name;
                response.ConnectionString = FindConnectionStringInProject(apiProject);
            }
            else
            {
                response.ProjectApi = activeDocumentProject;
                response.ApiModelsFolder = FindFolder(activeDocumentProject.ProjectItems, "ApiModels")?.Name;
                response.ProjectCore = activeDocumentProject;
                response.DtoFolder = FindFolder(activeDocumentProject.ProjectItems, "Dto")?.Name;
                response.ProjectData = activeDocumentProject;
                response.ModelsFolder = FindFolder(activeDocumentProject.ProjectItems, "Models")?.Name;
                response.ConnectionString = FindConnectionStringInProject(activeDocumentProject);
            }

            if (
                response.ProjectApi is null
                || response.ApiModelsFolder is null
                || response.ProjectCore is null
                || response.DtoFolder is null
                || response.ProjectData is null
                || response.ModelsFolder is null
                || response.ConnectionString is null)
            {
                var configureProjectsWindow = new ConfigureProjectsWindow(dte, response);
                configureProjectsWindow.ShowDialog();

                ConfigureProjectsWindowInfo resp = configureProjectsWindow.ConfigureProjectsWindowInfo;

                if (resp is null) return null;

                response.ProjectApi = resp.ApiProject;
                response.ApiModelsFolder = resp.ApiModelsFolder;
                response.ProjectCore = resp.CoreProject;
                response.DtoFolder = resp.DtoFolder;
                response.ProjectData = resp.DataProject;
                response.ModelsFolder = resp.ModelsFolder;
                response.ProjectData = resp.DataProject;
                response.ConnectionString = resp.ConnectionString;
            }

            if (response.ProjectApi is null || response.ProjectCore is null || response.ProjectData is null)
                throw new Exception("Nie udało się ustalić projektów");

            if (response.ApiModelsFolder is null || response.DtoFolder is null || response.ModelsFolder is null)
                throw new Exception("Nie udało się ustalić folderów");

            if (response.ConnectionString is null)
                throw new Exception("Nie udało się ustalić connection stringa");

            var projectPaths = string.Join(",", response.ProjectApi.FullName, response.ProjectCore.FullName, response.ProjectData.FullName);

            VsixSettingsManager.SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ProjectPaths, projectPaths);

            return response;
        }

        public static string GetConnectionString(DTE2 dte, EnvDTE.Project activeDocumentProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ProjectsFromSettings response = new();

            string connectionString = null;

            var projects = GetAllProjects(dte.Solution);

            if ((activeDocumentProject.Name.EndsWith(".Api") || activeDocumentProject.Name.EndsWith(".Core") || activeDocumentProject.Name.EndsWith(".Data")) && projects.Count() > 1)
            {
                string projectNameWithoutExtension = RemoveSuffix(activeDocumentProject);
                EnvDTE.Project apiProject = projects.FirstOrDefault(a => a.Name.Equals($"{projectNameWithoutExtension}.Api", StringComparison.OrdinalIgnoreCase));
                connectionString = FindConnectionStringInProject(apiProject);
            }
            else
            {
                connectionString = FindConnectionStringInProject(activeDocumentProject);
            }

            if (
                string.IsNullOrWhiteSpace(connectionString))
            {
                var selectConnectionStringWindow = new SelectConnectionStringWindow();
                selectConnectionStringWindow.ShowDialog();

                if (string.IsNullOrWhiteSpace(selectConnectionStringWindow?.ConnectionString)) return null;

                connectionString = selectConnectionStringWindow?.ConnectionString;

            }

            if (string.IsNullOrWhiteSpace(connectionString))
                throw new Exception("Nie udało się ustalić connection stringa");

            return connectionString;
        }

        public static string RemoveSuffix(EnvDTE.Project activeDocumentProject)
        {
            string projectNameWithoutExtension = Path.GetFileNameWithoutExtension(activeDocumentProject.FullName);

            string[] suffixesToRemove = [".Api", ".Core", ".Data"];

            foreach (string suffix in suffixesToRemove)
            {
                if (projectNameWithoutExtension.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    projectNameWithoutExtension = projectNameWithoutExtension.Substring(0, projectNameWithoutExtension.Length - suffix.Length);
                    break;
                }
            }

            return projectNameWithoutExtension;
        }

        public static EnvDTE.Project GetContainingProjectName(EnvDTE.Document activeDocument)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ProjectItem projectItem = activeDocument.ProjectItem;
            if (projectItem is null) return null;
            return projectItem.ContainingProject;
        }

        public static IEnumerable<EnvDTE.Project> GetAllProjects(EnvDTE.Solution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (EnvDTE.Project project in solution.Projects)
            {
                //if (project == null) continue;

                if (project.Kind == ProjectKinds.vsProjectKindSolutionFolder)
                {
                    // Zagnieżdżone projekty w solution folderze
                    var subProjects = project.ProjectItems;
                    for (int i = 1; i <= subProjects.Count; i++)
                    {
                        var subProject = subProjects.Item(i).SubProject;
                        if (subProject != null)
                        {
                            foreach (var nested in GetProjectsRecursive(subProject))
                            {
                                //if (!string.IsNullOrWhiteSpace(project.FullName))
                                yield return nested;
                            }
                        }
                    }
                }
                else
                {
                    //if (!string.IsNullOrWhiteSpace(project.FullName))
                    yield return project;

                }
            }


            //foreach (EnvDTE.Project project in solution.Projects)
            //    if (!string.IsNullOrWhiteSpace(project.FullName))
            //        foreach (var nested in GetProjectsRecursive(project))
            //            yield return nested;
        }

        public static ProjectItem FindFolder(ProjectItems items, string folderName)
        {
            if (items is null)
                return null;

            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (ProjectItem item in items)
                if (item.Kind == EnvDTE.Constants.vsProjectItemKindPhysicalFolder && string.Equals(item.Name, folderName, StringComparison.OrdinalIgnoreCase))
                    return item;

            return null;
        }

        public static IEnumerable<EnvDTE.Project> GetProjectsRecursive(EnvDTE.Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project == null)
                yield break;

            if (project.Kind == ProjectKinds.vsProjectKindSolutionFolder && project.ProjectItems != null)
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    EnvDTE.Project subProject = item.SubProject;
                    if (subProject != null)
                    {
                        foreach (var nested in GetProjectsRecursive(subProject))
                        {
                            //if (!string.IsNullOrWhiteSpace(project.FullName))
                            yield return nested;
                        }

                    }
                }
            }
            else
            {
                //if (!string.IsNullOrWhiteSpace(project.FullName))
                yield return project;
            }
        }

        public static string? FindConnectionStrings(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string connectionStringToReturn = null;

            foreach (EnvDTE.Project project in dte.Solution.Projects)
            {
                if (string.IsNullOrWhiteSpace(project.FullName)) continue;

                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.Name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
                    {
                        string fullPath = item.FileNames[1];
                        var json = File.ReadAllText(fullPath);
                        var jObject = JObject.Parse(json);
                        var connectionString = jObject.SelectToken("ConnectionStrings.DefaultConnection")?.ToString();
                        if (!string.IsNullOrWhiteSpace(connectionString))
                        {
                            connectionStringToReturn = connectionString;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(connectionStringToReturn))
                    return connectionStringToReturn;

                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.Name.Equals("web.config", StringComparison.OrdinalIgnoreCase))
                    {
                        string fullPath = item.FileNames[1];
                        XDocument configXml = XDocument.Load(fullPath);

                        string? connStr = configXml
                            .Root?
                            .Element("connectionStrings")?
                            .Elements("add")
                            .FirstOrDefault(e => (string)e.Attribute("name") == "DefaultConnection")
                            ?.Attribute("connectionString")
                            ?.Value;

                        connectionStringToReturn = connStr;

                        break;
                    }
                }
            }

            return connectionStringToReturn;
        }

        public static string MapToSqlType(string typeName)
        {
            switch (typeName.ToLowerInvariant())
            {
                case "bit": return "BIT";
                case "datetime": return "DATETIME";
                case "smalldatetime": return "SMALLDATETIME";
                case "date": return "DATE";
                case "time": return "TIME";
                case "int": return "INT";
                case "bigint": return "BIGINT";
                case "smallint": return "SMALLINT";
                case "tinyint": return "TINYINT";
                case "nvarchar": return "NVARCHAR(MAX)";
                case "varchar": return "VARCHAR(MAX)";
                case "nchar": return "NCHAR";
                case "char": return "CHAR";
                case "text": return "TEXT";
                case "ntext": return "NTEXT";
                case "decimal": return "DECIMAL";
                case "numeric": return "NUMERIC";
                case "money": return "MONEY";
                case "smallmoney": return "SMALLMONEY";
                case "float": return "FLOAT";
                case "real": return "REAL";
                case "uniqueidentifier": return "UNIQUEIDENTIFIER";
                case "xml": return "XML";
                case "binary": return "BINARY";
                case "varbinary": return "VARBINARY(MAX)";
                case "image": return "IMAGE";
                case "timestamp": return "ROWVERSION"; // alias
                default: return "SQL_VARIANT"; // fallback dla nieznanych
            }
        }

        public static List<ColumnInfo> GetColumnsFromDb(string table, string schema, string connectionString)
        {
            var columns = new List<ColumnInfo>();

            using (SqlConnection connection = new(connectionString))
            {
                connection.Open();
                using (SqlCommand command = new($@"
SELECT 
    COLUMN_NAME, 
    DATA_TYPE, 
    IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE 
    LOWER(TABLE_SCHEMA) = LOWER(@tableSchema)
    AND LOWER(TABLE_NAME) = LOWER(@tableName)
    AND COLUMN_NAME NOT IN ('Id','CreateDate','CreateUser_Id','ModifyDate','ModifyUser_Id','IsActive')", connection))
                {
                    command.Parameters.AddWithValue("@tableName", table);
                    command.Parameters.AddWithValue("@tableSchema", schema);

                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string columnName = reader.GetString(0);
                            string dataType = reader.GetString(1);
                            bool isNullable = reader.GetString(2) == "YES";

                            ColumnInfo columnInfo = new(columnName, dataType, isNullable);

                            columns.Add(columnInfo);
                        }
                    }
                }
            }

            return columns.Distinct().ToList();
        }

        public static string? FindConnectionStringInProject(EnvDTE.Project project)
        {
            if (project is null) return null;

            ThreadHelper.ThrowIfNotOnUIThread();
            string connectionStringToReturn = null;
            foreach (ProjectItem item in project.ProjectItems)
            {
                if (item.Name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
                {
                    string fullPath = item.FileNames[1];
                    var json = File.ReadAllText(fullPath);
                    var jObject = JObject.Parse(json);
                    var connectionString = jObject.SelectToken("ConnectionStrings.DefaultConnection")?.ToString();
                    if (!string.IsNullOrWhiteSpace(connectionString))
                    {
                        connectionStringToReturn = connectionString;
                        break;
                    }

                }
            }

            if (!string.IsNullOrWhiteSpace(connectionStringToReturn))
                return connectionStringToReturn;

            foreach (ProjectItem item in project.ProjectItems)
            {
                if (item.Name.Equals("web.config", StringComparison.OrdinalIgnoreCase))
                {
                    string fullPath = item.FileNames[1];
                    XDocument configXml = XDocument.Load(fullPath);

                    string? connStr = configXml
                        .Root?
                        .Element("connectionStrings")?
                        .Elements("add")
                        .FirstOrDefault(e => (string)e.Attribute("name") == "DefaultConnection")
                        ?.Attribute("connectionString")
                        ?.Value;

                    connectionStringToReturn = connStr;

                    break;
                }
            }

            return connectionStringToReturn;
        }

        public static string GetInitials(string tableName)
        {
            MatchCollection matches = Regex.Matches(tableName, @"[A-Z]");
            string initials = string.Empty;
            foreach (Match match in matches)
                initials += match.Value;

            return initials;
        }

        public static List<TableModel> GetSchemasAndTables(string connectionString)
        {
            var tables = new List<TableModel>();

            using (var sqlConnection = new SqlConnection(connectionString))
            {
                sqlConnection.Open();

                string query = @"
                                    SELECT TABLE_SCHEMA, TABLE_NAME 
                                    FROM INFORMATION_SCHEMA.TABLES 
                                    WHERE TABLE_TYPE = 'BASE TABLE'";

                using (var cmd = new SqlCommand(query, sqlConnection))

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string schema = reader.GetString(0);
                        string table = reader.GetString(1);
                        tables.Add(new TableModel(schema, table));
                    }
                }
            }
            return tables;
        }

        public static List<ParameterItem> GetTableFunctionParameters(string connectionString, string schema, string name)
        {
            var selectItems = new List<ParameterItem>();

            using (var sqlConnection = new SqlConnection(connectionString))
            {
                sqlConnection.Open();

                string query = @"SELECT 
    [p].[name], 
    [t].[name] AS [type_name]
FROM [sys].[parameters] AS [p]
JOIN [sys].[types] AS [t] 
    ON [p].[user_type_id] = [t].[user_type_id]
WHERE [p].[object_id] = OBJECT_ID(@fullName);";

                using (var cmd = new SqlCommand(query, sqlConnection))
                {
                    cmd.Parameters.AddWithValue("@fullName", $"[{schema}].[{name}]");

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string parameterName = reader.GetString(0);
                            string parameterType = reader.GetString(1);
                            selectItems.Add(new ParameterItem(parameterName, parameterType));
                        }
                    }
                }
            }
            return selectItems;
        }

        public static List<SelectItem> GetItemsToGenerateResponseFromSql(string connectionString)
        {
            var selectItems = new List<SelectItem>();

            using (var sqlConnection = new SqlConnection(connectionString))
            {
                sqlConnection.Open();

                string query = @"
                                    SELECT 
    [s].[name] AS [Schema],
    [o].[name] AS [ObjectName],
    CASE 
        WHEN [o].[type] = 'U'  THEN 'Tabela'
        WHEN [o].[type] IN ('TF', 'IF') THEN 'Funkcja tabelaryczna'
        WHEN [o].[type] = 'P'  THEN 'Procedura'
        ELSE [o].[type_desc]
    END AS [ObjectType]
FROM [sys].[objects] AS [o]
INNER JOIN [sys].[schemas] AS [s] ON [o].[schema_id] = [s].[schema_id]
WHERE 
    [o].[type] IN ('U', 'P', 'TF', 'IF')
ORDER BY 
    [Schema], [ObjectType], [ObjectName];";

                using (var cmd = new SqlCommand(query, sqlConnection))

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string schema = reader.GetString(0);
                        string objectName = reader.GetString(1);
                        string objectType = reader.GetString(2);
                        selectItems.Add(new SelectItem(schema, objectName, objectType));
                    }
                }
            }
            return selectItems;
        }

        public static List<ReferencedTableModel> GetReferencedTables(string connectionString, string schema, string table)
        {
            var referencedTables = new List<ReferencedTableModel>();

            using (SqlConnection connection = new(connectionString))
            {
                connection.Open();
                using (SqlCommand command = new(@"SELECT 
    [cp].[name] AS [ParentColumn],
    [cp].[is_nullable] AS [ParentColumnIsNullable],
    [s2].[name] AS [ReferencedSchema],
    [tr].[name] AS [ReferencedTable],
    [cr].[name] AS [ReferencedColumn]
FROM 
    [sys].[foreign_keys] AS [fk]
INNER JOIN 
    [sys].[foreign_key_columns] AS [fkc] ON [fkc].[constraint_object_id] = [fk].[object_id]
INNER JOIN 
    [sys].[tables] AS [tp] ON [fk].[parent_object_id] = [tp].[object_id]
INNER JOIN 
    [sys].[schemas] AS [s1] ON [tp].[schema_id] = [s1].[schema_id]
INNER JOIN 
    [sys].[columns] AS [cp] ON [fkc].[parent_column_id] = [cp].[column_id] AND [cp].[object_id] = [tp].[object_id]
INNER JOIN 
    [sys].[tables] AS [tr] ON [fk].[referenced_object_id] = [tr].[object_id]
INNER JOIN 
    [sys].[schemas] AS [s2] ON [tr].[schema_id] = [s2].[schema_id]
INNER JOIN 
    [sys].[columns] AS [cr] ON [fkc].[referenced_column_id] = [cr].[column_id] AND [cr].[object_id] = [tr].[object_id]
WHERE 
    LOWER([tp].[name]) = LOWER(@tableName)
    AND LOWER([s1].[name]) = LOWER(@tableSchema)
ORDER BY 
    [fk].[name];", connection))
                {
                    command.Parameters.AddWithValue("@tableName", table);
                    command.Parameters.AddWithValue("@tableSchema", schema);

                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string parentColumn = reader.GetString(0);
                            bool parentColumnIsNullable = reader.GetBoolean(1);
                            string referencedSchema = reader.GetString(2);
                            string referencedTable = reader.GetString(3);
                            string referencedColumn = reader.GetString(4);

                            ReferencedTableModel referencedTableModel = new(parentColumn, parentColumnIsNullable, referencedSchema, referencedTable, referencedColumn);

                            referencedTables.Add(referencedTableModel);
                        }
                    }
                }
            }
            return referencedTables;
        }

        public static string ReturnIDependencyUsing(EnvDTE.Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var projectPath = project.FullName;

            XDocument xmlDoc = XDocument.Load(projectPath);
            XElement targetFrameworkElement = xmlDoc.Descendants("TargetFramework").FirstOrDefault();
            string targetFramework = targetFrameworkElement?.Value;

            return targetFramework switch
            {
                "net8.0" or "net8.0-windows" => "Mroczware.Framework.Infrastructure.Interfaces.DependencyInjection",
                "net6.0" or "net6.0-windows" => "Mroczware.Framework.Infrastructure.Interfaces",
                _ => "Mroczware.Framework",
            };
        }

        public static void ShowStatusBarMessage(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            IVsStatusbar statusBar = (IVsStatusbar)ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar));
            if (statusBar == null) return;

            statusBar.IsFrozen(out int frozen);
            if (frozen != 0)
                statusBar.FreezeOutput(0);

            statusBar.SetText($"Mroczware TOOL INFO: {message}");
        }

        public static string SqlTypeToCSharp(string sqlType, bool isNullable)
        {
            string csharpType;
            sqlType = Regex.Replace(sqlType, @"\([^\)]*\)", "");
            switch (sqlType.ToLower())
            {
                case "tinyint":
                    csharpType = "byte";
                    break;
                case "smallint":
                    csharpType = "short";
                    break;
                case "int":
                    csharpType = "int";
                    break;
                case "bigint":
                    csharpType = "long";
                    break;
                case "decimal":
                case "numeric":
                case "money":
                case "smallmoney":
                    csharpType = "decimal";
                    break;
                case "float":
                    csharpType = "double";
                    break;
                case "real":
                    csharpType = "float";
                    break;
                case "bit":
                    csharpType = "bool";
                    break;
                case "char":
                case "varchar":
                case "text":
                case "nchar":
                case "nvarchar":
                case "ntext":
                case "xml":
                    csharpType = "string";
                    break;
                case "date":
                case "datetime":
                case "datetime2":
                case "smalldatetime":
                    csharpType = "DateTime";
                    break;
                case "time":
                    csharpType = "TimeSpan";
                    break;
                case "binary":
                case "varbinary":
                case "image":
                case "timestamp":
                    csharpType = "byte[]";
                    break;
                case "uniqueidentifier":
                    csharpType = "Guid";
                    break;
                case "sql_variant":
                case "geography":
                case "geometry":
                case "hierarchyid":
                    csharpType = "object";
                    break;
                default:
                    csharpType = "object";
                    break;
            }

            if (isNullable)
                csharpType += "?";

            return csharpType;
        }
    }
}
