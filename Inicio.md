# LoomLCI

Segundo cerebro del proyecto LoomLCI.

## Estado

Arquitectura v0.1 reconciliada y baseline cerrada. Milestone 1 (Process), Milestone 2 (Filesystem), C1 (Native Process + Job Objects), C2 (ConPTY), D0 (resource lifetime/expiry + `process_release`), Python Runtime E1 y **Agent Support F1** están implementados y validados. F1.3 quedó confirmado por el benchmark real-world final: **4/4 positivos claros, 3/3 controles simples y escalada 8A sin plan -> 8B con plan** usando la skill del plugin 0.2.1. Antes de Computer se cerró el diseño de [[Deployment portable]] para instalar LoomLCI side-by-side, arrancarlo con doble clic y llevarlo a otra PC Windows sin depender del repo/IvanSpace. **Computer sigue siendo la próxima capability grande.**

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
- [[F1.3 - Benchmark real-world Work Plan]]
- [[Plan y tareas]]
- [[Programmatic Tool Calling]]
- [[Integracion con ChatGPT]]
- [[Deployment portable]]
- [[Fuentes]]
