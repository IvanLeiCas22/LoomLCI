---
tipo: plan_tecnico
proyecto: LoomLCI
bloque: DX-01
fecha: 2026-10-09
estado: cerrado_validado_y_desplegado_seq9
prioridad: maxima_previa_RB05_RB06
---

# DX-01 — Optimización del ciclo de desarrollo, tests y empaquetado

> **DX-01 IMPLEMENTADO, VALIDADO Y PROBADO EN DESPLIEGUE SUPERVISADO REAL.** Decisión 2026-10-09: optimizar el workflow antes de RB-05 y RB-06; RB-04 sigue cerrado. El desarrollo de DX-01 inicialmente no necesitó cutover; posteriormente se preparó un cambio inocuo de comentario en Host y el usuario realizó una actualización de prueba seq8→seq9 desde PowerShell Windows externo. Runtime actual: **sequence 9, healthy/ready**, rollback seq8. **La ejecución por el usuario fue un experimento único para medir tiempos; NO se adoptó como workflow regular. El asistente sigue ejecutando el ciclo técnico de principio a fin, con aprobación del usuario y supervisor externo al Host durante cutover.** No confundir una prevalidación rápida con la aceptación completa para producción.

## Investigación del estado real

Archivos revisados: `scripts/Build-PortablePackage.ps1`, `scripts/Build-WindowsInstaller.ps1`, `scripts/Test-LiveIsolatedTunnelE2E.ps1`, `tests/LoomLCI.IntegrationTests/McpStdioTests.cs` y [[RB-03 - Actualización local secuencia 8]].

1. `Build-WindowsInstaller.ps1` siempre invoca `Build-PortablePackage.ps1`; este siempre ejecuta `dotnet publish` para Host y Launcher, genera ZIP portable con `Compress-Archive` y, salvo `-SkipPortableTests`, ejecuta **53 tests Launcher y 21 tests MCP contra el Host publicado**. Luego Inno Setup vuelve a comprimir la carpeta portable en un setup independiente, sin consumir el ZIP.
2. Durante RB-03, dos empaquetados fallaron *tardíamente* por `StdioAdapterExposesSelfDescribingToolContracts` y `PluginContractSnapshotMatchesAdvertisedTools`: errores de la descripción de `work_close` y del snapshot `plugin/contract/mcp-contract.json`. Esas dos pruebas pueden ejecutarse focalizadamente **antes de construir un instalador completo**.
3. `McpStdioTests.GetHostDll` acepta `LOOMLCI_TEST_HOST_DLL` para seleccionar explícitamente un Host compilado; hay que verificar que realmente pertenece al código actual antes de utilizarlo (evitar falsos verdes de artefactos viejos).
4. `Test-LiveIsolatedTunnelE2E.ps1` genera **dos instaladores completos** (dos `Build-WindowsInstaller.ps1` con `-SkipPortableTests`) para probar el cutover/rollback. Debe reservarse para cambios en Launcher, setup, update o supervisor, o para gates programados; no es razonable repetirlo ante toda modificación pequeña de Host.
5. El cutover productivo supervisado `scripts/Invoke-SafeCutover.ps1` NO es cuello de botella y **no debe simplificarse ni ejecutarse como hijo del Host que detiene**.

### Tiempos y evidencia

- `Test-LiveIsolatedTunnelE2E.ps1` anterior: 2026-10-09 02:46:50Z–02:49:01Z, ~131 s, incluyendo dos builds, E2E, rollback y uninstall aislado.
- Compilación/paquete final: ~163 s desde creación hasta artefacto; **21 IntegrationTests: 1m43s (103 s)** y **Inno Setup: 29,047 s** (log de `build.log`). El resto (~31 s) incluye .NET publish, ZIP, hashes y demás IO. Estas categorías son aproximadas.
- Dos intentos de paquete rechazados: ~37 y ~83 s. Las pruebas focalizadas MCP que se repitieron después pasaban en ~11 s.
- Cutover productivo: backup/stop/installer/start/health aproximadamente **9 s** (log de `cutover.log`), excluyendo smoke, análisis y documentación.
- Tamaños: ZIP portable **77,57 MiB**; setup **57,01 MiB**; Host publicado ~118,62 MiB en 244 archivos; Launcher single-file ~70,25 MiB. No tomar tiempo de compresión como tiempo de compilación de C#.
- Suite completa de código RB-03 fue ~433/433; el tiempo de pruebas completas no se debe sumar sin criterio al de package, porque en este episodio hubo ejecuciones repetidas.

