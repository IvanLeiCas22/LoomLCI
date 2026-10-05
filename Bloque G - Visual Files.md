# Bloque G - Visual Files

> Estado: **investigación y análisis iniciados; propuesta v0.1 en formación**. No se modificó código de producto. Este bloque pasa a ser anterior a [[Bloque H - Computer]] y busca que el agente pueda consumir archivos visuales directamente, sin tener que abrirlos en una aplicación gráfica.

## Motivación

La superficie pública actual de LoomLCI tiene 19 tools. Filesystem permite descubrir rutas, buscar texto, leer UTF-8 y modificar archivos, pero no existe una ruta nativa para entregar al modelo contenido binario visual.

Hoy faltan tres capacidades básicas:

1. ver una imagen local como imagen real;
2. extraer texto de un PDF de forma eficiente;
3. renderizar una página PDF como imagen cuando el documento sea escaneado, diagramático o visual.

Computer resolverá captura de la sesión interactiva, no lectura directa de archivos. Abrir un PNG o PDF en una GUI sólo para poder verlo sería una dependencia innecesaria y una peor abstracción.

## Alcance inicial propuesto

Agregar tres tools read-only:

- `filesystem_view_image`
- `filesystem_read_pdf`
- `filesystem_render_pdf_page`

Esto llevaría el catálogo objetivo de 19 a 22 tools antes de Computer. Computer H1 agregaría luego sus seis tools sobre esa base.

No incluir en G1:

- OCR propio;
- edición de imágenes;
- edición/generación de PDF;
- Office/LibreOffice;
- video;
- SVG como renderer visual dedicado;
- extracción masiva automática de todas las páginas como imágenes;
- computer vision propio;
- un sistema general de archivos multimedia.

El modelo ya tiene visión. Loom sólo necesita transportar una representación visual fiable y acotada.

## Hallazgo 1 - MCP ya tiene el bloque correcto para imágenes

El SDK MCP C# usado por LoomLCI (`ModelContextProtocol 2.2.0`) expone `ImageContentBlock` y soporta resultados mixtos con texto + structuredContent + bloques de imagen.

La forma correcta es:

```csharp
ImageContentBlock.FromBytes(bytes, "image/png")
```

La documentación actual del SDK también indica que otros binarios pueden viajar como `EmbeddedResourceBlock`, pero el cliente decide cómo los representa. Un `application/pdf` binario no se convierte automáticamente a una imagen ni garantiza que llegue al modelo como documento interpretable. Para Loom conviene por lo tanto usar texto extraído y, cuando haga falta visión, render de página -> PNG.

### Regresión reportada en MCP 2.2.0

Existe un issue abierto del SDK C# (#1835, agosto 2026) donde asignar bytes crudos directamente a `ImageContentBlock.Data` / `BlobResourceContents.Blob` dentro de un `CallToolResult` puede producir una serialización inválida.

Se hizo una reproducción local con exactamente 2.2.0:

- `ImageContentBlock.FromBytes(...)`;
- serialización del `CallToolResult` completo con `McpJsonUtilities.DefaultOptions`;
- JSON con base64 correcto;
- deserialize correcto;
- round-trip OK.

Conclusión provisional: **no usar nunca asignación raw a `Data`**. Centralizar la creación en un helper y congelar un integration/regression test que serialice el resultado completo.

## Hallazgo 2 - Imagen local puede ser una operación simple y stateless

Para PNG/JPEG/WebP no hace falta decodificar ni crear un recurso durable.

Flujo:

```text
path
 -> resolver relativo contra WorkSession si corresponde
 -> validar archivo / tamaño / tipo por magic bytes
 -> leer bytes acotados
 -> metadata estructurada
 -> ImageContentBlock.FromBytes(...)
```

No hace falta ObservationHandle ni ResourceRegistry.

### Formatos v0.1

Propuesta conservadora:

- PNG
- JPEG
- WebP

No confiar sólo en extensión. Validar firma del archivo y hacer que MIME coincida con el contenido.

Otros formatos pueden incorporarse después mediante conversión a PNG si la evidencia real lo justifica.

## Tool propuesta - filesystem_view_image

Read-only.

