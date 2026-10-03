# Milestone 2 - Filesystem vertical slice

> Estado: implementado y validado el 2026-10-03.
>
> Objetivo: agregar acceso estructurado al filesystem real del usuario sin convertir el adapter MCP en dueño de la semántica ni introducir sandbox artificial.

## Resultado actual

Nueva capability stateless:

- `FilesystemCapability` en Core.
- `IFilesystemProvider` como contrato interno.
- `WindowsFilesystemProvider` como implementación Windows Full Trust.
- MCP sigue siendo un adapter fino.
- Todas las operaciones pasan por `InvocationRunner` y quedan correlacionadas por InvocationId.

Tools MCP nuevas:

- `filesystem_list_tree`
- `filesystem_find_paths`
- `filesystem_search_text`
- `filesystem_read_files`
- `filesystem_apply_patch`
- `filesystem_manage_directory`

## Semántica de rutas

- una ruta absoluta puede usarse sin WorkSession;
- una ruta relativa requiere `workId` cuya WorkSession tenga `baseDirectory`;
- no existe cwd global mutable;
- Full Trust significa acceso a lo que permita el usuario actual de Windows, sin roots artificiales de Loom;
- los recorridos recursivos no siguen reparse points/junctions, evitando ciclos y expansión accidental.

## Descubrimiento y lectura

### list_tree

Árbol recursivo acotado:

- `maxDepth`: 1..32
- `maxEntries`: 1..5000
- devuelve rutas relativas al root consultado
- distingue file / directory / symlink

### find_paths

Búsqueda literal de paths:

- uno o varios queries con OR
- modos `substring` y `suffix`
- filtro opcional por tipo
- sin glob ni regex implícitos

### search_text

Búsqueda literal de texto:

- case-sensitive opcional
- línea/columna + contexto acotado
- máximo 16 MiB por archivo
- presupuesto agregado de 64 MiB por búsqueda
- archivos binarios simples se omiten
- recorrido streaming; no materializa primero todo el árbol

### read_files

- hasta 32 archivos por llamada
- offset/limit por líneas, 1-based
- máximo 16 MiB por archivo
- máximo 64 MiB agregado
- conserva los line endings originales dentro del rango devuelto, evitando que un read altere silenciosamente el texto que luego se usa para un patch

## Edición estructurada

`filesystem_apply_patch` soporta:

- `write`
- `replace`
- `delete`
- `move`

Reglas iniciales:

- 1..64 cambios por llamada
- `replace` es exacto y exige `expectedOccurrences`
- paths tocados más de una vez en el mismo batch se rechazan; ediciones dependientes se separan en llamadas distintas
- primero se valida el batch completo
- si falla durante la ejecución se intenta rollback en orden inverso
- rollback conserva bytes originales de archivos ya existentes
- replace/overwrite conserva BOM/encoding común existente; archivos nuevos se escriben UTF-8 sin BOM
- directorios se manejan por `filesystem_manage_directory`
- create crea padres faltantes; delete sólo elimina directorios vacíos

## Concurrencia

Filesystem no crea ResourceHandles.

Las Invocations de lectura/búsqueda pueden coexistir con otras operaciones y con Process. No se agregó lock global al Core.

## Tests

Estado al cierre del milestone:

- Core.Tests: 3/3
- Windows.Tests: 12/12
  - 7 Process existentes
  - 5 Filesystem nuevos
- IntegrationTests MCP: 2/2
  - Process roundtrip existente
  - Filesystem roundtrip real: work_create → mkdir → apply_patch → list_tree → find_paths → search_text → read_files → replace → work_close

Total: 17 tests verdes.

El Host completo también fue recompilado en una salida alternativa con 0 warnings / 0 errors porque las instancias MCP vivas de ChatGPT mantienen bloqueadas las DLL del `bin/Debug` normal. No fue necesario terminar esas conexiones.

## Decisiones que se mantienen

- Filesystem es stateless: no usa Handle Registry.
- WorkSession aporta contexto de ruta relativa, no una frontera de seguridad.
- Core no depende de Windows ni de MCP.
- Shell/Process sigue siendo escape hatch general; Filesystem es la vía preferida para descubrir, buscar, leer y editar archivos.
- imágenes/PDF no se agregaron a este slice; pueden añadirse como operaciones estructuradas después si los evals justifican su valor.

## Siguiente paso

ConPTY + Job Objects / semántica interactiva de Process.
