namespace Planner.Endpoints.Http;

/// <summary>
/// Bounds the JSON envelope for the existing 5 MiB UTF-8 CSV upload. System.Text.Json can emit
/// six ASCII bytes for one input byte (for example a control character as <c>\u0000</c>); the
/// filename is bounded by the existing 500-character audit column. The extra 64 bytes cover the
/// verified property names, quotes, comma, and braces in the Web JSON DTO wrapper.
/// </summary>
public static class TeambegeleidingImportRequestBodyLimits
{
    public const int CsvFileMaxBytes = 5 * 1024 * 1024;
    public const int AuditFilenameMaxCharacters = 500;
    public const int JsonWrapperOverheadBytes = 64;
    public const int MaxJsonBytes =
        (6 * CsvFileMaxBytes) + (6 * AuditFilenameMaxCharacters) + JsonWrapperOverheadBytes;
}
