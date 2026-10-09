# Bloque A - Process output

## Estado

Implementado, validado y cerrado en Git. Cambio funcional: `edd7373 feat: preserve process output history`.

## Problema actual

`WindowsProcessResource` crea un `BoundedChunkBuffer` independiente para stdout y stderr con `StreamBufferChars = 1 MiB`.

Cada pump lee hasta 4096 caracteres y hace `Append(string)`. El buffer asigna un cursor entero por chunk y descarta chunks antiguos cuando supera 1 MiB.

`process_read` acepta cursores separados de stdout/stderr y un `maxChars` total. `WindowsProcessResource.Read` divide ese presupuesto 50/50 entre ambos streams.

### Problemas verificados

1. **Pérdida irreversible después de 1 MiB.**
   - Prueba live: proceso emitió 1.200.000 caracteres.
   - Una lectura desde cursor 0 devolvió `earliestAvailableCursor = 63` y `truncated = true`.
   - La parte anterior ya no era recuperable.

2. **Cursor incorrecto cuando una lectura corta un chunk.**
   - Prueba live: proceso emitió `abcdef` en un único chunk.
   - `process_read(maxChars=1)` devolvió `a` con `nextCursor=1`.
   - Leer desde cursor 1 devolvió vacío: `bcdef` quedó inaccesible aunque seguía en memoria.
   - Causa: el cursor identifica chunks, no posiciones dentro del texto.

3. **Presupuesto rígido 50/50.**
   - Si stderr está vacío, stdout sólo puede usar la mitad de `maxChars` por llamada y viceversa.
   - No pierde información por sí mismo, pero obliga a más polling y hace ineficiente el presupuesto.

4. **Retención/lifecycle de recursos independientes.**
   - Los recursos session-owned se liberan al cerrar la WorkSession.
   - Un proceso `independent` no tiene hoy `process_close` ni TTL; su handle/recurso queda registrado hasta que el Host termina.
   - Esto ya existe como cuestión de lifecycle, pero cobra más importancia si cada recurso mantiene un spool en disco.

## Referencias externas

### OpenAI Codex actual

- Unified exec usa un límite de captura de 1 MiB y un buffer head/tail con conteo explícito de bytes omitidos.
- Su resultado diferencia output retenido, `original_token_count` y `output_omitted_bytes`.
- El `codex exec-server` expone `process/read` con cursor `afterSeq`, `maxBytes` y `waitMs`, además de notifications `process/output` y `process/exited`.
- Hay issues abiertos en Codex proponiendo auto-spill de tool output a archivos/artifacts precisamente porque un cap head/tail sigue siendo no recuperable.

Conclusión: el patrón correcto es separar **captura/retención** de **presupuesto visible al modelo**. Un límite de respuesta es sano; un límite de captura que destruye la única copia es una pérdida de capacidad.

### .NET

La documentación de `System.Diagnostics.Process` recomienda lectura asíncrona de stdout/stderr para evitar deadlocks. El diseño actual de pumps independientes ya sigue ese principio.

`FileOptions.DeleteOnClose` permite que un archivo temporal se elimine automáticamente al cerrar su último handle, útil para spools ligados al lifecycle de un recurso.

## Alternativas evaluadas

### A. Aumentar el ring buffer

Ejemplo: 1 MiB -> 64 MiB.

Ventajas: cambio mínimo.

Problemas: sigue habiendo pérdida irreversible, consume mucha RAM por proceso y el límite vuelve a aparecer con builds/logs mayores. No resuelve el bug de cursor parcial.

**Descartada como solución de fondo.**

### B. Head/tail en memoria + metadata de omisión

Similar a Unified Exec de Codex.

Ventajas: memoria acotada; conserva principio y final; hace visible la pérdida.

Problemas: el medio sigue siendo irrecuperable. Es bueno como resumen/model-facing output, no como storage primario de LoomLCI.

**Útil como fallback cuando se alcanza una cuota, no como almacenamiento principal.**

### C. Spool temporal recuperable + cursores exactos

Cada stdout/stderr se almacena secuencialmente en un spool temporal ligado al `ProcessResource`. El cursor deja de representar “número de chunk” y pasa a representar una posición absoluta de texto. `process_read` devuelve como máximo el presupuesto solicitado, pero la parte no devuelta sigue disponible para una lectura posterior.

Ventajas:
- elimina la pérdida temprana de 1 MiB;
- resuelve correctamente cortes parciales;
- separa retención de tamaño de respuesta;
- permite releer desde 0 mientras el recurso conserve el historial;
- el agente no necesita anticipar cuánto output producirá el proceso.

Costes:
- I/O temporal en disco;
- hay que definir una cuota para evitar que un proceso runaway llene el disco;
- lifecycle/cleanup debe ser robusto.

**Alternativa recomendada.**

## Diseño propuesto

### 1. `ProcessOutputStore`

Reemplazar `BoundedChunkBuffer` por un store por stream con:
- append secuencial;
- spool temporal creado fuera del workspace;
- archivo abierto con lifecycle ligado al recurso y `DeleteOnClose` cuando sea viable;
- índice liviano en memoria por cada chunk recibido del pump;
- cursor absoluto en caracteres observados, no ID de chunk.

