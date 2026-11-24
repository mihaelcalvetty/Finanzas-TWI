using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Validators;
using FinanzasPersonales.Api.Data;   // Ajusta al namespace de tu DbContext
using FinanzasPersonales.Api.Models; // Ajusta al namespace de tu entidad Usuario
using Microsoft.EntityFrameworkCore;

namespace FinanzasPersonales.Api.Authentication;

public sealed record ExternalProvider(string Name, string Scheme, string CallbackPath);

public static class ExternalAuthentication
{
    public const string LocalScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string LoginPath = "/";
    public const string ProviderClaim = "external_provider";
    public static IReadOnlyList<ExternalProvider> Providers { get; } = Array.AsReadOnly(new[]
    {
        new ExternalProvider("Google", "Google", "/api/auth/google/callback"),
        new ExternalProvider("Discord", "Discord", "/api/auth/discord/callback"),
        new ExternalProvider("Twitch", "Twitch", "/signin-twitch"),
        new ExternalProvider("Spotify", "Spotify", "/signin-spotify"),
        new ExternalProvider("Dropbox", "Dev3.Dropbox", "/auth/dropbox/callback"),
        new ExternalProvider("Microsoft", "Dev3.Microsoft", "/auth/microsoft/callback"),
        new ExternalProvider("X", "X", "/api/auth/x/callback"),
        new ExternalProvider("Slack", "Slack", "/api/auth/slack/callback")
    });

