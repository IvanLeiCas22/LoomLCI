---
tipo: referencia_capacidad
estado: vigente
actualizado: 2026-10-09
---
# Process

## Modos

- `process_run`: comando corto, no interactivo, una llamada con exit code/stdout/stderr. No crea handle durable.
- `process_start`: retorna handle para tareas persistentes; luego `process_status` y `process_read`.
- `process_write`: entrada al proceso; `process_resize` sólo en ConPTY.
- `process_terminate`: termina proceso/árbol administrado; `process_release` libera un handle ya detenido o finalizado.

La ejecución se hace por **executable + arguments[]**: no se interpreta shell automáticamente. Para pipes, redirecciones o `&&`, iniciar explícitamente PowerShell/cmd. `ioMode=pipes` separa stdout/stderr; `ioMode=terminal` usa ConPTY para interacción terminal.

## Ciclo de vida

Procesos session-owned se limpian cuando la WorkSession se cierra. Los `Independent` pueden sobrevivir a `work_close`, pero siguen gestionados por LoomLCI; **no** son una vía segura para detener el propio Host durante un update.

La salida se conserva con cursores; evitar lecturas gigantescas, usar `nextCursor` y liberar recursos después de consumir la salida. Job Objects controlan descendientes según el modo de ejecución.

## Cuándo elegir

| Situación | Elección |
| --- | --- |
| `git status`, tests rápidos, consulta de versión | `process_run` |
| Proceso largo o salida incremental | `process_start` → `process_read` |
| REPL/TUI que necesita consola | `process_start` con `ioMode=terminal` |
| Procesos paralelos desde Python | `loom.process.run_many` en [[Python]] |
| Desplegar nueva versión de LoomLCI productivo | **Supervisor externo**; ver [[Actualizacion y recuperacion]] |

**Evidencias:** [[Bloque C1 - Native Process y Job Objects]], [[Bloque C2 - ConPTY]], [[Bloque D0 - Resource lifetime y expiry]], [[Ergonomía - process_run]], [[A4.1 - Ejecucion paralela local]].
