namespace FinanzasPersonales.Api;

public class CrearUsuarioDto
{
    public string Nombre { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public bool Activo { get; set; } = true;
}

public class UsuarioResponseDto
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTimeOffset FechaCreacion { get; set; }
    public bool Activo { get; set; }
}

public class ActualizarUsuarioDto
{
    public string Nombre { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
}