El archivo puede almacenar records de chunks (`charCount`, `byteCount`, payload UTF-8) y el índice guardar `startCursor -> fileOffset`. Esto permite entrar en mitad de un chunk sin perder el resto y evita depender de offsets UTF-8 para el cursor público.

### 2. Semántica de cursor

Mantener los campos públicos existentes (`requestedCursor`, `earliestAvailableCursor`, `nextCursor`) para evitar un cambio conceptual de API.

Cambiar su semántica interna a posición absoluta de texto:
- `0`: comienzo del output retenido;
- `nextCursor`: posición exacta inmediatamente posterior al último carácter devuelto;
- una llamada con `nextCursor` nunca salta caracteres por haber cortado un chunk.

Los handles/cursors siguen siendo opacos para el agente, por lo que no debería depender de que antes fueran IDs de chunk.

### 3. Presupuesto de respuesta

`maxChars` sigue siendo un límite de **respuesta**, no de captura.

Cambiar la división rígida 50/50:
- dar inicialmente una cuota equilibrada a stdout/stderr;
- reutilizar el espacio no consumido por un stream para completar el otro;
- nunca superar el `maxChars` total.

### 4. Cuota de spool

No preservar output literalmente infinito.

La cuota debe ser mucho mayor que el presupuesto visible al modelo y su superación debe ser explícita. Para la primera implementación se eligió **64 MiB de spool por stream** (32 Mi caracteres UTF-16): suficientemente por encima del máximo de respuesta de 1 Mi caracteres, acotado por proceso y fácil de testear. No es un límite semántico definitivo y puede convertirse en configuración más adelante.

Cuando se supere:
- nunca perder datos silenciosamente;
- reportar cantidad/rango omitido;
- preferir conservar información útil de principio y final antes que sólo el tail si la implementación lo permite.

Una primera implementación controlable puede preservar todo hasta la cuota y, sólo al superarla, pasar a una política explícita de omisión. El contrato debe distinguir `response limited` de `history no longer retained`.

### 5. Cleanup

- session-owned: spool desaparece al `work_close` mediante `DisposeAsync`.
- Host shutdown/crash: `DeleteOnClose` reduce residuos temporales en Windows.
- independent: queda pendiente resolver explícitamente retención/close/TTL; no esconder este problema dentro del spool.

### 6. Tests necesarios antes de aceptar implementación

- output >1 MiB sigue íntegramente recuperable por páginas;
- `abcdef` con `maxChars=1` se recupera como `a`,`b`,`c`,`d`,`e`,`f` sin saltos;
- relectura desde cursor 0 es no destructiva;
- stdout-only puede aprovechar prácticamente todo `maxChars` aunque stderr esté vacío;
- stdout/stderr simultáneos no se bloquean entre sí;
- proceso terminado conserva output hasta liberar el recurso;
- work_close elimina spools de recursos session-owned;
- terminate conserva output hasta cleanup;
- cuota de spool: la pérdida, si ocurre, queda cuantificada y explícita;
- procesos concurrentes mantienen stores independientes.

## Propuesta de implementación por commits

1. Introducir `ProcessOutputStore` + cursor exacto + tests unitarios del store.
2. Integrarlo en `WindowsProcessResource` y corregir reparto de `maxChars`.
3. Agregar tests de integración/lifecycle y metadata MCP si cambia la explicación de cursores/truncation.
4. Smoke live con output >1 MiB y lecturas muy pequeñas.

## Resultado de implementación

- `BoundedChunkBuffer` fue reemplazado por `ProcessOutputStore` con spool temporal fuera del workspace y `DeleteOnClose`.
- Los cursores ahora son posiciones absolutas UTF-16 y `nextCursor` nunca salta texto por cortar un producer read.
- `maxChars` limita respuesta, no captura; stdout/stderr reutilizan dinámicamente el presupuesto no consumido por el otro stream.
- Cada stream retiene hasta 64 MiB de spool. Si se supera, `retentionLimitReached`, `retainedUntilCursor` y `observedUntilCursor` describen explícitamente el rango omitido.
- El store evita cortar pares sustitutos UTF-16: una lectura puede exceder `maxChars` en un code unit para devolver un carácter Unicode válido.
- Los spools se eliminan al liberar el recurso/WorkSession.
- El provider limpia el proceso si la inicialización del spool falla.
- Smoke live: `abcdef` recuperado carácter por carácter; 1.200.000 caracteres accesibles desde cursor 0 y 1.100.000; `😀X` leído como `😀` (cursor 0→2) y luego `X` (2→3).
- Prueba fresca posterior validó cursores, output grande, Unicode, presupuesto stdout/stderr, retención post-terminate y cleanup. Única fricción detectada: después de `work_close` los handles session-owned ya no pueden inspeccionarse; se aclaró explícitamente en la metadata de `work_close` que cualquier estado/output final debe leerse antes de cerrar.
- Validación final previa a esa aclaración: 40/40 tests, Release y Debug sin warnings/errores.

## No incluir en este bloque

- ConPTY/PTY.
- paginación de filesystem.
- redesign general de ResourceRegistry.
- `process_close`/TTL salvo que la implementación de spool demuestre que es imprescindible para no dejar recursos temporales sin control.
- long-poll `waitMs`; es interesante y Codex lo usa, pero no es necesario para resolver la pérdida de output.
