using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Threading;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text;
using System.Windows;
using System.Xml;
using System.Xml.Linq;
using Mroczware.PowerFullTool.Helpers;
using Mroczware.PowerFullTool.Models;
using Mroczware.PowerFullTool.UI.View;
using MroczwareTools;

namespace Mroczware.PowerFullTool
{
    public enum ActionTypeEnum
    {
        Get,
        GetList,
        Search,
        Create,
        Update,
        Delete,
        Merge,
        BulkCreate
    }
    public class CrudSelectionInfo
    {
        public string CustomName { get; set; }
        public bool CopyToClipboard { get; set; }
        public List<ActionTypeEnum> SelectedActions = new();
        public List<ReferencedTableModel> SelectedReferencedTables { get; set; } = [];
    }

    [Command(PackageIds.GenerateSql)]
    internal sealed class GenerateSql : BaseCommand<GenerateSql>
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

                EnvDTE.Project dataProject = selectedItem.Object as EnvDTE.Project;

                if (dataProject is null)
                    System.Windows.MessageBox.Show("Zaznaczony obiekt nie ma rozszerzenia .csproj.");

                string projectName = dataProject.Name;

                //if (!projectName.EndsWith(".Data", StringComparison.OrdinalIgnoreCase))
                //{
                //    var result = System.Windows.MessageBox.Show(
                //        $"Projekt '{projectName}' nie kończy się suffixem '.Data'. Czy na pewno chcesz wygenerować SQL w tym projekcie?",
                //        "Potwierdzenie",
                //        MessageBoxButton.YesNo,
                //        MessageBoxImage.Warning
                //    );

                //    if (result != MessageBoxResult.Yes)
                //        return; // użytkownik anulowa
                //}

                var connectionString = QuickAcationsHelper.GetConnectionString(dte, dataProject);

                if (string.IsNullOrWhiteSpace(connectionString)) return;

                if (dte.Solution == null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Brak solucji.");
                    return;
                }

                QuickAcationsHelper.ShowStatusBarMessage($"Próba połączenia z bazą danych... (Sprawdź VPN lub czy został ustawiony ConnectionString)");

                List<TableModel> schemesAndTables = QuickAcationsHelper.GetSchemasAndTables(connectionString);

                Table tableSelectWindow = new(schemesAndTables);

                await tableSelectWindow.ShowDialogAsync();

