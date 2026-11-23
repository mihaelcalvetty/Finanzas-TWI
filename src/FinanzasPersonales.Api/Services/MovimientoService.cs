using FinanzasPersonales.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FinanzasPersonales.Api;

public class MovimientoService
{
    private static readonly string[] TiposValidos = { "ingreso", "gasto" };
    private readonly FinanzasDbContext _db;

    public MovimientoService(FinanzasDbContext db)
    {
        _db = db;
    }

    public async Task<List<MovimientoResponseDto>> GetAllByUsuarioAsync(
        Guid usuarioId,
        Guid? cuentaId = null,
        Guid? categoriaId = null)
    {
        var query = _db.Movimientos
            .AsNoTracking()
            .Where(m => m.UsuarioId == usuarioId && m.Activo);

        if (cuentaId.HasValue)
            query = query.Where(m => m.CuentaId == cuentaId.Value);

        if (categoriaId.HasValue)
            query = query.Where(m => m.CategoriaId == categoriaId.Value);

        return await query
            .OrderByDescending(m => m.FechaMovimiento)
            .ThenByDescending(m => m.FechaRegistro)
            .Select(m => new MovimientoResponseDto
            {
                Id = m.Id,
                UsuarioId = m.UsuarioId,
                NombreUsuario = m.Usuario.Nombre,
                CuentaId = m.CuentaId,
                NombreCuenta = m.Cuenta.Nombre,
                CategoriaId = m.CategoriaId,
                NombreCategoria = m.Categoria.Nombre,
                Tipo = m.Tipo,
                Monto = m.Monto,
                Concepto = m.Concepto,
                FechaMovimiento = m.FechaMovimiento,
                Notas = m.Notas,
                FechaRegistro = m.FechaRegistro
            })
            .ToListAsync();
    }

    public async Task<MovimientoResponseDto?> GetByIdAsync(Guid id, Guid usuarioId)
    {
        return await _db.Movimientos
            .AsNoTracking()
            .Where(m => m.Id == id && m.UsuarioId == usuarioId && m.Activo)
            .Select(m => new MovimientoResponseDto
            {
                Id = m.Id,
                UsuarioId = m.UsuarioId,
                NombreUsuario = m.Usuario.Nombre,
                CuentaId = m.CuentaId,
                NombreCuenta = m.Cuenta.Nombre,
                CategoriaId = m.CategoriaId,
                NombreCategoria = m.Categoria.Nombre,
                Tipo = m.Tipo,
                Monto = m.Monto,
                Concepto = m.Concepto,
                FechaMovimiento = m.FechaMovimiento,
                Notas = m.Notas,
                FechaRegistro = m.FechaRegistro
            })
            .FirstOrDefaultAsync();
    }

    public async Task<bool> SoftDeleteAsync(Guid id, Guid usuarioId)
    {
        var movimiento = await _db.Movimientos
            .FirstOrDefaultAsync(m => m.Id == id && m.UsuarioId == usuarioId);

        if (movimiento is null)
            return false;

        if (!movimiento.Activo)
            throw new InvalidOperationException("El movimiento ya se encuentra inactivo o eliminado.");

        movimiento.Activo = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<MovimientoResponseDto> CreateAsync(Guid usuarioId, CrearMovimientoDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Concepto))
            throw new ArgumentException("El concepto del movimiento es obligatorio.");

        var tipoNormalizado = dto.Tipo.Trim().ToLowerInvariant();
        if (!TiposValidos.Contains(tipoNormalizado))
            throw new ArgumentException("Tipo de movimiento inválido. Use: ingreso o gasto.");

        if (dto.Monto <= 0)
            throw new ArgumentException("El monto debe ser mayor que cero.");

