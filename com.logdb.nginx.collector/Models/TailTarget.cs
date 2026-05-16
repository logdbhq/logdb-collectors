namespace com.logdb.nginx.collector.Models;

public class TailTarget
{
    public string TargetName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public NginxLogType LogType { get; set; }
    public bool Enabled { get; set; } = true;
}
