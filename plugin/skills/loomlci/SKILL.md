---
name: loomlci
description: Usa LoomLCI cuando el usuario pida trabajar sobre su computadora local mediante el runtime LoomLCI.
---

# LoomLCI

Usa las herramientas del servidor MCP `loomlci` como la interfaz local preferida cuando el usuario pida trabajar con LoomLCI.

## Estado actual

La versión actual expone:
- `work_create`
- `work_close`
- `process_start`
- `process_status`
- `process_read`
- `process_write`
- `process_terminate`

## Flujo

1. Crea una WorkSession cuando una tarea necesite recursos que sobrevivan entre llamadas o una base directory.
2. Reutiliza el `work_id` mientras continúe el mismo trabajo.
3. Para procesos largos, conserva el `process_handle`; no bloquees esperando si existe trabajo independiente que pueda hacerse mientras tanto.
4. Usa los cursores devueltos por `process_read` para continuar leyendo output incrementalmente. La lectura es no destructiva.
5. Un exit code distinto de cero es un resultado del proceso, no necesariamente un error de Loom.
6. Cierra la WorkSession al terminar para limpiar recursos session-owned, salvo que el usuario haya pedido deliberadamente dejar un recurso independiente vivo.
7. No inventes capacidades que LoomLCI todavía no expone. Si falta Filesystem, Python o Computer, indícalo claramente en vez de sustituirlo silenciosamente por otra herramienta cuando el usuario haya pedido específicamente LoomLCI.
