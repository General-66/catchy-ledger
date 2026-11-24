using System.Security.Cryptography;
using System.Text;
using Ledger.Infrastructure;

namespace Ledger.Api.Authentication;

/// <summary>What a key may do. A Write key can also read.</summary>
public enum ApiRole
{
    /// <summary>GET requests only: account details, balances, transactions.</summary>
    Read,

    /// <summary>Everything Read can do, plus opening accounts, posting and reversing.</summary>
    Write,
}

/// <summary>Who is calling: the name their key belongs to, which becomes CreatedBy on their transactions, and their role.</summary>
public sealed record ApiCaller(string Name, ApiRole Role);

/// <summary>The two authorization policies. Every endpoint names one, and one that names none still needs a valid key.</summary>
public static class LedgerPolicies
{
    public const string Read = "Read";
    public const string Write = "Write";
}

/// <summary>
/// The API keys from configuration: ApiKeys:{caller name}:Key and ApiKeys:{caller name}:Role. They live in User
/// Secrets on your laptop and in App Service settings in Azure, never in Git. Only SHA-256 hashes of the keys are kept
/// in memory, and every lookup compares against every key in constant time, so response times give an attacker no
/// hint about how close a guess was.
/// </summary>
public sealed class ApiKeyStore
{
    /// <summary>Short keys are guessable. A GUID (36 characters) is long enough.</summary>
    public const int MinimumKeyLength = 32;

    private readonly (byte[] Hash, ApiCaller Caller)[] _keys;

    public ApiKeyStore(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var keys = new List<(byte[] Hash, ApiCaller Caller)>();
        foreach (var entry in configuration.GetSection("ApiKeys").GetChildren())
        {
            var name = entry.Key;
            if (name.Length > RequestInfo.MaxLength || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                throw Misconfigured($"ApiKeys:{name}: a caller's name is up to {RequestInfo.MaxLength} letters, digits, dots, hyphens or underscores.");
            }

            var key = entry["Key"];
            if (key is null || key.Length < MinimumKeyLength)
            {
                throw Misconfigured($"ApiKeys:{name}:Key must be at least {MinimumKeyLength} characters long. A new GUID is fine.");
            }

            if (!Enum.TryParse<ApiRole>(entry["Role"], ignoreCase: true, out var role) || !Enum.IsDefined(role))
            {
                throw Misconfigured($"ApiKeys:{name}:Role must be Read or Write.");
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            if (keys.Exists(known => CryptographicOperations.FixedTimeEquals(known.Hash, hash)))
            {
                throw Misconfigured($"ApiKeys:{name}:Key is the same as another caller's key. Every caller needs a key of its own.");
            }

            keys.Add((hash, new ApiCaller(name, role)));
        }

        if (keys.Count == 0)
        {
            throw Misconfigured("No API keys are configured. Add at least one under ApiKeys, using Manage User Secrets on Ledger.Api.");
        }

        _keys = [.. keys];
    }

    /// <summary>The caller a key belongs to, or null if no caller has that key.</summary>
    public ApiCaller? Find(string presentedKey)
    {
        ArgumentNullException.ThrowIfNull(presentedKey);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        ApiCaller? match = null;
        foreach (var (keyHash, caller) in _keys)
        {
            // No early exit: every key is compared, whether or not one already matched.
            if (CryptographicOperations.FixedTimeEquals(keyHash, hash))
            {
                match = caller;
            }
        }

        return match;
    }

    private static InvalidOperationException Misconfigured(string message) => new($"API key configuration: {message}");
}