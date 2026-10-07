# LoomLCI

Segundo cerebro del proyecto LoomLCI.

## Estado

Arquitectura v0.1 reconciliada y baseline cerrada. Process, Filesystem, Native Process/Job Objects, ConPTY, resource lifetime, Python Runtime E1, **[[Python 1 - Paquetes administrados|Python 1]]**, **[[Python 2 - Bridge privado loom|Python 2]]**, **Agent Support F1**, **Visual Files G1**, Deployment/Launcher, [[Instalador Windows]], [[Auto-update firmado]] y [[Plugin metadata]] están implementados/validados hasta sus etapas indicadas. **Python 2 queda CERRADO end-to-end**: runtime activo `0.1.0-dev-python2`, healthy/ready sobre el mismo tunnel, `0.1.0-dev-python1` como rollback, Integration **19/19** contra Host publicado e instalado, consumer smoke directo OK y fresh-agent smoke **`P24_FRESH_AGENT_OK`** con metadata nueva de `python_execute`, `loom.fs` y `loom.process`. El Host instalado publica **25 tools** con Work Plan habilitado. El plugin privado sigue en **0.3.0**; la próxima etapa es su reconciliación final de skill/metadata. Computer H1 sigue pausado.

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
- [[Bloque F - Agent Support]]
- [[Bloque G - Visual Files]]
- [[G1.1 - Local image]]
- [[Bloque H - Computer]]
- [[F1.3 - Benchmark real-world Work Plan]]
- [[Ergonomía - Work Plan patch]]
- [[Plan y tareas]]
- [[Programmatic Tool Calling]]
- [[Integracion con ChatGPT]]
- [[Deployment portable]]
- [[Fuentes]]
