# Bloque G - Visual Files

> Estado: **G1.0 y G1.1 implementados y validados técnicamente; G1.1 sólo espera refresh del catálogo de ChatGPT para el smoke visual directo.** El próximo bloque funcional es G1.2 PDF text worker.

## Objetivo

Permitir que el agente consuma archivos visuales directamente, sin tener que abrirlos en una GUI:

1. ver una imagen local como imagen real;
2. extraer texto de un PDF de forma eficiente y paginada;
3. renderizar una página PDF como imagen para documentos gráficos, diagramas o scans.

Computer H1 resolverá captura de la sesión interactiva. Visual Files G1 resuelve lectura directa de archivos.

## Superficie pública v0.1

Agregar tres tools read-only:

- `filesystem_view_image`
- `filesystem_read_pdf`
- `filesystem_render_pdf_page`

Catálogo:

- actual tras G1.1: 20 tools;
- después de G1 completo: 22 tools;
- después de Computer H1, si mantiene sus seis tools: 28 tools.

Todas las tools G1 serán:

- `ReadOnly = true`
- `Destructive = false`
- `Idempotent = true`
- `OpenWorld = false`

No necesitan ResourceRegistry ni estado durable.

## Fuera de alcance G1

- OCR propio;
- edición de imágenes;
- edición/generación de PDF;
- Office/LibreOffice;
- video;
- SVG como renderer dedicado;
- extracción automática de todas las páginas como imágenes;
- computer vision propio;
- passwords/credenciales de PDF;
- un framework multimedia general.

El modelo ya tiene visión. Loom debe transportar una representación fiable, acotada y verificable.

# 1. Transporte de imágenes MCP

## ImageContentBlock

LoomLCI usa `ModelContextProtocol 2.2.0`. El SDK permite resultados mixtos con:

- `structuredContent`;
- `TextContentBlock`;
- `ImageContentBlock`.

La forma obligatoria en G1 será:

```csharp
ImageContentBlock.FromBytes(bytes, "image/png")
```

No asignar bytes crudos directamente a `ImageContentBlock.Data`.

### Evidencia local

Con exactamente MCP 2.2.0 se verificó:

- bytes -> base64 correcto;
- serialización del `CallToolResult` completo con `McpJsonUtilities.DefaultOptions`;
- deserialize;
- round-trip OK.

Existe además el issue upstream #1835 sobre serialización inválida cuando se construyen bloques binarios por asignación raw. Por eso G1 debe centralizar la creación en un helper y congelar un regression test.

## Límite real del Secure MCP Tunnel

Se midió el transporte real:

```text
ChatGPT -> Secure MCP Tunnel -> LoomLCI instalado
```

Usando `filesystem_read_files` con archivos temporales y descartando el payload en el orchestrator para no introducirlo en el contexto:

- 1 MiB: OK;
- 4 MiB: OK;
- 8 MiB: OK;
- ~9,33 MiB: OK;
- ~9,70 MiB: OK;
- 12 MiB: HTTP 413.

El log del tunnel-client informó explícitamente:

```text
request_body_too_large
Tunnel request or response payload exceeds 10485760 byte limit
```

Por lo tanto el hard limit actual es **10 MiB = 10.485.760 bytes** por request/response del tunnel.

El fallo de 12 MiB además provocó el cierre del runtime instalado actual. G1 no debe depender de que el túnel rechace un payload: debe impedir localmente que se genere.

## Cap binario + cap MCP serializado

La aproximación inicial de usar sólo el crecimiento 4/3 de base64 resultó insuficiente.

Con MCP C# SDK 2.2.0, `McpJsonUtilities.DefaultOptions` usa el encoder JSON por defecto. El carácter `+` de base64 se serializa como `\u002B`, mientras `/` y `=` no se expanden. Por eso el tamaño real depende del contenido binario.

Evidencia con `CallToolResult` mixto real:

- 6,00 MiB random -> ~9.045.460 bytes JSON;
- 6,25 MiB random -> ~9.422.428 bytes;
- 6,50 MiB random -> ~9.799.117 bytes;
- 6,75 MiB random -> ~10.176.241 bytes;
- 7,00 MiB random -> ~10.553.744 bytes, ya por encima del túnel;
- patrón adversarial cuyo base64 es casi sólo `+` -> expansión JSON aproximada **8x** respecto del binario.

