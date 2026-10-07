# Python 3 - Outputs binarios e imágenes

> Estado: **P3.0 y P3.1 CERRADOS. P3.2 (evaluation / deployment / plugin) pendiente.** `python_execute` ya entrega mixed `StructuredContent + ImageContentBlock`, aplica el presupuesto MCP de 9 MiB y conserva semántica post-execution recuperable.

## Objetivo

Permitir que código ejecutado dentro de `python_execute` produzca contenido visual/binario que llegue al modelo sin salir del worker Python, sin MCP recursivo y reutilizando los límites/semántica ya validados por Visual Files.

El primer tipo model-visible recomendado es **imagen**. El transporte interno debe quedar preparado para otros outputs binarios, pero no conviene prometer blobs/audio genéricos hasta validar su comportamiento real en ChatGPT.

## Estado actual relevante

### Python

- catálogo MCP público: **25 tools**;
- `python_execute` reutiliza un worker persistente por WorkSession;
- protocolo Worker/Host: **v3**, length-prefixed JSON UTF-8 sobre Named Pipe;
- request frame: **2 MiB**;
- final response frame: **40 MiB**;
- bridge call: **2 MiB**;
- bridge result: **8 MiB**;
- resultado Python interno: status + stdout/stderr + truncation + exception + typed outputs;
- resultado MCP de `python_execute`: StructuredContent con metadata no binaria + `ImageContentBlock` para PNG/JPEG/WebP válidos;
- `loom.__bridge_version__ == 2`;
- assets `worker.py` / `loom_bridge.py` son content-addressed, por lo que un Host nuevo materializa automáticamente scripts nuevos sin modificar CPython 3.14.8.

### Visual Files

Camino ya validado end-to-end:

```text
bytes válidos
  -> ImageContentBlock.FromBytes(...)
  -> CallToolResult mixto
  -> Secure MCP Tunnel
  -> ChatGPT/model vision
```

Límites actuales:

- imagen PNG/JPEG/WebP: **6 MiB** raw;
- `CallToolResult` serializado visual: **9 MiB**;
- tunnel observado: hard limit **10 MiB**;
- el cálculo autoritativo considera base64 + escaping JSON real;
- las tools visuales que devuelven imagen **omiten outputSchema** porque ChatGPT descartaba el `ImageContentBlock` adicional cuando el resultado mixto era tratado como output tipado.

## Hallazgos

### 1. No usar el bridge RPC para transportar la imagen

No conviene implementar algo como:

```python
loom._bridge_call("output.image", {"data": ...})
```

El bridge call está limitado a **2 MiB**. Incluso base64 de una imagen de ~1,5 MiB ya rozaría ese límite, muy por debajo de los 6 MiB permitidos por Visual Files.

Además sería un round-trip innecesario durante la ejecución.

### 2. No usar archivos temporales como transporte principal

Es técnicamente viable hacer Python -> temp file -> Host -> ImageContentBlock, pero agrega:

- I/O a disco;
- cleanup en crash/cancel;
- ownership/ACL/stale files;
- más estados de lifecycle;
- semántica distinta para bytes en memoria vs paths.

Puede seguir siendo un fallback futuro, pero no es la opción recomendada para P3.

### 3. El resultado final de python_execute es el punto correcto

Recomendación:

```python
loom.display_image(data)
```

debe ser una API **local al worker**, no un bridge method.

Durante la ejecución acumula outputs en estado execution-local. Al terminar, `worker.py` incorpora esos outputs al frame final junto a stdout/stderr/exception. El Host decodifica, valida y `PythonTools` los convierte a `ImageContentBlock`.

Ventajas:

- no MCP recursivo;
- no nuevos recursos/handles;
- no archivos temporales;
- mantiene outputs ligados a un único execute;
- una excepción Python normal puede conservar imágenes producidas antes de la excepción, igual que stdout/stderr;
- timeout/cancel/crash siguen descartando el resultado completo y el worker según la semántica actual.

### 4. JSON/base64 interno es suficiente

Con el cap raw actual:

```text
6 MiB = 6.291.456 bytes
base64 = 8.388.608 bytes
```

Python `json.dumps(..., ensure_ascii=True)` no escapa `+`, por lo que una imagen de 6 MiB sigue ocupando ~8 MiB dentro del pipe. No hace falta introducir framing binario/raw en P3.

Raw sidecar frames ahorrarían copias, pero aumentarían mucho la complejidad de framing, interleaving, cancelación y tests para un payload ya acotado.

### 5. Hay que endurecer el frame final combinado

El frame actual es 32 MiB. Con:

