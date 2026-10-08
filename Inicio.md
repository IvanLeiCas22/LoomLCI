# LoomLCI

Segundo cerebro del proyecto LoomLCI.

## Estado

**Corte operativo verificado el 2026-10-08.** El repositorio `main` está limpio y sincronizado con `origin/main`. La instalación real ejecuta **Release 5, `0.1.0-dev-42ee90c` (sequence 5)**, con Host y Launcher actualizados, `healthy=true`, `ready=true` y el mismo Secure MCP Tunnel. Se conserva `0.1.0-dev-python3` (sequence 0) como rollback. `update check` confirma que la Release 5 es la última del canal configurado. Ver [[Auto-update firmado]] y [[Deployment portable]].

**Capacidades cerradas:** Process, Filesystem, Native Process/Job Objects, ConPTY, resource lifetime, Python Runtime E1, [[Python 1 - Paquetes administrados|Python 1]], [[Python 2 - Bridge privado loom|Python 2]], [[Python 3 - Outputs binarios e imágenes|Python 3 (P3.0–P3.2)]], Agent Support F1, Visual Files G1, ergonomía (`process_run` y `work_plan_patch`), Launcher, [[Instalador Windows]], update firmado y [[Plugin metadata]]. El Host publica **25 tools MCP**; el bridge privado incluye `loom.fs`, `loom.process` y `loom.display_image`, y el plugin privado canónico/publicado está en **0.5.1**. Suite Release ejecutada nuevamente: **366/366**, incluyendo **21/21 IntegrationTests** (binarios Release existentes).

**Próximos trabajos, no errores activos:** [[Bloque H - Computer|Computer H1]] sigue **pausado**, aunque su diseño está listo; el roadmap anterior ya está **cerrado** y no hay otro bloque aprobado. Las actualizaciones automáticas en segundo plano y la publicación automática de releases quedan para un horizonte lejano. La visión de imágenes Python in-memory por el modelo funciona con el workaround del plugin; `UI_RENDER` inline en ChatGPT Web/Code Mode sigue siendo una limitación externa no bloqueante. Ver [[Roadmap post-G1]] y [[Preguntas abiertas]].

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
- [[Python 1 - Paquetes administrados]]
- [[Python 2 - Bridge privado loom]]
- [[Python 3 - Outputs binarios e imágenes]]
- [[Bloque F - Agent Support]]
- [[Bloque G - Visual Files]]
- [[G1.1 - Local image]]
- [[Bloque H - Computer]]
- [[F1.3 - Benchmark real-world Work Plan]]
- [[Ergonomía - Work Plan patch]]
- [[Plan y tareas]]
- [[Roadmap post-G1]]
- [[Auto-update firmado]]
- [[Plugin metadata]]
- [[Programmatic Tool Calling]]
- [[Integracion con ChatGPT]]
- [[Deployment portable]]
- [[Instalador Windows]]
- [[Fuentes]]
