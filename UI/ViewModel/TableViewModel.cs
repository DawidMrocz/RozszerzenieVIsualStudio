using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using Mroczware.PowerFullTool.Models;

namespace Mroczware.PowerFullTool.UI.ViewModel
{
    public class TableViewModel : BaseViewModel
    {
        public ICommand ConfirmSelectionCommand { get; }
        public ICommand CloseWindowCommand { get; }
        public ICommand SelectTableCommand { get; }

        // ###########################################################
        //                           PROPERTY
        // ###########################################################

        private string _searchText;

        private TableModel _selectedTable;

        private readonly ICollectionView _filteredTablesView;

        public ICollectionView FilteredTables => _filteredTablesView;

        public ObservableCollection<TableModel> Tables { get; set; }

        public TableModel SelectedTable
        {
            get => _selectedTable;
            set
            {
                if (_selectedTable != value)
                {
                    _selectedTable = value;
                    OnPropertyChanged(nameof(SelectedTable));
                }
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText != value)
                {
                    _searchText = value;
                    OnPropertyChanged(nameof(SearchText));
                    _filteredTablesView.Refresh();
                }
            }
        }

        // ###########################################################
        //                          CONSTRUCTORS
        // ###########################################################

        public TableViewModel()
        {
            Tables = new ObservableCollection<TableModel>();

            _filteredTablesView = CollectionViewSource.GetDefaultView(Tables);
            _filteredTablesView.Filter = item => FilterCondition(item as TableModel);

            ConfirmSelectionCommand = new RelayCommand(ConfirmSelection);
            CloseWindowCommand = new RelayCommand(CloseWindow);
            SelectTableCommand = new RelayCommand<TableModel>(SelectTable);
        }

        // ###########################################################
        //                          FUNCTIONS
        // ###########################################################

        public void LoadTables(IEnumerable<TableModel> tables)
        {
            Tables.Clear();
            foreach (var table in tables.OrderBy(t => t.Key).ThenBy(t => t.Value))
            {
                Tables.Add(table);
            }
        }

        public void SelectTable(TableModel table)
        {
            if (SelectedTable != null && SelectedTable.Key == table.Key)
            {
                SelectedTable.IsSelected = false;
                SelectedTable = null;
            }
            else
            {
                foreach (var t in Tables)
                {
                    t.IsSelected = false;
                }
                table.IsSelected = true;
                SelectedTable = table;
            }

            OnPropertyChanged(nameof(SelectedTable));
        }

        private bool FilterCondition(TableModel table)
        {
            if (table == null) return false;
            return string.IsNullOrWhiteSpace(SearchText) ||
                   table.Key.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   table.Value.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
