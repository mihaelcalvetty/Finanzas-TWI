// Harness aislado: identidades simuladas, claves efímeras y ningún acceso a PostgreSQL/OAuth real.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinanzasPersonales.Api.Authentication;
using FinanzasPersonales.Api.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal static class AuthSessionVerification
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GoogleAuthController).Assembly.GetName().Name,
            ContentRootPath = System.IO.Path.Combine(args[0], "src/FinanzasPersonales.Api"),
            EnvironmentName = "Development"
        });
        builder.Configuration.Sources.Clear();
        var config = new Dictionary<string, string?>();
        foreach (var provider in ExternalAuthentication.Providers)
        {
            config[$"Authentication:{provider.Name}:ClientId"] = "fixture-client";
            config[$"Authentication:{provider.Name}:ClientSecret"] = "fixture-secret";
        }
        builder.Configuration.AddInMemoryCollection(config);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("https://127.0.0.1:0");
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddExternalAuthentication(builder.Configuration);
        var xBackchannel = new XTokenFixture();
        builder.Services.PostConfigure<OAuthOptions>("X", options => options.Backchannel = new HttpClient(xBackchannel));
        builder.Services.AddAuthorization();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(GoogleAuthController).Assembly);
        await using var app = builder.Build();
        app.UseSession();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
            Check((await schemes.GetAllSchemesAsync()).Count(s => s.Name == "Cookies") == 1, "Debe existir una sola cookie.");
            foreach (var provider in ExternalAuthentication.Providers)
            {
                var scheme = (await schemes.GetSchemeAsync(provider.Scheme))!;
                var context = new DefaultHttpContext { RequestServices = app.Services };
                context.Request.Scheme = "https";
                var identity = new ClaimsIdentity(provider.Scheme);
                RemoteAuthenticationOptions remoteOptions;
                Func<TicketReceivedContext, Task> normalize;
                if (provider.Name is "Google" or "Microsoft" or "Slack")
                {
                    var options = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(provider.Scheme);
                    Check(!options.SaveTokens && options.UsePkce == (provider.Name != "Slack") && options.ProtocolValidator.RequireNonce, "OIDC debe conservar nonce y usar PKCE donde está soportado, sin guardar tokens.");
                    // Emula exclusivamente el resultado de un ID token YA validado por el middleware.
                    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "https://fixture.example/issuer|subject"));
                    identity.AddClaim(new Claim(ClaimTypes.Name, "Fixture " + provider.Name));
                    identity.AddClaim(new Claim(ClaimTypes.Email, "fixture@example.invalid"));
                    remoteOptions = options;
                    normalize = options.Events.OnTicketReceived;
                }
                else
                {
                    var options = app.Services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get(provider.Scheme);
                    Check(!options.SaveTokens, "OAuth no debe guardar tokens en la cookie.");
                    using var backchannel = new HttpClient(new ProfileFixture(provider.Name));
                    using var token = OAuthTokenResponse.Success(JsonDocument.Parse("{\"access_token\":\"fixture-access-token\"}"));
                    var ticketContext = new OAuthCreatingTicketContext(new ClaimsPrincipal(identity), new AuthenticationProperties(),
                        context, scheme, options, backchannel, token, default);
                    await options.Events.CreatingTicket(ticketContext);
                    Check(identity.FindFirst(ClaimTypes.NameIdentifier)?.Value == provider.Name.ToLowerInvariant() + "|fixture-id",
                        "Debe leer el identificador estable del perfil.");
                    remoteOptions = options;
                    normalize = options.Events.OnTicketReceived;
                }
                var received = new TicketReceivedContext(context, scheme, remoteOptions,
                    new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties(), provider.Scheme));
                await normalize(received);
                Check(received.Principal!.FindFirst("provider")?.Value == provider.Name, "Contrato provider común.");
                Check(received.Principal.FindFirst("external_provider")?.Value == provider.Name, "Contrato external_provider común.");
                Check(received.Principal.FindFirst("usuario_id") is null, "No inventar ni vincular Guid por correo.");

                var cookieOptions = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
                var protectedTicket = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(
                    received.Principal, new AuthenticationProperties(), "Cookies"));
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                handler.CookieContainer.Add(new Uri(baseUrl), new Cookie(cookieOptions.Cookie.Name!, protectedTicket, "/") { Secure = true });
                using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
                async Task<JsonElement> Json(string path)
                {
                    var response = await client.GetAsync(path);
                    Check(response.IsSuccessStatusCode, "GET debe responder 200: " + path);
                    return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
                }
                var me = await Json("/api/auth/me");
                Check(me.GetProperty("isAuthenticated").GetBoolean(), "Cookie compartida debe autenticar.");
                Check(me.GetProperty("proveedor").GetString() == provider.Name, "Proveedor de la sesión debe coincidir.");
                Check(me.GetProperty("usuarioId").ValueKind == JsonValueKind.Null, "ID externo no es Guid local.");
                Check(me.GetProperty("nombre").GetString() == "Fixture " + provider.Name, "Nombre debe conservarse.");
                Check(provider.Name == "X" ? me.GetProperty("email").ValueKind == JsonValueKind.Null :
                    me.GetProperty("email").GetString() == "fixture@example.invalid", "Correo opcional debe conservarse sin inventarlo.");
                var route = "/api/auth/" + provider.Name.ToLowerInvariant() + "/user";
                Check((await client.GetAsync(route)).StatusCode == HttpStatusCode.OK, "user del proveedor propio debe responder 200.");
                var other = ExternalAuthentication.Providers.First(p => p.Name != provider.Name);
                Check((int)(await client.GetAsync("/api/auth/" + other.Name.ToLowerInvariant() + "/user")).StatusCode == 403,
                    "user de otro proveedor debe responder 403.");
                Check((int)(await client.PostAsync("/api/auth/logout", new StringContent(""))).StatusCode == 400,
                    "Logout requiere CSRF incluso con cookie válida.");
                var csrf = await Json("/api/auth/csrf");
                client.DefaultRequestHeaders.Add("RequestVerificationToken", csrf.GetProperty("token").GetString());
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                Check((int)(await client.PostAsync("/api/auth/logout?returnUrl=https://evil.example", new StringContent(""))).StatusCode == 400,
                    "Logout no debe permitir redirecciones abiertas.");
                Check((await client.PostAsync("/api/auth/logout", new StringContent(""))).StatusCode == HttpStatusCode.OK,
                    "Logout compartido debe funcionar.");
                Check(!(await Json("/api/auth/me")).GetProperty("isAuthenticated").GetBoolean(), "Cookie debe quedar eliminada.");
                Check((int)(await client.GetAsync(route)).StatusCode == 401, "user sin cookie vuelve a 401.");
            }
            // Flujo completo X con middleware real y backchannel simulado: PKCE, Basic,
            // state corto/correlación, consulta de perfil y emisión de cookie local.
            var xHandler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var xClient = new HttpClient(xHandler) { BaseAddress = new Uri(baseUrl) };
            var xChallenge = await xClient.GetAsync("/api/auth/x/login?returnUrl=/api/auth/me");
            Check((int)xChallenge.StatusCode == 302, "X debe producir challenge.");
            var xQuery = QueryHelpers.ParseQuery(xChallenge.Headers.Location!.Query);
            var xState = xQuery["state"].ToString();
            Check(xState.Length == 43, "state X debe ser opaco y menor de 500 caracteres.");
            xBackchannel.ExpectedChallenge = xQuery["code_challenge"].ToString();
            var callback = "/api/auth/x/callback?code=fixture-code&state=" + Uri.EscapeDataString(xState);
            var xCallback = await xClient.GetAsync(callback);
            Check((int)xCallback.StatusCode == 302 && xCallback.Headers.Location!.OriginalString == "/api/auth/me", "X debe completar callback.");
            var xSession = JsonDocument.Parse(await xClient.GetStringAsync("/api/auth/me")).RootElement;
            Check(xSession.GetProperty("isAuthenticated").GetBoolean() && xSession.GetProperty("proveedor").GetString() == "X", "X debe crear sesión compartida.");
            Check(xSession.GetProperty("identificadorExterno").GetString() == "x|roundtrip-id", "X debe usar el ID estable del perfil.");
            Check(xSession.GetProperty("email").ValueKind == JsonValueKind.Null, "X no requiere correo para autenticar.");
            Check((await xClient.GetAsync("/api/auth/x/user")).StatusCode == HttpStatusCode.OK, "Endpoint X user debe reconocer la cookie.");
            var xReplay = await xClient.GetAsync(callback);
            Check((int)xReplay.StatusCode == 302 && xReplay.Headers.Location!.OriginalString.Contains("error=x-failed"), "No reutilizar state X consumido.");
            Check(xBackchannel.Exchanges == 1, "Replay no debe canjear otro código.");

            xBackchannel.RejectToken = true;
            var rejectedChallenge = await xClient.GetAsync("/api/auth/x/login");
            var rejectedQuery = QueryHelpers.ParseQuery(rejectedChallenge.Headers.Location!.Query);
            xBackchannel.ExpectedChallenge = rejectedQuery["code_challenge"].ToString();
            var rejected = await xClient.GetAsync("/api/auth/x/callback?code=fixture-code&state=" + Uri.EscapeDataString(rejectedQuery["state"].ToString()));
            Check((int)rejected.StatusCode == 302 && rejected.Headers.Location!.OriginalString.Contains("error=x-failed"), "Token rechazado debe producir error controlado.");
            Check(!rejected.Headers.Location!.OriginalString.Contains("fixture-secret"), "No filtrar secretos en errores.");
            Console.WriteLine($"OK: {checks} comprobaciones de perfiles, cookies y logout para ocho proveedores, incluido callback X simulado con PKCE/Basic/state.");
            Console.WriteLine("NO se probó autenticación real ni validación de tokens emitidos por los proveedores.");
        }
        finally { await app.StopAsync(); }
    }

    private sealed class ProfileFixture(string provider) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Headers.Authorization?.Scheme == "Bearer", "Token solo por Authorization.");
            if (provider == "Twitch") Check(request.Headers.Contains("Client-Id"), "Twitch requiere Client-Id.");
            if (provider == "Dropbox") Check(request.Method == HttpMethod.Post, "Perfil Dropbox usa POST.");
            var profile = provider switch
            {
                "Discord" => "{\"id\":\"fixture-id\",\"global_name\":\"Fixture Discord\",\"email\":\"fixture@example.invalid\"}",
                "Twitch" => "{\"data\":[{\"id\":\"fixture-id\",\"display_name\":\"Fixture Twitch\",\"email\":\"fixture@example.invalid\"}]}",
                "Spotify" => "{\"id\":\"fixture-id\",\"display_name\":\"Fixture Spotify\",\"email\":\"fixture@example.invalid\"}",
                "Dropbox" => "{\"account_id\":\"fixture-id\",\"name\":{\"display_name\":\"Fixture Dropbox\"},\"email\":\"fixture@example.invalid\"}",
                "X" => "{\"data\":{\"id\":\"fixture-id\",\"name\":\"Fixture X\",\"username\":\"fixture_x\"}}",
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(profile, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class XTokenFixture : HttpMessageHandler
    {
        public string ExpectedChallenge { get; set; } = string.Empty;
        public int Exchanges { get; private set; }
        public bool RejectToken { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "api.x.com", "X solo usa endpoints HTTPS oficiales.");
            if (request.RequestUri.AbsolutePath == "/2/oauth2/token")
            {
                Exchanges++;
                Check(request.Method == HttpMethod.Post && request.Headers.Authorization?.Scheme == "Basic", "Cliente web X debe usar POST + HTTP Basic.");
                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!));
                Check(credentials == "fixture-client:fixture-secret", "Basic debe usar OAuth 2 Client ID/Secret.");
                var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                Check(!form.ContainsKey("client_secret") && form["grant_type"] == "authorization_code", "No duplicar secretos en el cuerpo.");
                Check(form["code"] == "fixture-code" && form.ContainsKey("redirect_uri"), "Canje debe incluir código y callback.");
                var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(form["code_verifier"].ToString())));
                Check(challenge == ExpectedChallenge, "El verifier debe corresponder al challenge S256 inicial.");
                return new HttpResponseMessage(RejectToken ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
                {
                    Content = new StringContent(RejectToken ? "{\"error\":\"invalid_client\"}" :
                        "{\"access_token\":\"fixture-x-access-token\",\"token_type\":\"bearer\"}", Encoding.UTF8, "application/json")
                };
            }
            Check(request.RequestUri.AbsolutePath == "/2/users/me" && request.Headers.Authorization?.Scheme == "Bearer", "Perfil X debe consultarse con token de usuario, no app-only.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"id\":\"roundtrip-id\",\"name\":\"Roundtrip X\",\"username\":\"roundtrip_x\"}}", Encoding.UTF8, "application/json")
            };
        }
    }
}
