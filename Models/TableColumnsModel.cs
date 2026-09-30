using System.Collections.Generic;
using System.Linq;

namespace Mroczware.PowerFullTool.Models
{
    public class TableColumnsModel
    {
        public List<string> CrudOperations { get; set; }
        public string TableName { get; set; }
        public List<ColumnModel> ColumnsAll { get; set; }
        public List<ColumnModel> Columns { get; set; }
        public List<ColumnModel> ColumnsNoId { get; set; }

        public TableColumnsModel Normalize()
        {
            return new TableColumnsModel
            {
                CrudOperations = CrudOperations,
                TableName = TableName,
                Columns = NormalizeColumnNames(Columns),
                ColumnsNoId = NormalizeColumnNames(ColumnsNoId),
                ColumnsAll = NormalizeColumnNames(ColumnsAll)
            };
        }

        public static List<ColumnModel> NormalizeColumnNames(List<ColumnModel> columnsAll)
        {
            return [.. columnsAll.Select(x => new ColumnModel(x.Name.Replace("_Id","Id"), x.DataType, x.DatabaseDataType, x.IsNullable, x.IsIdentity, x.MaxLength))];
        }
    }
}
