# Preguntas abiertas

## Arquitectura general

- Baseline v0.1 reconciliada y aceptada en [[Arquitectura propuesta]], [[Especificacion interna v0.1]] y [[Estructura del repositorio v0.1]].
- Mantener explícita la distinción entre arquitectura implementada y capabilities futuras.

## Python Runtime

El vertical slice E1 quedó diseñado en [[Bloque E - Python Runtime]]. Cerrado para E1: runtime privado CPython 3.14.8 embeddable, Named Pipe versionado, un worker lazy por WorkSession, timeout/reset y stdlib-only.

Preguntas deliberadamente diferidas:

- paquetes de terceros posteriores a E1;
- forma exacta del futuro bridge `loom.*`;
- outputs de imagen cuando la capa MCP utilizada tenga una ruta binaria estable;
- integración PyAutoGUI/Computer.

## Deployment portable

Implementado y validado en la PC principal en [[Deployment portable]]:

- instalación side-by-side por usuario;
- Host Release self-contained `win-x64`;
- Launcher `start`/`stop`/`status`/`setup`;
- tunnel-client v0.0.14 oficial con hash fijado;
- secrets por máquina con DACL user-only y referencia `file:`;
- profile/state dirs aislados;
- acceso directo de escritorio;
- cutover y rollback reales validados; el legacy queda preservado y detenido como fallback.

Validación en segunda PC completada. Diferido/no bloqueante:

- DPAPI;
- autoarranque al login;
- MSI/MSIX;
- code signing;
- auto-update;
- win-arm64;
- bundle offline;
- estrategia final multi-PC/tunnels simultáneos;
- reconciliación de metadata portable de la skill/plugin.

## Visual Files

Investigación y diseño v0.1 cerrados en [[Bloque G - Visual Files]]. Quedan fijadas tres tools read-only (`filesystem_view_image`, `filesystem_read_pdf`, `filesystem_render_pdf_page`), `ImageContentBlock.FromBytes`, cap de 6 MiB binarios + 9 MiB MCP serializados para resultados visuales, PdfPig 0.1.16 aislado en `LoomLCI.PdfWorker`, Windows.Data.Pdf para render y contratos/errores finales. **G1.0, G1.1 y G1.2 están implementados.** G1.1 conserva el bloqueo upstream visual de ChatGPT; G1.2 está técnicamente validado en 235/235 tests, publicado/instalado, healthy/ready y con smoke directo ChatGPT/tunnel completado sobre el catálogo de 21 tools. No hay pregunta arquitectónica bloqueante para G1.3.

## Computer

Diseño H1 cerrado en [[Bloque H - Computer]]: observations session-owned con TTL corto, stale validation, WGC, DPI físico PMv2, DesktopInputGate + SendInput, UIA en MTA dedicado, seis tools MCP y Python/PyAutoGUI fuera del backend autoritativo.

Diferido después de H1:

- HDR/tone mapping;
- UIAccess/elevated UI;
- OCR/computer vision propio;
- eventos UIA;
- PyAutoGUI bridge;
- video/streaming;
- policy engine general.

## Agent Support

- F1.1 Core implementado/validado: estado Work Plan dentro de WorkSession, revision/CAS, límites y lifecycle.
- F1.2 implementado/validado: `work_plan_get`/`work_plan_update`, schema/annotations y opt-in estático por adapter.
- F1.3 cerrado: benchmark real-world [[F1.3 - Benchmark real-world Work Plan]] aprobado con 4/4 positivos, 3/3 controles y escalada 8A sin plan -> 8B con plan.
- Evaluar más adelante si hacen falta dependencias explícitas/DAG; no incluirlas en v0.1 sin evidencia.

## Operación

- WorkSession idle TTL, tombstone retention, ProcessHandle post-exit TTL y explicit release definidos en [[Bloque D0 - Resource lifetime y expiry]]; quedan futuras policies por nuevos resource kinds.
- Ergonomía futura: evaluar si comandos CLI muy cortos justifican una superficie que reduzca el ciclo process_start -> process_read; no es bloqueo funcional actual.
- Ergonomía futura: Work Plan CAS + IDs opacos es deliberadamente seguro pero verboso en workflows lineales; no simplificar sin preservar semántica de concurrencia.
- Hardening separado de G1: `filesystem_read_files` puede producir hoy respuestas mayores que el límite real de 10 MiB del Secure MCP Tunnel; una prueba de 12 MiB devolvió HTTP 413 y terminó esa ejecución del runtime. Evaluar guard MCP/caps/paginación sin mezclarlo silenciosamente con Visual Files.
- tamaños de buffers.
- formato y retención del audit durable.
- autenticación remota si se habilita HTTP fuera de localhost/tunnel.
- elevación futura.
