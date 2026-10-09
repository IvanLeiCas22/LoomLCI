---
tipo: operacion
estado: vigente
actualizado: 2026-10-09
---
# Diagnósticos

La capacidad RB-06 agrega registros locales **opt-in** para Host y Launcher. No se habilitan por defecto; no son telemetría remota ni un sistema de auditoría imposible de alterar. La habilitación y deshabilitación son independientes de las actualizaciones del Host.

## CLI

```powershell
$launcher = "$env:LOCALAPPDATA\Programs\LoomLCI\LoomLCI.Launcher.exe"
& $launcher diagnostics status
& $launcher diagnostics enable
& $launcher diagnostics tail 20
& $launcher diagnostics disable
& $launcher diagnostics clear --confirm
```

**Precaución:** `enable` comienza a generar registros del uso posterior; `clear --confirm` descarta los logs existentes y sólo puede funcionar cuando los diagnósticos están desactivados. No habilitar ni borrar durante pruebas no solicitadas.

## Almacenamiento y privacidad

- Config: `%LOCALAPPDATA%\LoomLCI\deployment\config\diagnostics.json`.
- Logs: `%LOCALAPPDATA%\LoomLCI\deployment\logs\diagnostics`, JSONL local con prefijos `host-` y `launcher-`.
- Config faltante, inválida o incompatible → **desactivado** (fail-closed).
- Allowlist: tipo y código de operación estables, resultado, tiempo y contador. No incluir rutas, textos arbitrarios, argumentos, stdout, stderr, tracebacks, scripts o secretos.
- Cuotas: registros hasta **2 KiB**, cada archivo hasta **4 MiB**, retención **7 días**, total hasta **32 MiB** (por componente hasta 16 MiB).
- Cola desacoplada: drops bajo carga o fallos de disco no deben romper el trabajo; el último evento puede perderse si el proceso termina abruptamente.

El comportamiento definitivo se implementa en `src/LoomLCI.Core/Observability/`, `src/LoomLCI.Host/LoomDiagnosticsService.cs` y `src/LoomLCI.Launcher/LauncherApplication.cs`.

**Evidencias:** [[RB-06 - Observabilidad durable y reconciliacion documental]], [[RB-06 - Release 11 desplegada]].
