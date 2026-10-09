---
tipo: referencia_capacidad
estado: vigente
actualizado: 2026-10-09
---
# Visual Files

Capacidad **read-only** para archivos locales visuales, separada internamente de Filesystem aunque conserve su prefijo en MCP.

| Herramienta | Resultado | Uso |
| --- | --- | --- |
| `filesystem_view_image` | Imagen PNG/JPEG/WebP | Inspeccionar un archivo local sin OCR ni base64 manual |
| `filesystem_read_pdf` | Texto paginado de PDF | Extraer texto de PDFs que lo contienen |
| `filesystem_render_pdf_page` | Imagen de una página | Ver disposición, gráficos, tablas o páginas escaneadas |

El texto PDF no hace OCR; en escaneos puede estar vacío. El render emplea worker separado `LoomLCI.PdfWorker`, no render in-process sin aislamiento. La salida visual es un bloque de imagen MCP y su presentación al usuario depende también del cliente.

## Límites implementados

- Archivos PDF máximos: **64 MiB**.
- Imágenes: **6 MiB** de binario máximo; respuesta MCP visual serializada presupuestada a **9 MiB**.
- En Python, `loom.display_image` conserva restricciones propias de cantidad, tamaño y payload.
- Los máximos de dimensiones de render y páginas por consulta se rigen por el schema MCP actual.

Ver `src/LoomLCI.Mcp/VisualFilesTools.cs`, `src/LoomLCI.Windows/VisualFiles/` y `src/LoomLCI.PdfWorker/`.

**Evidencias:** [[Bloque G - Visual Files]], [[G1.1 - Local image]], [[G1.2 - PDF text worker]], [[G1.3 - PDF render]], [[G1.4 - Evaluation + portable]].
