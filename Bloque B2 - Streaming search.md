# Bloque B2 - Streaming search

## Estado

Diseño aprobado e implementado. Motor: `0c854b4 feat: add streaming text search reader`. Integración/continuation: `1af8f8a feat: paginate streaming text search`. Falta únicamente validación fresh-agent posterior al refresh de schema.

## Hallazgos sobre la implementación actual

`filesystem_search_text`:
- enumera archivos en el mismo BFS determinista usado por B1;
- omite cualquier archivo >16 MiB;
- corta el scan al superar 64 MiB agregados;
- lee cada archivo completo con `File.ReadAllTextAsync`;
- parsea y conserva todas sus líneas en memoria;
- detecta binario si el texto decodificado contiene `NUL`;
- sólo entonces construye matches/context.

El contrato actual no puede continuar ni dentro de un archivo ni después del presupuesto de scan.

## Encoding y offsets

La conducta actual de `File.ReadAllText` detecta por BOM:
- UTF-8;
- UTF-16 LE/BE;
- UTF-32 LE/BE;
- sin BOM usa UTF-8.

Esto coincide con el helper `GetExistingTextEncoding` ya existente.

### No usar StreamReader.Position

`StreamReader` hace buffering. La documentación de .NET advierte que `BaseStream.Position` puede estar adelantado respecto del texto efectivamente consumido; usarlo como cursor produciría offsets incorrectos.

Decisión: el nuevo reader debe controlar los **bytes directamente** y usar un `Decoder`/encoding explícito.

### Validación práctica

Se generaron archivos temporales con los cinco encodings soportados y texto Unicode. Reanudar la decodificación desde un byte offset calculado exactamente al comienzo de una línea reprodujo el resto del texto de forma exacta en los cinco casos.

Conclusión: un cursor stateless puede reanudar dentro de archivo desde un **line-start byte offset**, siempre que se re-detecte/valide el encoding.

## Reader propuesto

Crear una pieza dedicada, por ejemplo `StreamingTextSearchReader`, separada del provider.

Responsabilidades:
1. abrir FileStream async/sequential;
2. detectar BOM/encoding;
3. leer bloques de bytes;
4. reconocer CRLF/LF/CR respetando el ancho/alineación del encoding;
5. decodificar incrementalmente;
6. mantener:
   - byte offset exacto de inicio de línea;
   - número de línea;
   - posición UTF-16 dentro de línea;
   - contexto anterior acotado;
   - estado de queries;
7. producir líneas/matches sin cargar el archivo completo.

No reutilizar directamente `ReadTextRangeAsync`: ese helper usa `StreamReader`, sirve para rangos por número de línea pero no expone offsets de bytes exactos para continuation.

## Matching literal en streaming

Hasta 32 queries, semantics actuales:
- OR;
- una línea física aparece una vez;
- `queryMatches` conserva todas las queries que coincidieron;
- columna = primera coincidencia, 1-based y en índice UTF-16;
- case-sensitive usa Ordinal;
- case-insensitive usa OrdinalIgnoreCase.

Para no conservar una línea gigante completa:
- procesar chars por bloques;
- conservar overlap de `maxQueryLength - 1` chars entre bloques;
- buscar sólo la primera aparición de cada query;
- contabilizar columna absoluta UTF-16;
- mantener una representación acotada de la línea para output/context.

## Acoplamiento B2/B3

B2 y B3 no son realmente independientes.

Si B2 hace streaming pero sigue materializando una línea completa, un JSON/minified bundle de cientos de MiB puede seguir:
- consumiendo memoria proporcional a la línea;
- generando una respuesta enorme con `maxResults=1`.

Ripgrep expone un problema equivalente con líneas enormes y ofrece `--max-columns` / preview para limitar lo que imprime.

**Recomendación:** implementar streaming + representación acotada/excerpt en el mismo subbloque. Se pueden separar en dos commits, pero no conviene desplegar B2 sin el guardrail de líneas.

## Cursor de search

Extender `FilesystemCursorCodec` con payload específico de search.

