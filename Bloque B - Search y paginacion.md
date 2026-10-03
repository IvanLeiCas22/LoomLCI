# Bloque B - Search y paginación

## Estado

Diseño aprobado. **B1 implementado, validado y cerrado en Git** (`12c2c1c feat: paginate filesystem traversal`); B2/B3 pendientes.

## Problema actual

`filesystem_list_tree`, `filesystem_find_paths` y `filesystem_search_text` tienen límites por llamada, pero no tienen continuation cursor.

Prueba live con el repo actual:
- `list_tree(maxEntries=3)` devuelve siempre las mismas 3 primeras entradas y `truncated=true`.
- `find_paths(maxResults=2)` devuelve siempre los mismos 2 primeros matches y `truncated=true`.
- `search_text(maxResults=2)` devuelve siempre las mismas 2 primeras líneas y `resultLimitReached=true`.

Repetir la llamada no permite progresar. Hoy el agente tiene que cambiar manualmente raíz/query, perdiendo exhaustividad y ergonomía.

## Recorrido actual

`WindowsFilesystemProvider.Enumerate`:
- usa BFS (`Queue`);
- ordena los hijos de cada directorio con `StringComparer.OrdinalIgnoreCase`;
- poda generated/custom exclusions;
- no sigue symlinks/reparse points;
- depth máximo 32.

BFS es bueno para `list_tree`: incluso en repos grandes muestra primero amplitud/top-level antes de consumir páginas en un único subtree. Conviene preservarlo.

## `search_text` actual

- Lee archivos completos con `File.ReadAllTextAsync`.
- Omite individualmente cualquier archivo >16 MiB.
- Tiene presupuesto agregado de 64 MiB por llamada.
- `maxResults` limita líneas coincidentes.
- Si alcanza 64 MiB o `maxResults`, informa incompletitud pero no permite continuar.
- Devuelve la línea coincidente completa y context lines completas: una línea minificada/enorme puede inflar mucho una respuesta.

## Referencia MCP

El patrón estándar de MCP para resultados grandes es cursor-based pagination:
- el servidor devuelve `nextCursor`;
- el cliente lo devuelve sin interpretarlo;
- el cursor es opaco;
- el orden debe ser determinista para que la continuación sea útil.

Aunque estas son tools custom y no `resources/list`, conviene adoptar exactamente la misma ergonomía: `cursor` opcional de entrada + `nextCursor` de salida.

## Alternativas evaluadas

### 1. Sólo aumentar límites

Descartado. Mueve el problema y aumenta contexto/coste sin volver exhaustiva la operación.

### 2. Offset simple para todo

Adecuado para metadata (`list_tree`/`find_paths`), pero insuficiente para `search_text`: reanudar dentro de un archivo grande obligaría a releer el contenido previo.

### 3. Cursor stateful server-side

Guardar enumerador/StreamReader vivo y devolver un token ID.

Ventajas: reanudación rápida y exacta.

Problemas:
- introduce nuevos recursos, TTL y cleanup;
- cursor muere al reiniciar Host;
- complica operaciones absolutas que hoy son stateless;
- reproduce problemas de lifecycle que acabamos de evitar en Process.

Descartado salvo evidencia futura de que el enfoque stateless no rinde.

### 4. Cursor opaco stateless + posición verificable

Recomendado.

El cursor es un token versionado Base64Url con:
- operación;
- fingerprint del request normalizado (root resuelto + queries/opciones; excluye sólo page size);
- posición de recorrido;
- path esperado en esa posición para detectar árbol modificado;
- para search dentro de archivo: line number + byte offset + metadata del archivo.

No necesita servidor state ni secret. El cliente lo trata como opaco. Cursor malformado/mismatched => `invalid_argument`; árbol/archivo cambiado => error explícito de cursor stale y reinicio desde cursor vacío.

## Diseño propuesto

### A. Infraestructura común de cursor

Agregar un codec interno versionado, por ejemplo `FilesystemCursorCodec`.

El cursor debe validar:
1. versión y operación;
2. fingerprint de parámetros;
3. posición no negativa;
4. path esperado al reanudar.

Page size (`maxEntries` / `maxResults`) no forma parte del fingerprint, para permitir cambiar el tamaño de la siguiente página. El resto de parámetros sí.

### B. `list_tree`

Mantener BFS actual.

Cursor:
- ordinal del próximo `Enumerate` item;
- relative path esperado en ese ordinal.

Al continuar:
- se reenumera metadata desde root;
- se salta hasta el ordinal;
- se valida que el path esperado coincida;
- se devuelve la siguiente página.

Costo: vuelve a recorrer metadata anterior, pero no abre contenido de archivos. Es un compromiso mucho más simple/robusto que persistir toda la frontera BFS en el token.

Resultado:
- `nextCursor: string?`;
- `truncated = nextCursor != null` para compatibilidad.

### C. `find_paths`

Mismo recorrido BFS y misma base de cursor.

El cursor debe apuntar al próximo match no devuelto usando:
- traversal ordinal;
- expected relative path.