- stdout máximo 1.048.576 code points;
- stderr máximo 1.048.576;
- escaping Unicode patológico;
- hasta 6 MiB de imagen;

el frame final puede superar 32 MiB aunque cada componente individual sea válido.

P3 debe resolverlo explícitamente. Recomendación:

- bump del protocolo Worker/Host **v2 -> v3**;
- elevar de forma acotada el final response frame a **40 MiB** (o valor equivalente calculado con margen);
- mantener guards específicos de cantidad/tamaño de outputs;
- el límite externo autoritativo sigue siendo el **CallToolResult MCP de 9 MiB**, no el frame local.

No conviene depender de que `_write_frame` mate el worker si el resultado combinado resulta grande.

### 6. python_execute debe convertirse en resultado MCP mixto

Actualmente `python_execute` anuncia `outputSchema`.

Para que las imágenes lleguen a visión, debe alinearse con `filesystem_view_image` / `filesystem_render_pdf_page`:

- mantener `StructuredContent` manual;
- omitir `OutputSchemaType` / outputSchema en `python_execute`;
- devolver:
  - TextContentBlock breve;
  - StructuredContent con metadata normal;
  - uno o más `ImageContentBlock` en éxito.

Esto cambia metadata MCP pero **no agrega una tool pública**: el catálogo sigue en 25.

### 7. Reutilizar validación Visual Files

No confiar en bytes/mime declarados por Python.

Conviene extraer el detector/validador PNG/JPEG/WebP hoy privado en `WindowsVisualFilesProvider` a un helper reutilizable de Core/VisualFiles.

Así:

- `filesystem_view_image` mantiene la misma validación;
- Python image outputs usan exactamente la misma detección;
- el MIME real lo determina LoomLCI a partir de bytes.

### 8. Límite por ejecución

Propuesta inicial:

- máximo **4 imágenes** por `python_execute`;
- máximo individual: **6 MiB**;
- máximo raw agregado: **6 MiB**;
- máximo MCP serializado total del `CallToolResult`: **9 MiB**.

El cap agregado permite varias plots pequeñas sin multiplicar el riesgo de transporte.

Si el resultado mixto supera 9 MiB por stdout/stderr + imágenes + escaping, devolver un error local estructurado, por ejemplo:

```text
code = unsupported
reason = python_result_too_large / visual_payload_too_large
```

No truncar ni descartar imágenes silenciosamente.

### 9. API Python recomendada

Inicialmente sólo una primitive sin dependencias externas:

```python
loom.display_image(data)
```

Acepta:

- `bytes`;
- `bytearray`;
- `memoryview`.

No aceptar objetos PIL/matplotlib mágicamente en la primera versión. El usuario/agente puede usar `io.BytesIO`:

```python
buf = io.BytesIO()
fig.savefig(buf, format="png")
loom.display_image(buf.getvalue())
```

El Host detecta PNG/JPEG/WebP.

No recomiendo path como input inicial: un archivo existente ya tiene el camino probado `filesystem_view_image`; P3 debe concentrarse en imágenes **en memoria generadas por Python**.

### 10. Semántica de ejecución/threading

El output collector debe reutilizar la disciplina ya probada del bridge:

- ligado al `requestId` activo;
- thread-safe;
- child threads con contexto heredado pueden emitir antes de finalizar el execute;
- threads tardíos/stale después de `_end_execution` reciben error;
- outputs son execution-local, no persisten al execute siguiente;
- globals/imports sí continúan persistiendo como hoy.

Una excepción Python normal conserva outputs generados antes de la excepción. Infra failure/cancel/timeout no devuelve outputs parciales.

### 11. Versionado

Recomendado:

- Worker/Host protocol: **v3**;
- `loom.__bridge_version__`: **2**, porque cambia la API Python pública aunque el RPC bridge siga conceptualmente igual;
- CPython: **sin cambio**;
- package-store schema: **sin cambio**;
- MCP public tool count: **25**.

Los assets content-addressed evitan stale worker/bridge después del deployment.

## Binarios genéricos

El SDK MCP 2.2.0 también contiene:

- `AudioContentBlock`;
- `EmbeddedResourceBlock`;
- `BlobResourceContents`.

Pero el propio SDK deja el render de embedded resources a criterio del cliente y LoomLCI **no tiene validación ChatGPT/tunnel end-to-end** para esos tipos.

Por eso P3 no debería exponer todavía `display_bytes` genérico ni audio como contrato estable.

La estructura interna de outputs puede quedar extensible por `kind`, pero el único kind público de P3 inicial debe ser `image`.