Por lo tanto un cap binario por sí solo no puede garantizar el límite del túnel.

Decisión G1:

```text
MaxImageBytes = 6 * 1024 * 1024
MaxVisualCallToolResultBytes = 9 * 1024 * 1024
```

Semántica:

1. **6 MiB** es el hard cap binario barato para imagen local y PNG renderizado;
2. **9 MiB** es el hard cap autoritativo del `CallToolResult` visual ya considerado como JSON MCP;
3. quedan ~1 MiB de margen respecto del hard limit externo de 10 MiB para JSON-RPC/control-plane y variaciones del transporte;
4. si cualquiera de los dos caps se excede, Loom rechaza localmente antes de enviar al tunnel.

### Cálculo exacto sin serializar el payload gigante

G1.0 no debe serializar el resultado completo sólo para medirlo.

Se validó una fórmula exacta para `ImageContentBlock.FromBytes` con el encoder de MCP 2.2.0:

- largo base64 = `4 * ceil(bytes / 3)`;
- cada carácter base64 `+` agrega 5 bytes extra al convertirse en `\u002B`;
- el resto del `CallToolResult` se serializa una vez usando una imagen vacía;
- tamaño final estimado = resultado base vacío + largo base64 escapado.

La fórmula coincidió **byte por byte** con la serialización real para tamaños aleatorios, padding y un caso `+` adversarial.

Esto evita una segunda copia grande del JSON y hace el guard reutilizable por Computer H.

La validación final de G1.1/G1.3 debe repetir el camino completo con un `ImageContentBlock` real por Secure MCP Tunnel.

# 2. filesystem_view_image

## Contrato

Entrada:

- `path`
- `workId?`

Resolución de path idéntica a Filesystem clásico.

Formatos G1:

- PNG;
- JPEG;
- WebP.

No confiar sólo en extensión. Validar magic bytes:

- PNG: firma PNG;
- JPEG: SOI/marker válido;
- WebP: RIFF + WEBP.

No decodificar ni recodificar si no es necesario.

## Resultado estructurado

DTO conceptual:

```text
VisualImageDto
- requestedPath
- fullPath
- mimeType
- sizeBytes
```

Content:

```text
TextContentBlock breve
ImageContentBlock.FromBytes(...)
```

## Robustez

Antes de leer:

- archivo existente y regular;
- tamaño <= 6 MiB;
- formato soportado.

G1.1 reemplaza la propuesta de comparar timestamps por una lectura estable con `FileShare.Read`; si ya existe un writer/lock incompatible, devolver `busy` retryable con `reason=file_busy`. Ver [[G1.1 - Local image]].

# 3. PDF textual

## Parser elegido

Usar **PdfPig 0.1.16**.

Razones:

- Apache-2.0;
- managed .NET;
- extracción de texto/layout;
- funciona en `net10.0`;
- `ContentOrderTextExtractor` da una representación de lectura mejor que depender sólo de `page.Text`;
- la versión 0.1.16 es la release estable investigada y contiene fixes posteriores a 0.1.15, incluido #1347.

`dotnet list package --vulnerable --include-transitive` no reportó vulnerabilidades conocidas para 0.1.16 usando NuGet.org durante esta investigación.

## Por qué NO debe ejecutarse dentro de LoomLCI.Host

La investigación cambió esta decisión.

Con PdfPig 0.1.15, `issue_1347.pdf` del propio corpus upstream produjo stack overflow y terminó el proceso.

PdfPig 0.1.16 corrige #1347 y ese archivo pasó correctamente. Sin embargo, al recorrer el corpus upstream con 0.1.16 apareció **otro stack overflow**, esta vez dentro de la ejecución de una función PDF Type 4.

`StackOverflowException` no es recuperable de forma segura con un `try/catch` normal. Un parser in-process permitiría que un PDF patológico terminara `LoomLCI.Host`.

Decisión: **PdfPig no se ejecutará in-process en G1**.

## PdfWorker one-shot

Agregar un helper privado:

```text
LoomLCI.PdfWorker
```

Responsabilidad única:

```text
PDF path + page range
    -> PdfPig 0.1.16
    -> ContentOrderTextExtractor
    -> JSON acotado por stdout
```

Características:

