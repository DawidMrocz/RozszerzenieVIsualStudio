using EnvDTE;
using EnvDTE80;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using Mroczware.PowerFullTool.Helpers;
using Mroczware.PowerFullTool.Models;
using MroczwareTools;

namespace Mroczware.PowerFullTool.Commands
{
    public class ProjectsFromSettings
    {
        public EnvDTE.Project ProjectApi { get; set; }
        public string ApiModelsFolder { get; set; }
        public EnvDTE.Project ProjectCore { get; set; }
        public string DtoFolder { get; set; }
        public EnvDTE.Project ProjectData { get; set; }
        public string ModelsFolder { get; set; }
        public string ConnectionString { get; set; }
    }

    [Command(PackageIds.GenerateEmptyRepository)]
    internal sealed class GenerateEmptyRepository : BaseCommand<GenerateEmptyRepository>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                DTE2 dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
                UIHierarchy hierarchy = dte.ToolWindows.SolutionExplorer;

                Array selectedItems = hierarchy.SelectedItems as Array;

                if (selectedItems is null)
                    System.Windows.MessageBox.Show("Nie zaznaczono projektu.");

                if (selectedItems.Length != 1)
                    System.Windows.MessageBox.Show("Można zanzaczyć tylko jeden projekt.");

                UIHierarchyItem selectedItem = (UIHierarchyItem)selectedItems.GetValue(0);

                EnvDTE.Project project = selectedItem.Object as EnvDTE.Project;

                if (project is null)
                    System.Windows.MessageBox.Show("Zaznaczony obiekt nie ma rozszerzenia .csproj.");

                var connectionString = QuickAcationsHelper.GetConnectionString(dte, project);

                if (string.IsNullOrWhiteSpace(connectionString)) return;

                QuickAcationsHelper.ShowStatusBarMessage($"Próba połączenia z bazą danych... (Sprawdź VPN lub czy został ustawiony ConnectionString)");

                List<TableModel> schemesAndTables = QuickAcationsHelper.GetSchemasAndTables(connectionString);

                UI.View.Table tableSelectWindow = new(schemesAndTables);

                await tableSelectWindow.ShowDialogAsync();

                if (
                    tableSelectWindow is null
                    || tableSelectWindow.SelectedTable is null
                    || string.IsNullOrWhiteSpace(tableSelectWindow.SelectedTable.Key)
                    || string.IsNullOrWhiteSpace(tableSelectWindow.SelectedTable.Value))
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Nie wybrano żadnej tabeli.");
                    return;
                }

                QuickAcationsHelper.ShowStatusBarMessage($"Pomyślnie połączono z bazą danych");

                string repoFolder = QuickAcationsHelper.FindFolder(project.ProjectItems, "Repositories")?.Name
                            ?? QuickAcationsHelper.FindFolder(project.ProjectItems, "Repository")?.Name ?? "Repositories";

                GenerateRepositoryInterfaceFile(repoFolder, tableSelectWindow.SelectedTable.Key, tableSelectWindow.SelectedTable.Value, project);
                GenerateRepositoryMainFile(repoFolder, tableSelectWindow.SelectedTable.Key, tableSelectWindow.SelectedTable.Value, project);

                QuickAcationsHelper.ShowStatusBarMessage($"Pomyślnie wygenerowano pliki.");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Wystąpił błąd podczas wykonywania polecenia 😞 : {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void GenerateRepositoryInterfaceFile(string repoFolder, string schema, string table, EnvDTE.Project activeDocumentProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string interfaceName = $"I{table}Repository";
            string fileName = $"{interfaceName}.cs";
            string projectDir = System.IO.Path.GetDirectoryName(activeDocumentProject.FullName);
            string directoryPath = Path.Combine(projectDir, repoFolder, schema, table);
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);
            if (File.Exists(filePath))
                return;

            StringBuilder sb = new();
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
    }
}
