namespace BlazorAdmin.Models;

/// <summary>
/// Eenvoudige Result-wrapper voor HttpClient calls. Vermijdt exceptions in de UI-laag.
/// </summary>
public class ApiResult<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public string? ErrorMessage { get; init; }
    public int StatusCode { get; init; }
    /// <summary>Machineleesbare foutcode uit de respons (<c>code</c>), bijv. <c>dubbelzinnig</c>; <c>null</c> als de server er geen meestuurt.</summary>
    public string? Code { get; init; }

    public static ApiResult<T> Ok(T data, int status = 200)
        => new() { Success = true, Data = data, StatusCode = status };

    public static ApiResult<T> Fail(string error, int status = 500, string? code = null)
        => new() { Success = false, ErrorMessage = error, StatusCode = status, Code = code };
}
