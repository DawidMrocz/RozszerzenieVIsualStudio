using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using Mroczware.PowerFullTool.Helpers;
using Mroczware.PowerFullTool.Models;

namespace Mroczware.PowerFullTool.UI.View
{

    public partial class GenerateSqlWindow : Window, INotifyPropertyChanged
    {
        public CrudSelectionInfo SelectedCrudInfo { get; private set; }

        private string _actionKey;
        private string _actionName;

        public string ActionSchemaTable { get; }

        private bool _copyToClipboard;
        public bool CopyToClipboard
        {
            get => _copyToClipboard;
            set
            {
                if (_copyToClipboard != value)
                {
                    _copyToClipboard = value;
                    OnPropertyChanged(nameof(CopyToClipboard));
                }
            }
        }

        public string ActionKey
        {
            get => _actionKey;
            set
            {
                if (_actionKey != value)
                {
                    _actionKey = value;
                    OnPropertyChanged(nameof(ActionKey));
                    ActionText = value;

                    // Jeśli akcja to nie GET, GETLIST ani SEARCH — wyczyść listę
                    if (_actionKey != "Get" && _actionKey != "GetList" && _actionKey != "Search")
                    {
                        ShowReferencedTables = false;
                        ReferencedTableList.Clear();
                    }
                    else
                    {
                        ShowReferencedTables = true;
                        LoadReferencedTables(_schema, _table);
                    }
                }
            }
        }

        public string ActionText
        {
            get => _actionName;
            set
            {
                if (_actionName != value)
                {
                    _actionName = value;
                    OnPropertyChanged(nameof(ActionText));
                }
            }
        }

        private bool _showReferencedTables;
        public bool ShowReferencedTables
        {
            get => _showReferencedTables;
            set
            {
                if (_showReferencedTables != value)
                {
                    _showReferencedTables = value;
                    OnPropertyChanged(nameof(ShowReferencedTables));
                }
            }
        }

        public ActionTypeEnum SelectedAction { get; set; }
        public ObservableCollection<string> ActionKeys { get; set; } = new ObservableCollection<string>();

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public GenerateSqlWindow(string schema, string table, string connectionString)
        {
            InitializeComponent();
            DataContext = this;
            LoadActionKeys();
            ActionSchemaTable = $"{schema}_{table}_";
            ReferencedTableList = new ObservableCollection<ReferencedTableModel>();
            _connectionString = connectionString;
            _schema = schema;
            _table = table; 
            ActionKey = ActionTypeEnum.Get.ToString();
        }

        private void LoadActionKeys()
        {
            ActionKeys.Clear(); // Czyszczenie kolekcji przed załadowaniem wartości
            foreach (var action in Enum.GetValues(typeof(ActionTypeEnum)))
            {
                ActionKeys.Add(action.ToString());
            }
            OnPropertyChanged(nameof(ActionKeys));
        }

        private void Wybierz_Click(object sender, RoutedEventArgs e)
        {
            SelectedCrudInfo = new CrudSelectionInfo
            {
                CustomName = _actionName?.Trim().Replace(" ", ""),
                SelectedReferencedTables = ReferencedTableList
                    .Where(t => t.IsSelected)
                    .ToList(),
                CopyToClipboard = this.CopyToClipboard
            };

            if (Enum.TryParse(_actionKey, out ActionTypeEnum actionEnum))
            {
                SelectedCrudInfo.SelectedActions = [actionEnum];
            }

            Close();
        }

        private void Anuluj_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        public ObservableCollection<ReferencedTableModel> ReferencedTableList { get; set; }
        private string _connectionString { get; set; }
        private string _schema { get; set; }
        private string _table { get; set; }

        public void LoadReferencedTables(string schema, string table)
        {
            ReferencedTableList.Clear();

            var referencedTables = QuickAcationsHelper.GetReferencedTables(_connectionString, schema, table);

            foreach (var referencedTable in referencedTables)
                ReferencedTableList.Add(referencedTable);

        }
    }
}