Entrada:

- `path`
- `workId?`

Salida estructurada:

- requestedPath;
- fullPath;
- mimeType;
- sizeBytes.

Content:

- bloque de texto breve;
- `ImageContentBlock` real.

Límite inicial a congelar antes de implementar. Debe ser conservador porque base64 aumenta el payload aproximadamente un tercio y Secure MCP Tunnel puede devolver `request_body_too_large` cuando una respuesta supera el límite del servicio. El límite numérico del servicio no está publicado en el contrato actual, por lo que hay que validarlo end-to-end antes de cerrar G1.

Candidato inicial: **16 MiB de archivo de imagen**, sujeto a prueba real por tunnel.

## Hallazgo 3 - PDF necesita dos caminos, no uno

Un PDF puede ser:

- principalmente texto;
- principalmente gráfico;
- una mezcla;
- un scan sin capa de texto.

Mandar siempre las páginas como imágenes es caro y malo para documentos largos. Extraer sólo texto pierde diagramas, planos, tablas visuales y scans.

Por eso conviene separar:

1. `filesystem_read_pdf` para lectura textual eficiente;
2. `filesystem_render_pdf_page` para visión de una página concreta.

El agente decide cuál usar según la tarea y puede combinar ambos.

## PDF textual - PdfPig

Se investigó `PdfPig`:

- open source;
- licencia Apache-2.0;
- compatible con .NET;
- extracción de texto, palabras, posiciones, metadata e imágenes;
- soporta documentos cifrados si se proporciona password;
- la release actual investigada es 0.1.15.

Prueba local realizada fuera del repo:

- proyecto `net10.0`;
- PdfPig 0.1.15;
- PDF generado de una página;
- `PdfDocument.Open` correcto;
- page count correcto;
- `ContentOrderTextExtractor.GetText(page)` devolvió correctamente el texto esperado.

Para Loom conviene usar `ContentOrderTextExtractor`, no confiar en `page.Text` como única representación, porque el orden interno del content stream no siempre coincide con el orden lógico de lectura.

Chequeo adicional local: `dotnet list package --vulnerable --include-transitive` no reportó paquetes vulnerables para el proyecto temporal con PdfPig 0.1.15 usando NuGet.org. Esto es sólo una fotografía actual y debe repetirse al fijar la dependencia.

## Tool propuesta - filesystem_read_pdf

Read-only.

Entrada provisional:

- `path`
- `workId?`
- `startPage = 1`
- `maxPages = 10`

Hard cap propuesto:

- máximo 25 páginas por llamada;
- máximo 64 MiB de PDF;
- límite agregado de caracteres en la respuesta;
- páginas 1-based públicamente.

Salida:

- requestedPath;
- fullPath;
- pageCount;
- startPage/endPage;
- hasMoreAfter;
- pages[] con pageNumber, text y textLength;
- señal explícita cuando una página tiene texto nulo o muy escaso.

Si el resultado indica poco texto y la tarea requiere entender la página, la descripción de la tool debe orientar a usar `filesystem_render_pdf_page`.

No renderizar páginas automáticamente dentro de `read_pdf`: mantiene la llamada barata y evita payload visual inesperado.

## PDF visual - Windows.Data.Pdf

Para render se investigó la API nativa `Windows.Data.Pdf`.

Ventajas:

- viene con Windows;
- no hay que redistribuir PDFium/Poppler/Ghostscript;
- puede obtener page count;
- soporta PDF protegido por password a nivel de API;
- `PdfPage.RenderToStreamAsync` permite renderizar una página;
- `PdfPageRenderOptions` permite limitar dimensiones y seleccionar encoder;
- se puede forzar PNG mediante `BitmapEncoder.PngEncoderId`.

Prueba local real:

- proyecto temporal `net10.0-windows10.0.19041.0`;
- PDF de prueba de una página;
- `PdfDocument.LoadFromFileAsync`;
- render a width 600;
- resultado de 4185 bytes;
- magic PNG `89504E470D0A1A0A`;
- ejecución correcta en esta PC.

## Consecuencia: Windows TFM pasa a G

