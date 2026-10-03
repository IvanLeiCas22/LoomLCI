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
- `filesystem_list_tree`
- `filesystem_find_paths`
- `filesystem_search_text`
- `filesystem_read_files`
- `filesystem_apply_patch`
- `filesystem_manage_directory`

## Flujo

1. Crea una WorkSession cuando una tarea necesite recursos que sobrevivan entre llamadas o una base directory para rutas relativas.
2. Reutiliza el `work_id` mientras continúe el mismo trabajo.
3. Para explorar archivos, usa `filesystem_list_tree` para estructura, `filesystem_find_paths` para nombres/rutas y `filesystem_search_text` para contenido.
4. Usa `filesystem_read_files` cuando ya conozcas los archivos relevantes y `filesystem_apply_patch` para cambios de texto estructurados. Usa `filesystem_manage_directory` sólo para crear o borrar directorios.
5. Las rutas absolutas de Filesystem no necesitan WorkSession; las relativas sí necesitan un `work_id` con `baseDirectory`.
6. Para procesos largos, conserva el `process_handle`; no bloquees esperando si existe trabajo independiente que pueda hacerse mientras tanto.
7. Usa los cursores devueltos por `process_read` para continuar leyendo output incrementalmente. La lectura es no destructiva.
8. Un exit code distinto de cero es un resultado del proceso, no necesariamente un error de Loom.
9. Cierra la WorkSession al terminar para limpiar recursos session-owned, salvo que el usuario haya pedido deliberadamente dejar un recurso independiente vivo.
10. No inventes capacidades que LoomLCI todavía no expone. Python Runtime y Computer todavía no forman parte de la superficie pública actual.
