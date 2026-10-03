# Milestone 1 - Process vertical slice

> Objetivo: validar la arquitectura con una capacidad real de punta a punta antes de construir el resto.

## Precondición

Git debe estar inicializado antes de escribir código, con `.gitignore` seguro para Obsidian y un commit baseline de la arquitectura actual.

## Qué debe existir al finalizar

    MCP client/test
          ↓
     LoomLCI.Mcp
          ↓
     LoomLCI.Core
          ↓
     LoomLCI.Windows.Process
          ↓
        Windows

## Scope

### Core

- WorkSession mínimo
- Invocation lifecycle
- Handle registry
- linked cancellation
- LoomError
- ActivitySource
- EventBus mínimo

### Process Windows

- direct executable launch con args[]
- working directory explícito
- environment overrides
- stdout/stderr por pipes
- ProcessHandle
- status
- stdin
- terminate process tree
- output buffers acotados con cursor
- exit metadata

ConPTY queda para el siguiente incremento si el modelo de pipes ya quedó sólido.

### MCP adapter mínimo

Sólo las tools necesarias para probar el slice.

Structured output + output schema.

No intentar exponer todavía toda la futura superficie de Loom.

## Casos de aceptación

1. Ejecutar un proceso corto y recibir exit code/stdout/stderr.
2. Un exit code no cero vuelve como resultado válido.
3. Arrancar un proceso largo y recibir proc_... rápidamente.
4. Consultar su estado desde otra tool call.
5. Leer output incrementalmente sin perderlo por polling repetido.
6. Escribir stdin.
7. Cancelar/terminar el árbol del proceso.
8. Cerrar WorkSession y limpiar procesos session-owned.
9. Handle inválido/expirado produce error estructurado recuperable.
10. Cancelar una Invocation no deja recursos parciales.
11. Dos procesos distintos pueden trabajar concurrentemente.
12. Cada Invocation aparece correlacionada en tracing/events.
13. MCP adapter no contiene lifecycle del proceso.

## Fuera de scope

- Filesystem tools
- ConPTY
- Computer Use
- Python Runtime
- Work Plan público
- sandbox
- elevation
- UI
- persistent audit storage

## Después del milestone

Orden propuesto:

1. ConPTY / process interactive semantics.
2. Filesystem.
3. Python Runtime.
4. Agent Support / Work Plan.
5. Computer Use.
6. evals reales con ChatGPT/Codex y PTC cuando esté disponible.
