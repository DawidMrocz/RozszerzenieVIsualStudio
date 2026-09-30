using System.Collections.Generic;
using System.ComponentModel;

namespace Mroczware.PowerFullTool.Models
{
    public class TableModel : ISelectItem, INotifyPropertyChanged
    {
        private bool _isSelected;

        public TableModel(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; set; }
        public string Value { get; set; }
        public string CapitalizedSchema => char.ToUpper(Key[0]) + Key.Substring(1);
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

    public class ReferencedTableModel : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ReferencedTableModel(string parentColumn,
                                    bool parentColumnIsNullable,
                                    string referencedSchema,
                                    string referencedTable,
                                    string referencedColumn)
        {
            ParentColumn = parentColumn;
            ParentColumnIsNullable = parentColumnIsNullable;
            ReferencedSchema = referencedSchema;
            ReferencedTable = referencedTable;
            ReferencedColumn = referencedColumn;
        }

        public string ParentColumn { get; set; }
        public bool ParentColumnIsNullable { get; set; }
        public string ReferencedSchema { get; set; }
        public string ReferencedTable { get; set; }
        public string ReferencedColumn { get; set; }
        public List<ColumnInfo> Columns { get; set; } = [];
        public string ReferencedName => $"[{ReferencedSchema}].[{ReferencedTable}]";
        public string ReferencedAlias { get; set; }
        public string TypeOfJoin => ParentColumnIsNullable ? "LEFT JOIN" : "INNER JOIN";
        //public string CapitalizedSchema => char.ToUpper(Key[0]) + Key.Substring(1);
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

    public class SettingsInfo : ISelectItem
    {
        public SettingsInfo(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; set; }
        public string Value { get; set; }

    }

    public interface ISelectItem
    {
        string Key { get; set; }
        string Value { get; set; }
    }
}