Después de cerrar imágenes, un spike independiente puede probar BlobResource/Audio por el tunnel antes de ampliar la API.

## Etapas propuestas

### P3.0 — Typed output foundation / protocol v3 — CERRADO

Implementado:

- contratos Core `PythonExecutionOutput` / `PythonExecutionOutputKind.Image`;
- límites privados: 4 outputs, 6 MiB por imagen y 6 MiB raw agregado;
- protocol Worker/Host **v3**;
- final response frame elevado de 32 MiB a **40 MiB** con regresión de presupuesto combinado;
- `loom.__bridge_version__ = 2`;
- collector execution-local ligado al request activo;
- `loom.display_image(bytes|bytearray|memoryview)` local al worker, sin RPC del bridge;
- outputs codificados como base64 sólo en el frame final JSON;
- parsing Host defensivo para count, kind, base64, tamaño individual y agregado;
- normal exception conserva outputs previos;
- outputs no persisten entre executes, pero globals/imports sí;
- stale child threads reciben `output_unavailable`;
- límites excedidos son recuperables y no invalidan el worker;
- `loom.capabilities()` permanece en **15** porque `display_image` no es capability RPC;
- **ningún cambio MCP visible** todavía: `python_execute` continúa con su contrato actual hasta P3.1.

Validación:

- Core Python focalizado: **28/28**;
- Windows Python/protocol focalizado: **51/51**;
- IntegrationTests: **20/20**;
- suite Release serial completa: **350/350**;
- contrato MCP exportado desde Host Release: **25 tools**, byte a byte idéntico al snapshot canónico;
- SHA-256 del contrato: `674e3edc6f209dbf15074b08ace8484cb7c67c122aa343d73a8df842d86e6064`.

No se desplegó el runtime instalado ni se actualizó el plugin privado en P3.0. Esos pasos permanecen para P3.2 después de cerrar el transporte MCP visual.

## P3.1 — MCP image transport + payload hardening — CERRADO

> Estado: **implementado y validado en código + Host Release.** Deployment del runtime instalado, consumer smoke real por tunnel y reconciliación/publicación del plugin permanecen deliberadamente para P3.2.

### Implementación cerrada

- detector `VisualImageFormat.DetectMimeType` compartido en Core por Visual Files y Python;
- `PythonExecutionDto.outputs[]` expone sólo `kind`, `mimeType` y `sizeBytes`;
- los bytes aparecen únicamente como `ImageContentBlock`;
- `python_execute` omite `UseStructuredContent` / `OutputSchemaType` y conserva StructuredContent manual;
- el `CallToolResult` candidato completo se serializa y mide contra **9 MiB**, también en text-only;
- formato inválido y over-budget devuelven errores atómicos `retryable=false`, `executionCompleted=true`, sin descartar worker/globals;
- normal Python exception puede conservar imágenes emitidas previamente;
- description + ServerInstructions distinguen imagen in-memory de archivos existentes;
- contrato MCP actualizado deliberadamente: **25 tools**, drift limitado a ServerInstructions, description de `python_execute` y desaparición de su `outputSchema`.

Validación P3.1:

- suite Release serial: **356/356**;
- IntegrationTests: **21/21** dentro de la suite;
- smoke focalizado contra Host Release publicado: **3/3**;
- contrato exportado desde Host Release: **25 tools**;
- SHA-256 del snapshot MCP: `2ed2531ccec731f483d7a913a37e406b4a75ce21724ff864c7678692f55cd24a`;
- runtime instalado y plugin privado **no** fueron actualizados en P3.1.

### Objetivo exacto

Conectar los typed image outputs ya producidos por P3.0 con el resultado MCP de `python_execute`, de modo que ChatGPT reciba `ImageContentBlock` reales sin agregar tools públicas ni cambiar la semántica de persistencia del worker.

P3.1 cambia la metadata/resultado MCP de `python_execute`, pero no despliega todavía el runtime instalado ni publica una nueva versión del plugin. Eso queda para P3.2.

### 1. Reutilizar un detector visual único

Antes de P3.1, el detector PNG/JPEG/WebP vivía como métodos privados puros dentro de `WindowsVisualFilesProvider`, aunque no dependía de Windows.

P3.1 lo extrajo a Core/VisualFiles como `VisualImageFormat.DetectMimeType(ReadOnlySpan<byte>)`.

Consumidores:

- `WindowsVisualFilesProvider.ReadImageAsync`;
- `PythonTools.Execute` para los outputs `PythonExecutionOutputKind.Image`.

Así ambos caminos aceptan/rechazan exactamente los mismos containers y el MIME nunca se toma de un valor declarado por Python.

