using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PropertyAuditTool.Services;

/// <summary>A validated filter shared by simple and expression mode.</summary>
public sealed class AuditCompiledFilter
{
    internal AuditCompiledFilter(string kind, string operation, string? operand, Regex? pattern)
    {
        ValueType = kind;
        Operator = operation;
        Value = operand;
        Pattern = pattern;
    }

    public string ValueType { get; }
    public string Operator { get; }
    public string? Value { get; }
    private Regex? Pattern { get; }

    public bool Matches(object? rawValue, bool applicable = true, bool readable = true)
    {
        if (!applicable || !readable) return false;
        var text = AuditFilterCompiler.Text(rawValue);
        var empty = string.IsNullOrWhiteSpace(text);
        if (Operator == "isEmpty") return empty;
        if (Operator == "hasText") return !empty;
        if (empty) return false;
        if (ValueType == "text")
        {
            return Operator switch
            {
                "equals" => string.Equals(text, Value, StringComparison.OrdinalIgnoreCase),
                "notEquals" => !string.Equals(text, Value, StringComparison.OrdinalIgnoreCase),
                "contains" => text.Contains(Value!, StringComparison.OrdinalIgnoreCase),
                "notContains" => !text.Contains(Value!, StringComparison.OrdinalIgnoreCase),
                "startsWith" => text.StartsWith(Value!, StringComparison.OrdinalIgnoreCase),
                "endsWith" => text.EndsWith(Value!, StringComparison.OrdinalIgnoreCase),
                "like" => Pattern!.IsMatch(text),
                _ => false,
            };
        }
        int comparison;
        if (ValueType == "number")
        {
            if (!AuditFilterCompiler.Number(text, out var number)) return false;
            AuditFilterCompiler.Number(Value!, out var operand);
            comparison = number.CompareTo(operand);
        }
        else if (ValueType == "date")
        {
            if (!AuditFilterCompiler.Date(rawValue, out var date)) return false;
            AuditFilterCompiler.Date(Value, out var operand);
            comparison = date.CompareTo(operand);
        }
        else
        {
            if (!AuditFilterCompiler.Boolean(rawValue, out var boolean)) return false;
            AuditFilterCompiler.Boolean(Value, out var operand);
            comparison = boolean.CompareTo(operand);
        }
        return Operator switch
        {
            "equals" => comparison == 0,
            "notEquals" => comparison != 0,
            "greaterThan" => comparison > 0,
            "greaterOrEqual" => comparison >= 0,
            "lessThan" => comparison < 0,
            "lessOrEqual" => comparison <= 0,
            _ => false,
        };
    }
}

