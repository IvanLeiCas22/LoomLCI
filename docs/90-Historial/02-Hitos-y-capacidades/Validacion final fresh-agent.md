# Validación final fresh-agent

## Estado

**Baseline v0.1 aceptada para continuar con nuevas capabilities.**

Prueba realizada desde un chat nuevo de ChatGPT normal, sin contexto previo del proyecto y usando exclusivamente las capacidades públicas expuestas por LoomLCI mediante Secure MCP Tunnel.

## Resultado

El agente descubrió **15 capabilities públicas** y ejercitó las 15:

- Work:
  - `work_create`
  - `work_close`
- Filesystem:
  - `filesystem_list_tree`
  - `filesystem_find_paths`
  - `filesystem_search_text`
  - `filesystem_read_files`
  - `filesystem_apply_patch`
  - `filesystem_manage_directory`
- Process:
  - `process_start`
  - `process_status`
  - `process_read`
  - `process_write`
  - `process_resize`
  - `process_terminate`
  - `process_release`

No se modificaron archivos existentes del proyecto. Todas las escrituras se limitaron a `loom-final-fresh-test`, que fue eliminado al finalizar.

## Cobertura observada

### Work/session

- creación correcta;
- `idleTimeoutSeconds=3600`;
- resolución relativa/ownership correctos;
- cierre correcto;
- uso posterior devolvió `resource_closed`.

### Filesystem

- `list_tree` paginado con distintos tamaños de página;
- continuaciones sin solapamientos observados;
- búsqueda de paths y de contenido;
- lectura de rangos acotados;
- creación, reemplazo, move y delete verificados;
- cleanup completo del directorio temporal.

### Process pipes

- proceso con exit code conocido **7**;
- stdout reconstruido exactamente como `abcdef`;
- stderr reconstruido exactamente como `ERR`;
- cursores independientes sin pérdida ni duplicación.

### Process terminal

- PowerShell confirmó consola real:
  - input no redirigido;
  - output no redirigido;
  - error no redirigido;
- tamaño inicial observado: **80x24**;
- resize observado desde dentro del proceso: **100x30**;
- input interactivo correcto;
- salida normal con código 0.

El redraw VT/ANSI después del resize se comportó como output de terminal real, no como duplicación de cursores.

### Lifecycle / terminate / release

- proceso largo observado vivo;
- `process_release` sobre proceso vivo devolvió `conflict` y orientó correctamente a `process_terminate`;
- `process_terminate` llevó el proceso a `terminated`;
- los tres handles terminales se liberaron;
- consultas posteriores devolvieron `resource_closed`;
- no quedaron procesos de prueba vivos.

## Errores y ergonomía

Los errores semánticos encontrados fueron accionables:

- `conflict`;
- `resource_closed`.

El agente se recuperó sin necesitar conocimiento implícito.

No surgieron naturalmente `resource_expired` ni `not_found`, por lo que la prueba fresh-agent no pretende reemplazar la cobertura automatizada de esos estados.

### Wrapping externo de errores

LoomLCI devuelve errores de ejecución como exige MCP:

- `CallToolResult.IsError = true`;
- `structuredContent` conserva `{ ok: false, error: { code, message, ... } }`;
- el contenido textual conserva el código y mensaje semánticos.

ChatGPT/túnel puede representar exteriormente esos tool errors como `INVALID_ARGUMENT` / `RuntimeException`. Esa presentación pertenece a la capa consumidora y no implica pérdida del error estructurado de LoomLCI. No se modifica LoomLCI para ocultar `isError`, porque hacerlo degradaría la semántica MCP.

## Validación automatizada asociada

Después de la prueba fresh-agent se agregó cobertura STDIO real de `process_terminate`.

Estado final automatizado:

- Core: **39/39**;
- Windows: **78/78**;
- Integration MCP: **6/6**;
- total Release: **123/123**;
- total Debug: **123/123**;
- build Release y rebuild aislado de IntegrationTests Debug: **0 warnings / 0 errores**.

## Operación

El Secure MCP Tunnel volvió a quedar bajo el supervisor administrado:

- alias: `loomlci`;
- proceso administrado corriendo;
- `healthz = live`;
- `readyz = ready`;
- `runtime_state = ready`;
- sin issues locales reportados.

## Conclusión

Desde la perspectiva de un agente completamente nuevo, la interfaz pública actual es descubrible, coherente y suficientemente robusta para incorporar nuevas capabilities.

**La baseline actual queda cerrada. Próximo bloque candidato: Python Runtime.**
