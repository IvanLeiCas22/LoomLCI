# Bloque D0 - Resource lifetime y expiry

## Estado

**D0.1–D0.4 implementados y validados. D0 cerrado.**

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

## D0.4 - Explicit release ✓

Implementado `process_release(processHandle)` como cleanup explícito de un proceso ya terminal:

- sólo acepta root en `exited` o `terminated`;
- `starting`, `running` o `terminating` devuelven `conflict` y orientan a `process_terminate`;
- descarta inmediatamente metadata pesada, output retenido, handles, Job y pipes/ConPTY;
- si aún quedan descendientes dentro del Job, el release también los limpia;
- el tombstone resultante es `resource_closed`, diferenciándolo del `resource_expired` automático;
- una segunda release es idempotente mientras exista el tombstone;
- release sobre un recurso ya expirado también es éxito idempotente, conservando el tombstone `expired`;
- después del pruning normal del tombstone, el handle pasa a `not_found`;
- no se agregó una tool genérica `resource_release`.

El Core incorporó `ResourceRegistry.CloseIfAsync<T>` como primitiva condicional reusable. Serializa con el mismo `Gate` de close/expiry, impide nuevos leases, espera operaciones activas y ejecuta el disposer una sola vez.

`process_release` deliberadamente no adquiere un lease de WorkSession: esto permite que converja con `work_close` incluso si la sesión ya está cerrando o cerrada.

Races validadas:

- release vs `process_read`: la lectura iniciada termina y release espera antes de eliminar el spool;
- release vs release: un solo dispose, ambas llamadas exitosas;
- release vs expiry: el ganador define `closed` o `expired`, release sigue siendo idempotente;
- release vs `work_close`: ambas operaciones convergen sin doble dispose;
- release vs procesos no terminales: no modifica el recurso;
- backend Windows real: root `independent` ya salido + child vivo → release mata el child, elimina el spool y deja `resource_closed`.

Contrato MCP:

- `ReadOnly=false`;
- `Destructive=true`;
- `Idempotent=true`;
- la descripción exige leer cualquier output final antes de release y aclara que no reemplaza a `process_terminate`.

Validación final de D0:

- Core: **39/39**;
- Windows: **78/78**;
- Integration MCP: **5/5**;
- total Debug: **122/122**;
- total Release: **122/122**;
- Debug/Release: **0 warnings, 0 errores**;
- integración MCP real validó `start → exit → read → release → status=resource_closed → release` idempotente;
- Secure MCP Tunnel con el Host Debug actualizado: **live/ready**;
- el catálogo de esta conversación permanece cacheado, por lo que `process_release` requiere un chat nuevo/refresco para aparecer como tool invocable desde ChatGPT.

## Estado final

**D0 cerrado.**

La base de lifecycle queda preparada para incorporar nuevos recursos duraderos —especialmente Python Runtime— sin crecimiento indefinido de registries ni cleanup ambiguo.
