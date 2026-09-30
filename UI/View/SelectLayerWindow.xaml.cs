using System.Windows;
using System.Windows.Controls;

namespace Mroczware.PowerFullTool.UI.View
{
    /// <summary>
    /// Interaction logic for SelectLayerWindow.xaml
    /// </summary>
    public partial class SelectLayerWindow : Window
    {
        public LayerTypeEnum? Layer { get; set; }
        private LayerTypeEnum SelectedLayer { get; set; }
        public bool AsynchronousGenerate { get; set; }

        public SelectLayerWindow()
        {
            InitializeComponent();
            CheckBoxAsynchronous.IsChecked = true;
        }

        private void RadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton)
                switch (radioButton.Tag.ToString())
                {
                    case "Controllers_Services_Repositories":
                        SelectedLayer = LayerTypeEnum.Controllers_Services_Repositories;
                        break;
                    case "Services_Repositories":
                        SelectedLayer = LayerTypeEnum.Services_Repositories;
                        break;
                    case "Repositories":
                        SelectedLayer = LayerTypeEnum.Repositories;
                        break;
                }
        }

        private void CheckBoxAsynchronous_Checked(object sender, RoutedEventArgs e)
        {
            var checkBox = sender as CheckBox;
            if (checkBox != null)
            {
                AsynchronousGenerate = true;
            }
        }

        private void CheckBoxAsynchronous_Unchecked(object sender, RoutedEventArgs e)
        {
            var checkBox = sender as CheckBox;
            if (checkBox != null)
            {
                AsynchronousGenerate = false;
            }
        }

        private void Wybierz_Click(object sender, RoutedEventArgs e)
        {
            Layer = SelectedLayer;
            Close();
        }

        private void Anuluj_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public enum LayerTypeEnum
    {
        Controllers_Services_Repositories,
        Services_Repositories,
        Repositories,
    }
}