No validar el formato dentro del parser del protocolo Worker/Host: bytes con formato no soportado son un error de contenido recuperable, no corrupción de protocolo, y **no deben invalidar el worker**.

### 2. Resultado estructurado de Python

Extender el DTO MCP con metadata no binaria de outputs:

```text
PythonExecutionOutputDto:
  kind = "image"
  mimeType
  sizeBytes
```

y agregar `outputs[]` a `PythonExecutionDto`.

Los bytes no aparecen en StructuredContent/base64 manual; sólo viven en los `ImageContentBlock`.

Esto aplica tanto a status `completed` como `exception`. Una excepción Python normal sigue siendo una tool call exitosa y puede llevar imágenes emitidas antes de la excepción.

### 3. Mixed CallToolResult de python_execute

Alinear `python_execute` con las dos tools visuales ya validadas:

- quitar `UseStructuredContent = true`;
- quitar `OutputSchemaType`;
- conservar StructuredContent construido manualmente por `McpToolResults.From`;
- agregar un `ImageContentBlock.FromBytes(bytes, detectedMime)` por output válido.

El SDK MCP 2.2.0 confirma que `UseStructuredContent` default es false y que habilitarlo hace que la tool anuncie output schema. La experiencia G1 ya demostró que ChatGPT preserva mixed structured + image para visión cuando no se anuncia `outputSchema`.

Acceptance del contrato:

- `python_execute.ProtocolTool.OutputSchema == null`;
- input schema sin cambios;
- catálogo sigue en **25 tools**.

### 4. Presupuesto MCP combinado: medir el CallToolResult real

No basta con sumar 6 MiB raw. System.Text.Json escapa caracteres de base64 como `+` a `\\u002B`, y stdout/stderr también pueden expandirse por escaping.

Para Python conviene construir primero el `CallToolResult` candidato completo y medirlo con el helper genérico existente:

```text
McpPayloadLimits.MeasureSerializedCallToolResultBytes(candidate)
```

contra:

```text
MaxCallToolResultBytes = 9 MiB
```

Esto mide exactamente:

- StructuredContent;
- stdout/stderr y exception metadata;
- metadata de outputs;
- TextContentBlock;
- todas las imágenes;
- base64;
- escaping JSON;
- overhead de múltiples ContentBlocks.

Agregar un helper booleano genérico en `McpPayloadLimits` si mejora la legibilidad, pero no hace falta rediseñar `McpVisualPayloadLimits` ni cambiar las tools G1.

La serialización temporal queda acotada por el frame privado de P3.0 (40 MiB), por lo que es una solución simple y suficientemente bounded para esta etapa.

### 5. Hardening también para resultados sin imágenes

Aplicar el guard de 9 MiB a **todo** resultado exitoso de `python_execute`, aunque `outputs.Count == 0`.

Motivo: dos streams cercanos a `maxOutputChars=1_048_576` pueden superar 9/10 MiB por UTF-8/escaping aun sin imágenes.

No reducir silenciosamente stdout/stderr ni descartar imágenes para hacer entrar el resultado. Si el resultado candidato excede el presupuesto, devolver un ToolEnvelope de error pequeño:

```text
code = unsupported
reason = python_result_too_large
retryable = false
executionCompleted = true
serializedCallToolResultBytes = ...
maxCallToolResultBytes = 9437184
imageCount = ...
rawImageBytes = ...
```

Mensaje recomendado: la ejecución terminó y el estado/side effects pueden haber cambiado; reducir `maxOutputChars` o tamaño/cantidad de imágenes en la siguiente operación.

No marcar retryable=true: repetir automáticamente el mismo código puede duplicar efectos.

### 6. Formato inválido

Si un output etiquetado como image no es PNG/JPEG/WebP válido según el detector compartido:

```text
code = unsupported
reason = unsupported_image_format
retryable = false
executionCompleted = true
outputIndex = ...
```

No devolver imágenes parciales si una de varias es inválida: el transporte visual del execute es atómico.

El worker queda sano y globals/side effects del execute permanecen, porque el fallo ocurre después de que Python terminó.

### 7. Descripción y ServerInstructions

Actualizar `python_execute` para enseñar:

- `loom.display_image(bytes|bytearray|memoryview)` para imágenes **generadas en memoria dentro de Python**;
- contenido admitido: PNG/JPEG/WebP;
- hasta 4 outputs / 6 MiB raw agregado;
- resultado MCP completo <=9 MiB;
- imágenes locales ya existentes siguen usando `filesystem_view_image`, sin leerlas manualmente a bytes;
- páginas PDF existentes siguen usando `filesystem_render_pdf_page`;
- invalid format / final payload over-budget son errores post-execution y no descartan el worker;
- una excepción Python normal puede conservar imágenes previas.

