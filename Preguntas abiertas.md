# Preguntas abiertas

> Revisión 2026-10-08: separar **preguntas todavía abiertas** de decisiones históricas ya resueltas. La productiva es Release 6; la auditoría identificó deudas concretas de integridad y recuperación que deben corregirse aunque el runtime siga operativo. Prioridades aprobadas en [[Roadmap de robustez post-auditoría]]. Estado general: [[Inicio]], [[Roadmap post-G1]].

## Arquitectura general

- Baseline v0.1 reconciliada y aceptada en [[Arquitectura propuesta]], [[Especificacion interna v0.1]] y [[Estructura del repositorio v0.1]].
- Mantener explícita la distinción entre arquitectura implementada y capabilities futuras.

## Python Runtime

**E1, Python 1, Python 2 y Python 3 (P3.0–P3.2) están cerrados.** El runtime privado CPython 3.14.8, el package store gestionado con `uv`, el bridge `loom.fs`/`loom.process` y las imágenes PNG/JPEG/WebP con `loom.display_image` se implementaron y validaron. Ver [[Bloque E - Python Runtime]], [[Python 1 - Paquetes administrados]], [[Python 2 - Bridge privado loom]] y [[Python 3 - Outputs binarios e imágenes]].

**Aún diferido (no bloqueante):** PyAutoGUI/Computer como integración adicional y soporte de outputs binarios genéricos/audio, que no formaron parte del cierre image-first. **Limitación del consumidor:** la UI de ChatGPT no muestra inline imágenes Python in-memory, aunque el modelo sí las recibe/ve mediante plugin 0.5.1; `UI_RENDER` no es un defecto pendiente del Host.

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

Validación en segunda PC completada. **Actualización operativa al 2026-10-08:** la PC principal ejecuta **Release 6 `0.1.0-dev-6b1566a` (sequence 6)**, healthy/ready; **Release 5** queda como rollback. El updater firmado, rollback e instalador completaron sus milestones históricos, pero la auditoría posterior abrió ajustes de robustez RB-02, RB-04 y RB-05 en [[Roadmap de robustez post-auditoría]]. Diferido/no bloqueante fuera de ese mini-roadmap:

- DPAPI;
- autoarranque al login;
- MSI/MSIX;
- code signing / Authenticode;
- auto-update silencioso/background y actualización automática del Launcher;
- win-arm64;
- bundle offline;
- estrategia final multi-PC/tunnels simultáneos.

## Visual Files

Investigación, implementación y evaluación final cerradas en [[Bloque G - Visual Files]] y [[G1.4 - Evaluation + portable]]. Quedan fijadas tres tools read-only (`filesystem_view_image`, `filesystem_read_pdf`, `filesystem_render_pdf_page`), `ImageContentBlock.FromBytes`, cap de 6 MiB binarios + 9 MiB MCP serializados para resultados visuales, PdfPig 0.1.16 aislado para texto y PDFium nativo aislado para render dentro de `LoomLCI.PdfWorker`. **G1.0–G1.4 están cerrados.** El follow-up de compatibilidad visual quedó resuelto: las dos tools que devuelven imagen no anuncian `outputSchema`, preservan `ImageContentBlock` y fueron verificadas con visión directa en ChatGPT. La repetición específica del paquete G1 actual en notebook queda diferida/no bloqueante. No queda pregunta arquitectónica u operativa bloqueante en Visual Files.

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
- Ergonomía: `process_run` **cerrado end-to-end** como one-shot real en Core, sin handle durable y reutilizando el backend Process existente.
- Ergonomía Work Plan: [[Ergonomía - Work Plan patch]] implementó `work_plan_patch` con revision/CAS e IDs estables. **Smoke directo en ChatGPT con catálogo refrescado completado**, incluido CAS stale/atomicidad; bloque cerrado end-to-end.
- tamaños de buffers.
- formato y retención del audit durable.
- autenticación remota si se habilita HTTP fuera de localhost/tunnel.
- elevación futura.
