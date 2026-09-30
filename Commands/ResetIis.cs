using EnvDTE;
using Microsoft.VisualStudio.Shell.Interop;
using MroczwareTools;
using System.Diagnostics;
using System.Security.Principal;

namespace Mroczware.PowerFullTool.Commands
{
    [Command(PackageIds.ResetIis)]
    internal sealed class ResetIis : BaseCommand<ResetIis>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                if (!IsRunningAsAdmin())
                {
                    ShowStatusBarMessage("Visual Studio nie jest uruchomione z uprawnieniami administratora. Niektóre akcje mogą być niedostępne.");
                    return;
                }

                IVsStatusbar statusBar = (IVsStatusbar)ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar));

                object cookie = 0;
                statusBar?.Animation(1, ref cookie);
                statusBar?.SetText("Restartowanie IIS...");

                await Task.Run(() =>
                {
                    try
                    {
                        var process = new System.Diagnostics.Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = "/c iisreset",
                                UseShellExecute = false,
                                Verb = "runas",
                                CreateNoWindow = true
                            }
                        };

                        process.Start();
                        process.WaitForExit();

                        if (process.ExitCode != 0)
                        {
                            throw new Exception($"Błąd iisreset: {process.StandardError.ReadToEnd()}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowStatusBarMessage($"Wyjątek podczas resetu IIS: {ex.Message}");
                    }
                });

                statusBar?.Animation(0, ref cookie);
                statusBar?.SetText("IIS został pomyślnie zrestartowany.");
            }
            catch (Exception ex)
            {
                ShowStatusBarMessage($"Wyjątek podczas resetu IIS 😞 : {ex.Message}");
            }
        }

        private bool IsRunningAsAdmin()
        {
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static void ShowStatusBarMessage(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            IVsStatusbar statusBar = (IVsStatusbar)ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar));
            if (statusBar == null) return;

            statusBar.IsFrozen(out int frozen);
            if (frozen != 0)
                statusBar.FreezeOutput(0);

            statusBar.SetText($"Mroczware TOOL INFO: {message}");
        }
    }
}
