using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using Mroczware.PowerFullTool.Models;
using Mroczware.PowerFullTool.UI.ViewModel;

namespace Mroczware.PowerFullTool.UI.View
{
    /// <summary>
    /// Interaction logic for LogsWindow.xaml
    /// </summary>
    public partial class LogsWindow : Window
    {
        private readonly TableViewModel _viewModel;
        public TableModel SelectedTable => _viewModel.SelectedTable;

        public LogsWindow(List<TableModel> tables)
        {
            InitializeComponent();
            WindowStartupLocation = WindowStartupLocation.Manual;
            _viewModel = new TableViewModel();

            _viewModel.LoadTables(tables);

            _viewModel.RequestClose += (s, e) =>
            {
                if (e.HasValue)
                    this.DialogResult = e;
                this.Close();
            };

            DataContext = _viewModel;
        }

        private void MoveWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                Window.GetWindow(this)?.DragMove();
        }
    }
}
