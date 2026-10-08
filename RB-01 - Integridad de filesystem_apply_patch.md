---
tipo: implementation
proyecto: LoomLCI
fecha: 2026-10-08
bloque: RB-01
estado: codigo_validado_pendiente_despliegue
---

# RB-01 — Integridad de filesystem_apply_patch

> **Implementado y validado en el repositorio; NO instalado en el runtime productivo.** Sigue activa Release 6 `0.1.0-dev-6b1566a` (sequence 6). RB-01 estará cerrado end-to-end sólo tras un despliegue seguro y smoke de consumidor. Ver [[Roadmap de robustez post-auditoría]].

## Alcance y motivos

La auditoría identificó corrupción de bytes al tratar UTF-8 inválido mediante decodificación permisiva y aceptación de `replace` con salida >16 MiB. La inspección encontró sobrescritura directa de destino antes de registrar undo, además de rollback best-effort que silenciaba restauraciones fallidas. Se confirmó mediante reproducción aislada, sin tocar archivos del proyecto.

La implementación mantiene `filesystem_apply_patch` con las mismas operaciones `write/replace/delete/move`, parámetros y resultados exitosos públicos. También mantiene la exposición del bridge privado `loom.fs.apply_patch()` sin cambio de esquema ni nuevo tool MCP.

## Implementación

Archivo principal: `src/LoomLCI.Windows/Filesystem/WindowsFilesystemProvider.cs`.

1. **Decodificación estricta y BOM:** UTF-8 sin BOM o UTF-8/UTF-16 LE/BE/UTF-32 LE/BE con BOM; errores de bytes o caracteres Unicode inválidos se rechazan como `unsupported`, sin normalización silenciosa. Archivos con NUL se rechazan como texto. El contenido válido conserva codificación, BOM y saltos de línea existentes si el reemplazo no los modifica.
2. **Control de tamaño final:** límite de **16 MiB de bytes codificados**, incluyendo BOM, tanto para `write` como `replace`; se valida antes de mutar. Para `replace` se calcula de forma acotada el tamaño proyectado antes de construir una cadena expandida, evitando asignaciones potencialmente enormes.
3. **Staging + recuperación:** nuevo contenido a temporal hermano `.loomlci-*.tmp`, flush al disco; reemplazo de archivo existente con `File.Replace` y copia de seguridad `.loomlci-*.bak` preparada y verificada antes de publicar; archivo nuevo mediante `File.Move`. Se registra undo antes de publicar el reemplazo. Los temporales se limpian al terminar.
4. **Integridad concurrente:** los lotes se serializan mediante un lock compartido del provider en el proceso Host; SHA-256 compara bytes con el snapshot de validación y con el respaldo antes de publicar. Un cambio externo detectado devuelve `conflict` y conserva el contenido externo. Sigue existiendo una ventana de carrera residual frente a procesos ajenos entre la última comprobación y el rename/reemplazo.
5. **Errores de recuperación explícitos:** rollback de todas las entradas tras un fallo; si un archivo no puede restaurarse, devuelve `rollback_failed` con rutas y errores de recuperación en `LoomError.Details` y conserva respaldos disponibles. Si el lote se aplicó pero falló la limpieza, devuelve `cleanup_failed` indicando `appliedChanges` y respaldos restantes. No afirmar que un fallo reportado implique ausencia de mutaciones.

**Garantías deliberadamente fuera del alcance:** no existe journal persistente para un lote frente a corte eléctrico o terminación brutal del Host; no se garantiza aislamiento transaccional con escrituras de aplicaciones ajenas; no se garantiza restauración automática de cambios hechos por terceros entre publicación y rollback. Los temporales/backups podrían quedar ante crash duro y deben recuperarse siguiendo un procedimiento explícito. El seguimiento de incidentes de auto-update pertenece a **RB-02**.

## Validación ejecutada

- Pruebas focalizadas `FilesystemCapabilityTests`: **48/48** correctas (antes **27/27**), 21 casos adicionales.
- Suite Release completa `dotnet test LoomLCI.slnx -c Release --no-restore --verbosity quiet`: **400/400**, 0 errores y 0 omitidos.
  - Core 141, Windows 196, Launcher 30, MCP 6, PdfWorker 6, Integration 21.
- Regresiones de codificación: UTF-8 inválido, UTF-16 inválido, preservación byte a byte de UTF-8 sin BOM y UTF-8/UTF-16/UTF-32 con BOM en LE/BE según corresponde.
- Regresiones de límites: frontera exacta de 16 MiB con UTF-8 BOM, crecimiento de `replace` superior a 16 MiB, `write` sobre UTF-32 cuyo tamaño final codificado excedería 16 MiB.
- Regresiones de atributos: conservación del atributo Hidden. Regresiones de recuperación: fallo antes de publicar, cancelación, cambio externo concurrente, fallo tras publicar dos destinos, rollback de `move` binario sobre destino existente y fallo forzado del rollback con respaldo retenido.
- Regresión de concurrencia: ocho lotes independientes escribiendo el mismo destino mediante el provider.
- Comprobaciones de temporales: no quedan `.loomlci-*.tmp/.bak` cuando la operación finaliza correctamente o el rollback tiene éxito.

**Límite de la evidencia:** las pruebas se ejecutaron contra binarios Release construidos desde el repositorio. No representan todavía la versión instalada ni un smoke de ChatGPT sobre el nuevo código.

## Pendiente de cierre

- [x] Investigación, diseño y aprobación del usuario.
- [x] Implementación y regresiones.
- [x] Suite Release completa e Integration.
- [x] Commit de código y documentación (referencia en historial Git).
- [ ] Preparación del paquete y cutover supervisado **externamente** al Host, coordinados con RB-02/RB-04 por los riesgos previos del actualizador.
- [ ] Smoke productivo de `filesystem_apply_patch` y `loom.fs.apply_patch` en archivos temporales, incluido rechazo de UTF-8 inválido y control de límites.
- [ ] Declaración `CERRADO end-to-end` tras pruebas en runtime instalado y verificación del rollback.

## Próximo paso

Avanzar con investigación y análisis de **RB-02** antes de repetir el cutover. No usar el propio Host a detener como supervisor.