    public static IServiceCollection AddExternalAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Los handlers pueden registrar cuerpos de error remotos: solo se permiten avisos sanitizados.
        services.AddLogging(logging =>
        {
            logging.AddFilter("Microsoft.AspNetCore.Authentication.OAuth.OAuthHandler", LogLevel.None);
            logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler", LogLevel.None);
            logging.AddFilter("FinanzasPersonales.Api.Authentication.XOAuthHandler", LogLevel.None);
        });
        var isHttpMode = configuration["USE_HTTP_COOKIES"] == "true";
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "RequestVerificationToken";
            options.Cookie.Name = configuration["ANTIFORGERY_COOKIE_NAME"] ??
                (isHttpMode ? "Finanzas.Antiforgery" : "__Host-Finanzas.Antiforgery");
            options.Cookie.SecurePolicy = isHttpMode ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = isHttpMode ? SameSiteMode.Lax : SameSiteMode.Strict;
        });

        // ÚNICO registro de la cookie local para todo el backend.
        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultScheme = LocalScheme;
            options.DefaultSignInScheme = LocalScheme;
            options.DefaultAuthenticateScheme = LocalScheme;
            options.DefaultChallengeScheme = LocalScheme;
        }).AddCookie(LocalScheme, options =>
        {
            options.Cookie.Name = configuration["AUTH_COOKIE_NAME"] ?? ".FinanzasPersonales.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = isHttpMode ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = isHttpMode ? SameSiteMode.Lax : SameSiteMode.None;
            options.LoginPath = LoginPath;
            options.AccessDeniedPath = LoginPath;
            options.LogoutPath = "/api/auth/logout";
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => RedirectOrStatus(context, 401);
            options.Events.OnRedirectToAccessDenied = context => RedirectOrStatus(context, 403);
        });

        var callbacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in Providers)
        {
            var callback = Callback(configuration, provider);
            if (!callbacks.Add(callback))
                throw new InvalidOperationException("Dos proveedores tienen el mismo CallbackPath.");

            var clientId = Value(configuration, provider.Name, "ClientId");
            var secret = Value(configuration, provider.Name, "ClientSecret");
            // No registrar proveedores incompletos ni placeholders de plantillas anteriores.
            if (clientId is null || secret is null) continue;

            if (provider.Name is "Google" or "Microsoft" or "Slack")
            {
                var tenant = Value(configuration, provider.Name, "TenantId") ?? "common";
                if (provider.Name == "Microsoft" && tenant is not ("common" or "consumers" or "organizations") &&
                    !Guid.TryParse(tenant, out _))
                    throw new InvalidOperationException("Microsoft TenantId debe ser common, consumers, organizations o un GUID.");
                authentication.AddOpenIdConnect(provider.Scheme,
                    options => ConfigureOidc(options, provider, clientId, secret, callback, tenant, isHttpMode));
            }
            else if (provider.Name == "X")
            {
                authentication.AddOAuth<OAuthOptions, XOAuthHandler>(provider.Scheme,
                    options => ConfigureOAuth(options, provider, clientId, secret, callback, isHttpMode));
                // X limita state a 500 caracteres; el state protegido estándar puede superarlos.
                // El caché guarda solo propiedades cifradas del intento, nunca tokens de acceso.
                services.AddOptions<OAuthOptions>(provider.Scheme).PostConfigure<IDistributedCache>((options, cache) =>
                    options.StateDataFormat = new XCachedStateFormat(options.StateDataFormat, cache, options.RemoteAuthenticationTimeout));
            }
            else
                authentication.AddOAuth(provider.Scheme,
                    options => ConfigureOAuth(options, provider, clientId, secret, callback, isHttpMode));
        }

        return services;
    }

    private static void ConfigureOAuth(OAuthOptions options, ExternalProvider provider,
        string clientId, string secret, string callback, bool isHttpMode = false)
    {
        options.SignInScheme = LocalScheme;
        options.ClientId = clientId;
        options.ClientSecret = secret;
        options.CallbackPath = callback;
        options.SaveTokens = false;
        options.UsePkce = provider.Name is "Dropbox" or "Spotify" or "X";
        options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
        options.BackchannelTimeout = TimeSpan.FromSeconds(30);
        options.CorrelationCookie.SecurePolicy = isHttpMode ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.CorrelationCookie.HttpOnly = true;
        options.CorrelationCookie.SameSite = isHttpMode ? SameSiteMode.Lax : SameSiteMode.None;
        var endpoints = provider.Name switch
        {
            "Discord" => ("https://discord.com/oauth2/authorize", "https://discord.com/api/oauth2/token",
                "https://discord.com/api/users/@me", new[] { "identify", "email" }),
            "Twitch" => ("https://id.twitch.tv/oauth2/authorize", "https://id.twitch.tv/oauth2/token",
                "https://api.twitch.tv/helix/users", new[] { "user:read:email" }),
            "Spotify" => ("https://accounts.spotify.com/authorize", "https://accounts.spotify.com/api/token",
                "https://api.spotify.com/v1/me", new[] { "user-read-email", "user-read-private" }),
            "Dropbox" => ("https://www.dropbox.com/oauth2/authorize", "https://api.dropboxapi.com/oauth2/token",
                "https://api.dropboxapi.com/2/users/get_current_account", new[] { "account_info.read" }),
            "X" => ("https://x.com/i/oauth2/authorize", "https://api.x.com/2/oauth2/token",
                "https://api.x.com/2/users/me", new[] { "users.read", "tweet.read" }),
            _ => throw new InvalidOperationException("Proveedor OAuth no soportado.")
        };
        options.AuthorizationEndpoint = endpoints.Item1;
        options.TokenEndpoint = endpoints.Item2;
        options.UserInformationEndpoint = endpoints.Item3;
        options.Scope.Clear();
        foreach (var scope in endpoints.Item4) options.Scope.Add(scope);
        options.Events = new OAuthEvents
        {
            OnCreatingTicket = context => ReadProfile(context, provider.Name),
            OnTicketReceived = context => NormalizeTicket(context, provider.Name),
            OnRemoteFailure = context => Failure(context, provider.Name),
            OnAccessDenied = context =>
            {
                var returnUrl = context.Properties?.RedirectUri;
                context.HandleResponse();
                context.Response.Redirect(ErrorPath(provider.Name, "cancelled", returnUrl));
                return Task.CompletedTask;
            }
        };
    }

    private static async Task ReadProfile(OAuthCreatingTicketContext context, string provider)
    {
        if (string.IsNullOrWhiteSpace(context.AccessToken))
            throw new AuthenticationFailureException("No se recibió una identidad válida.");
        using var request = new HttpRequestMessage(provider == "Dropbox" ? HttpMethod.Post : HttpMethod.Get,
            context.Options.UserInformationEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        if (provider == "Twitch") request.Headers.Add("Client-Id", context.Options.ClientId);
        if (provider == "Dropbox") request.Content = new StringContent("null", Encoding.UTF8, "application/json");
        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
            throw new AuthenticationFailureException("No se pudo consultar la identidad del proveedor.");
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted),
            cancellationToken: context.HttpContext.RequestAborted);
        var profile = document.RootElement;
        if (provider == "Twitch")
        {
            if (!profile.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                throw new AuthenticationFailureException("El proveedor no devolvió una identidad.");
            profile = data[0];
        }
        if (provider == "X")
        {
            if (!profile.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new AuthenticationFailureException("X no devolvió una identidad válida.");
            profile = data;
        }
        var id = Text(profile, provider == "Dropbox" ? "account_id" : "id");
        if (string.IsNullOrWhiteSpace(id)) throw new AuthenticationFailureException("Falta el identificador del proveedor.");
        var name = Text(profile, "display_name") ?? Text(profile, "global_name") ?? Text(profile, "username") ?? Text(profile, "login");
        if (provider == "X") name = Text(profile, "name") ?? Text(profile, "username");
        if (provider == "Dropbox" && profile.TryGetProperty("name", out var nested))
            name = Text(nested, "display_name");
        context.Identity!.AddClaim(new Claim(ClaimTypes.NameIdentifier, provider.ToLowerInvariant() + "|" + id));
        context.Identity.AddClaim(new Claim(ClaimTypes.Name, name ?? provider));
        if (Text(profile, "email") is string email)
            context.Identity.AddClaim(new Claim(ClaimTypes.Email, email));
    }

    private static void ConfigureOidc(OpenIdConnectOptions options, ExternalProvider provider,
        string clientId, string secret, string callback, string tenant, bool isHttpMode = false)
    {
        options.Authority = provider.Name switch
        {
            "Google" => "https://accounts.google.com",
            "Slack" => "https://slack.com",
            _ => $"https://login.microsoftonline.com/{tenant}/v2.0"
        };
        options.ClientId = clientId;
        options.ClientSecret = secret;
        options.CallbackPath = callback;
        options.SignInScheme = LocalScheme;
        options.RequireHttpsMetadata = true;
        options.ResponseType = OpenIdConnectResponseType.Code;
        // En HTTP local las cookies Lax no viajan en un POST desde Google/Microsoft.
        // Code + query devuelve solo el código por GET; los tokens se canjean en el servidor.
        // Slack publica únicamente query y no anuncia soporte PKCE en su discovery.
        options.ResponseMode = isHttpMode || provider.Name == "Slack" ? OpenIdConnectResponseMode.Query : OpenIdConnectResponseMode.FormPost;
        options.UsePkce = provider.Name != "Slack";
        options.SaveTokens = false;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        options.Scope.Clear();
        foreach (var scope in new[] { "openid", "profile", "email" }) options.Scope.Add(scope);
        options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
        options.BackchannelTimeout = TimeSpan.FromSeconds(30);
        options.CorrelationCookie.SecurePolicy = isHttpMode ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.CorrelationCookie.HttpOnly = true;
        options.CorrelationCookie.SameSite = isHttpMode ? SameSiteMode.Lax : SameSiteMode.None;
        options.NonceCookie.SecurePolicy = isHttpMode ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.NonceCookie.HttpOnly = true;
        options.NonceCookie.SameSite = isHttpMode ? SameSiteMode.Lax : SameSiteMode.None;
        options.ProtocolValidator.RequireNonce = true;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidAudience = clientId;
        options.TokenValidationParameters.ValidateLifetime = true;
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
        options.TokenValidationParameters.RequireSignedTokens = true;
        options.TokenValidationParameters.RequireExpirationTime = true;
        if (provider.Name == "Microsoft")
            options.TokenValidationParameters.IssuerValidator = AadIssuerValidator.GetAadIssuerValidator(options.Authority).Validate;
        else if (provider.Name == "Slack")
            options.TokenValidationParameters.ValidIssuer = "https://slack.com";
        else
            options.TokenValidationParameters.ValidIssuers = ["https://accounts.google.com", "accounts.google.com"];
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = context =>
            {
                var subject = context.Principal?.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(context.SecurityToken.Issuer))
                    context.Fail("No se recibió una identidad válida.");
                else
                    ((ClaimsIdentity)context.Principal!.Identity!).AddClaim(new Claim(ClaimTypes.NameIdentifier,
                        context.SecurityToken.Issuer + "|" + subject));
                return Task.CompletedTask;
            },
            OnTicketReceived = context => NormalizeTicket(context, provider.Name),
            OnRemoteFailure = context => Failure(context, provider.Name),
            OnAccessDenied = context =>
            {
                var returnUrl = context.Properties?.RedirectUri;
                context.HandleResponse();
                context.Response.Redirect(ErrorPath(provider.Name, "cancelled", returnUrl));
                return Task.CompletedTask;
            }
        };
    }

    private static Task NormalizeTicket(TicketReceivedContext context, string provider)
    {
        var principal = context.Principal;
        var id = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(id))
        {
            context.Fail("No se recibió una identidad válida.");
            return Task.CompletedTask;
        }
        // Misma identidad y mismos claims para todos los proveedores; sin vínculos por correo.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id), new(ProviderClaim, provider), new("provider", provider),
            new(ClaimTypes.Name, principal!.Identity?.Name ?? principal.FindFirstValue("name") ?? provider)
        };
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");
        if (!string.IsNullOrWhiteSpace(email)) claims.Add(new Claim(ClaimTypes.Email, email));
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, LocalScheme));
        return Task.CompletedTask;
    }

    private static string? Text(JsonElement json, string property) => json.ValueKind == JsonValueKind.Object &&
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        if (context.Request.Path.StartsWithSegments("/api") ||
            context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IApiBehaviorMetadata>() is not null)
            context.Response.StatusCode = status;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    private static string ErrorPath(string provider, string error, string? returnUrl = null)
    {
        var errorValue = provider.ToLowerInvariant() + "-" + error;
        if (!string.IsNullOrWhiteSpace(returnUrl) && Uri.TryCreate(returnUrl, UriKind.Absolute, out _))
        {
            // Añadir el parámetro antes del fragmento (#...), no dentro de él.
            return QueryHelpers.AddQueryString(returnUrl, "error", errorValue);
        }
        return QueryHelpers.AddQueryString(LoginPath, "error", errorValue);
    }

    private static Task Failure(RemoteFailureContext context, string provider)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Finanzas.Authentication");
        // No registrar excepciones/respuestas remotas que podrían incluir tokens o códigos.
        var reason = context.Failure?.Message.Contains("Correlation failed", StringComparison.OrdinalIgnoreCase) == true
            ? "correlation_failed" : "remote_authentication_failed";
        logger.LogWarning("Fallo en autenticación remota de {Provider}. Motivo: {Reason}", provider, reason);
        var returnUrl = context.Properties?.RedirectUri;
        context.HandleResponse();
        context.Response.Redirect(ErrorPath(provider, "failed", returnUrl));
        return Task.CompletedTask;
    }

    private static string? Value(IConfiguration configuration, string provider, string key) => new[]
    {
        configuration[$"Authentication:{provider}:{key}"], configuration[$"Auth:{provider}:{key}"],
        configuration[$"AUTH__{provider.ToUpperInvariant()}__{key.ToUpperInvariant()}"]
    }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("tu_", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("placeholder_", StringComparison.OrdinalIgnoreCase));

    private static string Callback(IConfiguration configuration, ExternalProvider provider)
    {
        var path = Value(configuration, provider.Name, "CallbackPath");
        var redirect = Value(configuration, provider.Name, "RedirectUri");
        if (path is null && redirect is not null)
        {
            if (!Uri.TryCreate(redirect, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException($"RedirectUri de {provider.Name} no es válida.");
            path = uri.AbsolutePath;
        }
        path ??= provider.CallbackPath;
        var reserved = Providers.SelectMany(p => new[] { "/api/auth/" + p.Name.ToLowerInvariant() + "/login",
            "/api/auth/" + p.Name.ToLowerInvariant() + "/user", "/auth/" + p.Name.ToLowerInvariant() + "/login",
            "/auth/" + p.Name.ToLowerInvariant() + "/user" })
            .Concat(new[] { LoginPath, "/auth/login", "/auth/dev3", "/auth/dev3/session", "/auth/dev3/logout",
                "/api/auth/me", "/api/auth/logout", "/api/auth/csrf", "/api/auth/providers", "/auth/login/session", "/auth/login/logout" });
        if (!Regex.IsMatch(path, "^/(?:[A-Za-z0-9_-]+/)*[A-Za-z0-9_-]+$") || reserved.Contains(path, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"CallbackPath de {provider.Name} debe ser una ruta local libre.");
        return path;
    }
}
