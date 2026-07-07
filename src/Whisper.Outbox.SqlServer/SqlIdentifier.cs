namespace Whisper.Outbox.SqlServer;

/// <summary>Escaping helpers for host-configured SQL identifiers.</summary>
internal static class SqlIdentifier
{
    /// <summary>Bracketed identifier for DDL/DML contexts.</summary>
    public static string Bracket(string identifier)
        => $"[{Strip(identifier)}]";

    /// <summary>The raw identifier with brackets stripped, e.g. for catalog-name comparisons.</summary>
    public static string Strip(string identifier)
        => identifier.Replace("[", string.Empty).Replace("]", string.Empty);

    /// <summary>Escapes a value interpolated inside an N'...' string literal.</summary>
    public static string EscapeLiteral(string value)
        => value.Replace("'", "''");
}
