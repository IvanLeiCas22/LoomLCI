---
tipo: implementacion
proyecto: LoomLCI
fecha: 2026-10-09
bloque: RB-06
estado: cerrado_end_to_end_seq11
---

# RB-06 — Observabilidad durable y reconciliación documental

## Estado

**CERRADO end-to-end, desplegado productivamente en secuencia 11** (`0.1.0-dev-fc4373bdff73`) el 2026-10-09 mediante IvanSpace externo, con `CUTOVER_OK`, backup, hashes e integridad de datos, Launcher healthy/ready y smoke MCP Python fresh-agent (16 capacidades). Rollback seq10 conservado. **La función instalada permanece desactivada por defecto: cero archivos de diagnóstico y config opt-in ausente**, sin habilitarla en productivo. Evidencia completa en [[RB-06 - Release 11 desplegada]]. Próxima secuencia >=12.

**Validación final de fuente aprobada:** suite Release completa **448/448 (exit 0)**; Core **155/155**, Launcher **62/62**, Windows **196/196**, MCP **6/6**, PDF Worker **6/6**, integración **23/23**, incluidas dos pruebas nuevas con **Host real por MCP STDIO** en modos activado/desactivado. Se conserva el contrato de MCP: **25 herramientas**, sin endpoints/telemetría remotos añadidos; el plugin privado no cambia.

## Diseño ejecutado

- `LoomEventBus` preserva el canal existente de 1.024 eventos `DropOldest`; un observador **secundario**, independiente y best-effort no roba eventos a `TryRead`/`ReadAllAsync` ni hace operaciones de disco en el hilo productor. Las excepciones del observador se aíslan del negocio.
- `LoomDiagnosticsService` (Host) filtra eventos mediante `DiagnosticEventProjector` y los coloca en una cola independiente de **512 elementos**. El consumidor escribe en el disco fuera del hilo MCP; ante saturación incrementa el contador y emite `EventsDropped` en una oportunidad posterior mientras está habilitado. El proceso no falla por problemas del sink.
- `DiagnosticEventProjector` es una **allowlist de tipos y campos**, no una serialización de `LoomEvent.Payload`: guarda fecha/hora, componente, tipo, operación estable, éxito/fallo, código de error estable, duración (ms) o contador. Se ignoran campos `path`, `message`, `traceback`, scripts, claves, stdout/stderr y payloads arbitrarios. Un símbolo no validado se descarta. `PythonExecutionException` distingue excepciones Python ordinarias (el transporte MCP puede completarse exitosamente).
- `DiagnosticsLog` está compartido por Core/Host y Launcher mediante inclusión del archivo fuente, sin dependencia adicional. El Launcher registra comandos mediante etiquetas fijas, resultado y fases de journal `UpdateStage` **después** de cada escritura exitosa; no guarda argumento completo, configuración, contenido de secretos o rutas.
- La variable `LOOMLCI_DATA_ROOT` se propaga desde Launcher a tunnel-client para que el Host utilice la misma raíz, incluida la operación con rutas aisladas. Como fallback el Host usa `%LOCALAPPDATA%\LoomLCI\deployment`.
- La lectura de `config/diagnostics.json` es fail-closed: no existe, es inválida o su schema no es compatible → **desactivado**. Los cambios se observan al procesar eventos nuevos sin reiniciar Host. Sin opt-in no se genera ningún archivo de log.

## Comandos, almacenamiento y límites

**Disponible desde el Launcher productivo secuencia 11** (desactivado hasta `diagnostics enable`):

```text
LoomLCI.Launcher.exe diagnostics status
LoomLCI.Launcher.exe diagnostics enable
LoomLCI.Launcher.exe diagnostics disable
LoomLCI.Launcher.exe diagnostics tail [1..100]
LoomLCI.Launcher.exe diagnostics clear --confirm
```

