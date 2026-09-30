using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Input;
using Mroczware.PowerFullTool.Models;

namespace Mroczware.PowerFullTool.UI.View
{
    public partial class GenerateResponseFromSqlWindow : Window, INotifyPropertyChanged
    {
        public ICommand SelectTableCommand { get; }

        public GenerateResponseFromSqlResponse Response { get; private set; }

        private int _selectedTabIndex;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (_selectedTabIndex != value)
                {
                    _selectedTabIndex = value;
                    OnPropertyChanged(nameof(SelectedTabIndex));
                }
            }
        }

        private SelectItem _SelectedItem;
        public SelectItem SelectedItem
        {
            get => _SelectedItem;
            set
            {
                if (_SelectedItem != value)
                {
                    _SelectedItem = value;
                    OnPropertyChanged(nameof(SelectedItem));
                }
            }
        }

        private string _sqlQuery;
        public string SqlQuery
        {
            get => _sqlQuery;
            set { _sqlQuery = value; OnPropertyChanged(nameof(SqlQuery)); }
        }

        private readonly ICollectionView _filteredItemsView;
        public ICollectionView FilteredItems => _filteredItemsView;

        private string _searchText;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText != value)
                {
                    _searchText = value;
                    OnPropertyChanged(nameof(SearchText));
                    _filteredItemsView.Refresh();
                }
            }
        }

        private string _classNameText;
        public string ClassNameText
        {
            get => _classNameText;
            set
            {
                if (_classNameText != value)
                {
                    _classNameText = value;
                    OnPropertyChanged(nameof(ClassNameText));
                }
            }
        }

        public ObservableCollection<SelectItem> Items { get; set; } = new ObservableCollection<SelectItem>();

        public GenerateResponseFromSqlWindow(List<SelectItem> selectItems)
        {
            InitializeComponent();
            LoadSelectItems(selectItems);
            SelectedTabIndex = 0;
            _filteredItemsView = CollectionViewSource.GetDefaultView(Items);
            _filteredItemsView.Filter = item => FilterCondition(item as SelectItem);
            SelectTableCommand = new RelayCommand<SelectItem>(SelectTable);
            DataContext = this;
        }

        public void SelectTable(SelectItem selectItem)
        {
            if (SelectedItem != null && SelectedItem.Key == selectItem.Key)
            {
                SelectedItem.IsSelected = false;
                SelectedItem = null;
            }
            else
            {
                foreach (var t in Items)
                {
                    t.IsSelected = false;
                }
                selectItem.IsSelected = true;
                SelectedItem = selectItem;
            }

            OnPropertyChanged(nameof(SelectedItem));
        }

        public void LoadSelectItems(IEnumerable<SelectItem> tables)
        {
            Items.Clear();
            foreach (var table in tables.OrderBy(t => t.Key).ThenBy(t => t.Value))
                Items.Add(table);
        }

        private void Wybierz_Click(object sender, RoutedEventArgs e)
        {
            var response = new GenerateResponseFromSqlResponse();
            response.TabIndex = SelectedTabIndex;
            response.ClassName = ClassNameText;
            if (SelectedTabIndex == 0)
            {
                response.SqlQuery = SqlQuery;
            }
            else if (SelectedTabIndex == 1)
            {
                response.Schema = SelectedItem.Key;
                response.Name = SelectedItem.Value;
                response.Type = SelectedItem.Type;
            }
            else
            {
                Close();
            }

            Response = response;

            Close();
        }

        private void Anuluj_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        private bool FilterCondition(SelectItem selectItem)
        {
            if (selectItem == null) return false;
            return string.IsNullOrWhiteSpace(SearchText) ||
                   selectItem.Key.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   selectItem.Value.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