- un proceso nuevo por llamada a `filesystem_read_pdf`;
- no se registra como ProcessHandle público;
- no entra al ResourceRegistry;
- se lanza con la infraestructura nativa existente de Process;
- usar Job Object / kill-on-close ya probado por `WindowsNativeProcessLauncher`;
- pipes, no terminal;
- timeout duro de **20 s**;
- Job Object con `JOB_OBJECT_LIMIT_PROCESS_MEMORY` de **256 MiB** además de kill-on-close;
- stdout acotado;
- stderr sólo diagnóstico acotado;
- al terminar/fallar se libera todo inmediatamente.

Si el worker hace stack overflow, crash o salida anormal, muere sólo el worker.

### Evidencia para el límite de memoria

Con PdfPig 0.1.16, `ContentOrderTextExtractor` sobre PDFs sintéticos de una página produjo aproximadamente:

- 131.072 caracteres -> 93,5 MiB peak working set;
- 262.144 caracteres -> 138,8 MiB;
- 524.288 caracteres -> 227,1 MiB;
- 1.048.576 caracteres -> 412,6 MiB.

También se validó localmente que `JOB_OBJECT_LIMIT_PROCESS_MEMORY` fuerza el límite: un child bajo un Job de 128 MiB recibió `OutOfMemoryException` al intentar seguir creciendo, sin afectar al proceso padre.

Como G1 limita el texto público a 65.536 code points por página y 262.144 agregados, **256 MiB** deja margen amplio para documentos normales y corta casos de expansión patológica. Si el worker detecta OOM/resource limit, debe devolver un error acotado; si muere abruptamente, el cliente lo clasifica como worker crash.

### Corpus dirigido

Se clonó temporalmente el repositorio upstream de PdfPig fuera del repo de LoomLCI. Contenía 251 PDFs de integración/benchmark.

Con PdfPig 0.1.16 y aislamiento por proceso se probó un subconjunto dirigido de **54 PDFs**: casos problemáticos/issue reproductions + los más grandes.

Resultado:

- 51 OK;
- 3 errores normales/capturables;
- 0 crashes del worker.

Errores normales:

- 2 PDFs cifrados -> `PdfDocumentEncryptedException`;
- 1 PDF inválido -> `PdfDocumentFormatException`.

El mayor texto observado en las primeras 10 páginas del subconjunto fue **47.314 caracteres**.

Esto valida 0.1.16 como parser, pero no justifica confiarle la estabilidad del Host. El worker sigue siendo obligatorio.

# 4. filesystem_read_pdf

## Entrada final v0.1

```text
path
workId?
startPage = 1
maxPages = 10
```

Reglas:

- páginas públicas 1-based;
- `startPage >= 1`;
- `maxPages` rango 1..25;
- PDF máximo: **64 MiB**;
- no password en G1.

## Límites de texto

Decisión:

```text
MaxPageTextCodePoints = 65_536
MaxTotalTextCodePoints = 262_144
```

Motivos:

- 10 páginas del corpus dirigido no superaron 47.314 caracteres agregados;
- 64 Ki code points cubre con margen una lectura típica;
- 256 Ki mantiene una llamada útil pero muy por debajo del límite del tunnel;
- documentos largos se recorren por páginas, no con una respuesta gigante.

Semántica:

- `maxPages` limita páginas solicitadas;
- cada página se limita a 65.536 code points y reporta `textTruncated`;
- el resultado completo se limita a 262.144 code points;
- si el agregado alcanzaría el límite antes de una nueva página, detener antes de esa página y devolver `nextPage`;
- una página individual patológica puede quedar truncada; G1 no añade offset de caracteres dentro de una página;
- si una página tiene texto vacío/muy corto y la tarea requiere comprenderla, usar `filesystem_render_pdf_page`.

## Resultado estructurado

```text
PdfTextReadDto
- requestedPath
- fullPath
- sizeBytes
- pageCount
- startPage
- endPage
- totalTextLength
- outputLimitReached
- hasMoreAfter
- nextPage?
- pages[]

PdfTextPageDto
- pageNumber
- text
- textLength
- textTruncated
```

No renderizar páginas automáticamente dentro de esta tool.

# 5. PDF visual

## Renderer elegido

Usar `Windows.Data.Pdf`.

Ventajas:

- API de Windows ya disponible en la plataforma objetivo;
- no hay que redistribuir PDFium/Poppler/Ghostscript;
- page count;
- render de página;
- control de dimensiones;
- PNG mediante `BitmapEncoder.PngEncoderId`.

