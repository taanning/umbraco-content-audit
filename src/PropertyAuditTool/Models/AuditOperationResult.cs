namespace PropertyAuditTool.Models;

public enum AuditErrorType
{
    Validation,
    Forbidden,
}

/// <summary>
/// Outcome of a service operation: either a value, a 400 validation error, or a 403 denial.
/// </summary>
public sealed class AuditOperationResult<T>
{
    public T? Value { get; private init; }

    public AuditErrorType? ErrorType { get; private init; }

    /// <summary>Field-name => messages for validation errors.</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; private init; }

    public string? ForbiddenMessage { get; private init; }

    public bool Succeeded => ErrorType is null;

    public static AuditOperationResult<T> Success(T value) => new() { Value = value };

    public static AuditOperationResult<T> ValidationError(IDictionary<string, string[]> errors) =>
        new() { ErrorType = AuditErrorType.Validation, Errors = new Dictionary<string, string[]>(errors) };

    public static AuditOperationResult<T> Forbidden(string message) =>
        new() { ErrorType = AuditErrorType.Forbidden, ForbiddenMessage = message };
}
