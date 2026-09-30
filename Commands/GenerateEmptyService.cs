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
    [Command(PackageIds.GenerateEmptyService)]
    internal sealed class GenerateEmptyService : BaseCommand<GenerateEmptyService>
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

                GenerateServiceInterfaceFile(tableSelectWindow.SelectedTable.Key, tableSelectWindow.SelectedTable.Value, project);
                GenerateServiceMainFile(tableSelectWindow.SelectedTable.Key, tableSelectWindow.SelectedTable.Value, project);

                QuickAcationsHelper.ShowStatusBarMessage($"Pomyślnie wygenerowano pliki.");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Wystąpił błąd podczas wykonywania polecenia 😞 : {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void GenerateServiceMainFile(string schema, string table, EnvDTE.Project coreProject)
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
            sb.AppendLine($"using {coreProject.Name}.Services.{schema}.{table};");
            sb.AppendLine();
            sb.AppendLine($"namespace {coreProject.Name}.Services.{schema}.{table}");
            sb.AppendLine("{");
            sb.AppendLine($"    internal partial class {className} : I{className}");
            sb.AppendLine("    {");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(filePath, sb.ToString());
            coreProject.ProjectItems.AddFromFile(filePath);
        }

        private void GenerateServiceInterfaceFile(string schema, string table, EnvDTE.Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            schema = char.ToUpper(schema[0]) + schema.Substring(1);

            string interfaceName = $"I{table}Service";
            string fileName = $"{interfaceName}.cs";
            string projectDir = Path.GetDirectoryName(project.FullName);
            string directoryPath = Path.Combine(projectDir, "Services", schema, table);
            string filePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);

            if (File.Exists(filePath))
                return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"using {QuickAcationsHelper.ReturnIDependencyUsing(project)};");
            sb.AppendLine();
            sb.AppendLine($"namespace {project.Name}.Services.{schema}.{table}");
            sb.AppendLine("{");
            sb.AppendLine($"    public interface {interfaceName} : IDependency");
            sb.AppendLine("    {");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(filePath, sb.ToString());
            project.ProjectItems.AddFromFile(filePath);
        }
    }
}
