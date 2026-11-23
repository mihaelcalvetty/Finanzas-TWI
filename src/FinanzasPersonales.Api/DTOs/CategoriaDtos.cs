namespace FinanzasPersonales.Api;

public class CrearCategoriaDto
{
    public string Nombre { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public decimal? LimiteMensual { get; set; }
    public bool Activa { get; set; } = true;
}

public class CategoriaResponseDto
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public string NombreUsuario { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public decimal? LimiteMensual { get; set; }
    public bool Activa { get; set; }
}

public class ActualizarCategoriaDto
{
    public string Nombre { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public decimal? LimiteMensual { get; set; }
}