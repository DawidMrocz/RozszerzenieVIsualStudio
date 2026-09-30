namespace Mroczware.PowerFullTool.Models
{
    public class ParameterItem
    {
        public ParameterItem(string name, string type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; set; }
        public string Type { get; set; }
    }
}