        var usuario = await _db.Usuarios
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == usuarioId);

        if (usuario is null)
            throw new ArgumentException("El usuario autenticado no existe.");

        if (!usuario.Activo)
            throw new ArgumentException("El usuario autenticado está inactivo.");

        var cuenta = await _db.Cuentas
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == dto.CuentaId && c.UsuarioId == usuarioId);

        if (cuenta is null)
            throw new ArgumentException("La cuenta especificada no existe o no pertenece al usuario.");

        if (!cuenta.Activa)
            throw new ArgumentException("La cuenta está inactiva y no admite nuevos movimientos.");

        var categoria = await _db.Categorias
            .AsNoTracking()
            .FirstOrDefaultAsync(cat => cat.Id == dto.CategoriaId && cat.UsuarioId == usuarioId);

        if (categoria is null)
            throw new ArgumentException("La categoría especificada no existe o no pertenece al usuario.");

        if (categoria.Tipo != tipoNormalizado)
            throw new ArgumentException("El tipo del movimiento no coincide con el tipo de la categoría.");

        var movimiento = new Movimiento
        {
            UsuarioId = usuarioId,
            CuentaId = dto.CuentaId,
            CategoriaId = dto.CategoriaId,
            Tipo = tipoNormalizado,
            Monto = dto.Monto,
            Concepto = dto.Concepto.Trim(),
            FechaMovimiento = dto.FechaMovimiento,
            Notas = string.IsNullOrWhiteSpace(dto.Notas) ? null : dto.Notas.Trim(),
            FechaRegistro = DateTimeOffset.UtcNow
        };

        _db.Movimientos.Add(movimiento);
        await _db.SaveChangesAsync();

        return new MovimientoResponseDto
        {
            Id = movimiento.Id,
            UsuarioId = movimiento.UsuarioId,
            NombreUsuario = usuario.Nombre,
            CuentaId = movimiento.CuentaId,
            NombreCuenta = cuenta.Nombre,
            CategoriaId = movimiento.CategoriaId,
            NombreCategoria = categoria.Nombre,
            Tipo = movimiento.Tipo,
            Monto = movimiento.Monto,
            Concepto = movimiento.Concepto,
            FechaMovimiento = movimiento.FechaMovimiento,
            Notas = movimiento.Notas,
            FechaRegistro = movimiento.FechaRegistro
        };
    }

    public async Task<MovimientoResponseDto?> UpdateAsync(
        Guid id,
        Guid usuarioId,
        ActualizarMovimientoDto dto)
    {
        var movimiento = await _db.Movimientos
            .Include(m => m.Usuario)
            .FirstOrDefaultAsync(m => m.Id == id && m.UsuarioId == usuarioId);

        if (movimiento is null)
            return null;

        if (!movimiento.Activo)
            throw new InvalidOperationException("No se puede actualizar un movimiento inactivo o eliminado.");

        if (string.IsNullOrWhiteSpace(dto.Concepto))
            throw new ArgumentException("El concepto del movimiento es obligatorio.");

        var tipoNormalizado = dto.Tipo.Trim().ToLowerInvariant();
        if (!TiposValidos.Contains(tipoNormalizado))
            throw new ArgumentException("Tipo de movimiento inválido. Use: ingreso o gasto.");

        if (dto.Monto <= 0)
            throw new ArgumentException("El monto debe ser mayor que cero.");

        var cuenta = await _db.Cuentas
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == dto.CuentaId && c.UsuarioId == usuarioId);

        if (cuenta is null)
            throw new ArgumentException("La cuenta especificada no existe o no pertenece al usuario.");

        if (!cuenta.Activa)
            throw new ArgumentException("La cuenta está inactiva y no admite movimientos.");

        var categoria = await _db.Categorias
            .AsNoTracking()
            .FirstOrDefaultAsync(cat => cat.Id == dto.CategoriaId && cat.UsuarioId == usuarioId && cat.Activa);

        if (categoria is null)
            throw new ArgumentException("La categoría especificada no existe, está inactiva o no pertenece al usuario.");

        if (categoria.Tipo != tipoNormalizado)
            throw new ArgumentException("El tipo del movimiento no coincide con el tipo de la categoría.");

        movimiento.CuentaId = dto.CuentaId;
        movimiento.CategoriaId = dto.CategoriaId;
        movimiento.Tipo = tipoNormalizado;
        movimiento.Monto = dto.Monto;
        movimiento.Concepto = dto.Concepto.Trim();
        movimiento.FechaMovimiento = dto.FechaMovimiento;
        movimiento.Notas = string.IsNullOrWhiteSpace(dto.Notas) ? null : dto.Notas.Trim();

        await _db.SaveChangesAsync();

        return new MovimientoResponseDto
        {
            Id = movimiento.Id,
            UsuarioId = movimiento.UsuarioId,
            NombreUsuario = movimiento.Usuario.Nombre,
            CuentaId = movimiento.CuentaId,
            NombreCuenta = cuenta.Nombre,
            CategoriaId = movimiento.CategoriaId,
            NombreCategoria = categoria.Nombre,
            Tipo = movimiento.Tipo,
            Monto = movimiento.Monto,
            Concepto = movimiento.Concepto,
            FechaMovimiento = movimiento.FechaMovimiento,
            Notas = movimiento.Notas,
            FechaRegistro = movimiento.FechaRegistro
        };
    }
}
