# LoomLCI

Segundo cerebro del proyecto LoomLCI.

## Estado

Arquitectura v0.1 reconciliada y baseline cerrada. Process, Filesystem, Native Process/Job Objects, ConPTY, resource lifetime, Python Runtime E1, **[[Python 1 - Paquetes administrados|Python 1]]**, **[[Python 2 - Bridge privado loom|Python 2 P2.0+P2.1+P2.2+P2.3]]**, **Agent Support F1**, **Visual Files G1**, Deployment/Launcher, [[Instalador Windows]], [[Auto-update firmado]] y [[Plugin metadata]] están implementados/validados hasta sus etapas indicadas. El Host instalado publica **25 tools** con Work Plan habilitado; el runtime activo sigue en `0.1.0-dev-python1` healthy/ready porque el cutover de Python 2 está reservado para P2.4. En código, Python 2 ya incluye protocolo v2 + router modular + `loom.fs` + `loom.process` + hardening P2.3; la suite Release serial actual es **337/337** e Integration **19/19**. El plugin privado sigue en **0.3.0** hasta la reconciliación final posterior al bloque Python. Computer H1 sigue pausado; el siguiente objetivo de [[Roadmap post-G1]] es **P2.4: Deployment / consumer smoke**.

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