### Evidencia local

Con proyecto temporal `net10.0-windows10.0.19041.0`:

- `PdfDocument.LoadFromFileAsync` correcto;
- render a stream;
- PNG válido;
- magic `89504E470D0A1A0A`;
- render sintético a 2400x3200 (~7,7 MP): ~420 ms y ~80 MiB peak working set en esta PC.

Con inputs aleatorios/truncados devolvió errores COM rápidos en vez de colgar el proceso en las pruebas realizadas. Con estos límites, el renderer puede permanecer in-process en G1; el riesgo fuerte que justificó aislamiento está en PdfPig/texto.

# 6. filesystem_render_pdf_page

## Entrada final v0.1

```text
path
workId?
page
maxWidth = 1800
maxHeight = 2400
```

Reglas:

- `page` 1-based;
- `maxWidth` y `maxHeight`: 256..4096;
- mantener aspect ratio dentro de ese bounding box;
- PDF máximo 64 MiB;
- PNG resultante máximo 6 MiB y resultado MCP serializado <=9 MiB.

Si el PNG excede 6 MiB o el resultado MCP estimado excede 9 MiB:

- no enviarlo;
- devolver `unsupported`;
- `details.reason = rendered_image_too_large`;
- sugerir repetir con dimensiones menores.

## Resultado estructurado

```text
PdfPageRenderDto
- requestedPath
- fullPath
- pdfSizeBytes
- page
- pageCount
- width
- height
- mimeType = image/png
- imageSizeBytes
```

Content:

```text
TextContentBlock breve
ImageContentBlock.FromBytes(png, "image/png")
```

Una sola página por llamada.

# 7. PDFs protegidos

No agregar password a las tools G1.

Prueba local con PDF cifrado generado temporalmente, repetida con la versión final elegida PdfPig 0.1.16:

PdfPig:

- sin password -> `PdfDocumentEncryptedException`;
- password incorrecto -> la misma excepción;
- password correcto -> OK.

Windows.Data.Pdf:

- sin password -> `COMException`, HRESULT `0x8007052B`;
- password incorrecto -> mismo HRESULT;
- password correcto -> OK.

Como G1 no acepta password, ambos caminos deben mapear a:

```text
code = unsupported
details.reason = password_protected_pdf
```

Mensaje: el PDF está protegido y passwords no están soportados por esta versión de la tool.

# 8. Arquitectura interna final

No agrandar `WindowsFilesystemProvider`.

```text
MCP
  -> VisualFilesTools
      -> VisualFilesCapability
          -> IVisualFilesProvider
              -> WindowsVisualFilesProvider
                  -> imagen raw
                  -> PdfWorkerClient
                      -> LoomLCI.PdfWorker / PdfPig 0.1.16
                  -> Windows.Data.Pdf
```

Core:

- contratos;
- límites;
- path resolution compartida;
- mapping semántico;
- ninguna dependencia Windows/PdfPig.

Windows:

- lectura binaria acotada;
- detección de formatos;
- PdfWorkerClient;
- Windows.Data.Pdf;
- proceso/Job Object.

MCP:

- DTOs;
- schemas;
- annotations;
- mixed content;
- mapping de `ToolEnvelope`.

Host:

- registrations;
- localizar/deployar `LoomLCI.PdfWorker.exe`.

## Path resolver

`FilesystemCapability.ResolvePath` hoy es privado.

G1 debe extraer una abstracción/helper Core compartido para conservar exactamente:

- path absoluto;
- path relativo a WorkSession;
- error si path relativo no tiene base directory;
- Full Trust: baseDirectory sigue siendo contexto, no sandbox;
- validación de paths inválidos.

No duplicar esta lógica.

# 9. Errores públicos

No agregar códigos Loom top-level nuevos en G1.

Usar los existentes con `details.reason` específico.

