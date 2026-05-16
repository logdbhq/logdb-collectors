namespace com.logdb.docker.collector.Models;

public class EstimateSizeRequest
{
    public string LogPath { get; set; } = "";
    public DateTime? FromUtc { get; set; }
}