## Diseño e implementación

### DX-01.A — Fail-fast y perfil de validación rápida

- Agregar un comando/script de prevalidación claramente distinto de la *aceptación para producción*.
- Compilar en `Release` y ejecutar primero `StdioAdapterExposesSelfDescribingToolContracts` + `PluginContractSnapshotMatchesAdvertisedTools` contra un Host de **esa compilación**, con `LOOMLCI_TEST_HOST_DLL` explícito y comprobado.
- Añadir filtros de tests afectados por el cambio; un resultado `fast pass` **jamás** autoriza un cutover por sí solo.
- Reportar duraciones y salida/estado por etapa; fallar al primer error con código no cero. No ocultar flakiness mediante reintentos automáticos de tests fallidos.

### DX-01.B — Evitar empaquetado redundante

- Opción **opt-in** para omitir `Compress-Archive` del portable si sólo se necesita el instalador Inno. Conservar comportamiento histórico por defecto para quien necesite ZIP y mantener `package.json`, licencias, checks de assets y SHA-256/metadata del setup.
- `Test-LiveIsolatedTunnelE2E.ps1` podrá omitir ZIP en sus dos builds, porque consume el setup y la carpeta portable. Verificar que no cambia su teardown.
- Estudiar reutilización de **artefactos idénticos y verificados**; nunca reutilizar una carpeta mutable por sólo coincidencia de nombre/versión. Requiere invariantes de contenido, hashes y lock. Postergar cache compleja si DX-01.A/B cubren el cuello de botella.

### DX-01.C — Gates proporcionados por tipo de cambio

| Caso | Pruebas de iteración | Gate previo a producción |
| --- | --- | --- |
| Código Host/Core/MCP | Compilación + tests focalizados + contratos MCP rápidos | Suite total + Host publicado contra integración + setup verificado + smoke |
| Launcher / updater / Inno / supervisor | Tests focalizados Launcher + contratos + E2E aislado genuino | Suite total + E2E cutover/rollback + setup + supervisor externo |
| Sólo documentación | No ejecutar full build sin necesidad | Ningún cutover por cambios sólo documentales |

La secuencia monotónica y política de distribución siguen siendo obligatorias. **Contexto al diseñar DX-01:** runtime seq8, próxima secuencia >=9. **Estado actual tras el ensayo:** seq9, próxima secuencia **>=10**.

### DX-01.D — Medición y regresiones del workflow

- Cronometrar explícitamente build Host, build Launcher, contract-fast, Launcher tests, integración full, ZIP (si aplica), Inno y preflight; distinguir wall time de duración xUnit e informar resumen legible.
- Añadir smoke/test automatizado de modo con/sin ZIP, manifest y metadata inalterados, falla temprana del snapshot sin setup “apto”, preservación de `-SkipPortableTests` sólo para entornos permitidos, y compatibilidad de rutas de builds portables existentes.
- Medir A/B en la misma máquina, sin modificar la instalación productiva; reportar números reales y no estimar ahorros como garantías.

## Límites de seguridad

No modificar `Invoke-SafeCutover.ps1`, rollback ni journal para ganar velocidad. No omitir la integración contra Host **publicado** ni tests Release completos en un deployment final. El diseño original se implementó en staging aislado, sin modificar entonces el runtime seq8; **más tarde** se desplegó un cambio inocuo de prueba con supervisión externa (seq9). El cutover requiere validación final, backup y supervisión externa al Host, no necesariamente una acción humana manual.

## Criterios de aceptación

- [x] Prevalidación rápida falla antes del empaquetado ante descripción/snapshot desactualizados; inyección controlada y restauración exacta verificadas.
- [x] `Build-WindowsInstaller` genera setup verificable sin ZIP con `-SkipPortableZip` explícito; `Build-PortablePackage` mantiene ZIP por defecto y acepta `-SkipZip`. Ambos modos validados con binarios reales.
- [x] Etapas de preflight, publicación, tests, ZIP e Inno cronometradas con `DX01_STAGE`; preservados tests de Host publicado, metadata, SHA-256 y preflight supervisor. Los switches heredados `-SkipTests`/`-SkipPortableTests` continúan siendo **sólo para validación aislada**, no aceptación de producción.
- [x] Suite Release 433/433, empaquetado nuevo con 53/53 Launcher y 21/21 IntegrationTests contra Host publicado, más E2E genuino aislado con cutover/rollback y shortcuts intactos. **Al cerrar la implementación inicial**, el producto seguía en seq8; posteriormente se actualizó a seq9 para el ensayo.
- [x] Documentación y mediciones de ambos modos. Código de DX-01 commiteado y validado; el cutover **opcional** del experimento posterior completó seq9 con rollback seq8.

