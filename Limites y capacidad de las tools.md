# Límites y capacidad de las tools

## Objetivo

Los límites de LoomLCI deben controlar coste, memoria y tamaño de respuesta sin volver inaccesible información legítima de la PC del usuario.

Regla de diseño: **limitar la respuesta o el trabajo por llamada, no la capacidad total**. Cuando un límite se alcanza, el agente debe poder detectarlo y continuar de forma estructurada.

## Hallazgos 2026-10-03

### Errores MCP

- `LoomLCI.Host` devuelve fallos de dominio como `CallToolResult` con `isError=true`, texto compacto y `structuredContent.ok=false`.
- El cliente MCP stdio de integración recibe ese resultado normalmente.
- La app live de ChatGPT transforma el mismo `isError=true` en una excepción exterior `INVALID_ARGUMENT / RuntimeException`, aunque conserva el texto de dominio.
- El log del Secure MCP Tunnel muestra que el Host completó la llamada con `IsError = True` y el dispatcher la reenvió; no hay evidencia de excepción local.
- No falsear `isError=false` para adaptarse a ChatGPT: rompería la semántica MCP correcta.

### Filesystem

Límites actuales relevantes:
- `list_tree`: depth <= 32, entries <= 5000.
- `find_paths`: <= 32 queries, depth <= 32, results <= 1000.
- `search_text`: <= 32 queries, depth <= 32, results <= 500, context <= 3, 16 MiB por archivo, 64 MiB escaneados.
- `read_files`: <= 32 archivos, <= 10000 líneas por archivo, 16 MiB por archivo y 64 MiB agregados.
- `apply_patch`: <= 64 cambios y 16 MiB por archivo/contenido de texto.

Problemas prioritarios:
1. `read_files` rechazaba un archivo >16 MiB incluso si sólo se pedían unas pocas líneas. El presupuesto agregado sumaba el tamaño completo de los archivos, no lo realmente devuelto.
2. `search_text` omitía silenciosamente archivos >16 MiB. `truncated` no distinguía límite de resultados de límite de escaneo.
3. `apply_patch` validaba tamaño/textualidad también para `delete` y `move`, impidiendo operar sobre archivos grandes/binarios y usando backups completos en memoria.
4. `list_tree`, `find_paths` y `search_text` tienen límites de resultados sin cursor/paginación.

Etapa 1 implementada:
- `read_files` lee por streaming y permite rangos de archivos >16 MiB cuando se especifica `limit`.
- La lectura completa sin `limit` mantiene el guardrail de 16 MiB y explica cómo continuar por rangos.
- El presupuesto agregado de `read_files` mide texto realmente devuelto, no tamaño de archivos fuente.
- `search_text` mantiene temporalmente sus límites de 16/64 MiB, pero ahora expone `resultLimitReached`, `scanLimitReached`, `skippedLargeFileCount` y una muestra `skippedLargeFiles`; `truncated` pasa a significar cualquier incompletitud conocida.
- `apply_patch` separa operaciones textuales de operaciones de archivo: `write/replace` siguen sujetos al límite textual; `delete/move` funcionan con archivos grandes/binarios sin leer su contenido.
- Los backups de `write/delete/move` se realizan mediante archivos temporales hermanos en disco en lugar de copiar archivos completos a memoria.

### Process

- Cada stream conserva sólo 1 MiB en memoria; output anterior se pierde si el productor supera el buffer antes de ser leído.
- `process_read.maxChars` limita respuesta, pero el buffer limita capacidad histórica.
- Hay un edge case: cortar parcialmente un chunk puede avanzar el cursor completo y perder la parte no devuelta.

Implementado en [[Bloque A - Process output]]: spool temporal recuperable por stream, cursores absolutos UTF-16, presupuesto de respuesta separado de captura, cuota explícita de 64 MiB por stream y metadata de retención cuando se alcanza.

## Pendientes

- [x] Etapa 1: lectura ranged de archivos grandes + tests.
- [x] Señalización detallada de límites de `search_text`.
- [ ] Diseñar continuación/paginación para list/find/search sin inflar respuestas.
- [x] Rediseñar retención de stdout/stderr sin pérdida temprana.
- [ ] Evaluar capacidades estructuradas para PDF/imágenes/documentos separadas de `read_files`.
