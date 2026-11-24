using DotNetEnv;
using FinanzasPersonales.Api;
using FinanzasPersonales.Api.Authentication;
using FinanzasPersonales.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

// Encontrar .env también al ejecutar desde src/; respetar variables ya exportadas.
Env.NoClobber().TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// Soporte multi-instancia: Data Protection con llaves compartidas
var dataProtectionBuilder = builder.Services.AddDataProtection()
    .SetApplicationName("FinanzasPersonales");

var keysDirectory = builder.Configuration["DATA_PROTECTION_KEYS_PATH"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "temp-keys");

if (!string.IsNullOrWhiteSpace(keysDirectory))
{
    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
}

// Configuración de Forwarded Headers (para proxies inversos y contenedores Docker)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddExternalAuthentication(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.AddOptions<VinculosIdentidadDev3Options>()
    .Bind(builder.Configuration.GetSection("Dev3"))
    .Validate(options => options.EsValida(), "Los vínculos de identidad Dev3 deben ser explícitos, válidos y no duplicados.")
    .ValidateOnStart();
builder.Services.AddScoped<AccesoDev3Filter>();

builder.Services.AddDbContext<FinanzasDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession();
builder.Services.AddHttpClient();

builder.Services.AddScoped<UsuarioService>();
builder.Services.AddScoped<CategoriaService>();
builder.Services.AddScoped<CuentaService>();
builder.Services.AddScoped<MovimientoService>();
builder.Services.AddScoped<ReportesService>();
// Los servicios antiguos de OAuth manual se conservan en disco, pero los seis
// proveedores usan ahora los middleware comunes; no se vinculan usuarios por email.

// Configuración de CORS dinámica y multi-origen
var rawCors = builder.Configuration["CORS_ALLOWED_ORIGINS"];
var defaultOrigins = new[] { "http://127.0.0.1:5500", "http://localhost:5500", "http://127.0.0.1:3000", "http://localhost:3000" };
var allowedOrigins = !string.IsNullOrWhiteSpace(rawCors)
    ? rawCors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : defaultOrigins;

builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendPolicy", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Trabajo explícito de migración: no iniciar el servidor ni migrar en cada réplica.
if (builder.Configuration["MIGRATE_DATABASE_ONLY"] == "true")
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<FinanzasDbContext>().Database.MigrateAsync();
    return;
}

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors("FrontendPolicy");

if (builder.Configuration["DISABLE_HTTPS_REDIRECTION"] != "true")
{
    app.UseHttpsRedirection();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
