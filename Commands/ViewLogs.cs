using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using Mroczware.PowerFullTool.Helpers;
using Mroczware.PowerFullTool.Models;
using Mroczware.PowerFullTool.UI.View;
using MroczwareTools;

namespace Mroczware.PowerFullTool
{
    internal class LogFileInfo
    {
        public string FullPath { get; set; } = string.Empty;
        public string FileName => Path.GetFileName(FullPath);
        public DateTime LastModified { get; set; }
        public string DisplayText => $"{FileName} ({LastModified:yyyy-MM-dd HH:mm:ss})";
    }

    [Command(PackageIds.ViewLogs)]
    internal sealed class ViewLogs : BaseCommand<ViewLogs>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (await ServiceProvider.GetGlobalServiceAsync(typeof(DTE)) is not DTE2 dte)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Brak dte.");
                    return;
                }

                EnvDTE.Solution solution = dte.Solution;
                if (dte.Solution is null)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Brak solucji.");
                    return;
                }

                string currentDirectory = Directory.GetCurrentDirectory();
                string[] logsDirectories = Directory.GetDirectories(currentDirectory, "Logs", SearchOption.AllDirectories);

                List<string> allErrorFiles = logsDirectories
                    .Select(dir =>
                    {
                        string[] files = Directory.GetFiles(dir, "error*.log");
                        if (files.Length == 0) return null;

                        string newestFile = files.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                        return newestFile;
                    })
                    .Where(file => file != null)
                    .ToList();

                if (allErrorFiles.Count == 0)
                {
                    QuickAcationsHelper.ShowStatusBarMessage("Nie znaleziono plików error*.log.");
                    return;
                }

                string newestFile = allErrorFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();

                DateTime newestTime = File.GetLastWriteTime(newestFile);

                var recentFiles = allErrorFiles
                .Select(f => new LogFileInfo
                {
                    FullPath = f,
                    LastModified = File.GetLastWriteTime(f)
                })
                .Where(f => (newestTime - f.LastModified) <= TimeSpan.FromSeconds(15))
                .OrderByDescending(f => f.LastModified)
                .OrderBy(f => f.LastModified)
                .ToList();

                foreach (EnvDTE.Document openDoc in dte.Documents.Cast<EnvDTE.Document>())
                    if (openDoc.FullName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                        openDoc.Close(vsSaveChanges.vsSaveChangesNo);

                if(recentFiles.Count == 1)
                {
                    dte.ItemOperations.OpenFile(recentFiles.First().FullPath);

                    EnvDTE.Document doc = dte.ActiveDocument;
                    if (doc != null && doc.Object("TextDocument") is EnvDTE.TextDocument textDoc)
                    {
                        EditPoint endPoint = textDoc.EndPoint.CreateEditPoint();
                        endPoint.Parent.Selection.MoveToPoint(endPoint);
                        endPoint.TryToShow(vsPaneShowHow.vsPaneShowTop, null);
                    }
                }
                else
                {
                    string commonPrefix = GetCommonPathPrefix(recentFiles.Select(r => r.FullPath));
                    var logList = recentFiles.Select(f =>
                    {
                        string relativePath = f.FullPath.Replace(commonPrefix, "");
                        string displayPath = Path.GetDirectoryName(relativePath) ?? "";

                        return new TableModel(
                            f.FullPath,
                            $"{f.FileName}  -  {f.LastModified:yyyy-MM-dd HH:mm:ss}  -  {displayPath}"
                        );
                    }).ToList();

                    LogsWindow tableSelectWindow = new(logList);

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

                    dte.ItemOperations.OpenFile(tableSelectWindow.SelectedTable.Key);

                    EnvDTE.Document doc = dte.ActiveDocument;
                    if (doc != null && doc.Object("TextDocument") is EnvDTE.TextDocument textDoc)
                    {
                        EditPoint endPoint = textDoc.EndPoint.CreateEditPoint();
                        endPoint.Parent.Selection.MoveToPoint(endPoint);
                        endPoint.TryToShow(vsPaneShowHow.vsPaneShowTop, null);
                    }
                }             
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Wystąpił błąd podczas wykonywania polecenia 😞 : {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GetCommonPathPrefix(IEnumerable<string> paths)
        {
            var pathList = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToList();

            if (!pathList.Any())
                return string.Empty;

            var splitPaths = pathList
                .Select(p => p.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Split([Path.DirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                .ToList();

            int maxCompare = splitPaths.Min(s => s.Length);

            int i;
            for (i = 0; i < maxCompare; i++)
            {
                var seg = splitPaths[0][i];
                if (!splitPaths.All(sp => string.Equals(sp[i], seg, StringComparison.OrdinalIgnoreCase)))
                    break;
            }

            if (i == 0)
                return string.Empty;

            string common = string.Join(Path.DirectorySeparatorChar.ToString(), splitPaths[0].Take(i));
            return common + Path.DirectorySeparatorChar;
        }
    }
}