Actualizar ServerInstructions con la misma distinción in-memory vs archivo local.

No tocar todavía la skill publicada 0.4.0: el runtime instalado sigue siendo Python 2 hasta P3.2. La reconciliación/plugin 0.5.0 se hace junto con deployment/consumer smoke.

### 8. Contrato MCP y snapshot

P3.1 introduce drift MCP **esperado**:

- descripción de `python_execute`;
- `outputSchema`: objeto actual -> null/ausente;
- ServerInstructions;
- input schema y annotations: sin cambios;
- tool count: **25**.

Después de tests:

1. exportar contrato desde Host Release;
2. revisar el diff para confirmar que sólo cambió lo esperado;
3. actualizar deliberadamente `plugin/contract/mcp-contract.json`;
4. verificar 25 tools.

No publicar plugin en P3.1.

### 9. Tests propuestos

**Core / Visual**

- detector compartido reconoce PNG/JPEG/WebP existentes;
- rechaza containers truncados/inválidos;
- WindowsVisualFilesProvider conserva todos sus tests actuales después de extraer el detector.

**MCP unit**

- mixed Python result con 1 y varias imágenes round-trip;
- StructuredContent contiene metadata, nunca bytes;
- medición genérica coincide exactamente con serialización real;
- candidate justo bajo/sobre 9 MiB;
- error candidate siempre queda por debajo del budget.

**Integration / Host Release**

1. tools/list:
   - `python_execute.OutputSchema == null`;
   - description menciona `loom.display_image`, formatos y 9 MiB;
   - 25 tools.
2. imagen válida:
   - Python genera PNG pequeño in-memory sin escribir archivo;
   - `loom.display_image`;
   - `CallToolResult.Content` = Text + Image;
   - MIME detectado por Host;
   - StructuredContent = status/stdout/... + outputs metadata.
3. múltiples imágenes válidas.
4. normal exception después de `display_image`:
   - IsError=false;
   - status=exception;
   - imagen preservada.
5. bytes inválidos:
   - IsError=true;
   - no `ImageContentBlock`;
   - reason `unsupported_image_format`;
   - marker/global posterior prueba que el mismo worker sigue vivo.
6. resultado >9 MiB:
   - error `python_result_too_large`;
   - sin imagen parcial;
   - `executionCompleted=true`;
   - worker/global state preservado.
7. caso text-only >9 MiB, para confirmar que el hardening no depende de image outputs.
8. regresiones Python 2 completas + suite serial.

### 10. Lo que P3.1 no debe hacer

No:

- cambiar protocol v3 ni bridge API v2 salvo bug encontrado;
- agregar nueva tool MCP;
- usar temp files;
- agregar MIME como argumento confiable a `loom.display_image`;
- soportar path/PIL/matplotlib mágicamente;
- publicar runtime instalado;
- actualizar plugin privado;
- abrir BlobResource/audio.

### Criterio de cierre P3.1

P3.1 queda cerrado cuando:

- detector PNG/JPEG/WebP es compartido por Visual Files y Python MCP output;
- `python_execute` devuelve mixed StructuredContent + ImageContentBlock;
- no anuncia outputSchema;
- múltiples imágenes funcionan hasta los límites P3.0;
- el CallToolResult real se mide y nunca sale por encima de 9 MiB;
- text-only también queda protegido;
- invalid format y over-budget son errores post-execution recuperables sin matar el worker;
- normal Python exception puede conservar imágenes;
- contrato queda en 25 tools con drift intencional revisado/snapshot actualizado;
- suite Release queda verde;
- runtime/plugin siguen sin cutover hasta P3.2.

### Decisión recomendada

Implementar P3.1 **sin cambiar P3.0**: validar formatos al borde MCP, construir el mixed result, medir el CallToolResult real y rechazar atómicamente cualquier respuesta que no sea segura para el tunnel.

Es la ruta de menor complejidad y reutiliza directamente los dos comportamientos ya probados en LoomLCI: typed outputs privados de P3.0 y mixed visual results de G1.

### P3.2 — Evaluation / deployment / plugin

> Estado: **investigación/diseño + implementación/deployment/plugin cerrados; fresh-agent final pendiente.** No fue necesario reabrir P3.0/P3.1 ni agregar una tool MCP.

#### Estado real previo al cutover

Repo:

