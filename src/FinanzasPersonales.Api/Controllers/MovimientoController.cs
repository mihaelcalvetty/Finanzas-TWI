using System.Security.Claims;
using FinanzasPersonales.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class MovimientoController : ControllerBase
{
    private readonly MovimientoService _service;

    public MovimientoController(MovimientoService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<MovimientoResponseDto>>> GetAll(
        [FromQuery] Guid? cuentaId,
        [FromQuery] Guid? categoriaId)
    {
        if (!TryGetUsuarioId(out var usuarioId))
        {
            return Unauthorized(new { message = "No se pudo identificar al usuario autenticado." });
        }

        var movimientos = await _service.GetAllByUsuarioAsync(usuarioId, cuentaId, categoriaId);
        return Ok(movimientos);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MovimientoResponseDto>> GetById(Guid id)
    {
        if (!TryGetUsuarioId(out var usuarioId))
        {
            return Unauthorized(new { message = "No se pudo identificar al usuario autenticado." });
        }

        var movimiento = await _service.GetByIdAsync(id, usuarioId);
        return movimiento is null ? NotFound() : Ok(movimiento);
    }

    [HttpPost]
    public async Task<ActionResult<MovimientoResponseDto>> Create(CrearMovimientoDto dto)
    {
        if (!TryGetUsuarioId(out var usuarioId))
        {
            return Unauthorized(new { message = "No se pudo identificar al usuario autenticado." });
        }

        try
        {
            var creado = await _service.CreateAsync(usuarioId, dto);
            return CreatedAtAction(nameof(GetById), new { id = creado.Id }, creado);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<MovimientoResponseDto>> Update(Guid id, ActualizarMovimientoDto dto)
    {
        if (!TryGetUsuarioId(out var usuarioId))
        {
            return Unauthorized(new { message = "No se pudo identificar al usuario autenticado." });
        }

        try
        {
            var actualizado = await _service.UpdateAsync(id, usuarioId, dto);
            return actualizado is null ? NotFound() : Ok(actualizado);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!TryGetUsuarioId(out var usuarioId))
        {
            return Unauthorized(new { message = "No se pudo identificar al usuario autenticado." });
        }

        try
        {
            var eliminado = await _service.SoftDeleteAsync(id, usuarioId);
            return eliminado ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    private bool TryGetUsuarioId(out Guid usuarioId)
    {
        var rawId = User.FindFirstValue("usuario_id")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(rawId, out usuarioId);
    }
}