Fingerprint incluye:
- root resuelto;
- queries y su orden;
- caseSensitive;
- includeGenerated;
- excludeDirectories;
- maxDepth;
- contextLines.

Fingerprint NO incluye:
- maxResults;
- presupuesto interno de scan.

Así se puede cambiar page size entre páginas, igual que B1.

Continuation dentro de archivo:
- traversal ordinal del archivo;
- expected relative path;
- line number de la próxima línea a procesar;
- byte offset exacto de esa línea;
- file length observado;
- LastWriteTimeUtc ticks;
- opcionalmente encoding id/preamble para validación adicional.

Al reanudar:
1. reenumerar metadata BFS hasta el ordinal;
2. validar expected path/type;
3. validar file length + timestamp;
4. detectar encoding;
5. seek al byte offset de inicio de línea;
6. continuar.

No hay server-side state ni TTL.

## Mutaciones

No se promete snapshot isolation.

Además de la stale detection de B1:
- snapshot de length/last-write antes de leer una página;
- si se reanuda dentro del archivo, validar contra cursor;
- volver a leer metadata al terminar el segmento y fallar si cambió durante la lectura.

Es best-effort, no una protección criptográfica contra alguien que reescriba contenido y restaure metadata.

## Context lines y continuation

Para `contextAfter` hay que leer hasta 3 líneas posteriores al match.

Si una página termina por `maxResults`, el cursor debe apuntar a la **línea inmediatamente posterior al último match devuelto**, no después de las líneas leídas sólo como contexto.

Eso implica releer hasta 3 líneas en la página siguiente. Es preferible a saltar posibles matches.

## Presupuesto de 64 MiB

Cambiar significado:
- deja de ser límite total de capacidad;
- pasa a ser **scan budget por llamada/página**;
- `bytesRead` sigue siendo bytes procesados en esa página.

Primera implementación recomendada:
- detenerse en límites seguros de línea;
- si se alcanza el presupuesto dentro de una línea, terminar esa línea antes de devolver cursor;
- por tanto el presupuesto es estricto entre líneas y puede tener overrun en una línea patológicamente grande.

Como el matcher será chunked y el output acotado, ese overrun no implica memoria proporcional a la línea, sólo más I/O/trabajo en esa llamada.

No recomiendo añadir continuation intra-line ahora: exigiría serializar estado parcial de hasta 32 queries y complica mucho el cursor. Evaluarlo sólo si pruebas reales muestran archivos one-line gigantes como caso prioritario.

## resultLimitReached / scanLimitReached / truncated

Semantics propuestas:
- `nextCursor != null`: queda trabajo por recorrer.
- `truncated = nextCursor != null`: compatibilidad/simple incompleteness de esta página.
- `resultLimitReached=true`: la página se detuvo porque llegó a maxResults.
- `scanLimitReached=true`: la página se detuvo porque llegó al scan budget.
- ambos pueden ser true en el mismo page si coinciden.

Cuando maxResults se llena no conviene escanear arbitrariamente hacia adelante sólo para saber si hay otro match. Por eso una página siguiente puede legítimamente resultar vacía y finalizar sin nextCursor.

## Archivos >16 MiB

Eliminar `MaxTextFileBytes` como filtro de search.

El mismo reader procesa archivos de cualquier tamaño por páginas.

`skippedLargeFileCount` / `skippedLargeFiles` dejan de tener sentido y deberían salir del nuevo contrato.

## Binarios: incompatibilidad con la semántica vieja

La implementación actual puede decidir “este archivo es binario” porque primero lee el archivo completo y después busca NUL.

Con paginación streaming eso no es compatible con un scan budget: para garantizar que ningún NUL existe habría que recorrer el archivo entero antes de devolver el primer match.

Opciones evaluadas:
1. pre-scan completo de NUL: preserva semantics pero rompe el objetivo de paginación;
2. late NUL detection: puede descubrir en página N que un archivo cuyos matches ya devolvió era binario;
3. clasificación determinista por prefijo: práctica, barata y estable entre páginas.

