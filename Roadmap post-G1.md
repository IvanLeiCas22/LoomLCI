# Roadmap post-G1

> Estado: **activo para planificación.** Computer H1 queda pausado. El trabajo futuro seleccionado se concentra en Ergonomía, Producto/Deployment y Python.

## Objetivo

Mejorar LoomLCI sin abrir todavía Computer Use, priorizando cambios que mejoren primero la propia capacidad de desarrollar LoomLCI, luego su experiencia como producto instalado y finalmente la potencia del runtime Python.

## Orden acordado

### 1. Ergonomía de agente

1. `process_run` — **CERRADO end-to-end**: ejecución one-shot de comandos cortos con resultado completo en una sola llamada, incluyendo smoke directo desde ChatGPT.
2. Ergonomía de Work Plan — **IMPLEMENTADO/INSTALADO**: `work_plan_patch` reduce el costo de milestones pequeños sin perder revision/CAS ni identidad estable. Suite 269/269 y runtime healthy/ready; falta sólo smoke directo de la nueva tool desde un chat con catálogo refrescado.

Motivo para ir primero: son cambios acotados, de bajo riesgo y se pueden dogfoodear durante todos los bloques siguientes.

### 2. Producto / Deployment

Orden preliminar:

1. mejorar UX del Launcher;
2. instalador Windows convencional;
3. base de auto-update con validación y rollback;
4. mecanismo de generación/verificación de metadata y skill del plugin.

La reconciliación final del plugin/skill se cerrará después de incorporar las nuevas capabilities Python, para evitar documentar dos veces una superficie todavía cambiante.

### 3. Python

Orden preliminar:

1. paquetes de terceros administrados/versionados por LoomLCI;
2. bridge privado `loom.*` entre el worker Python y capabilities de LoomLCI;
3. outputs binarios/imágenes desde Python hacia el modelo.

La ruta binaria debe reutilizar las lecciones de Visual Files y respetar los límites MCP/tunnel ya fijados.

## Reconciliación plugin/skill

Es un tema transversal:

- construir temprano el mecanismo de generación/verificación dentro del roadmap de Producto;
- mantener el schema vivo MCP como fuente de verdad;
- evitar que el plugin vuelva a depender de rutas Debug del repo;
- hacer la reconciliación final después de Ergonomía + Python;
- mantener la publicación/actualización del plugin como paso externo al runtime local, salvo evidencia futura que justifique otro acoplamiento.

## Workflow de cada mejora

1. investigar estado actual del código;
2. analizar alternativas y contrato;
3. documentar sólo decisiones suficientemente maduras;
4. volver al usuario con propuesta concreta;
5. implementar únicamente con aprobación;
6. validar tests + runtime/tunnel cuando corresponda;
7. actualizar documentación;
8. commit;
9. para cambios de runtime instalado, usar LoomLCI como herramienta principal e IvanSpace sólo para cutover/fallback/recuperación.

## Próxima acción

**Acceptance final de Ergonomía 2.**

`work_plan_patch` ya está implementado, validado, publicado e instalado en `0.1.0-dev-work-plan-patch`. La conversación de implementación conserva el catálogo anterior de 23 tools, por lo que falta únicamente abrir un chat con catálogo refrescado y ejecutar un smoke directo de la nueva tool.

Después de ese smoke, el siguiente objetivo del roadmap es **Producto / Deployment 1: mejorar UX del Launcher**.
