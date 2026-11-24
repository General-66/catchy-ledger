using Ledger.Domain;

namespace Ledger.Api.Errors;

/// <summary>Collects everything wrong with a request, keyed by the JSON field it's about, so one 400 can list it all.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, string[]> _errors = [];

    public bool Any => _errors.Count > 0;

    public void Add(string field, string message) =>
        _errors[field] = _errors.TryGetValue(field, out var earlier) ? [.. earlier, message] : [message];

    public void RequireText(string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(field, "Required.");
        }
        else if (value.Length > maxLength)
        {
            Add(field, $"At most {maxLength} characters.");
        }
    }

    /// <summary>Checks a currency code against the currencies the domain knows, without throwing.</summary>
    public void RequireCurrency(string field, string? code)
    {
        try
        {
            Currency.FromCode(code ?? "");
        }
        catch (ArgumentException)
        {
            Add(field, $"'{code}' isn't a supported currency. Use an upper-case ISO 4217 code, such as ZAR.");
        }
    }

    public IDictionary<string, string[]> ToDictionary() => _errors;
}