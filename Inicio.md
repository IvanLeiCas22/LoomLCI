# LoomLCI

Segundo cerebro del proyecto LoomLCI.

## Estado

Arquitectura v0.1 reconciliada y baseline cerrada. Milestone 1 (Process), Milestone 2 (Filesystem), C1 (Native Process + Job Objects), C2 (ConPTY), D0 (resource lifetime/expiry + `process_release`), Python Runtime E1, **Agent Support F1** y **Visual Files G1** están implementados y validados. F1.3 quedó confirmado por el benchmark real-world final: **4/4 positivos claros, 3/3 controles simples y escalada 8A sin plan -> 8B con plan** usando la skill del plugin 0.2.1. [[Deployment portable]] está **implementado y validado end-to-end en dos PCs Windows x64**; la repetición específica de los follow-ups recientes en notebook queda diferida/no bloqueante. Visual Files tiene visión directa confirmada en ChatGPT. El hardening de `filesystem_read_files` cerró además el riesgo de superar el límite del tunnel mediante un cap MCP serializado de 9 MiB; suite Release actual **251/251** y runtime instalado healthy/ready. **Computer H1 queda pausado por decisión actual; no hay otro bloque funcional activo.**

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
