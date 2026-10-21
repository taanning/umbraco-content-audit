using System.Globalization;

namespace PropertyAuditTool.Services;

/// <summary>
/// Evaluates a persisted raw property value against an audit operator.
/// </summary>
/// <remarks>
/// Values are converted to strings invariantly; <c>0</c> and <c>false</c> count as
/// filled. No JSON/HTML/block parsing or trimming is applied — stored nonblank
/// markup/JSON counts as filled even when an editor would render it empty.
/// </remarks>
public static class AuditValueEvaluator
{
    public const string OperatorIsEmpty = "isEmpty";
    public const string OperatorHasText = "hasText";
    public const string OperatorContains = "contains";
    public const string OperatorEquals = "equals";

    public static readonly IReadOnlySet<string> SupportedOperators = new HashSet<string>
    {
        OperatorIsEmpty,
        OperatorHasText,
        OperatorContains,
        OperatorEquals,
    };

    public static bool RequiresSearchValue(string @operator) =>
        @operator is OperatorContains or OperatorEquals;

    /// <summary>
    /// Returns whether the value matched the operator and whether the stored value was empty.
    /// </summary>
    public static (bool Matches, bool IsEmpty) Evaluate(string @operator, object? rawValue, string? searchValue)
    {
        var text = Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;
        var empty = string.IsNullOrWhiteSpace(text);

        var matches = @operator switch
        {
            OperatorIsEmpty => empty,
            OperatorHasText => !empty,
            OperatorContains => text.Contains(searchValue!, StringComparison.OrdinalIgnoreCase),
            OperatorEquals => string.Equals(text, searchValue, StringComparison.OrdinalIgnoreCase),
            _ => throw new InvalidOperationException($"Unsupported operator '{@operator}'."),
        };

        return (matches, empty);
    }
}