- Config opt-in: `%LOCALAPPDATA%\LoomLCI\deployment\config\diagnostics.json`, separado de `machine.json`.
- Archivos: `%LOCALAPPDATA%\LoomLCI\deployment\logs\diagnostics\{host|launcher}-YYYYMMDD-PID-NNNN.jsonl`.
- JSONL UTF-8 sin BOM, registros individuales de máximo **2 KiB**, archivo de máximo **4 MiB**, almacenamiento agregado de hasta **32 MiB** (**16 MiB por componente**) y **7 días** de retención, sujetos a limpieza en cada escritura del componente. Las escrituras realizan apertura/cierre del archivo, pero una interrupción abrupta puede perder o truncar la última línea.
- `tail` ofrece hasta **100 líneas válidas**; omite líneas corruptas o incompletas; `status` indica estado de opt-in, cantidad y tamaño, cuotas.
- `clear --confirm` sólo funciona estando **desactivado**. No borra archivos arbitrarios; únicamente los `*.jsonl` de nombres `host-`/`launcher-` sin atributo reparse-point. Los logs son locales, no son enviados fuera de la PC.
- Escritura y poda son best-effort. Si no se puede escribir, el trabajo MCP y las operaciones de Launcher deben seguir funcionando. Las marcas de pérdida sólo son observables cuando posteriormente llega otro evento; **no se garantiza el registro del último evento perdido antes de un apagado brusco**.
- Se heredan los permisos NTFS del directorio local del usuario; no se introduce servicio administrador ni acceso desde un usuario distinto. Se recomienda comprobar ACL de la carpeta en validaciones de instalación posteriores.

## Pruebas y límites de aceptación

- Core: filtros de privacidad, trazas Python excluidas, símbolos arbitrarios rechazados; observador secundario no consume el bus tradicional y una excepción de observador no altera `Publish`.
- Launcher: opt-in, config corrupta deshabilitada, rechazos de símbolos, rotación a 4 MiB, retención 7 días, cuota y limpieza, CLI y bloqueo de `clear` mientras esté habilitado; caso de update real simulado valida `UpdateStage` hasta `Committed`.
- Integración MCP STDIO: Host real ejecutado con data root temporal, sin logs al estar desactivado, con registros de `WorkSessionCreated` al habilitar; no serializa etiquetas libres ni contamina stdout.
- **Validado end-to-end:** suite completa Release fuente **448/448** (exit 0), build secuencia 11 (`RB06_SEQ11_PACKAGE_OK`), 62/62 Launcher y 23/23 integración MCP contra Host publicado; prueba `diagnostics` aislada PASS, preflight y cutover IvanSpace `CUTOVER_OK`, Launcher productivo healthy/ready e integridad posterior `RB06_SEQ11_POSTCUTOVER_INTEGRITY_PASS`. Smoke ChatGPT MCP Python de 16 capacidades exitoso. **No se habilitaron diagnósticos productivos** ni se ejecutó un uninstall/purge. Ver [[RB-06 - Release 11 desplegada]].

## Riesgos conocidos y decisiones

- Es **diagnóstico local opt-in** y limitado, no un sistema de auditoría a prueba de manipulaciones ni de pérdidas; un usuario con permisos locales puede editar logs. Los archivos finales incompletos se ignoran en `tail`.
- El registro no contiene texto íntegro de errores ni rutas, por privacidad; para investigar un problema complejo puede requerirse reproducirlo con herramientas de desarrollo.
- La cola auxiliar descarta eventos cuando se satura, emitiendo un contador posteriormente; no bloquea procesos de usuario.
- No recopila telemetría remota ni introduce herramientas MCP públicas. Si se desea observabilidad externa, será una decisión posterior e independiente.
- **Cierre de release:** integridad post-instalación, pruebas aisladas de registro opt-in, funcionamiento con el túnel productivo (smoke MCP), `machine.json`, secretos, directorios y accesos directos de RB-05 preservados. No se hizo una activación productiva de diagnósticos: permanece una elección posterior del usuario. Ver [[RB-06 - Release 11 desplegada]].

Referencias: [[Roadmap de robustez post-auditoría]], [[RB-05 - Release 10 desplegada]], [[DX-01 - Optimizacion workflow de desarrollo y despliegue]], [[Preguntas abiertas]], [[Decisiones]].