| Caso | Code | reason |
|---|---|---|
| path/input inválido | `invalid_argument` | específico cuando aporte |
| archivo inexistente | `not_found` | - |
| ACL/OS denial | `access_denied` | - |
| formato de imagen no soportado | `unsupported` | `unsupported_image_format` |
| imagen > 6 MiB | `unsupported` | `image_too_large` |
| imagen bloqueada por writer/lock | `busy` | `file_busy` |
| PDF > 64 MiB | `unsupported` | `pdf_too_large` |
| PDF cifrado | `unsupported` | `password_protected_pdf` |
| PDF inválido/malformado | `unsupported` | `invalid_pdf` |
| PNG render > 6 MiB o payload MCP >9 MiB | `unsupported` | `rendered_image_too_large` |
| page fuera de rango | `invalid_argument` | `page_out_of_range` |
| PDF/archivo futuro cambia durante una lectura que no use lock estable | `conflict` | `file_changed_during_read` |
| worker agotó 20 s | `deadline_exceeded` | `pdf_worker_timeout` |
| worker alcanza límite de memoria/OOM controlado | `unsupported` | `pdf_resource_limit` |
| worker crash/salida anormal | `execution_failed` | `pdf_worker_crashed` |
| fallo renderer no clasificable | `execution_failed` | `pdf_render_failed` |

El adapter MCP debe conservar estos errores dentro del `ToolEnvelope` igual que las tools actuales.

# 10. Helper MCP visual

`McpToolResults` hoy produce structuredContent + un TextContentBlock.

Agregar una ruta reutilizable:

```text
ToolEnvelope<T>
 -> StructuredContent
 -> TextContentBlock
 -> optional ImageContentBlock(s)
```

Reglas:

- errores no incluyen image blocks;
- image block sólo después de validar cap;
- usar exclusivamente `ImageContentBlock.FromBytes`;
- helper reutilizable por Computer H1.

# 11. Windows TFM pasa a G1

`Windows.Data.Pdf` requiere Windows TFM.

G1 realizará:

- `LoomLCI.Core`: queda `net10.0`;
- `LoomLCI.Mcp`: queda `net10.0`;
- `LoomLCI.Windows`: `net10.0-windows10.0.19041.0`;
- `LoomLCI.Host`: mismo Windows TFM;
- `LoomLCI.Windows.Tests`: mismo Windows TFM;
- `LoomLCI.IntegrationTests`: mismo Windows TFM;
- `SupportedOSPlatformVersion=10.0.19041.0`.

La plataforma portable ya es Windows x64. Computer H reutilizará esta migración.

# 12. Test strategy

## MCP / imagen

- serialize/deserialize de `ImageContentBlock.FromBytes`;
- PNG/JPEG/WebP reales;
- magic mismatch;
- 6 MiB binary boundary;
- >6 MiB rechazo local;
- 9 MiB serialized MCP boundary;
- patrón base64 adversarial con `+`;
- mixed structured + text + image;
- Secure MCP Tunnel real;
- ChatGPT ve la imagen.

## PDF worker

- PDF normal;
- PDF sin texto;
- PDF de muchas páginas;
- PDF >64 MiB;
- corrupto/truncado;
- encrypted;
- issue reproductions del corpus upstream;
- worker crash deliberado;
- timeout deliberado;
- memory-limit/OOM deliberado;
- Job cleanup;
- stdout inválido/oversized;
- archivo modificado mientras se procesa.

## PDF render

- página normal;
- scan/visual;
- page bounds;
- max dimensions;
- preserving aspect ratio;
- PNG >6 MiB / payload MCP >9 MiB;
- encrypted;
- corrupt/truncated.

## Real-world

Antes de cerrar G1:

1. imagen local real visible en ChatGPT;
2. PDF textual real;
3. PDF con tablas/diagramas usando texto + render;
4. PDF escaneado usando render;
5. payload cercano al cap;
6. PDF inválido;
7. worker crash sin afectar Host;
8. Secure MCP Tunnel;
9. fresh-agent discoverability;
10. publish portable.

# 13. Implementación por etapas

## G1.0 - Binary/image foundation

> **Implementado y validado.** Windows TFM, harness Release, helper MCP mixto, guard de payload y tests MCP quedaron incorporados sin exponer todavía tools Visual Files.

### TFM / WinRT

Migrar:

- `LoomLCI.Windows` -> `net10.0-windows10.0.19041.0`;
- `LoomLCI.Host` -> mismo TFM;
- `LoomLCI.Windows.Tests` -> mismo TFM;
- `LoomLCI.IntegrationTests` -> mismo TFM;
- `SupportedOSPlatformVersion=10.0.19041.0` en esos cuatro proyectos.

Mantener:

