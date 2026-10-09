---
tipo: indice
estado: vigente
actualizado: 2026-10-09
---
# LoomLCI — Inicio

Esta es la **puerta de entrada de la bóveda Obsidian**. Las notas vigentes explican cómo está construido y cómo funciona LoomLCI hoy. El material antiguo se consulta en [[Indice historico]].

## Orientación rápida

| Necesito... | Consultar |
| --- | --- |
| Conocer instalación, release y capacidades verificadas | [[Estado del sistema]] |
| Entender capas, recursos y protocolos | [[Arquitectura actual]] |
| Revisar compromisos y criterios técnicos | [[Decisiones vigentes]] |
| Usar Filesystem | [[Filesystem]] |
| Ejecutar comandos, procesos y terminales | [[Process]] |
| Utilizar Python persistente y paquetes | [[Python]] |
| Leer imágenes y PDFs | [[Visual Files]] |
| Manejar planes de trabajo | [[Work Plan]] |
| Instalar y conectar ChatGPT | [[Instalacion y conexion]] |
| Actualizar, volver atrás o recuperarse | [[Actualizacion y recuperacion]] |
| Configurar registros opt-in | [[Diagnosticos]] |
| Compilar, probar y contribuir | [[Guia de desarrollo]] |
| Ver lo pendiente y lo diferido | [[Backlog]] |

## Modelo documental

- **Vigente:** documentos de `docs/00-Inicio` a `docs/06-Planificacion`. Los hechos implementados deben verificarse contra el código y el contrato MCP real.
- **Histórico:** `docs/90-Historial`. Conserva las 58 notas preexistentes, incluidos resultados, hipótesis, diseños y comprobaciones de cada release. Una afirmación etiquetada «vigente» dentro de una nota histórica pertenece a su fecha original.
- **Código:** `src/` es autoridad para la implementación; `plugin/contract/mcp-contract.json` y el catálogo MCP publicado rigen nombres, argumentos y annotations; `scripts/` y `tests/` son autoridad para builds y verificación.

## Cómo trabajar

Revisar **código actual → investigar → proponer → recibir aprobación → implementar → probar → desplegar con supervisor externo cuando proceda → actualizar documentación vigente y evidencia histórica**. No convertir un documento de planificación en una prueba de despliegue real.

Al finalizar un cambio, actualizar sólo las páginas canónicas afectadas; registrar pruebas y releases con fecha y fuente. Ver [[Guia documental]].