Esto evita una página final vacía: al llenar `maxResults`, se puede avanzar metadata hasta detectar si existe otro match y generar cursor sólo entonces.

### D. `search_text`

Rediseñar el motor a streaming.

Objetivos:
- eliminar el límite individual de 16 MiB;
- mantener 64 MiB como **presupuesto de scan por llamada/página**, no como barrera total;
- poder reanudar dentro de un archivo grande sin releer su prefijo.

Cursor search:
- traversal/file ordinal;
- expected relative path;
- line number de reanudación;
- exact byte offset al comienzo de esa línea;
- file length + last-write metadata para detectar modificación del archivo actual.

El reader debe detectar encoding/BOM y mantener offsets exactos de línea. Al reanudar abre el mismo archivo, valida metadata, hace seek al byte offset y continúa.

El presupuesto de 64 MiB puede excederse mínimamente hasta un límite de línea/context controlado, pero nunca fuerza pérdida: si queda trabajo, se devuelve `nextCursor`.

`resultLimitReached` y `scanLimitReached` pasan a explicar **por qué terminó esta página**, no una pérdida permanente.

`truncated` puede mantenerse por compatibilidad con semántica simple: `nextCursor != null`.

Los campos `skippedLargeFileCount/skippedLargeFiles` dejan de tener sentido y deberían eliminarse del nuevo contrato, porque archivos grandes ya no se omiten.

### E. Mutaciones entre páginas

No prometer snapshot isolation del filesystem.

Para evitar duplicados/saltos silenciosos:
- cursor guarda path esperado en la posición de reanudación;
- si la reenumeración ya no coincide, se devuelve cursor stale/conflict y se pide reiniciar;
- si se reanuda dentro de archivo, validar length/last-write metadata.

Así preferimos fallo explícito antes que resultados incorrectamente exhaustivos.

### F. Líneas enormes / tamaño de respuesta

Hallazgo adicional: hoy una línea coincidente se devuelve completa. Con search sobre archivos grandes esto puede producir respuestas enormes incluso con `maxResults=1`.

Conviene corregirlo dentro del mismo rediseño:
- devolver excerpt acotado por línea;
- mantener `queryMatches.column` relativo a la línea original;
- agregar metadata tipo `textStartColumn` + `textTruncated` para que el excerpt sea interpretable;
- context lines también deben acotarse;
- `filesystem_read_files` sigue siendo la herramienta para expandir contexto exacto.

No fijar el número final sólo por intuición; 500–2000 chars por excerpt es un rango razonable a validar con pruebas de ergonomía.

## Semántica MCP propuesta

Inputs nuevos en las tres tools:
- `cursor?: string` — opaque cursor from previous call; reuse with the same traversal/search inputs.

Outputs nuevos:
- `nextCursor?: string` — null/omitted when exhaustive.

El agente no interpreta tokens. Flujo:
1. llamada normal sin cursor;
2. si `nextCursor` existe y necesita exhaustividad, repetir con ese cursor;
3. terminar cuando no haya `nextCursor`.

## Implementación recomendada por etapas después del OK

1. **B1 – Cursor infrastructure + list/find pagination** ✅
   - cursor Base64Url versionado y stateless;
   - fingerprint de traversal/search inputs (page size excluido);
   - detección explícita de cursor malformed/mismatched/stale;
   - BFS preservado y orden reforzado con tie-break ordinal;
   - `list_tree` y `find_paths` exponen `cursor`/`nextCursor`;
   - tests de varias páginas, cambio de page size, args mismatched y árbol modificado;
   - smoke live: `list_tree` continuó 2→3 entradas sin repetir; `find_paths` continuó 2→3 matches sin repetir.

2. **B2 – Streaming text search + search cursor** ✅
   - reader streaming con offsets exactos y soporte UTF-8/16/32;
   - eliminado el skip de archivos >16 MiB;
   - 64 MiB como page scan budget continuable;
   - cursor stateless dentro del archivo + stale detection;
   - binary-prefix heuristic con diagnostics;
   - bounded excerpts integrados para evitar líneas gigantes;
   - suite y smoke live >64 MiB correctos.

3. **B3 – Ergonomía/validación final**
   - la parte funcional de bounded excerpts + MCP metadata + integration tests fue absorbida por B2;
   - pendiente sólo refresh de schema + fresh-agent test de paginación completa.

## Criterios de aceptación

- una búsqueda/listado limitado puede recorrerse hasta el final sólo siguiendo `nextCursor`;
- no hay duplicados ni huecos en filesystem estable;
- cambio relevante entre páginas produce stale cursor explícito en lugar de resultados silenciosamente incorrectos;
- `search_text` encuentra matches en archivos >16 MiB;
- scan >64 MiB se completa en varias páginas sin perder zonas;
- result limit y scan limit son continuables;
- ningún page excede agresivamente el presupuesto de output;
- generated/exclusions/maxDepth mantienen exactamente sus semantics actuales;
- cursor permanece opaco y suficientemente pequeño;
- no se introduce ningún recurso/TTL/lifecycle server-side para paginación.
