using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using Mroczware.PowerFullTool;
using Mroczware.PowerFullTool.Helpers;
using Mroczware.PowerFullTool.UI.View;
using System.IO;
using Mroczware.PowerFullTool.Commands;

namespace UI.View
{

    public class ConfigureProjectsWindowInfo
    {
        public EnvDTE.Project ApiProject { get; set; }
        public string ApiModelsFolder { get; set; }
        public EnvDTE.Project CoreProject { get; set; }
        public string DtoFolder { get; set; }
        public EnvDTE.Project DataProject { get; set; }
        public string ModelsFolder { get; set; }
        //public string ServicesFolder { get; set; }
        //public string RepositoriesFolder { get; set; }
        public string ConnectionString { get; set; }
    }
    public enum SettingKeyEnum
    {
        ProjectPaths,
        ConnectionString,
        ApiFolder,
        DtoFolder,
        ModelsFolder,
        ApiProject,
        CoreProject,
        DataProject
    }

    public partial class ConfigureProjectsWindow : System.Windows.Window, INotifyPropertyChanged
    {
        private DTE2 _dte;

        public ConfigureProjectsWindowInfo ConfigureProjectsWindowInfo { get; private set; }

        private string _apiSelectedProject;
        private string _coreSelectedProject;
        private string _dataSelectedProject;

        private EnvDTE.Project _apiProject;
        private string _apiModelsFolder;
        private EnvDTE.Project _coreProject;
        private string _dtoFolder;
        private EnvDTE.Project _dataProject;
        private string _modelsFolder;

        public string ApiSelectedProject
        {
            get => _apiSelectedProject;
            set
            {
                if (_apiSelectedProject != value)
                {
                    _apiSelectedProject = value;
                    OnPropertyChanged(nameof(ApiSelectedProject));
                    UpdateApiProjects();
                }
            }
        }

        public string ApiText
        {
            get => _apiModelsFolder;
            set
            {
                if (_apiModelsFolder != value)
                {
                    _apiModelsFolder = value;
                    OnPropertyChanged(nameof(ApiText));
                }
            }
        }

        public string CoreSelectedProject
        {
            get => _coreSelectedProject;
            set
            {
                if (_coreSelectedProject != value)
                {
                    _coreSelectedProject = value;
                    OnPropertyChanged(nameof(CoreSelectedProject));
                    UpdateCoreProjects();
                }
            }
        }

        public string CoreText
        {
            get => _dtoFolder;
            set
            {
                if (_dtoFolder != value)
                {
                    _dtoFolder = value;
                    OnPropertyChanged(nameof(CoreText));
                }
            }
        }

        public string DataSelectedProject
        {
            get => _dataSelectedProject;
            set
            {
                if (_dataSelectedProject != value)
                {
                    _dataSelectedProject = value;
                    OnPropertyChanged(nameof(DataSelectedProject));
                    UpdateDataProjects();
                }
            }
        }

        public string DataText
        {
            get => _modelsFolder;
            set
            {
                if (_modelsFolder != value)
                {
                    _modelsFolder = value;
                    OnPropertyChanged(nameof(DataText));
                }
            }
        }

        public EnvDTE.Project ApiProject
        {
            get => _apiProject;
            set
            {
                if (_apiProject != value)
                {
                    _apiProject = value;
                    OnPropertyChanged(nameof(ApiProject));
                }
            }
        }

        public EnvDTE.Project CoreProject
        {
            get => _coreProject;
            set
            {
                if (_coreProject != value)
                {
                    _coreProject = value;
                    OnPropertyChanged(nameof(CoreProject));
                }
            }
        }

        public EnvDTE.Project DataProject
        {
            get => _dataProject;
            set
            {
                if (_dataProject != value)
                {
                    _dataProject = value;
                    OnPropertyChanged(nameof(DataProject));
                }
            }
        }

        //public string ApiSelectedProject
        //{
        //    get => _apiSelectedProject;
        //    set
        //    {
        //        if (_apiSelectedProject != value)
        //        {
        //            _apiSelectedProject = value;
        //            OnPropertyChanged(nameof(ApiSelectedProject));
        //            UpdateApiProjects();
        //        }
        //    }
        //}

        private string _connectionString;

        public string ConnectionStringText
        {
            get => _connectionString;
            set
            {
                _connectionString = value;
                OnPropertyChanged(nameof(ConnectionStringText));
            }
        }

