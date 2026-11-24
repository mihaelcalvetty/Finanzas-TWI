using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace FinanzasPersonales.Api.Authentication;

// El middleware base conserva state, correlación, challenge y PKCE.
// Solo se adapta el canje de códigos al HTTP Basic exigido por X para clientes web.
public sealed class XOAuthHandler(
    IOptionsMonitor<OAuthOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : OAuthHandler<OAuthOptions>(options, logger, encoder)
{
    protected override async Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeContext context)
    {
        if (!context.Properties.Items.Remove(OAuthConstants.CodeVerifierKey, out var verifier) ||
            string.IsNullOrWhiteSpace(verifier))
            return OAuthTokenResponse.Failed(new AuthenticationFailureException("Falta el verificador PKCE de X."));

        using var request = new HttpRequestMessage(HttpMethod.Post, Options.TokenEndpoint);
        var credentials = WebUtility.UrlEncode(Options.ClientId) + ":" + WebUtility.UrlEncode(Options.ClientSecret);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = context.Code,
            ["redirect_uri"] = context.RedirectUri,
            ["code_verifier"] = verifier
        });
        using var response = await Backchannel.SendAsync(request, Context.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return OAuthTokenResponse.Failed(new AuthenticationFailureException("X rechazó el intercambio de autorización."));

        try
        {
            var payload = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(Context.RequestAborted), cancellationToken: Context.RequestAborted);
            return OAuthTokenResponse.Success(payload);
        }
        catch (JsonException)
        {
            return OAuthTokenResponse.Failed(new AuthenticationFailureException("X devolvió una respuesta de autorización inválida."));
        }
    }
}

// state opaco de 43 caracteres. El valor completo sigue protegido por Data Protection.
// Usar un IDistributedCache compartido (o afinidad) si se balancea un callback entre nodos.
internal sealed class XCachedStateFormat(
    ISecureDataFormat<AuthenticationProperties> inner, IDistributedCache cache, TimeSpan lifetime)
    : ISecureDataFormat<AuthenticationProperties>
{
    private const string Prefix = "Finanzas:X:OAuthState:";

    public string Protect(AuthenticationProperties data) => Protect(data, null);

    public string Protect(AuthenticationProperties data, string? purpose)
    {
        var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        cache.SetString(Prefix + key, inner.Protect(data, purpose), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = lifetime
        });
        return key;
    }

    public AuthenticationProperties? Unprotect(string? protectedText) => Unprotect(protectedText, null);

    public AuthenticationProperties? Unprotect(string? protectedText, string? purpose)
    {
        if (protectedText is null || !Regex.IsMatch(protectedText, "^[A-Za-z0-9_-]{43}$")) return null;
        var encrypted = cache.GetString(Prefix + protectedText);
        if (encrypted is null) return null;
        cache.Remove(Prefix + protectedText);
        return inner.Unprotect(encrypted, purpose);
    }
}
