# Bloque D0 - Resource lifetime y expiry

## Estado

**D0.1–D0.3 implementados y validados. D0.4 pendiente.**

Objetivo: evitar crecimiento indefinido de registries y dar semantics explícitas a cierre, expiración y cleanup antes de incorporar nuevos recursos duraderos como Python Runtime.

## D0.1 - Lifetime primitives ✓

Implementado:

- `System.TimeProvider` como reloj inyectable compartido por Core;
- `LifetimeOptions` con defaults:
  - WorkSession idle timeout: **60 min**;
  - tombstone retention: **60 min**;
  - sweep interval: **30 s**;
  - ProcessHandle post-exit retention: **15 min**;
- `ResourceRegistry` con estados:
  - `active`
  - `closing`
  - `closed`
  - `expired`
- timestamps de creación, último acceso y cambio de estado;
- `resource_expired` separado de `resource_closed` y `not_found`;
- cleanup físico inmediato al cerrar/expirar, conservando tombstone liviano;
- pruning de tombstones después del retention;
- `ResourceRegistry : IAsyncDisposable`, por lo que shutdown cierra también recursos `independent`.

## D0.2 - WorkSession expiry ✓

Implementado:

- expiry por inactividad;
- una Invocation activa mantiene viva la WorkSession mediante un lease interno;
- inicio y fin de Invocation refrescan actividad;
- `work_close` explícito sigue cancelando Invocations activas;
- race expiry vs nueva Invocation revalidada bajo estado de sesión;
- race expiry vs `work_close` serializada por `CloseGate`;
- expiry limpia recursos session-owned como `expired`;
- recursos independent sobreviven al expiry de la WorkSession;
- tombstones de WorkSession se podan después del retention;
- sweeper periódico como `BackgroundService` del Host;
- `work_create` devuelve `idleTimeoutSeconds` y documenta la expiración automática.

La lógica de expiry está en Core y es determinista. El Host sólo dispara `SweepExpiredAsync()` mediante `PeriodicTimer`.

## Validación

Tests con `Microsoft.Extensions.TimeProvider.Testing/FakeTimeProvider`, sin waits reales.

Cubierto:

- expiry por inactividad;
- refresh del TTL por actividad;
- Invocation larga bloquea expiry;
- el reloj idle recomienza al finalizar la Invocation;
- `work_close` cancela Invocation activa;
- `closed` vs `expired`;
- pruning a `not_found`;
- race expiry/work_close sin doble dispose;
- shutdown cierra recursos independent;
- schema/resultado MCP expone el TTL efectivo.

Resultado final:

- Core: **20/20**;
- Windows: **76/76**;
- Integration MCP: **5/5**;
- total Debug: **101/101**;
- total Release: **101/101**;
- Debug/Release: **0 warnings, 0 errores**;
- runtime administrado `loomlci`: actualizado y `ready`;
- smoke público por Secure MCP Tunnel: `work_create` devolvió `idleTimeoutSeconds: 3600`.

## D0.3 - ProcessHandle post-exit retention ✓

Implementado:

- un proceso `running`/`starting` no expira por falta de polling;
- el TTL empieza sólo cuando el root entra en `exited` o `terminated`;
- la ventana efectiva usa el más reciente entre `exitedAt` y el último acceso válido;
- `process_status` y `process_read` exitosos refrescan la ventana sliding;
- `process_terminate` exitoso también la refresca, útil cuando el root ya salió pero aún quedan descendientes;
- operaciones fallidas no refrescan el TTL;
- `postExitRetentionSeconds: 900` se publica en `process_start`;
- `retentionExpiresAt` se publica en `process_status` y dentro de `process_read.process` cuando el proceso ya es terminal;
- `ResourceOperationLease` impide que close/expiry disponga el recurso debajo de una operación ya iniciada;
- el sweep de Process comparte el `LifetimeSweeperService` existente, sin timers por proceso;
- `TimeProvider` llega hasta `WindowsProcessResource`, por lo que `StartedAt`/`ExitedAt` y TTL usan el mismo reloj inyectable;
- al expirar se liberan process handle, Job, pipes/ConPTY y spools; queda tombstone `resource_expired` hasta el pruning normal;
- un proceso `independent` sigue significando “sobrevive a `work_close`”, no “se desacopla de Loom”: después del exit del root también aplica el TTL;
- si en modo pipes quedan descendientes vivos cuando vence la retención, el disposal del Job los termina y elimina el spool;
- metadata MCP ajustada: `process_status`, `process_read` y `process_terminate` no se anuncian como estrictamente idempotentes porque las llamadas exitosas pueden extender la retención.

Validación específica:

- proceso vivo durante horas virtuales no expira;
- proceso de larga duración inicia su ventana recién en `ExitedAt`;
- expiry sin actividad;
- sliding refresh por status/read;
- write inválido post-exit no refresca;
- terminate post-exit refresca;
- read concurrente bloquea expiry hasta terminar;
- close explícito espera leases activos;
- backend Windows real: root de proceso `independent` sale, child queda vivo, vence TTL, child muere y spool se elimina;
- smoke público por Secure MCP Tunnel: `postExitRetentionSeconds=900`, exit code preservado y `retentionExpiresAt` avanzó entre `process_status` y `process_read`.

Resultado final acumulado:

- Core: **29/29**;
- Windows: **77/77**;
- Integration MCP: **5/5**;
- total Debug: **111/111**;
- total Release: **111/111**;
- Debug/Release: **0 warnings, 0 errores**;
- runtime administrado `loomlci`: actualizado y `ready`.

## Pendiente

### D0.4 - Explicit release

Diseñar `process_release`:

- sólo para procesos ya terminales;
- descarta inmediatamente handle/output retenido;
- proceso vivo devuelve conflicto y orienta a `process_terminate`;
- no introducir todavía una tool genérica `resource_release`.