public static class AuditFilterCompiler
{
    private static readonly HashSet<string> Types = ["text", "number", "date", "boolean"];
    private static readonly HashSet<string> Operators =
    [
        "isEmpty", "hasText", "equals", "notEquals", "contains", "notContains",
        "startsWith", "endsWith", "like", "greaterThan", "greaterOrEqual",
        "lessThan", "lessOrEqual",
    ];
    private static readonly HashSet<string> TextOnly =
        ["contains", "notContains", "startsWith", "endsWith", "like"];
    private static readonly HashSet<string> Ordered =
        ["greaterThan", "greaterOrEqual", "lessThan", "lessOrEqual"];
    private static readonly Regex Expression = new(
        @"^\s*(LIKE|!=|<>|>=|<=|=|>|<)\s*(.*?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex NumericLiteral = new(
        @"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex IsoDate = new(
        @"^[0-9]{4}-[0-9]{2}-[0-9]{2}(?:[Tt][0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]{1,7})?)?(?:[Zz]|[+-][0-9]{2}:[0-9]{2})?)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool TryCompile(
        string valueType, string operation, string? value, string? expression,
        out AuditCompiledFilter? compiled, out string? error)
    {
        compiled = null;
        error = null;
        if (!Types.Contains(valueType)) error = "Unsupported value type.";
        else if (value?.Length > 2000 || expression?.Length > 2000)
            error = "Filter values and expressions must be 2000 characters or fewer.";
        else if (expression is not null
            && !TryExpression(expression, out operation, out value, out error)) { }
        if (error is not null) return false;
        if (!Operators.Contains(operation)) error = "Unsupported filter operator.";
        else if (TextOnly.Contains(operation) && valueType != "text")
            error = "This operator is only available for text columns.";
        else if (Ordered.Contains(operation) && valueType is not ("number" or "date"))
            error = "Ordering comparisons require a number or date column.";
        else if (operation is not ("isEmpty" or "hasText"))
        {
            if (string.IsNullOrWhiteSpace(value)) error = "A non-empty comparison value is required.";
            else if (valueType == "number" && !Number(value, out _))
                error = "Enter a decimal number using a dot and no grouping separator.";
            else if (valueType == "date" && !Date(value, out _))
                error = "Enter an ISO date or timestamp.";
            else if (valueType == "boolean" && !bool.TryParse(value.Trim(), out _))
                error = "Enter true or false.";
        }
        if (error is not null) return false;
        Regex? pattern = null;
        if (operation == "like")
        {
            var escaped = Regex.Escape(Regex.Replace(value!, "%+", "%"))
                .Replace("%", ".*").Replace("_", ".");
            try
            {
                pattern = new Regex(
                    @"\A" + escaped + @"\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                        | RegexOptions.Singleline | RegexOptions.NonBacktracking,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                error = "The LIKE pattern is too complex.";
                return false;
            }
        }
        compiled = new AuditCompiledFilter(valueType, operation, value, pattern);
        return true;
    }

    private static bool TryExpression(
        string expression, out string operation, out string? value, out string? error)
    {
        operation = "";
        value = null;
        error = null;
        var match = Expression.Match(expression);
        if (!match.Success)
        {
            error = "Use a single comparison, for example != '' or LIKE '%a%'.";
            return false;
        }
        var token = match.Groups[2].Value;
        if (token.Length > 0 && token[0] is '\'' or '"')
        {
            var quote = token[0];
            if (token.Length < 2 || token[^1] != quote)
            {
                error = "Close the quoted comparison value.";
                return false;
            }
            var result = new StringBuilder();
            for (var index = 1; index < token.Length - 1; index++)
            {
                var character = token[index];
                if (character == quote)
                {
                    if (index + 1 >= token.Length - 1 || token[index + 1] != quote)
                    {
                        error = "Only one value is allowed; double an embedded quote to escape it.";
                        return false;
                    }
                    index++;
                }
                result.Append(character);
            }
            value = result.ToString();
        }
        else if (NumericLiteral.IsMatch(token) || bool.TryParse(token, out _)) value = token;
        else
        {
            error = "Quote text/date values or enter a numeric/boolean literal.";
            return false;
        }
        operation = match.Groups[1].Value.ToUpperInvariant() switch
        {
            "=" => value.Length == 0 ? "isEmpty" : "equals",
            "!=" or "<>" => value.Length == 0 ? "hasText" : "notEquals",
            ">" => "greaterThan",
            ">=" => "greaterOrEqual",
            "<" => "lessThan",
            "<=" => "lessOrEqual",
            "LIKE" => "like",
            _ => "",
        };
        return true;
    }

    public static string Text(object? raw)
    {
        if (raw is JsonElement element)
            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                JsonValueKind.String => element.GetString() ?? "",
                _ => element.GetRawText(),
            };
        return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
    }

    public static bool Number(string value, out decimal number) => decimal.TryParse(
        value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
            | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out number);

    public static bool Boolean(object? raw, out bool boolean)
    {
        var value = Text(raw).Trim();
        if (value == "0") { boolean = false; return true; }
        if (value == "1") { boolean = true; return true; }
        return bool.TryParse(value, out boolean);
    }

    public static bool Date(object? raw, out DateTimeOffset date)
    {
        if (raw is DateTimeOffset offset) { date = offset.ToUniversalTime(); return true; }
        if (raw is DateTime value)
        {
            date = new DateTimeOffset(value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value).ToUniversalTime();
            return true;
        }
        var text = Text(raw);
        date = default;
        return IsoDate.IsMatch(text) && DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
    }
}