- `LoomLCI.Core` -> `net10.0`;
- `LoomLCI.Mcp` -> `net10.0`;
- Core/MCP siguen genéricos y no adquieren dependencia Windows;
- Launcher permanece como está (`net10.0-windows`).

Prototipo limpio: build Release con el grafo anterior -> **0 warnings / 0 errors**.

### Corregir el harness de IntegrationTests junto con el TFM

El cambio de TFM expuso una deuda preexistente: `GetHostDll` tiene hardcodeados `Debug` y `net10.0`, y `dotnet test LoomLCI.slnx -c Release` no garantiza construir Host porque IntegrationTests no lo declara como dependencia.

Decisión G1.0:

- agregar `ProjectReference` de IntegrationTests a `LoomLCI.Host.csproj` con `ReferenceOutputAssembly="false"`;
- conservar `LOOMLCI_TEST_HOST_DLL` como override para pruebas contra un Host publicado;
- en el fallback local derivar Configuration + TFM desde `AppContext.BaseDirectory`, en vez de hardcodearlos.

Prototipo en checkout temporal limpio: **203/203 tests Release verdes**. Esto además elimina la posibilidad de que una suite Release use accidentalmente un Host Debug stale.

### Helper MCP mixto

Extender `McpToolResults` sin cambiar `ToolEnvelope`:

```text
structuredContent = ToolEnvelope<T>
Content[0] = TextContentBlock actual
Content[1..] = success content opcional
```

Para imágenes usar exclusivamente `ImageContentBlock.FromBytes(ReadOnlyMemory<byte>, mimeType)`.

Si el envelope es error, ignorar contenido visual de éxito y devolver sólo el bloque textual de error.

Con MCP 2.2.0 se verificó round-trip completo con:

- 900.000 bytes random;
- 7 MiB random;
- mixed text + structuredContent + image;
- bytes decodificados idénticos al input.

No actualizar MCP durante G1.0: 2.2.0 sigue siendo la release estable investigada y el camino `FromBytes` probado funciona; el issue #1835 afecta la construcción raw y queda cubierto por regression tests.

### Guard de payload

Fijar para Visual Files:

```text
MaxImageBytes = 6 MiB
MaxVisualCallToolResultBytes = 9 MiB
```

Agregar helper interno que estime exactamente el tamaño serializado del `CallToolResult` visual usando el cálculo de base64 escapado validado en esta investigación. No crear una copia gigante del JSON sólo para medirlo.

### Tests MCP

Crear `tests/LoomLCI.Mcp.Tests` (`net10.0`) y `InternalsVisibleTo("LoomLCI.Mcp.Tests")`, siguiendo el patrón ya usado por Windows.Tests.

Regression tests mínimos:

1. mixed text + structured + image round-trip;
2. estimador de tamaño == serialización real, incluyendo patrón `+` adversarial;
3. un resultado de error nunca incluye image content;
4. boundaries de 6 MiB binarios / 9 MiB serializados.

El prototipo del nuevo proyecto corrió **3/3 tests verdes**.

### Portable

El prototipo completo con Windows TFM se validó mediante `Build-PortablePackage.ps1`:

- Host Release self-contained `win-x64`: OK;
- Launcher single-file: OK;
- Launcher tests: 6/6;
- IntegrationTests contra el Host publicado: 9/9;
- ZIP + SHA-256: OK.

No hace falta cambiar el formato del paquete en G1.0.

### Cierre G1.0

Completado:

- Windows/Host/Windows.Tests/IntegrationTests migrados a `net10.0-windows10.0.19041.0` con `SupportedOSPlatformVersion=10.0.19041.0`;
- IntegrationTests ahora construye Host mediante `ProjectReference` de build y deriva Configuration/TFM dinámicamente, conservando `LOOMLCI_TEST_HOST_DLL` para published-host tests;
- `McpToolResults` admite success content adicional sin alterar `ToolEnvelope` ni los errores;
- `McpVisualPayloadLimits` implementa 6 MiB binarios, 9 MiB MCP y estimación exacta de base64 escapado;
- nuevo `LoomLCI.Mcp.Tests`: 4/4 tests verdes;
- suite Release completa: **207/207 tests verdes**;
- portable real: Host self-contained win-x64, Launcher 6/6, IntegrationTests contra Host publicado 9/9, ZIP + SHA-256 OK;
- el catálogo público sigue igual: **no se agregó ninguna tool Visual Files en G1.0**.

