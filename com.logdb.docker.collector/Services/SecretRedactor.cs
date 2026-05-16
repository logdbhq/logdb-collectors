namespace com.logdb.docker.collector.Services;

public static class SecretRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Length <= 4) return "****";
        return value[..2] + new string('*', Math.Min(value.Length - 4, 20)) + value[^2..];
    }
}
