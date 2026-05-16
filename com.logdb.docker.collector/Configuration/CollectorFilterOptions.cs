namespace com.logdb.docker.collector.Configuration;

public class CollectorFilterOptions
{
    public const string Section = "CollectorFilter";

    public List<string> IncludeContainerNames { get; set; } = new();
    public List<string> ExcludeContainerNames { get; set; } = new();
    public List<string> IncludeImages { get; set; } = new();
    public List<string> ExcludeImages { get; set; } = new();
    public Dictionary<string, string> IncludeLabels { get; set; } = new();
    public Dictionary<string, string> ExcludeLabels { get; set; } = new();
}
