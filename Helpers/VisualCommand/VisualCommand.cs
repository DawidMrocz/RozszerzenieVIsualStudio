using EnvDTE;
using EnvDTE80;
using System.Runtime.InteropServices;
using VSLangProj;

namespace Mroczware.PowerFullTool.Helpers
{
    public partial class VisualCommand
    {
        private static void SaveProjectItems(ProjectItems items)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null) return;

            foreach (ProjectItem item in items)
            {
                try
                {
                    if (item.Document != null && !item.Document.Saved)
                    {
                        item.Document.Save();
                    }

                    if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    {
                        SaveProjectItems(item.ProjectItems);
                    }
                }
                catch { }
            }
        }
        public static async Task ForceResxDesignerGenerationAsync(string resxFilePath)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                DTE2 dte = (DTE2)Package.GetGlobalService(typeof(DTE));

                ProjectItem resxItem = dte.Solution.FindProjectItem(resxFilePath);
                if (resxItem != null)
                {
                    VSProjectItem vsResxItem = resxItem.Object as VSProjectItem;
                    if (vsResxItem != null)
                    {
                        vsResxItem.RunCustomTool();
                        Console.WriteLine("Custom Tool executed successfully.");
                    }
                    else
                    {
                        Console.WriteLine("Failed to cast ProjectItem to VSProjectItem.");
                    }
                }
                else
                {
                    Console.WriteLine("File not found in the solution.");
                }
            }
            catch (COMException)
            {
                Console.WriteLine("No active Visual Studio instance found.");
            }
        }
    }
}
