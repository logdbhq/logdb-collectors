namespace com.logdb.nginx.collector.Configuration;

public class NginxBlockOptions
{
    public const string Section = "NginxBlock";

    /// <summary>
    /// Enable nginx-level IP blocking. Requires write access to the nginx config
    /// directory and ability to signal nginx for reload.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Path to the nginx deny-list config file managed by the collector.
    /// This file is overwritten on each change.
    /// </summary>
    public string DenyFilePath { get; set; } = "/etc/nginx/conf.d/logdb-blocked-ips.conf";

    /// <summary>
    /// Shell command to reload nginx after updating the deny file.
    /// </summary>
    public string ReloadCommand { get; set; } = "nginx -s reload";
}