## Comandos técnicos de referencia y gates

Estos comandos documentan el procedimiento técnico. **Los ejecuta normalmente el asistente mediante LoomLCI y, para un cutover que detenga el propio Host, mediante IvanSpace/supervisor externo. No constituyen instrucciones permanentes para que el usuario copie comandos.** Desde la raíz del repositorio, en Windows PowerShell:

```powershell
# Durante el desarrollo: gate corto, NO autoriza despliegue
.\scripts\Test-FastContracts.ps1

# Pruebas relevantes según los archivos afectados (ejemplo)
dotnet test tests\LoomLCI.Core.Tests\LoomLCI.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~LifetimeTests'

# Antes de aprobar una instalación: suite Release completa
dotnet test LoomLCI.slnx -c Release --no-restore -m:1

# Empaquetado completo para un instalador local, SIN ZIP redundante
.\scripts\Build-WindowsInstaller.ps1 -SkipPortableZip -Version 'VERSION_UNICA' -Sequence 10

# Portable clásico para distribución (con ZIP; mismo comportamiento de siempre)
.\scripts\Build-PortablePackage.ps1 -Version 'VERSION_UNICA' -Sequence 10
```

**Importante:** las secuencias 10 de arriba son ilustrativas (seq9 ya instalada). Antes de distribuir, se debe confirmar `highestSequence` y usar un identificador mayor que el instalado, nunca reutilizar una secuencia publicada. Construir en staging controlado y ejecutar el cutover real sólo desde un supervisor externo con validaciones de preflight, SHA-256 y backup. **No usar** `-SkipTests` ni `-SkipPortableTests` como evidencia de aceptación de un instalador de producción; son opciones heredadas para ensayos acotados. La prueba rápida no equivale al gate final.

## Resultados de validación DX-01 (2026-10-09)

| Verificación | Resultado |
| --- | --- |
| `Test-FastContracts.ps1` | PASS: dos tests de contrato MCP sobre Host Release compilado desde fuente; 22,5 s en primera corrida, 15,8 s con build incremental |
| Fail-fast negativo | PASS: snapshot MCP intencionalmente desactualizado, fallo esperado **15,6 s** antes de publish/ZIP/setup; snapshot restaurado byte a byte y Git lo mostró sin cambio |
| Suite Release completa | **433/433 PASS**, con `21/21` IntegrationTests |
| Installer `-SkipPortableZip` sin saltar tests | PASS, **168,5 s**, Launcher **53/53**, published Host Integration **21/21**, manifest/version/sequence/rutas/SHA-256 correctos y ZIP ausente |
| Portable clásico con ZIP | PASS, binarios y manifest correctos, ZIP verificado SHA-256; compresión medida **14,9 s** en esta máquina |
| `Test-LiveIsolatedTunnelE2E.ps1` | PASS **104,9 s**: dos instaladores sin ZIP, conexión a túnel de test, cutover supervisado, rollback, uninstall aislado, accesos directos conservados, exit 0 |

Medición de las etapas en el empaquetado completo sin ZIP: `portable-fast-preflight=16,0 s`, `publish-host=5,5 s`, `publish-launcher=3,6 s`, `launcher-tests=4,6 s`, `published-host-integration=107,7 s`, `inno-setup=30,3 s`. Son tiempos de pared de etapas y se superponen con el contador total `installer-portable`; **no sumarlos indiscriminadamente**.

**Conclusión:** el ahorro directo del ZIP es aproximadamente 15 s cuando habría que generarlo; la prevalidación agrega ~16 s a un build final exitoso, por lo que **DX-01 no reduce drásticamente un empaquetado que ya pasaría**, sino que evita reempaquetar cuando fallan contratos (como ocurrió durante seq8). El E2E aislado pasó de ~132 s históricos a ~105 s en esta corrida, pero son mediciones distintas, no un benchmark controlado del mismo run. Los 21 tests MCP contra Host publicado siguen siendo el costo dominante (~108 s) y no se eliminaron.

La mejora de caché/reutilización de artefactos quedó explícitamente **diferida**: aplicarla sin fingerprints completos podría permitir falsos verdes. Esta medición describe el estado **anterior** al cutover manual seq9.

## Experimento excepcional — preparación y despliegue ejecutados por el usuario (2026-10-09)

