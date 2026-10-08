# Roadmap post-G1

> Estado al 2026-10-08: **CERRADO end-to-end.** Ergonomía, Producto/Deployment y Python 1–3 completados. Release 5 instalada; Computer H1 continúa pausado. Se conserva el orden histórico de ejecución.

## Objetivo

Objetivo original (cumplido): mejorar LoomLCI sin abrir todavía Computer Use, priorizando primero la ergonomía del agente, luego el producto instalado y finalmente la potencia del runtime Python.

## Orden acordado

### 1. Ergonomía de agente

1. `process_run` — **CERRADO end-to-end**: ejecución one-shot de comandos cortos con resultado completo en una sola llamada, incluyendo smoke directo desde ChatGPT.
2. Ergonomía de Work Plan — **CERRADO end-to-end**: `work_plan_patch` reduce el costo de milestones pequeños sin perder revision/CAS ni identidad estable. Suite 269/269, runtime healthy/ready y smoke directo desde ChatGPT con catálogo refrescado completados correctamente.

Motivo para ir primero: son cambios acotados, de bajo riesgo y se pueden dogfoodear durante todos los bloques siguientes.

### 2. Producto / Deployment

Secuencia ejecutada:

1. mejorar UX del Launcher — **CERRADO end-to-end**: modo `--pause` opt-in para accesos directos, shortcuts de iniciar/detener, mensajes simplificados, salida UTF-8 y README portable con encoding estable;
2. [[Instalador Windows|instalador Windows convencional]] — **CERRADO end-to-end**: Inno Setup 7.1.0, instalación per-user sin admin, registro en Aplicaciones instaladas, uninstall real y reutilización de `SetupService`;
3. [[Auto-update firmado|base de auto-update con validación y rollback]] — **CERRADO end-to-end**: manifest firmado ECDSA, `sequence` anti-rollback, `update check/apply`, rollback transaccional, journal/crash recovery, limpieza `active + previous` y E2E real contra GitHub Releases;
4. [[Plugin metadata|mecanismo de generación/verificación de metadata y skill del plugin]] — **CERRADO end-to-end**: fuente canónica en repo, export real `initialize + tools/list`, snapshot de contrato, build/verifier anti-drift, neutralización del wiring Debug histórico y plugin privado actualizado por CAS/read-back a **0.5.1**. La evaluación fresh-agent histórica 0.4.0 fue 4/4 PASS; 0.5.1 agrega sólo un workaround de compatibilidad visual de ChatGPT Code Mode, sin alterar el contrato MCP.

### 3. Python

Secuencia ejecutada:

1. [[Python 1 - Paquetes administrados|paquetes de terceros administrados/versionados por LoomLCI]] — **CERRADO**: `uv` privado fijado/verificado, environments inmutables con locks+hashes, `python_packages_prepare`, binding por WorkSession, reset explícito al cambiar environment, GC protegido y deployment instalado validado;
2. [[Python 2 - Bridge privado loom|bridge privado `loom.*` entre el worker Python y capabilities de LoomLCI]] — **CERRADO end-to-end (P2.0–P2.4)**: protocolo privado v2, router modular, `loom.fs`, `loom.process`, lifecycle/ownership administrado, hardening, deployment real, consumer smoke y fresh-agent smoke completados;
3. [[Python 3 - Outputs binarios e imágenes|outputs binarios/imágenes desde Python hacia el modelo]] — **CERRADO end-to-end (P3.0–P3.2)**. Al cerrar P3, el runtime era `0.1.0-dev-python3`, con Python 2 como rollback. Quedaron validados el Host de 25 tools, `loom.display_image`, `ImageContentBlock`, presupuesto MCP de 9 MiB, Integration 21/21, suite Release 356/356 y fresh-agent ciego PASS. El workaround visual se publicó en el plugin **0.5.1**, sin drift del contrato MCP. `UI_RENDER` inline sigue siendo una limitación del consumidor; audio/blobs genéricos continúan diferidos.

**Despliegue posterior (2026-10-08):** Release 5 `0.1.0-dev-42ee90c` (sequence 5) instalada end-to-end con Host y Launcher corregidos; runtime healthy/ready y `update check` sin novedades. Suite Release actual **366/366**. Rollback: `0.1.0-dev-python3` (sequence 0). Ver [[Auto-update firmado]].

## Horizonte lejano / post-roadmap actual

Estas mejoras quedan **documentadas pero deliberadamente no priorizadas**, ahora que el roadmap Ergonomía/Producto/Python terminó. No son bloqueos del runtime actual:

- **publicación automática de releases**: GitHub Actions dispara desde un tag/release, construye los artefactos, ejecuta validaciones y publica automáticamente setup + ZIP de update + manifest + firma; requiere resolver de forma segura el acceso a la clave privada de firma dentro de CI;
- **update automático en las PCs instaladas**: chequeo periódico o al iniciar LoomLCI y aplicación automática/semiautomática de releases firmadas, reutilizando el motor transaccional, health check y rollback ya implementados;
- cualquier automatización de este bloque debe conservar como invariantes la firma obligatoria, `sequence` anti-rollback, journal/crash recovery y rollback seguro.

Por ahora el modelo operativo aceptado sigue siendo: **publicación manual de la GitHub Release + `update check/apply` explícito en cada instalación**.

## Reconciliación plugin/skill

**CERRADO.** El mecanismo de metadata/skill se implementó y verificó contra el contrato MCP vivo, se neutralizaron las rutas Debug antiguas y se publicó el plugin privado **0.5.1** mediante CAS/read-back. La publicación del plugin sigue separada del runtime local; para futuras versiones se conserva el procedimiento de [[Plugin metadata]].

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

## Siguiente etapa a decidir

El roadmap post-G1 quedó **cerrado** y no tiene tareas técnicas de P3 pendientes. **No se aprobó todavía otro bloque de implementación.** Una opción natural es retomar [[Bloque H - Computer|Computer H1]] (diseño cerrado, implementación pausada), previa revisión específica y aprobación del usuario. Publicación automática y auto-update periódico siguen en el horizonte lejano. La limitación externa `UI_RENDER` no bloquea la evolución de LoomLCI.