- HEAD: `e63316a feat: expose Python image outputs over MCP`;
- working tree limpio al iniciar esta investigación;
- P3.1 ya validado con suite Release **356/356**, Integration **21/21** y Host Release focalizado **3/3**;
- snapshot MCP canónico ya contiene P3.1: 25 tools, `python_execute.outputSchema` ausente, descripción/ServerInstructions con `loom.display_image`.

Instalación actualmente activa:

- `activeVersion = 0.1.0-dev-python2`, sequence **0**;
- `previousVersion = 0.1.0-dev-python1`, sequence **0**;
- `highestSequence = 3`;
- runtime `loomlci-installed`: `process_running=true`, `healthy=true`, `ready=true`;
- mismo tunnel existente;
- el Host instalado sigue en contrato Python 2: 25 tools pero `python_execute.outputSchema` todavía presente y description/ServerInstructions sin `loom.display_image`;
- dentro del worker instalado: `loom.__bridge_version__ == 1` y `hasattr(loom, "display_image") == False`.

Por lo tanto P3.2 sí necesita un cutover real: no alcanza con refresh del catálogo.

#### Estrategia de deployment recomendada

Usar el mismo flujo **portable side-by-side** validado por Python 1/Python 2, con una versión clara como:

`0.1.0-dev-python3`, sequence **0**.

Razones:

- P3 sólo cambia Host + assets Python embebidos; no requiere cambiar el modelo de instalación;
- `worker.py` y `loom_bridge.py` están embebidos y se materializan por hash, por lo que el Host nuevo crea automáticamente assets P3 sin reemplazar CPython 3.14.8 ni environments existentes;
- conserva rollback directo a `0.1.0-dev-python2`;
- permite validar el runtime antes de publicar una release GitHub;
- replica exactamente el camino ya probado en P2.4.

No usar `update apply` durante P3.2. El estado actual ya muestra una particularidad operativa: al tener active sequence 0 y `highestSequence=3`, `update check` considera la antigua release pública `0.1.0-dev-github-e2e` sequence 3 como “actualización disponible”. Aplicarla reinstalaría un Host anterior. Una publicación firmada de Python 3 tendría que usar sequence >=4; eso es un flujo de release distinto y no es necesario para cerrar P3.2.

Ese comportamiento de sequence en builds dev side-by-side es un follow-up de deployment, no un blocker de Python 3.

#### Orden concreto de implementación/validación

**Fase 1 — package + preflight**

1. suite Release serial sobre HEAD aprobado;
2. `Build-PortablePackage.ps1 -Version 0.1.0-dev-python3 -Sequence 0`;
3. dejar que el builder ejecute Launcher tests + IntegrationTests contra el Host publicado;
4. registrar tamaño/SHA-256 del ZIP y hash de `LoomLCI.Host.dll`.

**Fase 2 — setup side-by-side**

1. ejecutar setup desde el paquete nuevo reutilizando tunnel id y runtime-key file existentes, sin exponer la key en command line;
2. comprobar:
   - active = python3;
   - previous = python2;
   - highestSequence sigue en 3;
   - Host instalado coincide por hash con el package;
3. correr IntegrationTests contra la DLL instalada: esperado **21/21**;
4. exportar contrato desde el Host instalado y exigir:
   - 25 tools;
   - `python_execute.outputSchema == null`;
   - description + ServerInstructions con `loom.display_image`.

Setup no detiene el runtime actual; el cutover se hace después.

**Fase 3 — cutover**

El stop/start/status debe hacerse con **IvanSpace sólo como cutover/fallback**, porque detener LoomLCI desde la propia conexión LoomLCI corta la herramienta que coordina el cambio.

Secuencia:

1. `stop`;
2. `start`;
3. `status`;
4. exigir `healthy=true`, `ready=true`, tunnel esperado y active python3;
5. si falla, rollback inmediato a python2.

**Fase 4 — consumer smoke por Secure MCP Tunnel**

Primero validar el runtime antes de tocar el plugin:

1. WorkSession;
2. primer `python_execute`:
   - confirmar `loom.__bridge_version__ == 2`;
   - confirmar `loom.display_image`;
   - crear PNG pequeño totalmente en memoria;
   - guardar un marker global;
   - llamar `loom.display_image(...)`;
3. ChatGPT debe recibir y **ver/describir** la imagen dentro del mismo tool result;
4. segundo `python_execute` debe leer el marker y demostrar persistencia;
5. caso post-execution negativo opcional: bytes inválidos -> `unsupported_image_format` + worker sano;
6. control negativo: una imagen local existente debe seguir usando `filesystem_view_image`, no Python/base64 manual.

