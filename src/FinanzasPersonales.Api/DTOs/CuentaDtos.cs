namespace FinanzasPersonales.Api;

public class CrearCuentaDto
{
    public string Nombre { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string Moneda { get; set; } = "BOB";
    public decimal SaldoInicial { get; set; }
    public bool Activa { get; set; } = true;
}

public class CuentaResponseDto
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public string NombreUsuario { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string Moneda { get; set; } = string.Empty;
    public decimal SaldoInicial { get; set; }
    public bool Activa { get; set; }
}

public class ActualizarCuentaDto
{
    public string Nombre { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string? Moneda { get; set; }
    public decimal SaldoInicial { get; set; }
}