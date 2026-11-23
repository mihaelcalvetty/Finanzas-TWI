using FinanzasPersonales.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FinanzasPersonales.Api;

public class UsuarioService
{
    private readonly FinanzasDbContext _db;

    public UsuarioService(FinanzasDbContext db)
    {
        _db = db;
    }

    public async Task<List<UsuarioResponseDto>> GetAllAsync()
    {
        return await _db.Usuarios
            .AsNoTracking()
            .OrderBy(u => u.Nombre)
            .Select(u => new UsuarioResponseDto
            {
                Id = u.Id,
                Nombre = u.Nombre,
                Email = u.Email,
                FechaCreacion = u.FechaCreacion,
                Activo = u.Activo
            })
            .ToListAsync();
    }

    public async Task<UsuarioResponseDto?> GetByIdAsync(Guid id)
    {
        return await _db.Usuarios
            .AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new UsuarioResponseDto
            {
                Id = u.Id,
                Nombre = u.Nombre,
                Email = u.Email,
                FechaCreacion = u.FechaCreacion,
                Activo = u.Activo
            })
            .FirstOrDefaultAsync();
    }

    public async Task<UsuarioResponseDto> CreateAsync(CrearUsuarioDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Nombre))
            throw new ArgumentException("El nombre es obligatorio.");

        if (string.IsNullOrWhiteSpace(dto.Email))
            throw new ArgumentException("El correo electrónico es obligatorio.");

        var emailNormalizado = dto.Email.Trim().ToLowerInvariant();

        var existeEmail = await _db.Usuarios
            .AnyAsync(u => u.Email.ToLower() == emailNormalizado);

        if (existeEmail)
            throw new InvalidOperationException("Ya existe un usuario registrado con ese correo.");

        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            Nombre = dto.Nombre.Trim(),
            Email = emailNormalizado,
            PasswordHash = string.IsNullOrWhiteSpace(dto.PasswordHash) ? null : dto.PasswordHash,
            FechaCreacion = DateTimeOffset.UtcNow,
            Activo = dto.Activo
        };

        _db.Usuarios.Add(usuario);
        await _db.SaveChangesAsync();

        return new UsuarioResponseDto
        {
            Id = usuario.Id,
            Nombre = usuario.Nombre,
            Email = usuario.Email,
            FechaCreacion = usuario.FechaCreacion,
            Activo = usuario.Activo
        };
    }

    public async Task<UsuarioResponseDto?> UpdateAsync(Guid id, ActualizarUsuarioDto dto)
    {
        var usuario = await _db.Usuarios
            .FirstOrDefaultAsync(u => u.Id == id);

        if (usuario is null)
            return null;

        if (!usuario.Activo)
            throw new InvalidOperationException("No se puede actualizar un usuario inactivo o eliminado.");

        if (string.IsNullOrWhiteSpace(dto.Nombre))
            throw new ArgumentException("El nombre es obligatorio.");

        if (string.IsNullOrWhiteSpace(dto.Email))
            throw new ArgumentException("El correo electrónico es obligatorio.");

        var emailNormalizado = dto.Email.Trim().ToLowerInvariant();

        var existeEmailEnOtroUsuario = await _db.Usuarios
            .AnyAsync(u => u.Id != id && u.Email.ToLower() == emailNormalizado);

        if (existeEmailEnOtroUsuario)
            throw new InvalidOperationException("Ya existe otro usuario registrado con ese correo.");

        usuario.Nombre = dto.Nombre.Trim();
        usuario.Email = emailNormalizado;

        if (!string.IsNullOrWhiteSpace(dto.PasswordHash))
            usuario.PasswordHash = dto.PasswordHash;

        await _db.SaveChangesAsync();

        return new UsuarioResponseDto
        {
            Id = usuario.Id,
            Nombre = usuario.Nombre,
            Email = usuario.Email,
            FechaCreacion = usuario.FechaCreacion,
            Activo = usuario.Activo
        };
    }
}
