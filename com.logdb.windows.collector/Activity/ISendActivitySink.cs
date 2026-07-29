namespace com.logdb.windows.collector.Activity;

/// <summary>
/// Records how many records the collector handed to the server, tagged by
/// module / collection / host and split by outcome (success vs failed). Powers
/// the Throughput charts in the admin UI.
///
/// Implemented by <see cref="SendActivityTracker"/>. A <see cref="RecordingLogDbClient"/>
/// wraps each module's <c>ILogDBClient</c> and calls <see cref="Record"/> for
/// every batch, so capture is uniform across all modules without touching the
/// shared exporter libraries.
/// </summary>
public interface ISendActivitySink
{
    /// <summary>
    /// Record one send.
    /// </summary>
    /// <param name="module">Logical module / log type, e.g. "EventLog", "IIS", "Nginx", "Docker", "Metrics".</param>
    /// <param name="collection">Per-source collection if known (e.g. an event channel); may be null/empty.</param>
    /// <param name="host">Originating host / configured server name; may be null/empty.</param>
    /// <param name="records">Number of records in the batch (clamped to &gt;= 0).</param>
    /// <param name="success">True if the server accepted the batch.</param>
    /// <param name="whenUtc">When the send completed (UTC).</param>
    void Record(string module, string? collection, string? host, long records, bool success, DateTime whenUtc);
}
