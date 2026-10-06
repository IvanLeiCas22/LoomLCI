# LoomLCI

Segundo cerebro del proyecto LoomLCI.

## Estado

Arquitectura v0.1 reconciliada y baseline cerrada. Milestone 1 (Process), Milestone 2 (Filesystem), C1 (Native Process + Job Objects), C2 (ConPTY), D0 (resource lifetime/expiry + `process_release`), Python Runtime E1, **Agent Support F1** y **Visual Files G1** están implementados y validados. F1.3 quedó confirmado por el benchmark real-world final: **4/4 positivos claros, 3/3 controles simples y escalada 8A sin plan -> 8B con plan** usando la skill del plugin 0.2.1. [[Deployment portable]] está **implementado y validado end-to-end en dos PCs Windows x64**; la repetición específica del paquete G1 actual en notebook queda diferida/no bloqueante. **Visual Files G1.0–G1.4 está cerrado**: suite Release **249/249**, publish/package portable, corpus ChatGPT/tunnel y fresh-agent correctos. El follow-up visual quedó resuelto: `filesystem_view_image` y `filesystem_render_pdf_page` entregan imágenes directamente a la visión del modelo al omitir `outputSchema`. El siguiente bloque es **Computer H1**.

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