**Recomendación:** cambiar explícitamente a una heuristic de binario por prefijo decodificado (p.ej. primeros 8–64 KiB), realizada antes de devolver matches del archivo. Si hay NUL en ese prefijo, se omite el archivo. Después se trata como texto durante ese search.

Es un cambio de semantics pequeño pero necesario para streaming. Ripgrep también usa detección heurística basada en NUL y documenta que la estrategia de detección depende del modo de búsqueda.

Conviene exponer al menos:
- `skippedBinaryFileCount`;
- muestra `skippedBinaryFiles`;

para que el filtrado no sea silencioso.

El tamaño exacto del prefijo se fija durante implementación/tests, no por intuición.

## Representación acotada de líneas

Recomendación mínima para que B2 sea seguro:
- `FilesystemTextMatch.Text` pasa a ser excerpt, no necesariamente línea completa;
- agregar:
  - `TextStartColumn` (1-based respecto a línea original);
  - `TextTruncated`;
- `queryMatches.column` sigue siendo columna en la línea original;
- context lines también se acotan y deberían tener metadata si se quiere saber que fueron abreviadas.

La política exacta de excerpt debe intentar incluir los matches relevantes, no sólo truncar el final ciegamente.

Esto reemplaza el B3 original como cambio funcional; B3 puede quedar como etapa de ergonomía/schema/smoke final.

## Implementación sugerida después del OK

### Commit 1 — motor
- `StreamingTextSearchReader`;
- BOM/encoding + byte offsets;
- matcher literal chunked;
- binary-prefix heuristic;
- bounded line snapshots;
- tests unitarios de UTF-8/16/32, CRLF/LF/CR, Unicode, query crossing chunk boundaries y línea gigante.

### Commit 2 — continuation/contract
- search-specific cursor;
- `cursor` / `nextCursor`;
- 64 MiB page scan budget;
- result/scan continuation;
- stale detection;
- eliminar large-file skip;
- nuevos diagnostics binarios/excerpt;
- actualizar MCP metadata e integration tests.

### Validación
- >16 MiB con match después del antiguo límite;
- >64 MiB completado en varias páginas;
- resume dentro del mismo archivo;
- maxResults pequeño sin duplicados/huecos;
- context lines en borde de página;
- UTF-8/16/32;
- mutate file entre páginas => cursor_stale;
- giant single line no genera output/memoria proporcional a su tamaño;
- live smoke + fresh-agent test.

## Decisiones aprobadas e implementadas

1. B2 absorbió la parte funcional de B3: excerpts acotados de hasta 500 caracteres, con `textStartColumn` / `textTruncated` y metadata equivalente para contexto.
2. Binary detection usa un prefijo decodificado de 64 KiB y reporta `skippedBinaryFileCount` / `skippedBinaryFiles`.
3. El scan budget real sigue siendo 64 MiB por página y se aplica en límites de línea; una línea individual puede excederlo hasta completarse.

## Resultado de validación

- Reader probado en UTF-8, UTF-16 LE/BE y UTF-32 LE/BE con offsets exactos.
- CRLF/LF/CR, Unicode, query cruzando buffer y línea gigante cubiertos.
- Archivo >16 MiB con match posterior al límite viejo se encuentra normalmente.
- Continuation por `maxResults` no duplica ni salta matches y reconstruye context lines.
- Cursor incompatible => `invalid_argument`; archivo mutado => `conflict` con `cursor_stale`.
- Suite completa: **55/55 tests**.
- Smoke live real con archivo de 67.584.019 bytes:
  - página 1: `bytesRead=67.108.864` (64 MiB exactos), sin match, `scanLimitReached=true`, `nextCursor` en línea 16.385;
  - página 2: match `needle-after-budget` en línea 16.501, `bytesRead=475.155`, sin truncación ni continuación restante.
- Runtime Debug actualizado y operativo.

Pendiente: fresh-agent test después de refrescar las acciones/schema de la app.
