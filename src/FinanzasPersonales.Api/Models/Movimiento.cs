
using FinanzasPersonales.Api.Models;

namespace FinanzasPersonales.Api;

public class Movimiento
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid CuentaId { get; set; }
    public Guid CategoriaId { get; set; }
    public string Tipo { get; set; } = string.Empty; // ingreso, gasto
    public decimal Monto { get; set; }
    public string Concepto { get; set; } = string.Empty;
    public DateOnly FechaMovimiento { get; set; }
    public string? Notas { get; set; }
    public DateTimeOffset FechaRegistro { get; set; } = DateTimeOffset.UtcNow;
    public bool Activo { get; set; } = true;

    // Propiedades de navegación
    public Usuario Usuario { get; set; } = null!;
    public Cuenta Cuenta { get; set; } = null!;
    public Categoria Categoria { get; set; } = null!;
    public ICollection<MovimientoEtiqueta> MovimientoEtiquetas { get; set; } = new List<MovimientoEtiqueta>();

}
