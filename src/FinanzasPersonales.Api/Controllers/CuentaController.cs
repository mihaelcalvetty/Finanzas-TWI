using System.Security.Claims;
using FinanzasPersonales.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class CuentaController : ControllerBase
{
    private readonly CuentaService _service;

    public CuentaController(CuentaService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<CuentaResponseDto>>> GetAll()
    {
        if (!TryGetUsuarioId(out var usuarioId))
        {
            return Unauthorized(new { message = "No se pudo identificar al usuario autenticado." });
        }

        var cuentas = await _service.GetAllByUsuarioAsync(usuarioId);
        return Ok(cuentas);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CuentaResponseDto>> GetById(Guid id)
    {
        if (!TryGetUsuarioId(out var usuarioId))
        {
            return Unauthorized(new { message = "No se pudo identificar al usuario autenticado." });
        }

        var cuenta = await _service.GetByIdAsync(id, usuarioId);
        return cuenta is null ? NotFound() : Ok(cuenta);
    }

    [HttpPost]
    public async Task<ActionResult<CuentaResponseDto>> Create(CrearCuentaDto dto)
    {
        if (!TryGetUsuarioId(out var usuarioId))
        {
            return Unauthorized(new { message = "No se pudo identificar al usuario autenticado." });
        }

        try
        {
            var creada = await _service.CreateAsync(usuarioId, dto);
            return CreatedAtAction(nameof(GetById), new { id = creada.Id }, creada);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CuentaResponseDto>> Update(Guid id, ActualizarCuentaDto dto)
    {
        if (!TryGetUsuarioId(out var usuarioId))
        {
            return Unauthorized(new { message = "No se pudo identificar al usuario autenticado." });
        }

        try
        {
            var actualizada = await _service.UpdateAsync(id, usuarioId, dto);
            return actualizada is null ? NotFound() : Ok(actualizada);
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
            var eliminada = await _service.SoftDeleteAsync(id, usuarioId);
            return eliminada ? NoContent() : NotFound();
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