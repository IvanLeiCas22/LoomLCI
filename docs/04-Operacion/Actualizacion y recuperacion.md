---
tipo: operacion
estado: vigente
actualizado: 2026-10-09
---
# Actualización y recuperación

**Regla de seguridad:** una actualización que detiene el Host productivo requiere un **supervisor externo a LoomLCI**. Que un proceso sea `Independent` respecto de WorkSession **no basta**: puede seguir dentro del Job/árbol del Host que se apaga. El flujo supervisado está implementado en `scripts/Invoke-SafeCutover.ps1` y documentado en [[RB-04 - Supervisor externo y cutover seguro]].

## Flujo aprobado para nuevas releases

1. Investigar y aprobar cambios; revisar `git status` y código actual.
2. Ejecutar pruebas pertinentes y gate de contrato; producir paquete/instalador versionado.
3. Registrar manifest y hashes, asegurar secuencia de destino mayor que la instalada.
4. Desde un supervisor **externo validado**, ejecutar preflight read-only con identidad, version, secuencia, SHA-256, rutas, locks y estado.
5. Antes de detener, hacer backup durable verificable; ejecutar el cutover sólo bajo ese supervisor y comprobar logs/resultados/health.
6. Verificar integridad post-cutover, datos y accesos directos, posibilidad de rollback y smoke MCP desde ChatGPT.
7. Documentar evidencia de release en [[Indice historico]] y actualizar únicamente [[Estado del sistema]] como fuente del estado productivo.

## Comandos y scripts disponibles

```powershell
$launcher = "$env:LOCALAPPDATA\Programs\LoomLCI\LoomLCI.Launcher.exe"
& $launcher status
& $launcher update check
```

El Launcher implementa también `update apply` y `rollback`. **No ejecutarlos desde el propio Host productivo**: la disponibilidad de esos comandos no sustituye al supervisor externo. El script `Invoke-SafeCutover.ps1` ofrece `-Mode Preflight` o `-Mode Execute` con parámetros obligatorios de integridad y confirmación externa; no invocar `Execute` sin una nueva aprobación y preflight exitoso.

Los paquetes se generan con `scripts/Build-PortablePackage.ps1` y `scripts/Build-WindowsInstaller.ps1`. El launcher actualiza Host; cuando hace falta actualizar el propio Launcher, se realiza con instalador/supervisor apropiado. La publicación de GitHub Releases y la actualización completamente automática siguen diferidas: [[Backlog]].

## Recuperación y desinstalación

- Confirmar `previousVersion` y presencia de archivos antes de decidir rollback.
- Evitar «reinstalar encima» o limpiar configuraciones sin inventario y backup.
- Desinstalar **no** equivale a purgar datos: preservar por defecto la configuración/secretos y la propiedad de archivos ajenos, según [[RB-05 - Desinstalación segura y preservación de datos]].
- `purge-data --confirm-erase-deployment` es destructivo y requiere intención expresa.
- Diagnóstico local: [[Diagnosticos]]. Puede estar desactivado y aun así el Host funcionar normalmente.

**Historial verificable:** [[RB-04 - Release 7 desplegada]], [[RB-03 - Actualización local secuencia 8]], [[RB-05 - Release 10 desplegada]], [[RB-06 - Release 11 desplegada]], [[Auto-update firmado]].