`Windows.Data.Pdf` requiere Windows TFM. La misma restricción ya había sido encontrada durante la investigación de Computer.

Si adoptamos este renderer, el cambio:

- `LoomLCI.Windows` -> `net10.0-windows10.0.19041.0`;
- `LoomLCI.Host` -> mismo Windows TFM;
- `LoomLCI.Windows.Tests` -> mismo Windows TFM;
- `LoomLCI.IntegrationTests` -> mismo Windows TFM;
- Core y MCP permanecen `net10.0`;
- `SupportedOSPlatformVersion=10.0.19041.0`.

debe realizarse en **G1**, no esperar a Computer H1.

Esto no agrega una plataforma nueva: el deployment portable soportado ya es Windows x64. Además reduce riesgo futuro porque Computer reutilizará una migración ya validada.

## Tool propuesta - filesystem_render_pdf_page

Read-only.

Entrada provisional:

- `path`
- `workId?`
- `page` 1-based;
- `maxWidth` default 1800;
- `maxHeight` default 2400.

Salida estructurada:

- requestedPath;
- fullPath;
- page;
- pageCount;
- width;
- height;
- format = png;
- sizeBytes.

Content:

- PNG real mediante `ImageContentBlock.FromBytes`.

Renderizar **una página por llamada** en G1. Permite al agente elegir únicamente las páginas necesarias y mantiene el payload acotado.

## Arquitectura interna propuesta

No conviene seguir agrandando `WindowsFilesystemProvider`, que ya concentra traversal/search/read/write.

Mantener nombres públicos `filesystem_*`, porque desde la perspectiva del agente siguen siendo operaciones sobre archivos, pero separar internamente:

```text
MCP
  -> VisualFilesTools
      -> VisualFilesCapability
          -> IVisualFilesProvider
              -> WindowsVisualFilesProvider
                  -> raw image bytes
                  -> PdfPig text extraction
                  -> Windows.Data.Pdf page rendering
```

Core contendría contratos y validaciones; Windows las implementaciones concretas; MCP sólo mapea DTOs y contenido.

La resolución de paths debe compartir exactamente la semántica ya usada por Filesystem para:

- path absoluto;
- path relativo a WorkSession;
- errores de path;
- Full Trust sin convertir baseDirectory en sandbox.

Antes de implementar conviene extraer/reutilizar el resolver actual en vez de copiar lógica.

No hay estado durable entre llamadas; estas tools no necesitan ResourceRegistry.

## Helper MCP para resultados visuales

`McpToolResults` hoy genera:

- structuredContent;
- un TextContentBlock.

G1 necesita una variante que permita agregar bloques extra sin perder el envelope ni la semántica actual de error.

Forma deseada:

```text
ToolEnvelope<T>
 -> StructuredContent
 -> TextContentBlock
 -> optional ImageContentBlock(s)
```

El helper debe ser reutilizable por Computer H1.

Regla: crear imágenes únicamente mediante `ImageContentBlock.FromBytes`.

## Robustez

### Imágenes

El camino PNG/JPEG/WebP puede mantenerse muy pequeño:

- verificar existencia y regular file;
- cap de bytes antes de leer;
- magic bytes;
- MIME consistente;
- lectura acotada;
- no decodificar si no es necesario.

Esto reduce superficie frente a archivos maliciosos.

### PDF

PDF sí implica parsing complejo.

Controles mínimos:

- cap de tamaño antes de abrir;
- page bounds;
- page count razonable;
- límites de páginas y caracteres por llamada;
- errores de parser convertidos a errores Loom;
- casos truncados/malformados en tests;
- timeout de Invocation.

Riesgo residual: PdfPig es una librería managed in-process y una operación interna que no coopera con cancellation no puede ser interrumpida de forma dura por un CancellationToken.

Se hizo una primera prueba adversarial pequeña fuera del repo:

- PdfPig con 1 KiB de bytes aleatorios -> `PdfDocumentFormatException` en ~2 s;
- PdfPig con PDF truncado -> `PdfDocumentFormatException` en milisegundos;
- Windows.Data.Pdf con bytes aleatorios -> `COMException` en decenas de ms;
- Windows.Data.Pdf con PDF truncado -> `COMException` en milisegundos.

