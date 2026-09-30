using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;
using System.Linq;
using UI.View;

namespace Mroczware.PowerFullTool.Helpers
{
    public static class VsixSettingsManager
    {
        private const string CollectionName = "MyVsixSettings";

        public static void SaveSetting(IServiceProvider serviceProvider, SettingKeyEnum key, string value)
        {
            var settingsManager = new ShellSettingsManager(serviceProvider);
            var writableSettingsStore = settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);

            if (!writableSettingsStore.CollectionExists(CollectionName))
                writableSettingsStore.CreateCollection(CollectionName);

            if (string.IsNullOrWhiteSpace(value))
                writableSettingsStore.DeleteProperty(CollectionName, key.ToString());
            else
                writableSettingsStore.SetString(CollectionName, key.ToString(), value);
        }

        public static string LoadSetting(IServiceProvider serviceProvider, SettingKeyEnum key)
        {
            var settingsManager = new ShellSettingsManager(serviceProvider);
            var settingsStore = settingsManager.GetReadOnlySettingsStore(SettingsScope.UserSettings);

            if (settingsStore.CollectionExists(CollectionName) && settingsStore.PropertyExists(CollectionName, key.ToString()))
                return settingsStore.GetString(CollectionName, key.ToString());

            return null;
        }

        public static void ClearSettingsIfProjectsNamesAreDifferent(EnvDTE.Project dataProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string[] projectPathsSetting = LoadSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ProjectPaths)?.Split(',');
            if (projectPathsSetting is null || !projectPathsSetting.Any() || !projectPathsSetting.Contains(dataProject.FullName))
                ClearSettings();
        }

        public static void ClearSettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ConnectionString, null);
            SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ApiFolder, null);
            SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.DtoFolder, null);
            SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ModelsFolder, null);
            SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ApiProject, null);
            SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.CoreProject, null);
            SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.DataProject, null);
        }
    }
}
