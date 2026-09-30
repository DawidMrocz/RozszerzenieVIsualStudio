namespace Mroczware.PowerFullTool.Models
{
    public class ColumnModel
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public string DatabaseDataType { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public int? MaxLength { get; set; }

        public ColumnModel(string name, string dataType, string databaseDatatype, bool isNullable, bool isIdentity, int? maxLength)
        {
            Name = name;
            DataType = dataType;
            DatabaseDataType = databaseDatatype;
            IsNullable = isNullable;
            IsIdentity = isIdentity;
            MaxLength = maxLength;
        }
    }
}
