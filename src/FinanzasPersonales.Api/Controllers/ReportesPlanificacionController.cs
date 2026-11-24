using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FinanzasPersonales.Api.Authentication;
using FinanzasPersonales.Api.Data;
using FinanzasPersonales.Api.DTOs;
using FinanzasPersonales.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanzasPersonales.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/reportes/salud-financiera")]
[Route("api/reportes/planificacion")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ReportesPlanificacionController : ControllerBase
{
    private readonly FinanzasDbContext _db;
    private readonly IOptionsSnapshot<VinculosIdentidadDev3Options> _vinculosOptions;

    public ReportesPlanificacionController(
        FinanzasDbContext db,
        IOptionsSnapshot<VinculosIdentidadDev3Options> vinculosOptions)
    {
        _db = db;
        _vinculosOptions = vinculosOptions;
    }

    [HttpGet]
    public async Task<ActionResult<ReporteSaludFinancieraDto>> ObtenerReporte(CancellationToken cancellationToken)
    {
        var usuarioId = await ObtenerUsuarioIdAsync(cancellationToken);
        if (usuarioId == Guid.Empty)
        {
            return StatusCode(403, new { message = "No se pudo asociar la identidad actual a un identificador de usuario válido." });
        }

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var añoActual = (short)hoy.Year;
        var mesActual = (short)hoy.Month;

        // 1. Consultar Metas de Ahorro
        var metas = await _db.MetasAhorro.AsNoTracking()
            .Where(m => m.UsuarioId == usuarioId)
            .OrderByDescending(m => m.MontoObjetivo)
            .ToListAsync(cancellationToken);

        var totalObjetivo = metas.Sum(m => m.MontoObjetivo);
        var totalAhorrado = metas.Sum(m => m.MontoAhorrado);
        var avanceGlobal = totalObjetivo > 0 ? Math.Round((totalAhorrado / totalObjetivo) * 100m, 2) : 0m;

        var detalleMetas = metas.Select(m => new ItemMetaReporteDto(
            m.Id,
            m.Nombre,
            m.MontoObjetivo,
            m.MontoAhorrado,
            m.MontoObjetivo > 0 ? Math.Round((m.MontoAhorrado / m.MontoObjetivo) * 100m, 2) : 0m,
            m.FechaLimite,
            m.Estado
        )).ToList();

        var resumenMetas = new ResumenMetasDto(
            totalObjetivo,
            totalAhorrado,
            avanceGlobal,
            metas.Count,
            metas.Count(m => m.Estado.Equals("activa", StringComparison.OrdinalIgnoreCase)),
            metas.Count(m => m.Estado.Equals("completada", StringComparison.OrdinalIgnoreCase)),
            detalleMetas
        );

        // 2. Consultar Presupuestos Mensuales
        var presupuestos = await _db.PresupuestosMensuales.AsNoTracking()
            .Where(p => p.UsuarioId == usuarioId)
            .OrderByDescending(p => p.Año).ThenByDescending(p => p.Mes)
            .ToListAsync(cancellationToken);

        var presupuestosMesActual = presupuestos
            .Where(p => p.Año == añoActual && p.Mes == mesActual)
            .Sum(p => p.MontoLimite);

        var detallePresupuestos = presupuestos.Select(p => new ItemPresupuestoReporteDto(
            p.Id,
            p.CategoriaId,
            p.Año,
            p.Mes,
            p.MontoLimite
        )).ToList();

        var resumenPresupuestos = new ResumenPresupuestosDto(
            presupuestosMesActual,
            presupuestos.Count,
            añoActual,
            mesActual,
            detallePresupuestos
        );

        // 3. Consultar Movimientos Recurrentes
        var recurrencias = await _db.MovimientosRecurrentes.AsNoTracking()
            .Where(r => r.UsuarioId == usuarioId && r.Activo)
            .ToListAsync(cancellationToken);

        decimal gastosMensuales = 0m;
        decimal ingresosMensuales = 0m;

        foreach (var r in recurrencias)
        {
            var factor = r.Frecuencia.ToLowerInvariant() switch
            {
                "semanal" => 4.33m,
                "quincenal" => 2m,
                "mensual" => 1m,
                "anual" => 1m / 12m,
                _ => 1m
            };

            var estimadoMensual = r.MontoEstimado * factor;
            if (r.Tipo.Equals("gasto", StringComparison.OrdinalIgnoreCase))
                gastosMensuales += estimadoMensual;
            else if (r.Tipo.Equals("ingreso", StringComparison.OrdinalIgnoreCase))
                ingresosMensuales += estimadoMensual;
        }

        var detalleRecurrencias = recurrencias.Select(r => new ItemRecurrenciaReporteDto(
            r.Id,
            r.Concepto,
            r.Tipo,
            r.MontoEstimado,
            r.Frecuencia,
            r.ProximaFecha,
            r.Activo
        )).ToList();

        var resumenRecurrencias = new ResumenRecurrenciasDto(
            Math.Round(gastosMensuales, 2),
            Math.Round(ingresosMensuales, 2),
            Math.Round(ingresosMensuales - gastosMensuales, 2),
            recurrencias.Count,
            detalleRecurrencias
        );

        // 4. Consultar Recordatorios de Pago
        var recordatorios = await _db.RecordatoriosPago.AsNoTracking()
            .Where(rp => rp.UsuarioId == usuarioId)
            .OrderBy(rp => rp.FechaVencimiento)
            .ToListAsync(cancellationToken);

        var totalPendiente = recordatorios.Where(rp => !rp.Pagado).Sum(rp => rp.MontoEstimado ?? 0m);
        var totalPagado = recordatorios.Where(rp => rp.Pagado).Sum(rp => rp.MontoEstimado ?? 0m);
        var vencidos = recordatorios.Count(rp => !rp.Pagado && rp.FechaVencimiento < hoy);
        var pendientes = recordatorios.Count(rp => !rp.Pagado);
        var pagados = recordatorios.Count(rp => rp.Pagado);

        var detalleRecordatorios = recordatorios.Select(rp => new ItemRecordatorioReporteDto(
            rp.Id,
            rp.Titulo,
            rp.MontoEstimado,
            rp.FechaVencimiento,
            rp.Pagado,
            !rp.Pagado && rp.FechaVencimiento < hoy,
            rp.FechaPago,
            rp.Notas
        )).ToList();

        var resumenRecordatorios = new ResumenRecordatoriosDto(
            totalPendiente,
            totalPagado,
            recordatorios.Count,
            vencidos,
            pendientes,
            pagados,
            detalleRecordatorios
        );

        // 5. Diagnóstico Integral de Salud Financiera Planificada
        var diagnostico = GenerarDiagnostico(
            avanceGlobal,
            vencidos,
            resumenRecurrencias.FlujoNetoMensualEstimado,
            gastosMensuales,
            totalAhorrado
        );

        var reporte = new ReporteSaludFinancieraDto(
            usuarioId,
            DateTimeOffset.UtcNow,
            diagnostico,
            resumenMetas,
            resumenPresupuestos,
            resumenRecurrencias,
            resumenRecordatorios
        );

        return Ok(reporte);
    }

    [HttpPost("sembrar-datos-ejemplo")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SembrarDatosEjemplo(CancellationToken cancellationToken)
    {
        var usuarioId = await ObtenerUsuarioIdAsync(cancellationToken);
        if (usuarioId == Guid.Empty)
        {
            return StatusCode(403, new { message = "Identidad no disponible para sembrar datos." });
        }

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var año = (short)hoy.Year;
        var mes = (short)hoy.Month;

        // Asegurar que exista usuario en DB si es necesario
        var usuario = await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId, cancellationToken);
        if (usuario == null)
        {
            usuario = new Usuario
            {
                Id = usuarioId,
                Nombre = User.Identity?.Name ?? "Usuario Dev3",
                Email = $"usuario_{usuarioId:N}@local.dev",
                Activo = true,
                FechaCreacion = DateTimeOffset.UtcNow
            };
            _db.Usuarios.Add(usuario);
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Crear una categoría y cuenta de referencia si no existen
        var categoria = await _db.Categorias.FirstOrDefaultAsync(c => c.UsuarioId == usuarioId, cancellationToken);
        if (categoria == null)
        {
            categoria = new Categoria
            {
                Id = Guid.NewGuid(),
                UsuarioId = usuarioId,
                Nombre = "Servicios y Hogar",
                Tipo = "gasto",
                Activa = true
            };
            _db.Categorias.Add(categoria);
        }

        var cuenta = await _db.Cuentas.FirstOrDefaultAsync(c => c.UsuarioId == usuarioId, cancellationToken);
        if (cuenta == null)
        {
            cuenta = new Cuenta
            {
                Id = Guid.NewGuid(),
                UsuarioId = usuarioId,
                Nombre = "Cuenta Principal",
                Tipo = "banco",
                Moneda = "BOB",
                SaldoInicial = 5000m,
                Activa = true
            };
            _db.Cuentas.Add(cuenta);
        }
        await _db.SaveChangesAsync(cancellationToken);

        // Completar hasta dos registros por sección sin modificar los existentes.
        var metasExistentes = await _db.MetasAhorro.Where(m => m.UsuarioId == usuarioId)
            .Select(m => m.Nombre).ToListAsync(cancellationToken);
        var metasEjemplo = new[]
        {
                new MetaAhorro
                {
                    Id = Guid.NewGuid(),
                    UsuarioId = usuarioId,
                    Nombre = "Fondo de Emergencia",
                    MontoObjetivo = 10000m,
                    MontoAhorrado = 4250m,
                    FechaLimite = hoy.AddMonths(6),
                    Estado = "activa"
                },
                new MetaAhorro
                {
                    Id = Guid.NewGuid(),
                    UsuarioId = usuarioId,
                    Nombre = "Renovación de Laptop",
                    MontoObjetivo = 7000m,
                    MontoAhorrado = 7000m,
                    FechaLimite = hoy.AddMonths(-1),
                    Estado = "completada"
                }
        };
        foreach (var meta in metasEjemplo)
        {
            if (metasExistentes.Count >= 2) break;
            if (metasExistentes.Contains(meta.Nombre, StringComparer.OrdinalIgnoreCase)) continue;
            _db.MetasAhorro.Add(meta);
            metasExistentes.Add(meta.Nombre);
        }

        var presupuestosExistentes = await _db.PresupuestosMensuales.Where(p => p.UsuarioId == usuarioId)
            .Select(p => new { p.CategoriaId, p.Año, p.Mes }).ToListAsync(cancellationToken);
        // Buscar períodos libres para no infringir la clave única por categoría/año/mes.
        var periodo = new DateOnly(año, mes, 1);
        for (var intento = 0; presupuestosExistentes.Count < 2 && intento < 24; intento++, periodo = periodo.AddMonths(-1))
        {
            if (presupuestosExistentes.Any(p => p.CategoriaId == categoria.Id && p.Año == periodo.Year && p.Mes == periodo.Month)) continue;
            _db.PresupuestosMensuales.Add(new PresupuestoMensual
            {
                Id = Guid.NewGuid(),
                UsuarioId = usuarioId,
                CategoriaId = categoria.Id,
                Año = (short)periodo.Year,
                Mes = (short)periodo.Month,
                MontoLimite = 1800m
            });
            presupuestosExistentes.Add(new { CategoriaId = categoria.Id, Año = (short)periodo.Year, Mes = (short)periodo.Month });
        }

        var recurrenciasExistentes = await _db.MovimientosRecurrentes.Where(r => r.UsuarioId == usuarioId && r.Activo)
            .Select(r => r.Concepto).ToListAsync(cancellationToken);
        var recurrenciasEjemplo = new[]
        {
                new MovimientoRecurrente
                {
                    Id = Guid.NewGuid(),
                    UsuarioId = usuarioId,
                    CuentaId = cuenta.Id,
                    CategoriaId = categoria.Id,
                    Concepto = "Internet Fibra Óptica",
                    Tipo = "gasto",
                    MontoEstimado = 250m,
                    Frecuencia = "mensual",
                    DiaEjecucion = 15,
                    ProximaFecha = hoy.AddDays(7),
                    Activo = true
                },
                new MovimientoRecurrente
                {
                    Id = Guid.NewGuid(),
                    UsuarioId = usuarioId,
                    CuentaId = cuenta.Id,
                    CategoriaId = categoria.Id,
                    Concepto = "Salario Mensual",
                    Tipo = "ingreso",
                    MontoEstimado = 6500m,
                    Frecuencia = "mensual",
                    DiaEjecucion = 1,
                    ProximaFecha = hoy.AddMonths(1),
                    Activo = true
                }
        };
        foreach (var recurrencia in recurrenciasEjemplo)
        {
            if (recurrenciasExistentes.Count >= 2) break;
            if (recurrenciasExistentes.Contains(recurrencia.Concepto, StringComparer.OrdinalIgnoreCase)) continue;
            _db.MovimientosRecurrentes.Add(recurrencia);
            recurrenciasExistentes.Add(recurrencia.Concepto);
        }

        var recordatoriosExistentes = await _db.RecordatoriosPago.Where(r => r.UsuarioId == usuarioId)
            .Select(r => r.Titulo).ToListAsync(cancellationToken);
        var recordatoriosEjemplo = new[]
        {
                new RecordatorioPago
                {
                    Id = Guid.NewGuid(),
                    UsuarioId = usuarioId,
                    Titulo = "Factura de Luz Eléctrica",
                    MontoEstimado = 180m,
                    FechaVencimiento = hoy.AddDays(4),
                    Pagado = false,
                    Notas = "Recordar pagar por banca móvil"
                },
                new RecordatorioPago
                {
                    Id = Guid.NewGuid(),
                    UsuarioId = usuarioId,
                    Titulo = "Suscripción Dropbox Pro",
                    MontoEstimado = 85m,
                    FechaVencimiento = hoy.AddDays(-2),
                    Pagado = true,
                    FechaPago = hoy.AddDays(-2),
                    Notas = "Pagado puntualmente"
                }
        };
        foreach (var recordatorio in recordatoriosEjemplo)
        {
            if (recordatoriosExistentes.Count >= 2) break;
            if (recordatoriosExistentes.Contains(recordatorio.Titulo, StringComparer.OrdinalIgnoreCase)) continue;
            _db.RecordatoriosPago.Add(recordatorio);
            recordatoriosExistentes.Add(recordatorio.Titulo);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { mensaje = "Datos de ejemplo completados para el usuario actual." });
    }

    private static DiagnosticoFinancieroDto GenerarDiagnostico(
        decimal avanceMetas,
        int recordatoriosVencidos,
        decimal flujoNetoRecurrente,
        decimal gastosRecurrentes,
        decimal totalAhorrado)
    {
        var ratio = gastosRecurrentes > 0 ? Math.Round(totalAhorrado / gastosRecurrentes, 2) : 0m;

        if (recordatoriosVencidos > 0)
        {
            return new DiagnosticoFinancieroDto(
                "Alerta",
                "#ef4444",
                $"Tienes {recordatoriosVencidos} pago(s) vencido(s) pendiente(s). Regularízalos para evitar recargos o cortes de servicio.",
                ratio
            );
        }

        if (flujoNetoRecurrente < 0)
        {
            return new DiagnosticoFinancieroDto(
                "Atención requerida",
                "#f59e0b",
                "Tus compromisos recurrentes superan a tus ingresos estimados. Considera optimizar suscripciones o gastos fijos.",
                ratio
            );
        }

        if (avanceMetas >= 50m && flujoNetoRecurrente > 0)
        {
            return new DiagnosticoFinancieroDto(
                "Excelente",
                "#22c55e",
                "Excelente salud financiera: flujo recurrente positivo, sin deudas vencidas y progreso sólido en tus metas de ahorro.",
                ratio
            );
        }

        return new DiagnosticoFinancieroDto(
            "Estable",
            "#38bdf8",
            "Planificación equilibrada: cuentas al día y compromisos recurrentes cubiertos. Continúa aportando a tus metas.",
            ratio
        );
    }

    private Task<Guid> ObtenerUsuarioIdAsync(CancellationToken ct)
    {
        // 1. Si existe en context.Items (inyectado por filtros)
        if (HttpContext.Items.TryGetValue("UsuarioId", out var item) && item is Guid g && g != Guid.Empty)
            return Task.FromResult(g);

        // 2. Si viene como claim directo "usuario_id" o NameIdentifier parseable
        var claimId = User.FindFirstValue("usuario_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(claimId, out var parsedGuid) && parsedGuid != Guid.Empty)
            return Task.FromResult(parsedGuid);

        // 3. Revisar en VinculosIdentidadDev3Options
        var proveedor = User.FindFirstValue(Dev3Authentication.ProviderClaim) ?? User.FindFirstValue("provider");
        var externo = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(proveedor) && !string.IsNullOrWhiteSpace(externo))
        {
            var vinculo = _vinculosOptions.Value.VinculosIdentidad.FirstOrDefault(v =>
                v.Proveedor.Equals(proveedor, StringComparison.OrdinalIgnoreCase) &&
                v.IdentificadorExterno == externo);

            if (vinculo != null && vinculo.UsuarioId != Guid.Empty)
                return Task.FromResult(vinculo.UsuarioId);
        }

        // Nunca asociar cuentas financieras por correo: puede coincidir entre proveedores.
        // Fallback consistente para las identidades de la demostración.
        if (!string.IsNullOrWhiteSpace(externo))
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes($"{proveedor}:{externo}"));
            return Task.FromResult(new Guid(hash));
        }

        return Task.FromResult(Guid.Empty);
    }
}
