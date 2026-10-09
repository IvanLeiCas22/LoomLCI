---
tipo: referencia_capacidad
estado: vigente
actualizado: 2026-10-09
---
# Filesystem

## Qué permite

Acceder a los archivos visibles para la cuenta Windows: listar árboles, encontrar rutas, buscar texto, leer archivos y aplicar cambios estructurados. No hay una restricción adicional por `WorkSession.baseDirectory`: esa ruta es **contexto para resolver rutas relativas**, no una raíz de seguridad.

## Herramientas MCP

- `filesystem_list_tree`: árbol acotado con paginación.
- `filesystem_find_paths`: rutas o nombres por fragmentos literales; admite múltiples consultas, suffix y cursor.
- `filesystem_search_text`: búsqueda textual literal OR con contexto y paginación; no es regex.
- `filesystem_read_files`: lectura en lotes, rangos de línea y aviso de contenido anterior/posterior.
- `filesystem_apply_patch`: `write`, `replace`, `move`, `delete`; validar coincidencias y destinos antes de mutar.
- `filesystem_manage_directory`: crear carpetas o eliminar una **carpeta vacía**.

Las herramientas de imagen/PDF están separadas en [[Visual Files]], aunque preserven el prefijo MCP `filesystem_`.

## Criterios de uso

1. Ruta desconocida → `list_tree` o `find_paths`.
2. Contenido desconocido → `search_text`; después `read_files` de rutas exactas.
3. Edición textual precisa → `apply_patch` con `expectedOccurrences`; evitar ediciones amplias sin preflight.
4. Comprobar efecto con lectura/diff, y evitar tocar `artifacts/`, `bin/`, `obj/` y `.git/` salvo propósito explícito.
5. `includeGenerated=false` poda directorios técnicos por defecto; apuntar explícitamente a un directorio excluido si su contenido es el objetivo.

Para límites numéricos, cursor, payload y contratos exactos utilizar `src/LoomLCI.Mcp/FilesystemTools.cs` y `src/LoomLCI.Core/Filesystem/FilesystemContracts.cs` o el schema MCP vivo, no copiar tablas estáticas aquí.

**Evidencias:** [[Bloque B2 - Streaming search]], [[RB-01 - Integridad de filesystem_apply_patch]], [[Bloque B - Search y paginacion]], [[Milestone 2 - Filesystem vertical slice]].
