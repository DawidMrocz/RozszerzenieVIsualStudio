using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Xml.Linq;
using Mroczware.PowerFullTool.Helpers;

namespace UI.View
{
    public partial class SelectConnectionStringWindow : Window, INotifyPropertyChanged
    {
        public string ConnectionString { get; set; }
        public ConfigureProjectsWindowInfo ConfigureProjectsWindowInfo { get; private set; }

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
        public ObservableCollection<string> ConnectionStringKeys { get; set; } = new ObservableCollection<string>();

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        public SelectConnectionStringWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            InitializeComponent();
            DataContext = this;
            LoadConnectionStrings();
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

            if (ConnectionStringKeys.Count == 1)
                ConnectionStringText = ConnectionStringKeys.First();

        }

        private void SaveSettings(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsixSettingsManager.SaveSetting(ServiceProvider.GlobalProvider, SettingKeyEnum.ConnectionString, _connectionString);
            ConnectionString = _connectionString;
            Close();
        }

        private void CloseWindow(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