        private Dictionary<string, EnvDTE.Project> _projectDictionary;
        public ObservableCollection<string> ProjectKeys { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> ConnectionStringKeys { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> FoldersApiKeys { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> FoldersCoreKeys { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> FoldersDataKeys { get; set; } = new ObservableCollection<string>();

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public ICommand ClearApiSelectionCommand { get; }
        public ICommand ClearCoreSelectionCommand { get; }
        public ICommand ClearDataSelectionCommand { get; }

        public ConfigureProjectsWindow(DTE2 dte, ProjectsFromSettings input)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            InitializeComponent();
            DataContext = this;
            _dte = dte;
            _projectDictionary = new Dictionary<string, EnvDTE.Project>();
            ProjectKeys = new ObservableCollection<string>();
            LoadProjects();
            LoadConnectionStrings();

            ConnectionStringText = VsixSettingsManager.LoadSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ConnectionString) ?? input.ConnectionString;
            ApiText = VsixSettingsManager.LoadSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ApiFolder) ?? input.ApiModelsFolder;
            CoreText = VsixSettingsManager.LoadSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.DtoFolder) ?? input.DtoFolder;
            DataText = VsixSettingsManager.LoadSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ModelsFolder) ?? input.ModelsFolder;
            ApiSelectedProject = VsixSettingsManager.LoadSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ApiProject) ?? input.ProjectApi?.Name;
            CoreSelectedProject = VsixSettingsManager.LoadSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.CoreProject) ?? input.ProjectCore?.Name;
            DataSelectedProject = VsixSettingsManager.LoadSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.DataProject) ?? input.ProjectData?.Name;

            TakeFirstConnectionString();

            ClearApiSelectionCommand = new RelayCommand(ClearApiSelection);
            ClearCoreSelectionCommand = new RelayCommand(ClearCoreSelection);
            ClearDataSelectionCommand = new RelayCommand(ClearDataSelection);

        }

        private void ClearApiSelection() => ApiSelectedProject = null;
        private void ClearCoreSelection() => CoreSelectedProject = null;
        private void ClearDataSelection() => DataSelectedProject = null;

        // ✅ Poprawiona metoda UpdateSelectedProjects()
        private void UpdateApiProjects()
        {
            ApiProject = GetProject(ApiSelectedProject);
            LoadFoldersForApiProject(ApiProject, FoldersApiKeys);
        }

        private void UpdateCoreProjects()
        {
            CoreProject = GetProject(CoreSelectedProject);
            LoadFoldersForApiProject(CoreProject, FoldersCoreKeys);
        }

        private void UpdateDataProjects()
        {
            DataProject = GetProject(DataSelectedProject);
            LoadFoldersForApiProject(DataProject, FoldersDataKeys);
        }

        private EnvDTE.Project GetProject(string selectedKey)
        {
            return (!string.IsNullOrEmpty(selectedKey) && _projectDictionary.TryGetValue(selectedKey, out EnvDTE.Project project))
                ? project
                : null;
        }

        private void LoadProjects()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_dte.Solution is null) return;

            var projects = QuickAcationsHelper.GetAllProjects(_dte.Solution);

            foreach (EnvDTE.Project project in projects)
                if (!string.IsNullOrWhiteSpace(project.FullName) && !_projectDictionary.ContainsKey(project.Name))
                {
                    _projectDictionary[project.Name] = project;
                    ProjectKeys.Add(project.Name);
                }
        }

        private void LoadConnectionStrings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            List<string> connectionStrings = [];

            var directory = Directory.GetCurrentDirectory();
            var files = Directory.GetFiles(directory, "appsettings.json", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var jObject = JObject.Parse(json);

                    var connectionString = jObject.SelectToken("ConnectionStrings.DefaultConnection")?.ToString();
                    if (!string.IsNullOrWhiteSpace(connectionString))
                        connectionStrings.Add(connectionString);
                }
                catch
                {
                    continue;
                }
            }

            var webConfigFiles = Directory.GetFiles(directory, "web.config", SearchOption.AllDirectories);
            foreach (var file in webConfigFiles)
            {
                try
                {
                    XDocument configXml = XDocument.Load(file);

                    string? connStr = configXml
                        .Root?
                        .Element("connectionStrings")?
                        .Elements("add")
                        .FirstOrDefault(e => (string)e.Attribute("name") == "DefaultConnection")
                        ?.Attribute("connectionString")
                        ?.Value;

                    if (!string.IsNullOrWhiteSpace(connStr))
                        connectionStrings.Add(connStr);
                }
                catch
                {
                    continue;
                }
            }

            foreach (string connectionString in connectionStrings.Distinct())
                ConnectionStringKeys.Add(connectionString);
        }

        private void TakeFirstConnectionString()
        {       
            if (string.IsNullOrWhiteSpace(ConnectionStringText) && ConnectionStringKeys.Count == 1)
                ConnectionStringText = ConnectionStringKeys.First();
        }

        private void LoadFoldersForApiProject(EnvDTE.Project project, ObservableCollection<string> collection)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            collection.Clear();

            if (project is null) return;

            foreach (ProjectItem projectItem in project.ProjectItems)
                if (projectItem.Kind == EnvDTE.Constants.vsProjectItemKindPhysicalFolder) // Sprawdzamy, czy to folder
                    collection.Add(projectItem.Name);
        }

        private void SaveSettings(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            //string connectionString = ConnectionStringTextBox.Text;
            VsixSettingsManager.SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ConnectionString, _connectionString);
            VsixSettingsManager.SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ApiFolder, _apiModelsFolder);
            VsixSettingsManager.SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.DtoFolder, _dtoFolder);
            VsixSettingsManager.SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ModelsFolder, _modelsFolder);
            VsixSettingsManager.SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ApiProject, _apiProject?.Name);
            VsixSettingsManager.SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.CoreProject, _coreProject?.Name);
            VsixSettingsManager.SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.DataProject, _dataProject?.Name);

            ConfigureProjectsWindowInfo = new ConfigureProjectsWindowInfo();

            ConfigureProjectsWindowInfo.ApiModelsFolder = _apiModelsFolder;
            ConfigureProjectsWindowInfo.ApiProject = _apiProject;

            ConfigureProjectsWindowInfo.ConnectionString = _connectionString;

            ConfigureProjectsWindowInfo.DataProject = _dataProject;
            ConfigureProjectsWindowInfo.ModelsFolder = _modelsFolder;

            ConfigureProjectsWindowInfo.CoreProject = _coreProject;
            ConfigureProjectsWindowInfo.DtoFolder = _dtoFolder;

            //StatusBarHelper.ShowStatusBarMessage("Globalne ustawienia zostały zapisane!");
            Close();
        }

        private void ClearSettings(object sender, RoutedEventArgs e)
        {
            ApiText = null;
            CoreText = null;
            DataText = null;
            //ConnectionStringTextBox.Text = null;
            ApiSelectedProject = null;
            CoreSelectedProject = null;
            DataSelectedProject = null;
            ConnectionStringText = null;
        }

        private void CloseWindow(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
