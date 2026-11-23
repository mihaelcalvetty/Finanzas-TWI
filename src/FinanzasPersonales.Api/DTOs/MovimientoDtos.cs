namespace FinanzasPersonales.Api;

public class CrearMovimientoDto
{
    public Guid CuentaId { get; set; }
    public Guid CategoriaId { get; set; }
    public string Tipo { get; set; } = string.Empty;
    public decimal Monto { get; set; }
    public string Concepto { get; set; } = string.Empty;
    public DateOnly FechaMovimiento { get; set; }
    public string? Notas { get; set; }
}

public class MovimientoResponseDto
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public string NombreUsuario { get; set; } = string.Empty;
    public Guid CuentaId { get; set; }
    public string NombreCuenta { get; set; } = string.Empty;
    public Guid CategoriaId { get; set; }
    public string NombreCategoria { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public decimal Monto { get; set; }
    public string Concepto { get; set; } = string.Empty;
    public DateOnly FechaMovimiento { get; set; }
    public string? Notas { get; set; }
    public DateTimeOffset FechaRegistro { get; set; }
    
}

public class ActualizarMovimientoDto
{
    public Guid CuentaId { get; set; }
    public Guid CategoriaId { get; set; }
    public string Tipo { get; set; } = string.Empty;
    public decimal Monto { get; set; }
    public string Concepto { get; set; } = string.Empty;
    public DateOnly FechaMovimiento { get; set; }
    public string? Notas { get; set; }
}