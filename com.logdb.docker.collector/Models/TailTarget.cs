namespace com.logdb.docker.collector.Models;

public class TailTarget
{
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string Image { get; set; } = "";
    public string ImageTag { get; set; } = "";
    public string LogPath { get; set; } = "";
    public string? ComposeProject { get; set; }
    public string? ComposeService { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
}