## G1.1 - Local image

> **Implementado en `a9f50fb`.** Ver [[G1.1 - Local image]]. Validación local/STDIO/portable/tunnel operativo completa; queda sólo el smoke visual directo tras refrescar el catálogo cliente.

- path resolver compartido;
- contratos Core;
- `VisualFilesCapability`;
- `IVisualFilesProvider`;
- PNG/JPEG/WebP con validación estructural ligera;
- lectura estable con `FileShare.Read` y `busy/file_busy` ante writer activo;
- `filesystem_view_image`;
- guard 6 MiB binarios + 9 MiB MCP;
- STDIO + tunnel + ChatGPT.

## G1.2 - PDF text worker

- proyecto privado `LoomLCI.PdfWorker`;
- PdfPig 0.1.16;
- PdfWorkerClient;
- Job Object + 20 s + 256 MiB process-memory cap;
- page/text caps;
- password/invalid/crash mapping;
- `filesystem_read_pdf`;
- corpus dirigido.

## G1.3 - PDF render

- Windows.Data.Pdf;
- bounding dimensions;
- PNG cap;
- `filesystem_render_pdf_page`;
- visual/scan tests;
- MCP image output.

## G1.4 - Evaluation + portable

- suite completa Release;
- publish self-contained;
- incluir PdfWorker en payload;
- Secure MCP Tunnel;
- real-world/fresh-agent;
- segunda PC si el cambio de packaging lo justifica;
- actualizar deployment/documentación.

# 14. Hallazgo lateral: filesystem_read_files vs tunnel

La prueba de payload descubrió una inconsistencia preexistente:

- `filesystem_read_files` puede producir resultados muy por encima de 10 MiB;
- el Core permite hasta 64 MiB agregados;
- Secure MCP Tunnel tiene hard limit de 10 MiB;
- una respuesta de 12 MiB produjo HTTP 413 y terminó el runtime instalado de esa ejecución.

Esto **no bloquea G1** porque las nuevas tools quedan explícitamente por debajo del límite.

No cambiar silenciosamente Filesystem dentro de G1. Registrar como hardening separado para decidir si conviene:

- reducir caps públicos;
- agregar guard de tamaño en el adapter MCP;
- o introducir paginación/continuation más estricta para reads grandes.

# 15. Impacto sobre Computer H

Computer H1 hereda de G:

- Windows TFM migrado;
- helper MCP de imágenes probado;
- `ImageContentBlock` validado por STDIO/Host publicado; el smoke visual directo por ChatGPT queda pendiente sólo de refrescar el catálogo cliente;
- cap binario con evidencia real;
- patrón de contenido visual;
- packaging de helper ejecutable ya ejercitado.

H1.0 queda limitado a foundation específica de Computer.

# Cierre de investigación

Los cinco pendientes previos a implementación quedan cerrados:

- límite real de payload: medido;
- caps de texto PDF: fijados;
- robustez de PdfPig: investigada y mitigada con worker;
- PDFs protegidos: comportamiento probado y error fijado;
- DTOs/error codes/tool contracts: definidos.

G1.0 y la implementación de G1.1 quedaron cerrados técnicamente. No hay bloqueo arquitectónico conocido para iniciar G1.2; el único pendiente de G1.1 es el smoke visual directo tras refrescar el catálogo de ChatGPT.

## Fuentes

- MCP C# SDK tools/content types: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/tools/tools.md
- MCP C# SDK issue #1835: https://github.com/modelcontextprotocol/csharp-sdk/issues/1835
- PdfPig releases: https://github.com/UglyToad/PdfPig/releases
- PdfPig: https://github.com/UglyToad/PdfPig
- Windows.Data.Pdf PdfDocument: https://learn.microsoft.com/windows/uwp/api/windows.data.pdf.pdfdocument
- Windows.Data.Pdf RenderToStreamAsync: https://learn.microsoft.com/windows/uwp/api/windows.data.pdf.pdfpage.rendertostreamasync
- PdfPageRenderOptions: https://learn.microsoft.com/windows/uwp/api/windows.data.pdf.pdfpagerenderoptions
- Secure MCP Tunnel: https://developers.openai.com/api/docs/guides/secure-mcp-tunnels
