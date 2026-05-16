namespace com.logdb.docker.collector.Models;

public class ContainerInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string ImageTag { get; set; } = "";
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string LogPath { get; set; } = "";
    public Dictionary<string, string> Labels { get; set; } = new();
    public string? ComposeProject { get; set; }
    public string? ComposeService { get; set; }
    public bool IsIncluded { get; set; } = true;
    public string? ExclusionReason { get; set; }
}