**Finalidad:** comparar la duración real de los comandos con el tiempo total de un ciclo de trabajo del agente. **No fue una decisión de delegar operaciones al usuario para futuras versiones.** El workflow principal se ratifica al final de esta nota.

- Cambio inocuo: comentario en `src/LoomLCI.Host/Program.cs`, commit **`5f5cea9`**, sin efecto funcional. Versión construida **`0.1.0-dev-5f5cea9ecbf4`**, sequence **9**.
- Primer intento del usuario: `dotnet test LoomLCI.slnx` concluyó con exit 0 (sin salida por verbosity quiet); la política de ejecución de PowerShell bloqueó los `.ps1`. El marcador `PREPARACION_OK` original fue falso porque estaba fuera de un bloque `try/catch`; no se generó instalador. Se corrigió con `Set-ExecutionPolicy -Scope Process RemoteSigned`, `try/catch`, comprobaciones de artefactos, hashes y preflight. La ejecución frustrada había consumido **126,3 s** e **impide** interpretar el caso total como una sola corrida limpia.
- Segunda preparación (válida): **167,4 segundos**; prevalidación MCP, `publish` Host y Launcher, **53/53** Launcher, **21/21** MCP integración contra Host publicado (**~109,9 s** de wall time), Inno Setup (**~31,1 s**), hash/metadata y preflight read-only (`Preflight OK` seq8→seq9).
- Segundo paso, desde **PowerShell externo abierto por el usuario**, no un proceso hijo del Host: **19,6 segundos**. Salida `CUTOVER_OK` y `DESPLIEGUE_OK`; backup preservado en `%LOCALAPPDATA%\LoomLCI-CutoverBackups\20261009T041835Z-c1906ace1d85443cae40e5e6c8488ae0`; secuencia 8 conservada como rollback. El mismo Secure MCP Tunnel quedó operativo con `process_running=True`, `healthy=True`, `ready=True`, version seq9. Verificación independiente adicional del `Launcher status` desde LoomLCI MCP productivo con exit 0.
- **Total del camino válido de preparación+despliegue: 187,0 s = 3 min 7 s.** Incluyendo el intento previo bloqueado, fue más tiempo. La comparación con ~21 minutos del proceso anterior **no es A/B**: aquél incluía investigación, cambios y correcciones, pruebas adicionales, orquestación y documentación. El experimento demuestra que la secuencia de compilación, pruebas, empaquetado y actualización puede automatizarse y ejecutarse de forma autocontenida, sin sucesivas interacciones del agente. **Eso no obliga a que la ejecute el usuario**: el asistente debe conservar su responsabilidad operativa y minimizar llamadas redundantes.
- Estado final: **seq9 activo, seq8 rollback, ninguna GitHub Release/publicación pública**. Próxima secuencia de instalación >=**10**; no reutilizar seq9.

## Workflow vigente, ratificado después de DX-01

1. **Asistente (LoomLCI MCP productivo como herramienta principal):** examina el código actual, investiga, analiza y propone; espera la aprobación del usuario cuando corresponda.
2. **Asistente:** implementa, compila, ejecuta pruebas proporcionales y gates finales, empaqueta y verifica hashes/metadata, actualiza documentación Obsidian y realiza commits.
3. **Asistente + supervisor externo al Host:** cuando se aprueba un despliegue productivo, realiza el preflight, el backup, el cutover y smoke; **IvanSpace** es el fallback/supervisor apropiado para reiniciar LoomLCI sin autointerrumpirse. La necesidad de un supervisor externo es técnica, **no** un requisito de ejecución por el usuario.
4. **Usuario:** aprueba las decisiones y sólo ejecuta comandos excepcionalmente cuando la operación manual sea necesaria y acordada. No existe delegación habitual del build, testing o deployment.
5. **Eficiencia:** agrupar operaciones, no repetir tests innecesariamente durante iteraciones y preservar la batería completa cuando corresponda antes de producción. Las mediciones de ~3 min 7 s del camino exitoso describen la **ejecución de comandos**; los tiempos más largos del asistente incluyen revisión, implementación y razonamiento, no exclusivamente compilación.

**Próximo bloque:** investigación/análisis de [[Roadmap de robustez post-auditoría|RB-05]] y luego RB-06. Para futuras actualizaciones, priorizar un **script autocontenido ejecutado por el asistente desde un supervisor externo** (cuando corresponda), con verificación de códigos de salida y preflight; evitar turnos redundantes sin debilitar seguridad.
