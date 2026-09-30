global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using System;
global using Task = System.Threading.Tasks.Task;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Mroczware.PowerFullTool;
using Mroczware.PowerFullTool.Commands;

namespace MroczwareTools
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.MroczwareToolsString)]
    public sealed class MroczwareToolsPackage : ToolkitPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            //Sprawdzenie, czy jest najnowsza wersja
           // await CheckForUpdateAsync();
            // rejestracja poleceń


            await ResetIis.InitializeAsync(this);
            await GenerateDto.InitializeAsync(this);
            await GenerateParams.InitializeAsync(this);
            await GenerateRepoFromSql.InitializeAsync(this);
            await GenerateSql.InitializeAsync(this);
            await GenerateEmptyService.InitializeAsync(this);
            await GenerateEmptyRepository.InitializeAsync(this);
            await ViewLogs.InitializeAsync(this);
            await GenerateResponseFromSql.InitializeAsync(this);
            await GenerateIntegrationTest.InitializeAsync(this);
        }

        //private async Task CheckForUpdateAsync()
        //{
        //    const string FEED_URL = "https://example.com/mroczware_powerfulltool.xml";
        //    const string SETTINGS_COLLECTION = "Mroczware.PowerFullTool";
        //    const string PROP_LAST_VERSION = "LastNotifiedVersion";

        //    try
        //    {
        //        using HttpClient http = new();
        //        using var stream = await http.GetStreamAsync(FEED_URL);

        //        XmlSerializer serializer = new XmlSerializer(typeof(AtomFeed));
        //        if (serializer.Deserialize(stream) is not AtomFeed feed) return;

        //        string remoteVersion = feed.Entries?.FirstOrDefault()?.Vsix?.Version;

        //        if (string.IsNullOrWhiteSpace(remoteVersion)) return;

        //        string localVersion = Vsix.Version;
        //        if (localVersion.Equals(remoteVersion, StringComparison.OrdinalIgnoreCase)) return;

        //        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        //        IVsSettingsManager settingsManager = await GetServiceAsync(typeof(SVsSettingsManager)) as IVsSettingsManager;
        //        if (settingsManager is null) return;

        //        settingsManager.GetWritableSettingsStore(
        //            (uint)__VsSettingsScope.SettingsScope_UserSettings,
        //            out IVsWritableSettingsStore store);

        //        if (store is null) return;

        //        bool collectionExists = store.CollectionExists(SETTINGS_COLLECTION, out int cExists) == 0 && cExists != 0;
        //        if (collectionExists)
        //        {
        //            bool propExists = store.PropertyExists(SETTINGS_COLLECTION, PROP_LAST_VERSION, out int pExists) == 0 && pExists != 0;
        //            if (propExists)
        //            {
        //                store.GetString(SETTINGS_COLLECTION, PROP_LAST_VERSION, out string last);
        //                if (last == remoteVersion) return;
        //            }
        //        }

        //        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        //        int result = VsShellUtilities.ShowMessageBox(
        //            this,
        //            $"Dostępna nowa wersja {remoteVersion} rozszerzenia {Vsix.Name}.",
        //            "Nowa aktualizacja",
        //            OLEMSGICON.OLEMSGICON_INFO,
        //            OLEMSGBUTTON.OLEMSGBUTTON_OK,
        //            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

        //        if (!collectionExists)
        //            store.CreateCollection(SETTINGS_COLLECTION);

        //        store.SetString(SETTINGS_COLLECTION, PROP_LAST_VERSION, remoteVersion);
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Diagnostics.Debug.WriteLine($"[Mroczware.UpdateCheck] {ex}");
        //    }
        //}
    }
}