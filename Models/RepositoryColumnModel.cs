namespace Mroczware.PowerFullTool.Models
{
    public class RepositoryColumnModel : ColumnModel
    {
        public string RepositoryColumnName { get; set; }
        public RepositoryColumnModel(
            string name, 
            string dataType,
            string databaseDatatype,
            string repositoryColumnName,
            bool isNullable, 
            bool isIdentity,
            int? maxLength
            ) : base(name, dataType, databaseDatatype, isNullable, isIdentity, maxLength)
        {
            RepositoryColumnName = repositoryColumnName;
        }
    }
}