Esto es una señal inicial razonable, no una prueba de peor caso. No construir todavía un PDF worker aislado sólo por posibilidad teórica. Antes de decidirlo conviene ampliar el corpus con PDFs reales problemáticos/grandes. Si aparecen hangs o consumo no acotable, entonces sí justificar helper process + Job Object.

## Password-protected PDFs

Las APIs investigadas soportan password, pero no conviene meter manejo de credenciales en G1 sin necesidad.

Propuesta inicial:

- detectar/error claro para PDF protegido;
- no exponer `password` en el contrato v0.1;
- agregarlo sólo si aparece un caso real que lo requiera.

## Tool surface resultante

Antes de G:

- 19 tools.

Después de G1 propuesto:

- Work: 2
- Filesystem clásico: 6
- Visual Files con prefijo filesystem: 3
- Process: 7
- Python: 2
- Work Plan: 2

Total: **22 tools**.

Después de Computer H1, si mantiene las seis tools diseñadas:

Total: **28 tools**.

## Implementación incremental propuesta

### G1.0 - Binary/image foundation

- mover Windows TFMs requeridos;
- helper MCP mixed structured + image;
- regression test `ImageContentBlock.FromBytes`;
- build/tests/publish portable.

Todavía sin PDF parser.

### G1.1 - Local image

- contratos Core;
- `IVisualFilesProvider`;
- path resolution compartida;
- PNG/JPEG/WebP;
- `filesystem_view_image`;
- tests de tipo/tamaño/magic bytes;
- integration STDIO;
- prueba real por Secure MCP Tunnel y ChatGPT.

### G1.2 - PDF text

- PdfPig fijado;
- page range;
- ContentOrderTextExtractor;
- caps y truncation;
- `filesystem_read_pdf`;
- PDFs normales, vacíos, corruptos y de muchas páginas.

### G1.3 - PDF render

- Windows.Data.Pdf;
- PNG bounded;
- `filesystem_render_pdf_page`;
- PDF visual/scan;
- page bounds;
- integración MCP con ImageContentBlock.

### G1.4 - Evaluation + portable

- suite completa;
- publish self-contained;
- Secure MCP Tunnel;
- imagen real visible en ChatGPT;
- PDF textual real;
- PDF visual real;
- PDF escaneado;
- malformed/oversized;
- fresh-agent discoverability;
- actualizar deployment y documentación.

## Impacto sobre Computer H

Computer sigue siendo el bloque siguiente, pero hereda de G:

- Windows TFM ya migrado;
- helper MCP de imágenes ya probado;
- ImageContentBlock ya validado por tunnel/ChatGPT;
- límites de payload con evidencia real.

Por eso H1.0 deberá simplificarse al cerrar G: Computer ya no tendrá que ser el primer consumidor de WinRT ni el primer productor de imágenes MCP.

## Pendientes de investigación antes de implementación

1. congelar límite real de payload de imagen con prueba Secure MCP Tunnel;
2. decidir límite agregado de texto para `filesystem_read_pdf`;
3. ejecutar corpus pequeño de PDFs corruptos/grandes para decidir si PdfPig in-process es suficiente;
4. comprobar comportamiento de PDF protegido y mapear error;
5. definir DTOs/error codes exactos;
6. reconciliar H1.0 una vez cerrado el diseño de G.

## Fuentes

- MCP C# SDK tools/content types: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/tools/tools.md
- MCP C# SDK issue #1835: https://github.com/modelcontextprotocol/csharp-sdk/issues/1835
- PdfPig: https://github.com/UglyToad/PdfPig
- Windows.Data.Pdf PdfDocument: https://learn.microsoft.com/windows/uwp/api/windows.data.pdf.pdfdocument
- Windows.Data.Pdf RenderToStreamAsync: https://learn.microsoft.com/windows/uwp/api/windows.data.pdf.pdfpage.rendertostreamasync
- PdfPageRenderOptions: https://learn.microsoft.com/windows/uwp/api/windows.data.pdf.pdfpagerenderoptions
- Secure MCP Tunnel: https://developers.openai.com/api/docs/guides/secure-mcp-tunnels