La prueba visual debe hacerse en un chat/catalog refresh que realmente reciba la metadata nueva; chats ya abiertos pueden conservar el snapshot viejo, como ocurrió en P2.4.

#### Plugin 0.5.0

El plugin privado publicado sigue correctamente en **0.4.0**, pero su guidance visual quedó obsoleto para P3:

- skill 0.4.0 dice todavía que para imágenes se usen siempre visuales top-level;
- README dice lo mismo;
- el verifier/test actual protege `loom.fs`/`loom.process`, pero **no** protege `loom.display_image`.

Cambios de P3.2:

- bump único `plugin/plugin.json`: **0.4.0 -> 0.5.0**;
- skill:
  - imagen generada **en memoria dentro de Python** -> `loom.display_image`;
  - imagen local ya existente -> `filesystem_view_image`;
  - página PDF existente -> `filesystem_render_pdf_page`;
  - no cargar un archivo local a Python sólo para mostrarlo;
- README con la misma distinción;
- extender `Verify-PluginPackage.ps1` y `PluginSkillTracksFinalPythonWorkflow` para exigir al menos:
  - `loom.display_image`;
  - `filesystem_view_image`;
  - `filesystem_render_pdf_page`;
  - guidance in-memory vs archivo existente.

No hace falta modificar manifest description/keywords salvo el bump: “Python + contenido visual” ya describe correctamente el producto.

#### Orden de publicación del plugin

No publicar 0.5.0 antes del cutover: mientras el runtime activo siga en Python 2, esa skill enseñaría una API que todavía no existe.

Después del consumer smoke:

1. modificar skill/README/verifier/test;
2. focused tests + suite Release;
3. ejecutar `Build-PluginPackage.ps1` contra el **Host instalado Python 3** mediante `-HostPath`, para que el anti-drift pruebe el runtime real y no sólo el repo;
4. debe confirmar 25 tools y snapshot P3.1 sin drift;
5. releer justo antes de publicar el `current_release_id` del plugin privado;
6. actualizar el mismo plugin por CAS como **0.5.0**;
7. read-back de manifest/skill/README + neutralizadores MCP;
8. no crear un plugin nuevo ni modificar el wiring del tunnel.

Estado observado durante investigación:

- plugin id actual: el mismo plugin privado existente;
- versión publicada: **0.4.0**;
- release actual: `pluginrel_6ac5e253c70081918df6046afb0572fa`.

El release id debe releerse antes del CAS y no tratarse como constante.

#### Resultado de implementación / deployment / plugin

Package y preflight:

- suite Release serial posterior a cambios: **356/356**;
- portable: `0.1.0-dev-python3`, sequence **0**;
- ZIP: **81.313.386 bytes**;
- SHA-256: `bc2637d2be39fc57409ef56dae39efbfcf348059a9f6b99fa23d2b73a39f6401`;
- Launcher builder: **20/20**;
- IntegrationTests contra Host publicado: **21/21**.

Instalación/cutover:

- setup side-by-side: **OK**;
- `activeVersion = 0.1.0-dev-python3`;
- `previousVersion = 0.1.0-dev-python2`;
- `highestSequence = 3` preservado;
- hash de `LoomLCI.Host.dll` package vs instalación: idéntico, `5587be0ab1a1ee6f400ed7238b07e4370c394d25d565531f0e959f4a2c6f5000`;
- IntegrationTests contra DLL instalada: **21/21**;
- contrato instalado: **25 tools**, `python_execute.outputSchema == null`, description + ServerInstructions con `loom.display_image`;
- cutover mediante IvanSpace sólo para `stop/start/status`: **OK**;
- runtime final: `process_running=true`, `healthy=true`, `ready=true`, mismo tunnel.

Consumer smoke en el chat de implementación:

- `loom.__bridge_version__ == 2`;
- `loom.display_image` presente y acepta PNG generado completamente en memoria;
- StructuredContent reportó `outputs=[{kind:image,mimeType:image/png,...}]`;
- marker global persistió en un segundo `python_execute`;
- bytes inválidos devolvieron `unsupported_image_format` y el worker siguió sano;
- el control de archivo local existente se ejecutó directamente con `filesystem_view_image` y se inspeccionó visualmente correctamente;
- este chat conservó el descriptor viejo de `python_execute` tras el cutover, por lo que su binding Code Mode no expuso el `ImageContentBlock` del mixed result al modelo. Esto reproduce la aspereza conocida de snapshot/caché del consumidor y obliga a hacer la aceptación visual positiva en un chat nuevo.

Plugin 0.5.0:

