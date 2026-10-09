---
tipo: implementacion
proyecto: LoomLCI
bloque: RB-03
fecha: 2026-10-08
estado: cerrado_end_to_end
---

# RB-03 — Errores y recuperación al cerrar recursos

> **CERRADO end-to-end en build local `0.1.0-dev-f2802e2ba043` (sequence 8).** El Host nuevo quedó instalado y conectado, tras ensayo aislado E2E de actualización/rollback y despliegue supervisado por IvanSpace. Ver [[RB-03 - Actualización local secuencia 8]] y [[Roadmap de robustez post-auditoría]].

## Problema reproducido

`ResourceRegistry.DisposeEntryAsync` retorna fallo si el disposer lanza excepción y deja el recurso `Active`, pero `TransitionOwnedAsync` descartaba el error. `WorkSessionManager.CloseAsync` confirmaba `Closed` y `work_close` retornaba éxito aunque el recurso seguía activo. En expiración ocurría lo mismo: sesión `Expired` con un recurso `Active`. Un segundo `work_close` y el siguiente sweep no reintentaban.

La reproducción externa temporal con un recurso ficticio que falla en el primer intento y funciona en el segundo confirmó ambos falsos éxitos, sin modificar archivos del proyecto. La suite anterior carecía de casos para esos errores.

## Cambios de código

1. **`ResourceRegistry.cs`:** `CloseOwnedAsync`/`ExpireOwnedAsync` pasan a devolver `LoomResult<Unit>`; visitan **todos** los recursos session-owned aunque uno falle. Si hay errores, responden `cleanup_failed` con `Retryable=true` y `failed_resources` (handle, código, mensaje), sin perder el registro. `ResourceRegistry.DisposeAsync` no vacía el registro cuando no logra limpiar todas las entradas; informa error.
2. **`WorkSessionManager.cs`:** sólo ejecuta `CompleteClose` al terminar todas las liberaciones. Ante fallo, la sesión permanece `Closing` con `ClosingTarget` (`Closed` o `Expired`), bloqueada para nuevas operaciones. `work_close` repetido reintenta sin alterar el objetivo de una expiración anterior. `WorkSessionManager.DisposeAsync` retiene sesiones fallidas y comunica el error. Los cierres concurrentes siguen serializados por `CloseGate`.
3. **Expiración:** `SweepExpiredAsync` reintenta sesiones `Closing` cada `SweepInterval`, tanto cierres explícitos como expiraciones. Devuelve el nuevo recuento opcional `FailedCleanups` sin romper constructores existentes. Registra el evento `WorkSessionCleanupFailed` y procesa las demás sesiones aunque una falle.
4. **`LifetimeSweeperService.cs`:** advertencia cuando hay limpiezas fallidas; las excepciones imprevistas no interrumpen indefinidamente el servicio de barrido.
5. **`WindowsProcessResource.cs` y `WindowsPythonWorkerResource.cs`:** disponer es de llamada única concurrente, compartiendo la finalización o el mismo error mediante un `TaskCompletionSource`. Si la primera liberación falla, la siguiente NO devuelve un éxito ficticio. Se intenta cerrar todos los componentes IO/pipe/proceso aunque otro falle. **No se repite arbitrariamente una liberación de handles nativos parcialmente ejecutada**, porque podría ser insegura.
6. **Sin nuevas herramientas MCP** ni cambios en parámetros de `work_close`; la nueva respuesta de error es intencional. Los recursos `Independent` siguen fuera del cierre de WorkSession.

## Semántica

| Escenario | Estado de WorkSession | Respuesta |
| --- | --- | --- |
| Todos los recursos liberados | `Closed` / `Expired` | Éxito |
| Algún disposer falla | `Closing`, objetivo conservado | `cleanup_failed`, reintentable |
| Reintento con éxito | `Closed` / `Expired` | Éxito |
| Fallo en barrido | `Closing`, pendiente | Se informa y reintenta en barridos posteriores |
| ResourceRegistry/WorkSessionManager shutdown parcial | Entradas fallidas preservadas | Excepción, sin borrado ciego |

**Límites:** `Retryable=true` significa que otro intento es admitido, no que todos los errores nativos sean recuperables. En los disposers de Windows que ya liberaron parcialmente un handle, la misma excepción puede persistir al repetir la llamada. La resolución de casos irreversibles requerirá diagnóstico. Los eventos actuales son efímeros (`DropOldest`); observabilidad durable permanece en RB-06.

## Verificaciones

- Pruebas focalizadas `LifetimeTests` y `WorkSessionTests`: **22/22** (antes 12/12).
- Confirmadas de forma adicional las dos regresiones preexistentes `WorkCloseDuringLateProviderStartDoesNotLeakProcess` y `...Worker` (evitar deadlock esperando un proveedor que ignora cancelación); **24/24** en ese filtro.
- Inyección de fallos: primer disposer falla, recuperación manual, fallo permanente retiene `Closing`, dos o más disposers fallan mientras los demás se limpian, cierres simultáneos, expiración reintentable, explícito tras expiración mantiene `Expired`, cierre automático de sesión bloqueada, independiente no afectado, shutdown parcial sin borrar entradas.
- **Suite Release completa 433/433 aprobada**, exit 0, sin tests omitidos, ejecutada secuencialmente con `dotnet test LoomLCI.slnx -c Release --no-restore -m:1 --verbosity quiet`: Core **151/151**, Windows **196/196**, Launcher **53/53**, Integration **21/21**, MCP **6/6**, PDF **6/6**.
- Hubo un fallo intermitente aislado durante una corrida paralela en `PostExitExpiryKillsIndependentDescendantsAndDeletesSpool`; pasó **10/10** intentos individuales consecutivos y en la corrida integral secuencial. No se atribuye causa concluyente; queda como observación de flakiness.

## Pendiente

- [x] Suite Release integral secuencial **433/433**, incluidas 21 IntegrationTests; sin omisiones.
- [x] Despliegue productivo local supervisado desde IvanSpace, con setup Inno auténtico, SHA-256, version/sequence, `CUTOVER_OK` y smoke de ChatGPT.
- [x] Smoke real: proceso PowerShell `SessionOwned` temporal iniciado desde ChatGPT; `work_close` exitoso y PID desaparecido. El manejo de `cleanup_failed` y reintentos se validó en pruebas controladas de Core, no se indujeron errores nativos en producción.
- [x] Previous sequence 7 conservada y Host disponible; rollback real ensayado en túnel independiente, sin ejecutarlo destructivamente en producción. PID de prueba y worker Python SessionOwned limpiados con `work_close`.

**Alcance final:** instalación local interna seq8 sin GitHub Release, push, firma/publicación de feed, cambio de túnel ni uninstall productivo. **La próxima secuencia deberá ser >=9.** Ver [[RB-03 - Actualización local secuencia 8]].
