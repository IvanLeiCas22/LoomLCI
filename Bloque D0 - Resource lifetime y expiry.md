# Bloque D0 - Resource lifetime y expiry

## Estado

**D0.1 y D0.2 implementados y validados. D0.3–D0.4 pendientes.**

Objetivo: evitar crecimiento indefinido de registries y dar semantics explícitas a cierre, expiración y cleanup antes de incorporar nuevos recursos duraderos como Python Runtime.

## D0.1 - Lifetime primitives ✓

Implementado:

- `System.TimeProvider` como reloj inyectable compartido por Core;
- `LifetimeOptions` con defaults:
  - WorkSession idle timeout: **60 min**;
  - tombstone retention: **60 min**;
  - sweep interval: **30 s**;
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

## Pendiente

### D0.3 - ProcessHandle post-exit retention

Definir e implementar:

- inicio del TTL sólo al entrar en estado terminal;
- sliding retention por `process_status` / `process_read`;
- liberación de spool/handles al vencer;
- `resource_expired` después del TTL;
- proceso vivo no expira por falta de polling.

### D0.4 - Explicit release

Diseñar `process_release`:

- sólo para procesos ya terminales;
- descarta inmediatamente handle/output retenido;
- proceso vivo devuelve conflicto y orienta a `process_terminate`;
- no introducir todavía una tool genérica `resource_release`.