- `plugin/plugin.json`: **0.4.0 -> 0.5.0**;
- skill + README distinguen:
  - imagen generada in-memory en Python -> `loom.display_image`;
  - imagen local existente -> `filesystem_view_image`;
  - página PDF existente -> `filesystem_render_pdf_page`;
- verifier + `PluginSkillTracksFinalPythonWorkflow` protegen esos markers;
- verifier fuente: **OK**, 0.5.0 / 25 tools / 12 refs explícitas;
- tests focalizados: **2/2**;
- build contra Host **instalado Python 3**: snapshot MCP sin drift;
- ZIP final: **4.896 bytes**, SHA-256 `7d608a2e3cbcdc55a4eb76e0eea8ce1f573c118ebed6f5683312e1f08fd844ad`;
- plugin id preservado: `plugins_6ac0a247c2b08191bca02893456adf28`;
- release anterior: `pluginrel_6ac5e253c70081918df6046afb0572fa` (0.4.0);
- release actual: `pluginrel_6ac66682d9908191b16d4033923f84fa` (0.5.0);
- update CAS: **OK**;
- read-back completo: **OK**;
- `.codex-plugin/plugin.json` sincronizado en 0.5.0;
- `mcp.json` / `.mcp.json` siguen neutralizados con `mcpServers: {}`;
- no se creó otro plugin ni se modificó el wiring del tunnel.

La primera llamada a Plugin Creator con la ruta Windows fue rechazada antes de mutar el plugin, igual que en 0.4.0. El ZIP se trasladó al entorno de Plugin Creator conservando los bytes y el segundo intento CAS fue exitoso.

El único criterio de cierre aún pendiente es el **fresh-agent visual positivo/negativo** en un chat nuevo que reciba el catálogo Python 3 y la skill 0.5.0.

#### Fresh-agent final

Ejecutar en chat nuevo después del runtime + plugin 0.5.0:

**Caso positivo — imagen Python in-memory**

Prompt sin nombrar `loom.display_image`: pedir generar una imagen simple en memoria con Python, mostrarla directamente y no escribirla a disco.

PASS si el agente:

- usa `python_execute`;
- descubre/usa `loom.display_image`;
- la imagen llega visualmente al modelo;
- no usa temp files ni `filesystem_view_image` para el output in-memory.

**Caso negativo — imagen local existente**

Dar la ruta de `artifacts/g11-smoke/visual-smoke.png` y pedir inspección visual.

PASS si usa directamente `filesystem_view_image`, sin Python ni lectura/base64 manual.

Estos dos casos prueban exactamente la nueva decisión de routing introducida por Python 3.

#### Criterio de cierre P3.2 / Python 3

P3.2 queda cerrado cuando:

- runtime instalado está en python3, healthy/ready, con python2 como rollback;
- Host instalado anuncia exactamente el contrato P3.1 de 25 tools;
- `loom.__bridge_version__ == 2` y `loom.display_image` existen en el worker instalado;
- consumer smoke recibe visión real desde un `python_execute`;
- persistencia del worker sigue funcionando después del output visual;
- imagen local existente sigue yendo por `filesystem_view_image`;
- plugin **0.5.0** fue actualizado por CAS y verificado por read-back;
- fresh-agent positivo/negativo pasan;
- documentación queda reconciliada y cambios de P3.2 commiteados.

No hace falta agregar más código al transporte P3.0/P3.1 salvo que alguna prueba instalada descubra una regresión real.

## Criterio de cierre Python 3

P3 queda cerrado cuando:

- no hay nueva tool MCP pública;
- protocol v3 + API loom v2 son robustos;
- `loom.display_image` entrega PNG/JPEG/WebP in-memory al modelo;
- 4 outputs / 6 MiB agregado / 9 MiB MCP se respetan sin depender del tunnel;
- `python_execute` mixed content llega a ChatGPT con StructuredContent + ImageContentBlock;
- normal exception conserva imágenes previas y worker sano;
- timeout/cancel/crash no filtran outputs parciales;
- fresh-agent elige `loom.display_image` para imagen generada en Python y `filesystem_view_image` para imagen local existente;
- runtime instalado + plugin reconciliado pasan smoke/read-back;
- repo/documentación quedan limpios y commiteados.

## Decisión recomendada

Implementar P3 como **image-output first**, usando el resultado final de `python_execute` y no el bridge RPC.

No introducir todavía:

- framing binario raw;
- temp-file transport;
- nueva tool MCP;
- objetos PIL/matplotlib mágicos;
- generic blobs/audio públicos.

Ese scope aprovecha todo lo ya validado por Visual Files y mantiene la complejidad acotada.
