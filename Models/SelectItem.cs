using System.ComponentModel;

namespace Mroczware.PowerFullTool.Models
{
    public class SelectItem : ISelectItem, INotifyPropertyChanged
    {
        private bool _isSelected;

        public SelectItem(string key, string value, string type)
        {
            Key = key;
            Value = value;
            Type = type;
        }

        public string Key { get; set; }
        public string Value { get; set; }
        public string Type { get; set; }
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

