# LoomLCI

Segundo cerebro del proyecto LoomLCI.

## Estado

Arquitectura v0.1 reconciliada y baseline cerrada. Milestone 1 (Process), Milestone 2 (Filesystem), C1 (Native Process + Job Objects), C2 (ConPTY), D0 (resource lifetime/expiry + `process_release`), Python Runtime E1 y **Agent Support F1** están implementados y validados. F1.3 quedó confirmado por el benchmark real-world final: **4/4 positivos claros, 3/3 controles simples y escalada 8A sin plan -> 8B con plan** usando la skill del plugin 0.2.1. [[Deployment portable]] está **implementado y validado end-to-end en dos PCs Windows x64**: instalación Release self-contained, launcher Start/Stop/Status/Setup, acceso directo, cutover/rollback, migración entre escritorio y notebook y smoke real desde ChatGPT en ambas máquinas. Quedan sólo mejoras de UX no bloqueantes. **Visual Files G1.0, G1.1, G1.2 y G1.3 están implementados. G1.1 conserva un bloqueo upstream porque ChatGPT no materializa `ImageContentBlock` como visión; G1.2 completó su smoke directo. G1.3 PDF render está instalado en `0.1.0-dev-63d256e9b658`, healthy/ready y validado técnica/portablemente; sólo resta refrescar el catálogo de ChatGPT de 21 a 22 tools para su smoke directo. Después sigue G1.4 Evaluation + portable y Computer continúa como H1.**

## Notas

- [[Decisiones]]
- [[Investigacion]]
- [[Arquitectura propuesta]]
- [[Especificacion interna v0.1]]
- [[Estructura del repositorio v0.1]]
- [[Milestone 1 - Process vertical slice]]
- [[Milestone 2 - Filesystem vertical slice]]
- [[Bloque C1 - Native Process y Job Objects]]
- [[Bloque C2 - ConPTY]]
- [[Bloque D0 - Resource lifetime y expiry]]
- [[Validacion final fresh-agent]]
- [[Preguntas abiertas]]
- [[Python Runtime]]
- [[Bloque E - Python Runtime]]
- [[Bloque F - Agent Support]]
- [[Bloque G - Visual Files]]
- [[G1.1 - Local image]]
- [[Bloque H - Computer]]
- [[F1.3 - Benchmark real-world Work Plan]]
- [[Plan y tareas]]
- [[Programmatic Tool Calling]]
- [[Integracion con ChatGPT]]
- [[Deployment portable]]
- [[Fuentes]]
