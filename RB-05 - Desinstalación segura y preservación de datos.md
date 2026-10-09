---
tipo: implementacion
proyecto: LoomLCI
fecha: 2026-10-09
bloque: RB-05
estado: validado_en_fuente_y_e2e_aislado_pendiente_despliegue
---

# RB-05 — Desinstalación segura y preservación de datos

## Estado

Implementado en fuente y comprobado en una instalación **aislada y real** de Inno Setup 7.1.0; **no desplegado todavía sobre el Launcher productivo seq9**, que continúa usando la política heredada. Se requiere aprobación independiente antes del siguiente cutover con **sequence >=10**. Nunca ejecutar el uninstaller productivo antiguo para probar este bloque.

## Hallazgo original y migración

La regla `Type: filesandordirs; Name: "{#LoomRoot}"` bajo `[UninstallDelete]` eliminaba sin discriminar el directorio global `%LOCALAPPDATA%\LoomLCI`, incluidos `deployment`, credenciales, `http-test`, entornos Python, paquetes y cachés.

El instalador corregido elimina esa regla, conserva solamente su limpieza dentro de `{app}` (versiones, tools y Launcher) y fija `UninstallLogMode=overwrite`. Esto último es indispensable: con `append` una instalación actualizada hereda instrucciones destructivas de uninstall antiguas. En actualizaciones **compatibles de AppId, arquitectura y modo de instalación**, el log se reemplaza. Si se cambia el modo de arquitectura, Inno puede crear un segundo `unins001.exe`; el ensayo requiere exactamente un uninstall ejecutable y falla de forma segura si detecta ambigüedad. Los archivos antiguos no registrados en el log nuevo pueden permanecer (preservación conservadora).

La precondición `InitializeUninstall` ejecuta el Launcher con roots aislados para detener el runtime correspondiente y limpiar sólo shortcuts propios. Si `machine.json` está presente pero falta el Launcher o falla `stop`, aborta uninstall antes de borrar; se ensayó exit **1** y archivos intactos. Si no existe `machine.json` (instalación parcial o purga previa), no intenta detener un runtime inexistente.

## Propiedad de recursos

- `ShortcutCreator` emite `{app}\.loomlci-shortcuts.json` con ubicación, destino y hashes SHA-256. Sólo adopta enlaces preexistentes que ya apuntan al Launcher exacto y usan `start --pause` / `stop --pause`. No sobrescribe enlaces con el mismo nombre de otra instalación. `uninstall-shortcuts` elimina sólo enlaces que conservan destino, argumentos y hash; sin evidencia de propiedad los deja intactos.
- `SetupService` crea el marcador `deployment\.loomlci-data-owner.json` asociado a `InstallRoot` y `DataRoot`; rechaza conflicto de marcadores preexistentes.
- `purge-data --confirm-erase-deployment` es una operación **separada, irreversible y voluntaria**. Sólo admite `DataRoot` terminado en `deployment` bajo LocalAppData, exige marcador propio y no acepta reparse points/junctions en antecesores ni dentro del árbol. Detiene antes el runtime. **Nunca purga** el directorio compartido padre, Python, `http-test`, `assets`, `packages` o `runtimes`.
- La purga debe invocarse **antes de desinstalar** mientras el Launcher está disponible. No hay opción implícita en el instalador.

## Evidencia de pruebas 2026-10-09

`scripts/Test-RB05-IsolatedUninstall.ps1` compila un *installer legacy* peligroso y el instalador corregido auténtico, con AppId/roots de tipo TEMP únicos, empleando exclusivamente el túnel de pruebas HTTP separado del productivo (no imprime la credencial). Aserciones completadas:
1. Instalación legacy y actualización Inno genuina con un solo uninstall log.
2. Stop fallido con `machine.json` corrupto -> uninstall bloqueado, exit 1, sin borrado.
3. Stop recuperado -> uninstall exit 0, datos `deployment` y `http-test` intactos; shortcut hashes productivos sin cambios.
4. Reinstalación exit 0 sobre deployment conservado.
5. Purga explícita exit 0, elimina únicamente deployment, conserva datos vecinos.
6. Uninstall posterior a purga exit 0 y persistencia de datos vecinos. Marcador final: `RB05_GENUINE_E2E_PASS`.

**Gate final (repetido): 437/437 suite Release completa, exit 0**, con 21/21 integración MCP. Pruebas unitarias de Launcher: **57/57**; Windows tests: **196/196**. La primera ejecución paralela de `dotnet test LoomLCI.slnx -c Release --no-restore` registró 1 fallo en `PostExitExpiryKillsIndependentDescendantsAndDeletesSpool` (ajeno a RB-05); la misma prueba aislada pasó **1/1**, seguida de la batería Windows completa **196/196**. Los otros grupos en la primera ejecución pasaron: Core 151/151, MCP 6/6, PDF 6/6 e Integration 21/21. Comprobar el resultado de la nueva suite serial antes de declarar gate final. El build Launcher Release y el builder Inno completos salieron con exit 0.

## Alcance y precauciones

El uninstall convencional conserva los secretos por diseño: no revoca la clave ni destruye el túnel remoto o la conexión ChatGPT. Los directorios de binarios `{app}\versions` y `{app}\tools` siguen considerados instalación gestionada y se limpian recursivamente, **no** son almacenamiento de usuario. No guardar datos ajenos en esas carpetas; una futura política de inventario binario exacto podría endurecerlo.

El marcador de propiedad evita que instalaciones nuevas reclamen un deployment que ya tenga marcador de otra, pero no permite demostrar por sí mismo que ningún portable histórico sin marcador comparta esos datos. Ante coexistencia o roots no exclusivos, no ejecutar purga sin inspección previa. La desinstalación estándar siempre preserva el árbol de datos. La comprobación antijunction es preventiva, no un mecanismo resistente a modificación adversarial concurrente del filesystem (TOCTOU).

El histórico en [[Instalador Windows]] describe el uninstall **anterior a RB-05**; la fuente actual queda registrada aquí. Para producción: backup, preflight, installer versión/sec nuevos, supervisor externo y smoke post-cutover. No editar/probar los roots productivos con uninstaller antes de una aprobación posterior.

Ver [[Roadmap de robustez post-auditoría]], [[DX-01 - Optimizacion workflow de desarrollo y despliegue]].
