# LoomLCI

Segundo cerebro del proyecto LoomLCI.

## Estado

Arquitectura v0.1 reconciliada y baseline cerrada. Process, Filesystem, Native Process/Job Objects, ConPTY, resource lifetime, Python Runtime E1, **[[Python 1 - Paquetes administrados|Python 1]]**, **[[Python 2 - Bridge privado loom|Python 2]]**, **Agent Support F1**, **Visual Files G1**, Deployment/Launcher, [[Instalador Windows]], [[Auto-update firmado]] y [[Plugin metadata]] están implementados/validados hasta sus etapas indicadas. **Python 2 queda CERRADO end-to-end**. El runtime activo ahora es **`0.1.0-dev-python3`**, healthy/ready sobre el mismo tunnel, con `0.1.0-dev-python2` como rollback. El Host instalado publica **25 tools**, bridge API v2 y `loom.display_image`; `python_execute` omite `outputSchema` y aplica presupuesto MCP exacto de **9 MiB**. Integration contra la DLL instalada: **21/21**; suite Release posterior: **356/356**. El plugin privado fue reconciliado/publicado por CAS a **0.5.0** con read-back OK. Computer H1 sigue pausado. [[Python 3 - Outputs binarios e imágenes|Python 3]] tiene P3.0/P3.1 cerrados y P3.2 implementado/deployado; falta únicamente el **fresh-agent visual final** en chat nuevo para cerrar end-to-end el routing `loom.display_image` vs `filesystem_view_image`.

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