                if (
                    tableSelectWindow is null
                    || tableSelectWindow.SelectedTable is null
                    || string.IsNullOrWhiteSpace(tableSelectWindow.SelectedTable.Key)
                    || string.IsNullOrWhiteSpace(tableSelectWindow.SelectedTable.Value))
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Nie wybrano żadnego settings'a z bazy danych");
                    return;
                }

                List<ColumnInfo> columnsFromDb = QuickAcationsHelper.GetColumnsFromDb(tableSelectWindow.SelectedTable.Value, tableSelectWindow.SelectedTable.Key, connectionString);

                if (columnsFromDb is null || !columnsFromDb.Any())
                {
                    QuickAcationsHelper.ShowStatusBarMessage($"Nie znaleziono obiektu w bazie: [{tableSelectWindow.SelectedTable.Value}].[{tableSelectWindow.SelectedTable.Key}]");
                    return;
                }

                QuickAcationsHelper.ShowStatusBarMessage($"Pomyślnie połączono z bazą danych");

                CrudSelectionInfo resp = ConfigureEdpoint(tableSelectWindow.SelectedTable.CapitalizedSchema, tableSelectWindow.SelectedTable.Value, connectionString);

                if (resp is null) return;

                ActionTypeEnum? actionName = resp.SelectedActions.FirstOrDefault();

                if (actionName is null) return;

                foreach (ReferencedTableModel referencedTable in resp.SelectedReferencedTables)
                {
                    var columns = QuickAcationsHelper.GetColumnsFromDb(referencedTable.ReferencedTable, referencedTable.ReferencedSchema, connectionString);
                    referencedTable.Columns.AddRange(columns);
                }

                string sqlFilePath = GenerateSqlFile(columnsFromDb, tableSelectWindow.SelectedTable, actionName.Value, resp.CustomName, dataProject, resp.SelectedReferencedTables, resp.CopyToClipboard);

                if (resp.CopyToClipboard == true)
                {
                    Clipboard.SetText(sqlFilePath);
                    QuickAcationsHelper.ShowStatusBarMessage($"Zapytanie SQL jest w schowku");
                }
                else
                {
                    
                    if (!string.IsNullOrWhiteSpace(sqlFilePath))
                        GenerateRESXFile(tableSelectWindow.SelectedTable, sqlFilePath, dataProject);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Wystąpił błąd podczas wykonywania polecenia 😞 : {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private CrudSelectionInfo ConfigureEdpoint(string schema, string table, string connectionString)
        {
            GenerateSqlWindow configurationCrud = new(schema, table, connectionString);
            configurationCrud.ShowDialog();
            CrudSelectionInfo resp = configurationCrud.SelectedCrudInfo;

            if (resp is null) return null;

            if (!resp.SelectedActions.Any())
            {
                System.Windows.Forms.MessageBox.Show("Nie wybrano żadnej akcji");
                return null;
            }

            if (string.IsNullOrWhiteSpace(resp.CustomName) && !resp.CopyToClipboard)
            {
                System.Windows.Forms.MessageBox.Show("Nie wpisano nazwy akcji");
                return null;
            }

            return configurationCrud.SelectedCrudInfo;
        }

        private string GenerateSqlFile(List<ColumnInfo> columns, TableModel TableModel, ActionTypeEnum action, string actionName, EnvDTE.Project dataProject, List<ReferencedTableModel> referencedTables, bool copyToClipboard)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string projectDir = Path.GetDirectoryName(dataProject.FullName);
            string directoryPath = Path.Combine(projectDir, "Resources", TableModel.CapitalizedSchema, TableModel.Value.Replace("_", ""));
            string fileName = $"{TableModel.CapitalizedSchema}_{TableModel.Value}_{actionName ?? action.ToString()}.sql";
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);

            if (File.Exists(filePath))
                return null;

            StringBuilder sb = new StringBuilder();

            switch (action)
            {
                case ActionTypeEnum.Create:
                    CreateCreateSQL(columns, TableModel, sb);
                    break;
                case ActionTypeEnum.Update:
                    CreateUpdateSQL(columns, TableModel, sb);
                    break;
                case ActionTypeEnum.Delete:
                    CreateDeleteSQL(columns, TableModel, sb);
                    break;
                case ActionTypeEnum.Get:
                    CreateGetSQL(columns, TableModel, referencedTables, sb);
                    break;
                case ActionTypeEnum.GetList:
                    CreateGetListSQL(columns, TableModel, referencedTables, sb);
                    break;
                case ActionTypeEnum.Search:
                    CreateSearchSQL(columns, TableModel, referencedTables, sb);
                    break;
                case ActionTypeEnum.Merge:
                    CreateMergeSQL(columns, TableModel, sb);
                    break;
                case ActionTypeEnum.BulkCreate:
                    CreateBulkCreateSQL(columns, TableModel, sb);
                    break;

            }

            if (copyToClipboard)
            {
                return sb.ToString();
            }
            else
            {
                File.WriteAllText(filePath, sb.ToString());
                dataProject.ProjectItems.AddFromFile(filePath);
                return filePath;
            }
        }

        /// <summary>
        /// Czy da się stworzyc RESX poprawnie uzupelniony ?
        /// </summary>
        /// <param name="TableModel"></param>
        /// <param name="info"></param>
        /// <param name="sqlFilePath"></param>
        /// <param name="action"></param>
        /// <param name="actionName"></param>
        private static void GenerateRESXFile(TableModel TableModel, string sqlFilePath, EnvDTE.Project dataProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string fileName = "Sql.resx";
            string projectDir = Path.GetDirectoryName(dataProject.FullName);
            string directoryPath = Path.Combine(projectDir, "Resources", TableModel.Key, TableModel.Value.Replace("_", ""));
            string filePath = Path.Combine(directoryPath, fileName);

            // Tworzenie folderów, jeśli nie istnieją
            Directory.CreateDirectory(directoryPath);

            // Tworzenie pliku RESX jeśli nie istnieje
            if (!File.Exists(filePath))
            {
                using (ResXResourceWriter resxWriter = new(filePath))
                {
                    resxWriter.Generate();
                }

                // Dodaj plik do projektu
                ProjectItem resxItem = dataProject.ProjectItems.AddFromFile(filePath);

                // Ustaw właściwości, aby klasa była publiczna
                resxItem.Properties.Item("BuildAction").Value = 3; // EmbeddedResource
                resxItem.Properties.Item("CustomTool").Value = "PublicResXFileCodeGenerator";
            }

            // Załaduj i edytuj RESX
            XDocument resxDoc = XDocument.Load(filePath);
            string name = Path.GetFileNameWithoutExtension(sqlFilePath);

            if (resxDoc.Root.Elements("data").Any(x => (string)x.Attribute("name") == name))
                return;

            resxDoc.Root.Add(
                new XElement("data",
                    new XAttribute("name", name),
                    new XAttribute("type", "System.Resources.ResXFileRef, System.Windows.Forms"),
                    new XElement("value", $"{name}.sql;System.String, mscorlib, Version=4.0.0.0;utf-8")
                )
            );

            resxDoc.Save(filePath);

            VisualCommand.ForceResxDesignerGenerationAsync(filePath).Wait();
        }

        private static void CreateCreateSQL(List<ColumnInfo> columns, TableModel TableModel, StringBuilder sb)
        {
            sb.AppendLine($"INSERT INTO [{TableModel.Key}].[{TableModel.Value}]");
            sb.AppendLine("(");
            sb.AppendLine($"    [CreateUser_Id],");
            sb.AppendLine($"    [CreateDate],");
            sb.AppendLine($"    [ModifyUser_Id],");
            sb.AppendLine($"    [ModifyDate],");
            sb.AppendLine($"    [IsActive],");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [{columns[i].ColumnName}]");
                else
                    sb.AppendLine($"    [{columns[i].ColumnName}],");
            }
            sb.AppendLine(")");
            sb.AppendLine("VALUES");
            sb.AppendLine("(");
            sb.AppendLine($"    @AuthorizedUserId,");
            sb.AppendLine($"    GETDATE(),");
            sb.AppendLine($"    @AuthorizedUserId,");
            sb.AppendLine($"    GETDATE(),");
            sb.AppendLine($"    1,");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    @{columns[i].ColumnName}"); // Ostatni bez przecinka
                else
                {
                    sb.AppendLine($"    @{columns[i].ColumnName},"); // Wszystkie inne z przecinkiem
                }
            }
            sb.AppendLine(");");
            sb.AppendLine();
            sb.AppendLine("SELECT SCOPE_IDENTITY();");
        }
        private static void CreateSearchSQL(List<ColumnInfo> columns, TableModel TableModel, List<ReferencedTableModel> referencedTables, StringBuilder sb)
        {
            Dictionary<string, int> aliasCounts = new();
            string alias = GetAlias(TableModel.Value, aliasCounts);

            foreach (var referencedTable in referencedTables)
                referencedTable.ReferencedAlias = GetAlias(referencedTable.ReferencedTable, aliasCounts);

            sb.AppendLine("SET @Page = ISNULL(@Page, 1);");
            sb.AppendLine("SET @PageSize = ISNULL(@PageSize, 10);");
            sb.AppendLine("SET @OrderDir = ISNULL(@OrderDir, 1);");
            sb.AppendLine("SET @OrderBy = ISNULL(@OrderBy, 'Name');");

            sb.AppendLine("DECLARE @OrderDesc VARCHAR(10) = CASE");
            sb.AppendLine("    WHEN @OrderDir < 0 THEN 'DESC'");
            sb.AppendLine("    WHEN @OrderDir >= 0 THEN 'ASC'");
            sb.AppendLine("END;");

            sb.AppendLine("DECLARE @SortExpression VARCHAR(300) = CONCAT(@OrderBy, ' ', @OrderDesc);");

            sb.AppendLine();
            sb.AppendLine("SELECT");
            sb.AppendLine($"    [{alias}].[Id],");
            foreach (var column in columns)
            {
                sb.AppendLine($"    [{alias}].[{column.ColumnName}] AS [{column.ReplacedName}],");
            }
            foreach (var referencedTable in referencedTables)
                foreach (var column in referencedTable.Columns)
                    sb.AppendLine($"    [{referencedTable.ReferencedAlias}].[{column.ColumnName}] AS [{referencedTable.ReferencedTable}{column.ReplacedName}],");

            sb.AppendLine("    COUNT(*) OVER () AS TotalRows");
            sb.AppendLine($"FROM");
            sb.AppendLine($"    [{TableModel.Key}].[{TableModel.Value}] (NOLOCK) AS [{alias}]");
            foreach (var referencedTable in referencedTables)
            {
                if (referencedTable.ParentColumnIsNullable)
                {
                    sb.AppendLine($"    LEFT JOIN {referencedTable.ReferencedName} (NOLOCK) AS [{referencedTable.ReferencedAlias}] ON [{alias}].[{referencedTable.ParentColumn}] = [{referencedTable.ReferencedAlias}].[{referencedTable.ReferencedColumn}] AND [{referencedTable.ReferencedAlias}].[IsActive] = 1");
                }
                else
                {
                    sb.AppendLine($"    INNER JOIN {referencedTable.ReferencedName} (NOLOCK) AS [{referencedTable.ReferencedAlias}] ON [{alias}].[{referencedTable.ParentColumn}] = [{referencedTable.ReferencedAlias}].[{referencedTable.ReferencedColumn}]");
                }
            }

            sb.AppendLine("WHERE");
            sb.AppendLine($"    [{alias}].[IsActive] = 1");
            foreach (var referencedTable in referencedTables.Where(r => !r.ParentColumnIsNullable))
                sb.AppendLine($"    AND [{referencedTable.ReferencedAlias}].[IsActive] = 1");

            foreach (var column in columns)
            {
                if (QuickAcationsHelper.SqlTypeToCSharp(column.DataType, false) == "string")
                {
                    sb.AppendLine($"    AND (@{column.ColumnName} IS NULL OR [{alias}].[{column.ColumnName}] LIKE '%' + @{column.ColumnName} + '%')");
                }
                else
                {
                    sb.AppendLine($"    AND (@{column.ColumnName} IS NULL OR [{alias}].[{column.ColumnName}] = @{column.ColumnName})");
                }
            }
            foreach (var referencedTable in referencedTables)
                foreach (var column in referencedTable.Columns)
                    if (QuickAcationsHelper.SqlTypeToCSharp(column.DataType, false) == "string")
                    {
                        sb.AppendLine($"    AND (@{referencedTable.ReferencedTable}{column.ColumnName} IS NULL OR [{referencedTable.ReferencedAlias}].[{column.ColumnName}] LIKE '%' + @{referencedTable.ReferencedTable}{column.ColumnName} + '%')");
                    }
                    else
                    {
                        sb.AppendLine($"    AND (@{referencedTable.ReferencedTable}{column.ColumnName} IS NULL OR [{referencedTable.ReferencedAlias}].[{column.ColumnName}] = @{referencedTable.ReferencedTable}{column.ColumnName})");
                    }

            sb.AppendLine("ORDER BY");
            foreach (var referencedTable in referencedTables)
                foreach (var column in referencedTable.Columns)
                {
                    sb.AppendLine($"    CASE WHEN @SortExpression = N'{referencedTable.ReferencedTable}{column.ReplacedName} ASC' THEN [{referencedTable.ReferencedAlias}].[{column.ColumnName}] END ASC,");
                    sb.AppendLine($"    CASE WHEN @SortExpression = N'{referencedTable.ReferencedTable}{column.ReplacedName} DESC' THEN [{referencedTable.ReferencedAlias}].[{column.ColumnName}] END DESC,");
                }


            for (int i = 0; i < columns.Count; i++)
            {
                sb.AppendLine($"    CASE WHEN @SortExpression = N'{columns[i].ReplacedName} ASC' THEN [{alias}].[{columns[i].ColumnName}] END ASC,");

                if (i == columns.Count - 1)
                {
                    sb.AppendLine($"    CASE WHEN @SortExpression = N'{columns[i].ReplacedName} DESC' THEN [{alias}].[{columns[i].ColumnName}] END DESC");
                }
                else
                {
                    sb.AppendLine($"    CASE WHEN @SortExpression = N'{columns[i].ReplacedName} DESC' THEN [{alias}].[{columns[i].ColumnName}] END DESC,");
                }
            }
            sb.AppendLine("OFFSET (@Page -1) * CAST(@PageSize AS INT) ROWS FETCH NEXT CAST(@PageSize AS INT) ROWS ONLY;");
        }



        private static string GetAlias(string tableName, Dictionary<string, int> aliasCounts)
        {
            string baseAlias = QuickAcationsHelper.GetInitials(tableName);

            if (!aliasCounts.ContainsKey(baseAlias))
            {
                aliasCounts[baseAlias] = 1;
                return baseAlias;
            }
            else
            {
                aliasCounts[baseAlias]++;
                return baseAlias + aliasCounts[baseAlias];
            }
        }


        private static void CreateGetSQL(List<ColumnInfo> columns, TableModel TableModel, List<ReferencedTableModel> referencedTables, StringBuilder sb)
        {
            Dictionary<string, int> aliasCounts = new();
            string alias = GetAlias(TableModel.Value, aliasCounts);

            foreach (var referencedTable in referencedTables)
                referencedTable.ReferencedAlias = GetAlias(referencedTable.ReferencedTable, aliasCounts);

            sb.AppendLine("SELECT");
            sb.AppendLine($"    [{alias}].[Id],");
            for (int i = 0; i < columns.Count; i++)
                if (i == columns.Count - 1)
                {
                    if (referencedTables.Any())
                    {
                        sb.AppendLine($"    [{alias}].[{columns[i].ColumnName}] AS [{columns[i].ReplacedName}],");
                    }
                    else
                    {
                        sb.AppendLine($"    [{alias}].[{columns[i].ColumnName}] AS [{columns[i].ReplacedName}]");
                    }
                }
                else
                    sb.AppendLine($"    [{alias}].[{columns[i].ColumnName}] AS [{columns[i].ReplacedName}],");

            foreach (var referencedTable in referencedTables)
                foreach (var column in referencedTable.Columns)
                {
                    if (referencedTables.IsLast(referencedTable) && referencedTable.Columns.IsLast(column))
                    {
                        sb.AppendLine($"    [{referencedTable.ReferencedAlias}].[{column.ColumnName}] AS [{referencedTable.ReferencedTable}{column.ReplacedName}]");
                    }
                    else
                    {
                        sb.AppendLine($"    [{referencedTable.ReferencedAlias}].[{column.ColumnName}] AS [{referencedTable.ReferencedTable}{column.ReplacedName}],");
                    }

                }

            sb.AppendLine($"FROM");
            sb.AppendLine($"    [{TableModel.Key}].[{TableModel.Value}] (NOLOCK) AS [{alias}]");
            foreach (var referencedTable in referencedTables)
            {
                if (referencedTable.ParentColumnIsNullable)
                {
                    sb.AppendLine($"    LEFT JOIN {referencedTable.ReferencedName} (NOLOCK) AS [{referencedTable.ReferencedAlias}] ON [{alias}].[{referencedTable.ParentColumn}] = [{referencedTable.ReferencedAlias}].[{referencedTable.ReferencedColumn}] AND [{referencedTable.ReferencedAlias}].[IsActive] = 1");
                }
                else
                {
                    sb.AppendLine($"    INNER JOIN {referencedTable.ReferencedName} (NOLOCK) AS [{referencedTable.ReferencedAlias}] ON [{alias}].[{referencedTable.ParentColumn}] = [{referencedTable.ReferencedAlias}].[{referencedTable.ReferencedColumn}]");
                }
            }
            sb.AppendLine("WHERE");
            sb.AppendLine($"    [{alias}].[IsActive] = 1");
            foreach (var referencedTable in referencedTables.Where(r => !r.ParentColumnIsNullable))
                sb.AppendLine($"    AND [{referencedTable.ReferencedAlias}].[IsActive] = 1");
            sb.AppendLine($"    AND [{alias}].[Id] = @Id;");
        }
        private static void CreateGetListSQL(List<ColumnInfo> columns, TableModel TableModel, List<ReferencedTableModel> referencedTables, StringBuilder sb)
        {
            Dictionary<string, int> aliasCounts = new();
            string alias = GetAlias(TableModel.Value, aliasCounts);

            foreach (var referencedTable in referencedTables)
                referencedTable.ReferencedAlias = GetAlias(referencedTable.ReferencedTable, aliasCounts);

            sb.AppendLine("SELECT");
            sb.AppendLine($"    [{alias}].[Id],");
            for (int i = 0; i < columns.Count; i++)
                if (i == columns.Count - 1)
                {
                    if (referencedTables.Any())
                    {
                        sb.AppendLine($"    [{alias}].[{columns[i].ColumnName}] AS [{columns[i].ReplacedName}],");
                    }
                    else
                    {
                        sb.AppendLine($"    [{alias}].[{columns[i].ColumnName}] AS [{columns[i].ReplacedName}]");
                    }
                }
                else
                    sb.AppendLine($"    [{alias}].[{columns[i].ColumnName}] AS [{columns[i].ReplacedName}],");

            foreach (var referencedTable in referencedTables)
                foreach (var column in referencedTable.Columns)
                {
                    if (referencedTables.IsLast(referencedTable) && referencedTable.Columns.IsLast(column))
                    {
                        sb.AppendLine($"    [{referencedTable.ReferencedAlias}].[{column.ColumnName}] AS [{referencedTable.ReferencedTable}{column.ReplacedName}]");
                    }
                    else
                    {
                        sb.AppendLine($"    [{referencedTable.ReferencedAlias}].[{column.ColumnName}] AS [{referencedTable.ReferencedTable}{column.ReplacedName}],");
                    }

                }

            sb.AppendLine($"FROM");
            sb.AppendLine($"    [{TableModel.Key}].[{TableModel.Value}] (NOLOCK) AS [{alias}]");
            foreach (var referencedTable in referencedTables)
            {
                if (referencedTable.ParentColumnIsNullable)
                {
                    sb.AppendLine($"    LEFT JOIN {referencedTable.ReferencedName} (NOLOCK) AS [{referencedTable.ReferencedAlias}] ON [{alias}].[{referencedTable.ParentColumn}] = [{referencedTable.ReferencedAlias}].[{referencedTable.ReferencedColumn}] AND [{referencedTable.ReferencedAlias}].[IsActive] = 1");
                }
                else
                {
                    sb.AppendLine($"    INNER JOIN {referencedTable.ReferencedName} (NOLOCK) AS [{referencedTable.ReferencedAlias}] ON [{alias}].[{referencedTable.ParentColumn}] = [{referencedTable.ReferencedAlias}].[{referencedTable.ReferencedColumn}]");
                }
            }
            sb.AppendLine("WHERE");
            sb.AppendLine($"    [{alias}].[IsActive] = 1");
            foreach (var referencedTable in referencedTables.Where(r => !r.ParentColumnIsNullable))
                sb.AppendLine($"    AND [{referencedTable.ReferencedAlias}].[IsActive] = 1");
        }
        private static void CreateUpdateSQL(List<ColumnInfo> columns, TableModel TableModel, StringBuilder sb)
        {
            sb.AppendLine($"UPDATE [{TableModel.Key}].[{TableModel.Value}]");
            sb.AppendLine("SET");
            sb.AppendLine("    [ModifyUser_Id] = @AuthorizedUserId,");
            sb.AppendLine("    [ModifyDate] = GETDATE(),");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [{columns[i].ColumnName}] = @{columns[i].ColumnName}");
                else
                {
                    sb.AppendLine($"    [{columns[i].ColumnName}] = @{columns[i].ColumnName},");
                }
            }
            sb.AppendLine("WHERE");
            sb.AppendLine("    [Id] = @Id;");
        }
        private static void CreateDeleteSQL(List<ColumnInfo> columns, TableModel TableModel, StringBuilder sb)
        {
            sb.AppendLine($"UPDATE [{TableModel.Key}].[{TableModel.Value}]");
            sb.AppendLine("SET");
            sb.AppendLine("    [ModifyUser_Id] = @AuthorizedUserId,");
            sb.AppendLine("    [ModifyDate] = GETDATE(),");
            sb.AppendLine("    [IsActive] = 0");
            sb.AppendLine("WHERE");
            sb.AppendLine("    [Id] = @Id;");
        }

        private static void CreateMergeSQL(List<ColumnInfo> columns, TableModel TableModel, StringBuilder sb)
        {
            sb.AppendLine($"DECLARE @{TableModel.Value}s TABLE (");
            sb.AppendLine($"    [Id] INT,");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [{columns[i].ColumnName}] {QuickAcationsHelper.MapToSqlType(columns[i].DataType)}");
                else
                    sb.AppendLine($"    [{columns[i].ColumnName}] {QuickAcationsHelper.MapToSqlType(columns[i].DataType)},");
            }
            sb.AppendLine(");");
            sb.AppendLine();
            sb.AppendLine($"INSERT INTO @{TableModel.Value}s (");
            sb.AppendLine($"    [Id],");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [{columns[i].ColumnName}]");
                else
                    sb.AppendLine($"    [{columns[i].ColumnName}],");
            }
            sb.AppendLine(")");
            sb.AppendLine("SELECT");
            sb.AppendLine($"    [T].[Id],");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [T].[{columns[i].ColumnName}]");
                else
                    sb.AppendLine($"    [T].[{columns[i].ColumnName}],");
            }
            sb.AppendLine($"FROM OPENJSON(@{TableModel.Value}Json)");
            sb.AppendLine("WITH (");
            sb.AppendLine($"    [Id] INT '$.Id',");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [{columns[i].ColumnName}] {QuickAcationsHelper.MapToSqlType(columns[i].DataType)} '$.{columns[i].ReplacedName}'");
                else
                    sb.AppendLine($"    [{columns[i].ColumnName}] {QuickAcationsHelper.MapToSqlType(columns[i].DataType)} '$.{columns[i].ReplacedName}',");
            }
            sb.AppendLine(") AS [T];");
            sb.AppendLine();
            sb.AppendLine("/* DOSTOSUJ WARUNKI DO SWOICH POTRZEB 😊 */");
            sb.AppendLine($"MERGE INTO [{TableModel.Key}].[{TableModel.Value}] AS Target");
            sb.AppendLine("USING (");
            sb.AppendLine("    SELECT");
            sb.AppendLine($"        [Id],");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"        [{columns[i].ColumnName}]");
                else
                    sb.AppendLine($"        [{columns[i].ColumnName}],");
            }
            sb.AppendLine($"    FROM @{TableModel.Value}s");
            sb.AppendLine(") AS Source");
            sb.AppendLine("ON");
            sb.AppendLine($"        Target.[Id] = Source.[Id]");
            sb.AppendLine("WHEN MATCHED THEN");
            sb.AppendLine("    UPDATE SET");
            sb.AppendLine("        Target.[ModifyDate] = GETDATE(),");
            sb.AppendLine("        Target.[ModifyUser_Id] = @AuthorizedUserId,");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"        Target.[{columns[i].ColumnName}] = Source.[{columns[i].ColumnName}]");
                else
                    sb.AppendLine($"        Target.[{columns[i].ColumnName}] = Source.[{columns[i].ColumnName}],");
            }
            sb.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
            sb.AppendLine("    INSERT (");
            sb.AppendLine($"        [CreateDate],");
            sb.AppendLine($"        [CreateUser_Id],");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"        [{columns[i].ColumnName}]");
                else
                    sb.AppendLine($"        [{columns[i].ColumnName}],");
            }
            sb.AppendLine("    )");
            sb.AppendLine("    VALUES (");
            sb.AppendLine($"        GETDATE(),");
            sb.AppendLine($"        @AuthorizedUserId,");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"        Source.[{columns[i].ColumnName}]");
                else
                    sb.AppendLine($"        Source.[{columns[i].ColumnName}],");
            }
            sb.AppendLine("    )");
            sb.AppendLine("WHEN NOT MATCHED BY SOURCE /* AND Target.XXX = XXX (ROZWAŻ OGRANICZENIE) */ THEN");
            sb.AppendLine("    UPDATE SET");
            sb.AppendLine("        Target.[ModifyDate] = GETDATE(),");
            sb.AppendLine("        Target.[ModifyUser_Id] = @AuthorizedUserId,");
            sb.AppendLine("        Target.[IsActive] = 0;");
        }

        private static void CreateBulkCreateSQL(List<ColumnInfo> columns, TableModel TableModel, StringBuilder sb)
        {
            sb.AppendLine($"DECLARE @{TableModel.Value}s TABLE (");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [{columns[i].ColumnName}] {QuickAcationsHelper.MapToSqlType(columns[i].DataType)}");
                else
                    sb.AppendLine($"    [{columns[i].ColumnName}] {QuickAcationsHelper.MapToSqlType(columns[i].DataType)},");
            }
            sb.AppendLine(");");

            sb.AppendLine();

            sb.AppendLine($"INSERT INTO @{TableModel.Value}s (");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [{columns[i].ColumnName}]");
                else
                    sb.AppendLine($"    [{columns[i].ColumnName}],");
            }
            sb.AppendLine(")");
            sb.AppendLine("SELECT");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [T].[{columns[i].ColumnName}]");
                else
                    sb.AppendLine($"    [T].[{columns[i].ColumnName}],");
            }
            sb.AppendLine($"FROM OPENJSON(@{TableModel.Value}Json)");
            sb.AppendLine("WITH (");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [{columns[i].ColumnName}] {QuickAcationsHelper.MapToSqlType(columns[i].DataType)} '$.{columns[i].ReplacedName}'");
                else
                    sb.AppendLine($"    [{columns[i].ColumnName}] {QuickAcationsHelper.MapToSqlType(columns[i].DataType)} '$.{columns[i].ReplacedName}',");
            }
            sb.AppendLine(") AS [T];");
            sb.AppendLine();


            sb.AppendLine($"INSERT INTO [{TableModel.Key}].[{TableModel.Value}] (");
            sb.AppendLine($"    [CreateUser_Id],");
            sb.AppendLine($"    [CreateDate],");
            sb.AppendLine($"    [ModifyUser_Id],");
            sb.AppendLine($"    [ModifyDate],");
            sb.AppendLine($"    [IsActive],");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [{columns[i].ColumnName}]");
                else
                    sb.AppendLine($"    [{columns[i].ColumnName}],");
            }
            sb.AppendLine(")");
            sb.AppendLine("SELECT");
            sb.AppendLine($"    @AuthorizedUserId,");
            sb.AppendLine($"    GETDATE(),");
            sb.AppendLine($"    @AuthorizedUserId,");
            sb.AppendLine($"    GETDATE(),");
            sb.AppendLine($"    1,");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == columns.Count - 1)
                    sb.AppendLine($"    [{columns[i].ColumnName}]");
                else
                    sb.AppendLine($"    [{columns[i].ColumnName}],");
            }
            sb.AppendLine($"FROM @{TableModel.Value}s");
        }

    }
}
