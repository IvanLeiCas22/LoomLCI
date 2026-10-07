# Roadmap post-G1

> Estado: **activo para planificación.** Computer H1 queda pausado. El trabajo futuro seleccionado se concentra en Ergonomía, Producto/Deployment y Python.

## Objetivo

Mejorar LoomLCI sin abrir todavía Computer Use, priorizando cambios que mejoren primero la propia capacidad de desarrollar LoomLCI, luego su experiencia como producto instalado y finalmente la potencia del runtime Python.

## Orden acordado

### 1. Ergonomía de agente

1. `process_run` — **CERRADO end-to-end**: ejecución one-shot de comandos cortos con resultado completo en una sola llamada, incluyendo smoke directo desde ChatGPT.
2. Ergonomía de Work Plan — **CERRADO end-to-end**: `work_plan_patch` reduce el costo de milestones pequeños sin perder revision/CAS ni identidad estable. Suite 269/269, runtime healthy/ready y smoke directo desde ChatGPT con catálogo refrescado completados correctamente.

Motivo para ir primero: son cambios acotados, de bajo riesgo y se pueden dogfoodear durante todos los bloques siguientes.

### 2. Producto / Deployment

Orden preliminar:

1. mejorar UX del Launcher — **CERRADO end-to-end**: modo `--pause` opt-in para accesos directos, shortcuts de iniciar/detener, mensajes simplificados, salida UTF-8 y README portable con encoding estable;
2. [[Instalador Windows|instalador Windows convencional]] — **CERRADO end-to-end**: Inno Setup 7.1.0, instalación per-user sin admin, registro en Aplicaciones instaladas, uninstall real y reutilización de `SetupService`;
3. [[Auto-update firmado|base de auto-update con validación y rollback]] — **CERRADO end-to-end**: manifest firmado ECDSA, `sequence` anti-rollback, `update check/apply`, rollback transaccional, journal/crash recovery, limpieza `active + previous` y E2E real contra GitHub Releases;
4. [[Plugin metadata|mecanismo de generación/verificación de metadata y skill del plugin]] — **CERRADO end-to-end**: fuente canónica en repo, export real `initialize + tools/list`, snapshot de contrato, build/verifier anti-drift, neutralización del wiring Debug histórico y plugin privado actualizado por CAS/read-back a **0.5.0**. La evaluación fresh-agent histórica 0.4.0 fue 4/4 PASS; la aceptación visual específica de Python 3 queda en P3.2.

### 3. Python

Orden preliminar:

1. [[Python 1 - Paquetes administrados|paquetes de terceros administrados/versionados por LoomLCI]] — **CERRADO**: `uv` privado fijado/verificado, environments inmutables con locks+hashes, `python_packages_prepare`, binding por WorkSession, reset explícito al cambiar environment, GC protegido y deployment instalado validado;
2. [[Python 2 - Bridge privado loom|bridge privado `loom.*` entre el worker Python y capabilities de LoomLCI]] — **CERRADO end-to-end (P2.0–P2.4)**: protocolo privado v2, router modular, `loom.fs`, `loom.process`, lifecycle/ownership administrado, hardening, deployment real, consumer smoke y fresh-agent smoke completados;
3. [[Python 3 - Outputs binarios e imágenes|outputs binarios/imágenes desde Python hacia el modelo]] — **P3.0 y P3.1 CERRADOS; P3.2 implementado/deployado, fresh-agent final pendiente**. Runtime activo `0.1.0-dev-python3` healthy/ready con Python 2 como rollback; Host instalado: 25 tools, bridge v2, `loom.display_image`, sin `outputSchema`; Integration **21/21** contra DLL instalada y suite Release posterior **356/356**. Plugin privado ya está en **0.5.0** por CAS/read-back. Falta únicamente aceptar en chat nuevo el mixed image result real y confirmar routing `loom.display_image` vs `filesystem_view_image`; blobs/audio genéricos siguen diferidos.

## Horizonte lejano / post-roadmap actual

Estas mejoras quedan **documentadas pero deliberadamente no priorizadas**. No deben desplazar el bloque Python activo ni los cierres ya definidos del roadmap:

- **publicación automática de releases**: GitHub Actions dispara desde un tag/release, construye los artefactos, ejecuta validaciones y publica automáticamente setup + ZIP de update + manifest + firma; requiere resolver de forma segura el acceso a la clave privada de firma dentro de CI;
- **update automático en las PCs instaladas**: chequeo periódico o al iniciar LoomLCI y aplicación automática/semiautomática de releases firmadas, reutilizando el motor transaccional, health check y rollback ya implementados;
- cualquier automatización de este bloque debe conservar como invariantes la firma obligatoria, `sequence` anti-rollback, journal/crash recovery y rollback seguro.

Por ahora el modelo operativo aceptado sigue siendo: **publicación manual de la GitHub Release + `update check/apply` explícito en cada instalación**.

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

**[[Python 3 - Outputs binarios e imágenes|P3.2: fresh-agent final]].**

Deployment, cutover, suite Release, smoke de persistencia/errores y plugin **0.5.0** ya están completados. El único pendiente es abrir un chat nuevo con catálogo/plugin refrescados y ejecutar los dos casos de aceptación: imagen generada completamente en memoria por Python debe llegar vía `loom.display_image`; imagen local existente debe seguir usando `filesystem_view_image`. Si ambos pasan, reconciliar documentación final y cerrar Python 3 end-to-end.
