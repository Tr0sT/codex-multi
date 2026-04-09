using System.Text;
using System.Text.Json;

namespace CodexMulti.Auth;

internal sealed class AuthInspector
{
    public AuthInspectionResult Inspect(string authJson)
    {
        using var document = JsonDocument.Parse(authJson);
        var root = document.RootElement;

        string? authMode = TryGetString(root, "auth_mode");
        string? accountId = null;
        string? email = null;

        if (root.TryGetProperty("tokens", out var tokensElement))
        {
            accountId = TryGetString(tokensElement, "account_id");
            email = ExtractEmail(tokensElement);
        }

        return new AuthInspectionResult(
            authMode,
            accountId,
            MaskEmail(email));
    }

    private static string? ExtractEmail(JsonElement tokensElement)
    {
        foreach (var tokenName in new[] { "id_token", "access_token" })
        {
            var jwt = TryGetString(tokensElement, tokenName);
            var email = TryGetEmailFromJwt(jwt);
            if (!string.IsNullOrWhiteSpace(email))
            {
                return email;
            }
        }

        return null;
    }

    private static string? TryGetEmailFromJwt(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadBytes = DecodeBase64Url(parts[1]);
            using var payloadDocument = JsonDocument.Parse(payloadBytes);
            var root = payloadDocument.RootElement;

            var directEmail = TryGetString(root, "email");
            if (!string.IsNullOrWhiteSpace(directEmail))
            {
                return directEmail;
            }

            if (root.TryGetProperty("https://api.openai.com/profile", out var profileElement))
            {
                var nestedEmail = TryGetString(profileElement, "email");
                if (!string.IsNullOrWhiteSpace(nestedEmail))
                {
                    return nestedEmail;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
        }

        return Convert.FromBase64String(normalized);
    }

    private static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var parts = email.Split('@', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        var localPart = parts[0];
        var visibleChars = Math.Min(3, localPart.Length);
        return $"{localPart[..visibleChars]}***@{parts[1]}";
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }
}

internal sealed record AuthInspectionResult(
    string? AuthMode,
    string? AccountId,
    string? EmailHint);
