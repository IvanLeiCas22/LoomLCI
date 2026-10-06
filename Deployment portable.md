# Deployment portable

> Estado: **implementado y validado end-to-end en dos PCs Windows x64**. El mismo paquete se instaló desde cero en una notebook sin repo/IvanSpace/.NET/Python preinstalados, conectó por el mismo Secure MCP Tunnel y fue controlado desde ChatGPT. El deployment portable queda cerrado funcionalmente; sólo quedan mejoras de UX/operación no bloqueantes.

## Objetivo

Separar definitivamente:

- **repositorio de desarrollo**: código, tests y builds Debug/Release;
- **instalación de uso**: Host Release publicado, launcher, tunnel-client y configuración local por máquina.

Visual Files G1 pasa a ser el próximo bloque funcional y Computer continúa después como H1. Este bloque sigue siendo infraestructura de deployment y operación previa a ambas capabilities.

## Restricciones de diseño

- no modificar Core/Filesystem/Process/Python/Work Plan para resolver deployment;
- no sustituir el runtime actual hasta validar el nuevo end-to-end;
- no depender de IvanSpace, Visual Studio, Python del sistema ni .NET instalado;
- no ejecutar como Windows Service: LoomLCI debe permanecer en la sesión interactiva del usuario, compatible con el futuro Computer;
- no elevar a administrador por defecto;
- soportar rutas de usuario con espacios;
- separar binarios instalados de estado/config/secrets por máquina.

## Layout implementado

```text
%LOCALAPPDATA%\Programs\LoomLCI\
    LoomLCI.Launcher.exe
    versions\
        <version>\
            LoomLCI.Host.exe
            ...
    tools\
        tunnel-client.exe

%LOCALAPPDATA%\LoomLCI\deployment\
    config\
        machine.json
    secrets\
        runtime-api-key.txt
    tunnel-profiles\
    tunnel-state\
    logs\
```

`machine.json` no contiene secretos. Debe registrar como mínimo schema/version de configuración, versión activa, versión anterior, tunnel id, alias local y versión fijada de tunnel-client. Las rutas derivables desde Known Folders no deben duplicarse innecesariamente.

## Host distribuible

Publicar `LoomLCI.Host` como:

- Release;
- `win-x64`;
- self-contained;
- carpeta normal, no single-file inicialmente.

Prueba realizada sin modificar el repo:

```text
dotnet publish LoomLCI.Host -c Release -r win-x64 --self-contained true
```

Resultado observado:

- publicación correcta;
- aproximadamente 81 MiB / 231 archivos;
- `LoomLCI.Host.exe` presente;
- IntegrationTests contra el Host publicado: **9/9**.

Esto confirma que el Host actual puede distribuirse sin requerir .NET instalado en la PC destino.

## tunnel-client

### Versión inicial fijada

Usar **OpenAI tunnel-client v0.0.14** en la primera implementación porque es exactamente la versión ya validada por la instalación actual:

```text
0.0.14+0f870e50a973fa820d4c409000059e181e8d242b
```

No mezclar el primer deployment con una actualización a v0.0.15.

### Obtención y supply chain

Primera estrategia recomendada: Setup descarga el ZIP oficial fijado, verifica un SHA-256 compilado/registrado en nuestro manifest y recién entonces extrae el binario.

Artefacto oficial:

```text
tunnel-client-v0.0.14-windows-amd64.zip
SHA-256:
784ab8da7b5a88f0109f1fd8aaf0a1c86067430b896dddf307ef7e3cc49fa1a5
```

La release oficial publica además `SHA256SUMS.txt`, SPDX, sidecars de licencias y evidencia de provenance/vulnerabilidades.

Verificación real en esta PC:

- el ZIP descargado desde la release oficial produjo exactamente el SHA esperado;
- el `tunnel-client.exe` extraído produjo SHA-256 `fcc85a69ec0ad82518e4f8964f60c45e31787957782a0fc9c1b0c44e82d61b9b`;
- ese hash coincide exactamente con el `tunnel-client.exe` actualmente usado por IvanSpace;
- el binario extraído reportó la misma versión 0.0.14.

### Redistribución

El proyecto `openai/tunnel-client` usa **Apache License 2.0**, que permite redistribución en forma binaria sujeto a sus condiciones. Si más adelante elegimos un bundle offline que incluya el binario, el paquete de LoomLCI debe incluir como mínimo:

- copia de Apache-2.0;
- NOTICE de OpenAI;
- sidecar de licencias de terceros de la release correspondiente;
- preferentemente también el SPDX publicado.

Para la primera versión, descargar el artefacto oficial durante Setup reduce superficie de mantenimiento y evita vendorear el binario dentro del repo.

## Secrets y runtime key

### Estado actual verificado

La runtime API key actual está en archivo y **no** embebida en YAML. La ACL real inspeccionada es:

- owner: usuario actual;
- herencia deshabilitada;
- una única regla explícita: usuario actual = FullControl.

Es exactamente el límite de acceso deseado para la primera versión.

### Decisión inicial

Mantener una runtime key por máquina como archivo local con DACL protegida sólo para el usuario actual.

Setup debe:

1. crear el directorio de secrets;
2. desactivar herencia;
3. dejar sólo el SID del usuario actual con FullControl;
4. escribir la key;
5. verificar luego la ACL;
6. generar el profile con referencia `file:...`, nunca con la key literal.

Cada PC debe usar preferentemente una runtime key distinta con permisos mínimos **Tunnels Read + Use**.

### Por qué no DPAPI inicialmente

DPAPI `CurrentUser` sí aporta cifrado at-rest ligado al usuario/máquina, pero `tunnel-client` consume directamente referencias `file:` o `env:`. Introducir DPAPI obligaría al launcher a descifrar la key y moverla a environment o a un archivo temporal antes de cada arranque.

Eso:

- crea una segunda ruta de secret handling;
- acopla más fuertemente el runtime al launcher;
- complica restart/diagnóstico;
- no cambia el trust boundary principal de LoomLCI Full Trust bajo el mismo usuario.

Para v1, key de scope reducido + ACL user-only es más simple y coincide con el camino ya validado. DPAPI queda como hardening opcional futuro si aparece un threat model que lo justifique.

## Profile y state isolation

Mantener separados:

- `TUNNEL_CLIENT_PROFILE_DIR=%LOCALAPPDATA%\LoomLCI\deployment\tunnel-profiles`;
- `TUNNEL_CLIENT_STATE_DIR=%LOCALAPPDATA%\LoomLCI\deployment\tunnel-state`.

La documentación oficial distingue explícitamente profile, runtime alias y state dir. El nuevo deployment no debe reutilizar el state root legacy durante la fase side-by-side.

Alias instalado:

```text
loomlci-installed
```

El alias legacy `loomlci` se conservó intacto durante el cutover y actualmente permanece detenido como fallback.

## Rutas con espacios

Prueba específica realizada:

- un `--mcp-command` con ruta absoluta quoted que contiene espacios fue interpretado incorrectamente por tunnel-client v0.0.14;
- un profile con `mcp-command = LoomLCI.Host.exe` funcionó correctamente cuando el directorio del Host se agregó al `PATH` heredado del proceso tunnel-client;
- `doctor` resolvió correctamente el ejecutable incluso estando dentro de una carpeta con espacios;
- una referencia `file:` hacia una runtime key en una ruta con espacios también pasó `doctor`.

Decisión:

- el profile usa `LoomLCI.Host.exe`, no una ruta absoluta quoted;
- Launcher antepone la carpeta de la versión activa al `PATH` sólo en el environment del proceso tunnel-client;
- no modificar el PATH global del usuario o del sistema.

## LoomLCI.Launcher

Superficie implementada:

```text
LoomLCI.Launcher.exe start
LoomLCI.Launcher.exe stop
LoomLCI.Launcher.exe status
LoomLCI.Launcher.exe setup
```

El acceso directo principal del escritorio ejecuta `start`.

Launcher **no** implementa su propio daemon ni protocolo de túnel. Delega lifecycle local a:

- `tunnel-client runtimes connect`;
- `tunnel-client runtimes status`;
- `tunnel-client runtimes stop`.

### start

1. validar `machine.json`, versión activa, Host y tunnel-client;
2. configurar PROFILE_DIR, STATE_DIR y PATH sólo para el child process;
3. consultar `runtimes status`;
4. si ya está healthy/ready, devolver éxito idempotente;
5. si no está activo, ejecutar `runtimes connect`;
6. volver a consultar `status --json`;
7. declarar éxito sólo con criterios de salud completos.

### status

Mostrar al usuario al menos:

- detenido / iniciando / listo / error;
- versión activa de LoomLCI;
- versión de tunnel-client;
- alias;
- health/ready.

No mostrar runtime key ni datos secretos.

### stop

Ejecutar `runtimes stop <alias>`. Esta operación detiene el runtime local y deja intacto el tunnel remoto y la configuración de ChatGPT.

## Setup inicial

Secuencia implementada:

1. validar Windows x64 y ubicación de Known Folders;
2. copiar Launcher y Host Release a una nueva carpeta versionada;
3. descargar tunnel-client v0.0.14 desde release oficial;
4. verificar SHA-256 antes de extraer;
5. verificar `tunnel-client --version`;
6. solicitar/importar tunnel id;
7. solicitar runtime key de esa máquina;
8. crear secret con ACL user-only;
9. generar profile local sin secretos literales;
10. ejecutar `tunnel-client doctor --explain --json`;
11. escribir `machine.json`;
12. crear acceso directo de escritorio mediante Shell Link de Windows;
13. **no** iniciar ni detener todavía el runtime legacy durante la instalación side-by-side.

La implementación usa staging para publicar la carpeta versionada del Host y escritura temporal para `machine.json`. No es todavía un instalador transaccional completo: un fallo tardío de Setup puede dejar archivos instalados sin activar ningún runtime, pero no modifica ni detiene el legacy.

## Criterio de instalación sana

Antes de cutover:

- todos los archivos de la versión activa existen;
- tunnel-client tiene versión y hash esperados;
- secret existe y su DACL está protegida correctamente;
- profile no contiene key literal;
- `doctor` devuelve result=ok;
- Host publicado pasó tests de integración;
- runtime legacy sigue funcionando.

Después de start:

- `process_running=true`;
- `healthy=true`;
- `ready=true`;
- `runtime_state=ready`;
- tunnel id coincide con el esperado;
- no hay issues locales relevantes;
- smoke real desde ChatGPT lista/ejecuta tools de LoomLCI.

## Primer cutover sin riesgo

La primera migración se hace manualmente y de forma reversible:

1. capturar `runtimes status loomlci --json` del runtime legacy y conservar tunnel id, profile, target y referencia runtime-only además del `repair_command`;
2. confirmar que el nuevo deployment pasó Setup + doctor;
3. detener **sólo** el runtime legacy;
4. iniciar `loomlci-installed`;
5. validar status completo;
6. hacer smoke real desde ChatGPT;
7. si falla cualquier punto, detener `loomlci-installed` y relanzar el runtime legacy con `runtimes connect` + tunnel id + referencia `--runtime-api-key file:...`; no depender ciegamente de un `repair_command` que requiera admin key.

No ejecutar simultáneamente los dos runtimes sobre el mismo tunnel durante esta prueba STDIO.

## Rollback de versiones una vez adoptado

Estructura:

```text
versions\
    <actual>\
    <anterior>\
```

`machine.json` mantiene `activeVersion` y `previousVersion`.

Update futuro:

1. instalar nueva versión en staging;
2. validar archivos;
3. stop;
4. cambiar activeVersion;
5. start + health;
6. smoke.

Si falla:

1. stop;
2. restaurar previousVersion;
3. start;
4. verificar health.

No implementar auto-update en la primera versión; sólo dejar la estructura preparada.

## Implementación y validación en PC principal

Implementado en commit `9db4f61`:

- nuevo proyecto `src/LoomLCI.Launcher`;
- comandos `start`, `stop`, `status`, `setup`;
- primer arranque desde paquete dispara setup si todavía no existe `machine.json`;
- build portable mediante `scripts/Build-PortablePackage.ps1`;
- Host Release self-contained `win-x64`;
- Launcher self-contained single-file;
- descarga fijada de tunnel-client v0.0.14 con verificación SHA-256 del ZIP y del EXE;
- runtime key en archivo con ACL user-only;
- profile/state aislados bajo `%LOCALAPPDATA%\LoomLCI\deployment`;
- acceso directo `LoomLCI.lnk` con argumento `start`;
- `PATH` modificado sólo para el child tunnel-client para resolver `LoomLCI.Host.exe`.

Validaciones realizadas:

- build de solución: **0 warnings / 0 errors**;
- suite completa: **203/203 tests** = 84 Core + 104 Windows + 9 Integration + 6 Launcher;
- build portable contra Host publicado: Launcher **6/6** + MCP Integration **9/9**;
- setup aislado en rutas con espacios: OK; profile sin secreto literal; ACL protegida; hash de tunnel-client correcto;
- setup real side-by-side: OK;
- acceso directo real creado y verificado;
- cutover legacy -> instalado: OK;
- runtime instalado: `process_running=true`, `healthy=true`, `ready=true`, sin issues;
- smoke real desde ChatGPT mediante `work_create` + `work_close`: OK;
- rollback instalado -> legacy: OK usando reconnect runtime-only;
- smoke real desde ChatGPT sobre legacy restaurado: OK;
- segundo cutover legacy -> instalado: OK;
- smoke final desde ChatGPT sobre la instalación portable: OK.

Paquete validado:

```text
LoomLCI-0.1.0-dev-9db4f61eafde-win-x64.zip
size: 69,255,594 bytes
SHA-256:
5a7056536384982ad7ef33f8af6bf53beba89144977984d96b9c8aa0cd8cf8f7
```

Estado operativo final en esta PC:

- runtime activo: `loomlci-installed`;
- Host: instalación Release bajo `%LOCALAPPDATA%\Programs\LoomLCI`;
- runtime legacy `loomlci`: detenido pero conservado para rollback;
- ChatGPT continúa usando el mismo tunnel remoto.

### Update G1.1 - Local image (2026-10-05)

Se validó un update side-by-side del deployment existente para G1.1:

- commit de producto: `a9f50fb feat: implement G1.1 local image`;
- build Release: **0 warnings / 0 errors**;
- suite completa: **223/223** = 84 Core + 119 Windows + 4 MCP + 10 Integration + 6 Launcher;
- builder portable final: Launcher **6/6** + IntegrationTests contra Host publicado **10/10**;
- paquete: `LoomLCI-0.1.0-dev-a9f50fb8fbe3-win-x64.zip`;
- SHA-256: `030ec622e66496bb0b3887beccc49d165380fd32bad577abbdc790f57ff82513`;
- setup side-by-side: OK;
- runtime activo final: `0.1.0-dev-a9f50fb8fbe3`, alias `loomlci-installed`;
- `process_running=true`, `healthy=true`, `ready=true`;
- tunnel id preservado;
- smoke real desde ChatGPT sobre el runtime final con `work_create` + `work_close`: OK.

La conversación donde se hizo el upgrade conservó el catálogo de 19 acciones cargado antes del update. Por eso el smoke visual de la nueva `filesystem_view_image` requirió refrescar las acciones de la app. En un chat refrescado se confirmó posteriormente la tool, pero en esa versión ChatGPT no materializó su `ImageContentBlock` como visión del modelo; ese diagnóstico histórico quedó resuelto más adelante mediante el follow-up de compatibilidad de `outputSchema`.

### Update G1.2 - PDF text worker (2026-10-05)

Se validó e instaló un nuevo update side-by-side para G1.2:

- commit de producto: `b6e41fd feat: implement G1.2 PDF text worker`;
- build Release: **0 warnings / 0 errors**;
- suite completa: **235/235** = 84 Core + 126 Windows + 4 MCP + 11 Integration + 6 Launcher + 4 PdfWorker;
- builder portable de validación: Launcher **6/6** + IntegrationTests contra Host publicado **11/11**;
- el Host publicado contiene `LoomLCI.PdfWorker.dll` + PdfPig y relanza su propio `LoomLCI.Host.exe` como child worker; no existe un segundo runtime .NET self-contained;
- paquete final: `LoomLCI-0.1.0-dev-b6e41fd645d3-win-x64.zip`;
- SHA-256: `73b2e7095c13334541da571f35e0b04760a4ee12c3771d87d36f1ce5a248fa23`;
- setup side-by-side: OK;
- cutover del alias `loomlci-installed`: stop/start OK;
- runtime activo final: `0.1.0-dev-b6e41fd645d3`, healthy/ready;
- tunnel id preservado;
- smoke real desde ChatGPT sobre el runtime nuevo mediante `work_create` + `work_close`: **OK**.

G1.2 lleva el catálogo del Host normal de 20 a **21 tools**. En un chat con catálogo actualizado se confirmó la presencia de `filesystem_read_pdf` y se ejecutó el smoke directo ChatGPT -> app -> tunnel -> runtime nuevo: un PDF textual real devolvió `Hello IvanSpace PDF` y un PDF sin capa textual devolvió éxito con texto vacío. El update queda operativo y validado end-to-end.

### Update G1.3 - PDF render (2026-10-05)

Se implementó, publicó e instaló el renderer PDF aislado:

- commit de producto: `d25c3ce feat: implement G1.3 PDF render`;
- build Release: **0 warnings / 0 errors**;
- suite completa: **245/245** = 84 Core + 133 Windows + 4 MCP + 12 Integration + 6 Launcher + 6 PdfWorker;
- builder con runtime G1.3: Launcher **6/6** + IntegrationTests contra Host publicado **12/12**;
- follow-up de packaging/licencias: `63d256e fix: include G1.3 dependency license`, sin cambios de runtime;
- Host publicado exacto de `63d256e9b658`: IntegrationTests **12/12**;
- backend: `bblanchon.PDFium.Win32 157.0.8086` + P/Invoke mínimo + `StbImageWriteSharp 1.16.7` dentro del child `--internal-pdf-render-worker-v1`;
- Host publicado contiene `LoomLCI.PdfWorker.dll`, PdfPig, `pdfium.dll` (7.494.656 bytes) y `StbImageWriteSharp.dll` (37.376 bytes), sin publicar un segundo runtime .NET; el paquete portable agrega los avisos/licencias de terceros;
- Host payload: **124.106.542 bytes**;
- paquete final: `LoomLCI-0.1.0-dev-63d256e9b658-win-x64.zip`;
- tamaño ZIP: **81.215.991 bytes**;
- SHA-256: `2afbb74b3801fbf1b18e2290e0269efe79b9137dcc024ed0de2cfc2406b48da7`;
- distribución: `THIRD-PARTY-NOTICES.txt` + `licenses/PDFium-LICENSE.txt` + `licenses/Apache-2.0.txt`;
- NuGet vulnerability/outdated checks: sin hallazgos;
- PDF vectorial por Host self-contained: **OK**, 1800x1272 / 172.817 bytes;
- PDF scan/raster por Host final: **OK**, 1800x1271 / 262.667 bytes e inspección visual correcta;
- PDF protegido por Host final: **OK**, `password_protected`;
- setup side-by-side: **OK**;
- cutover del alias `loomlci-installed`: stop/start **OK**;
- runtime activo final: `0.1.0-dev-63d256e9b658`, `process_running=true`, `healthy=true`, `ready=true`;
- tunnel preservado;
- smoke posterior al cutover desde ChatGPT con `work_create` + `work_close`: **OK**.

G1.3 lleva el catálogo del Host normal de 21 a **22 tools** con Work Plan. Con el catálogo refrescado se completó el smoke directo de `filesystem_render_pdf_page` desde ChatGPT: PDF real de una página -> PNG **1800x1080 / 101.550 bytes**; `page=2` devolvió correctamente `invalid_argument` con rango `1..1`. El diagnóstico inicial de materialización visual quedó posteriormente resuelto por el follow-up de compatibilidad descrito abajo.

### Cierre G1.4 - Evaluation + portable (2026-10-05)

La evaluación conjunta de Visual Files cerró correctamente en la PC principal:

- hardening de mappings de errores PDF incorporado;
- suite Release: **249/249**;
- builder portable: Launcher **6/6** + IntegrationTests contra Host publicado **12/12**;
- paquete de validación: `LoomLCI-0.1.0-dev-g14-validation-win-x64.zip`;
- SHA-256: `dc7b76b734680bb990dc6d7a64f10f0a96ad2da19eb63e7a3a7dab150d305e8e`;
- corpus real por ChatGPT/tunnel: imagen cercana al cap, PDF textual paginado, PDF mixto texto+diagrama, PDF scan sin text layer y PDF inválido: **OK**;
- fresh-agent: **OK**, con selección natural de `filesystem_view_image`, `filesystem_read_pdf` y `filesystem_render_pdf_page`;
- el diagnóstico inicial de `ImageContentBlock` quedó superado por un A/B directo: las tools visuales sin `outputSchema` preservan el contenido multimodal en ChatGPT.

La instalación portable general ya fue validada previamente en dos PCs Windows x64. No se repite ahora la notebook con el paquete específico G1.4: queda **diferido/no bloqueante** hasta que un cambio futuro de packaging, dependencias nativas o capabilities justifique repetir la validación multi-PC.

### Follow-up de compatibilidad visual (2026-10-05)

Se comparó LoomLCI contra IvanSpace y se aisló la causa del fallo visual en ChatGPT: cuando una tool que devuelve `structuredContent + ImageContentBlock` anuncia `outputSchema`, el adaptador tipado de ChatGPT expone la salida estructurada pero descarta el contenido multimodal adicional. Sin `outputSchema`, el bloque de imagen se preserva y llega a visión del modelo.

Cambio final:

- `filesystem_view_image`: sin `UseStructuredContent` / `OutputSchemaType`;
- `filesystem_render_pdf_page`: sin `UseStructuredContent` / `OutputSchemaType`;
- `filesystem_read_pdf`: conserva `outputSchema` porque su salida útil es texto/metadata;
- el `CallToolResult` de las dos tools visuales sigue incluyendo `structuredContent + TextContentBlock + ImageContentBlock`;
- tests de contrato fijan que las dos tools visuales no anuncian `OutputSchema` y que `filesystem_read_pdf` sí lo hace.

Validación:

- tests específicos de contrato/imagen/render: **3/3**;
- suite Release completa: **249/249**;
- builder portable: Launcher **6/6** + IntegrationTests **12/12**;
- paquete: `LoomLCI-0.1.0-dev-visual-output-fix-win-x64.zip`;
- SHA-256: `0f08bb92f56916852fd0e8beca776407b656261a3e4e94a7088a3441405a55cc`;
- runtime instalado: `0.1.0-dev-visual-output-fix`, healthy/ready;
- smoke `filesystem_view_image`: visión directa **OK**, describiendo correctamente `G1.1 VISUAL SMOKE`;
- smoke `filesystem_render_pdf_page`: visión directa **OK**, leyendo visualmente `SCAN 42` sin Python, shell, OCR ni extracción textual.

La clasificación `BLOCKED_UPSTREAM` deja de ser el estado actual de Visual Files; se conserva sólo como diagnóstico histórico anterior al A/B.

### Hardening `filesystem_read_files` vs tunnel (2026-10-06)

Se cerró el hallazgo lateral detectado durante G1 sin reducir la capacidad interna de Filesystem:

- Core/Windows mantiene **64 MiB** de texto agregado como límite interno de `read_files`;
- el adapter MCP aplica un presupuesto común de **9 MiB** sobre el `CallToolResult` serializado;
- fast-path: si el texto UTF-8 devuelto ya supera 9 MiB, se rechaza antes de serializar el resultado grande;
- exact-path: si el texto crudo entra, se mide la serialización MCP real para contemplar escaping JSON, paths y metadata;
- exceso -> `unsupported` con `reason=mcp_payload_too_large` y guía para usar rangos menores o dividir archivos;
- suite Release: **251/251** = 84 Core + 137 Windows + 5 MCP + 13 Integration + 6 Launcher + 6 PdfWorker;
- builder portable: Launcher **6/6** + IntegrationTests contra Host publicado **13/13**;
- paquete: `LoomLCI-0.1.0-dev-readfiles-payload-hardening-win-x64.zip`;
- SHA-256: `3308603363283675614dbe9b164e20fc8c549f2c5c0e8588a8d9a95a5b831d1d`;
- runtime instalado: `0.1.0-dev-readfiles-payload-hardening`, healthy/ready;
- smoke real por Secure MCP Tunnel: una lectura de **9 MiB + 1 byte** fue rechazada localmente y una lectura pequeña inmediatamente posterior funcionó, confirmando que el runtime permaneció operativo.

### Ergonomía Process: `process_run` (2026-10-06)

Se agregó una tool one-shot para comandos cortos sin cambiar el lifecycle durable de `process_start`:

- `ProcessCapability.RunAsync` usa `IProcessProvider` directamente y no registra `ProcessHandle`;
- espera exit + drain de stdout/stderr, devuelve exit code y output bounded, y dispone siempre el recurso;
- timeout/cancelación terminan el Job Object durante cleanup;
- exit codes no cero siguen siendo resultados normales de la tool;
- catálogo normal con Work Plan: **23 tools**;
- suite Release: **259/259** = Core 87 + Windows 141 + MCP 5 + PdfWorker 6 + Launcher 6 + Integration 14;
- builder portable: Launcher **6/6** + IntegrationTests contra Host publicado **14/14**;
- paquete: `LoomLCI-0.1.0-dev-process-run-win-x64.zip`;
- SHA-256: `aaa67eb47ab056e0bd206147cd4e346f8212491a63512be05ca8d310f9726e09`;
- runtime instalado: `0.1.0-dev-process-run`, `process_running=true`, `healthy=true`, `ready=true`;
- cutover realizado con IvanSpace; las tools ya conocidas por la conversación siguieron funcionando por el tunnel inmediatamente después.

Smoke directo final desde ChatGPT: **OK** en un chat nuevo con catálogo refrescado. `process_run` ejecutó `git status --short` con `exitCode=0`, stderr vacío, stdout completo/no truncado y sin `processHandle`; sólo devolvió `processId` como metadata. La capability queda cerrada end-to-end.

### Ergonomía Work Plan: `work_plan_patch` (2026-10-06)

Se implementó e instaló el follow-up de ergonomía de Work Plan:

- `work_plan_patch` con operaciones `add` / `update` / `remove`, manteniendo `expectedRevision` CAS y IDs `step_*` estables;
- `work_plan_update` conserva creación inicial, reorder, full replacement y clear;
- catálogo normal con Work Plan: **24 tools**;
- suite Release: **269/269** = Core 96 + Windows 141 + MCP 5 + PdfWorker 6 + Launcher 6 + Integration 15;
- Work Plan Core específico: **33/33**;
- builder portable: Launcher **6/6** + IntegrationTests contra Host publicado **15/15**;
- paquete: `LoomLCI-0.1.0-dev-work-plan-patch-win-x64.zip`;
- SHA-256: `75c18db8e955b57c13eac85445b4417e3b72096c38e10d15f2ea18e58c78c731`;
- setup side-by-side: OK;
- cutover del alias `loomlci-installed`: stop/start OK mediante IvanSpace sólo para evitar autoapagar la conexión LoomLCI activa;
- runtime activo final: `0.1.0-dev-work-plan-patch`, `process_running=true`, `healthy=true`, `ready=true`;
- IntegrationTests contra la DLL instalada: **15/15**;
- smoke posterior al cutover desde este mismo chat mediante `process_run`: OK.

La conversación que realizó el update conserva el catálogo anterior de 23 tools, por lo que el smoke directo de `work_plan_patch` queda pendiente de un chat con catálogo refrescado. El Host instalado ya publica la tool y su contrato fue validado contra la DLL instalada; el pendiente es únicamente de aceptación del consumidor.

### Hallazgo durante rollback

El `repair_command` emitido por el runtime legacy incluía `--admin-profile default` y, al ejecutarlo literalmente, falló porque `OPENAI_ADMIN_KEY` no estaba definido.

El rollback correcto no necesita admin key: se validó usando `runtimes connect` con el tunnel id conocido y una referencia `--runtime-api-key file:...`. Esa ruta dejó el legacy nuevamente healthy/ready y pasó smoke desde ChatGPT.

Por lo tanto, para rollback de un runtime existente **no se debe asumir que `repair_command` es ejecutable en el entorno actual**. Debe conservarse también el tunnel id, el profile/target y la referencia runtime-only necesaria para reconectar.

## Segunda PC Windows

Validación real completada con el mismo paquete portable en una notebook Windows x64:

1. se copió y extrajo el ZIP;
2. primera ejecución pidió tunnel id y runtime API key;
3. Setup descargó/verificó tunnel-client y creó el acceso directo;
4. la PC principal fue detenida antes de iniciar la notebook;
5. el acceso directo inició `loomlci-installed` en la notebook;
6. ChatGPT pudo crear una WorkSession, ejecutar Python y leer la configuración local de esa notebook;
7. el runtime de la notebook se detuvo con `LoomLCI.Launcher.exe stop`;
8. se inició nuevamente el acceso directo en la PC de escritorio;
9. ChatGPT confirmó que volvió a operar sobre el equipo de escritorio.

Conclusión: el flujo **ZIP -> setup -> acceso directo -> tunnel -> ChatGPT -> LoomLCI en otra PC** funciona sin repo, Visual Studio, .NET, Python del sistema ni IvanSpace.

Primera plataforma soportada: **Windows x64**. El primer uso de Python sigue requiriendo red para provisionar el CPython embeddable privado.

Con el mismo tunnel remoto, las PCs se usan alternativamente: se detiene LoomLCI en una antes de iniciar la otra. Para operación simultánea se mantiene la recomendación de un tunnel distinto por PC.

## Follow-up Launcher UX (2026-10-06)

La UX de escritorio del Launcher quedó cerrada sin introducir todavía una GUI completa:

- nueva opción global `--pause`: espera Enter antes de cerrar y está pensada para invocaciones desde accesos directos;
- la CLI normal (`start`, `stop`, `status`, `setup`) mantiene el comportamiento no interactivo anterior cuando no se pasa `--pause`;
- `LoomLCI.lnk` ejecuta `start --pause`;
- nuevo `Detener LoomLCI.lnk` ejecuta `stop --pause`;
- Setup informa ambos shortcuts y reemplaza terminología interna de cutover/legacy por mensajes más simples;
- Launcher fija stdout/stderr en UTF-8 para que automatización y captura por LoomLCI preserven acentos;
- el README portable se movió a `scripts/PortableReadme.template.txt` y el builder lo lee/escribe con UTF-8 explícito, eliminando el mojibake observado bajo Windows PowerShell 5.1.

Validación final:

- suite Release completa: **272/272** = Core 96 + Windows 141 + MCP 5 + PdfWorker 6 + Launcher 9 + Integration 15;
- tests Launcher: **9/9**;
- builder portable: Launcher **9/9** + IntegrationTests contra Host publicado **15/15**;
- paquete: `LoomLCI-0.1.0-dev-launcher-ux-win-x64.zip`;
- SHA-256: `032dfcf31fb3dff4a7ed1fa1453a7dbd6513ee166b03c016b45c060111fddd2e`;
- README generado verificado con tildes correctas;
- smoke del ejecutable publicado con `help --pause`: el proceso permaneció vivo hasta recibir Enter y luego salió con code 0;
- setup side-by-side sobre la instalación real: OK, creando ambos shortcuts;
- cutover realizado con IvanSpace sólo para el stop/start;
- runtime activo final: `0.1.0-dev-launcher-ux`, `process_running=true`, `healthy=true`, `ready=true`;
- smoke posterior al cutover desde ChatGPT mediante LoomLCI: **OK**.

## Follow-up Instalador Windows convencional (2026-10-06)

Ver [[Instalador Windows]] para el contrato y validación completos.

El deployment portable quedó como **fuente de verdad del payload**, pero el camino de instalación normal ahora es un setup EXE generado con Inno Setup 7.1.0:

- instalación per-user, sin UAC/admin por defecto;
- registro estándar en Aplicaciones instaladas;
- wizard para Tunnel ID y runtime key;
- configuración real delegada al mismo `LoomLCI.Launcher setup`;
- uninstall con stop previo y limpieza del root administrado;
- portable intermedio generado en staging temporal y eliminado al finalizar el builder.

Validación final: suite Release **272/272**, install/uninstall aislado completo, setup real `0.1.0-dev-installer`, cutover healthy/ready, smoke desde ChatGPT e IntegrationTests contra la DLL instalada **15/15**.

## Fuera de alcance inicial

- autoarranque al login;
- Windows Service;
- MSIX/MSI;
- auto-update;
- code signing;
- win-arm64;
- bundle totalmente offline;
- DPAPI;
- migración automática de la skill/plugin de ChatGPT.

Estas mejoras ya pueden evaluarse por separado ahora que el deployment portable real quedó validado en dos PCs. La aspereza original de ventanas que se cerraban sin confirmación y la falta de un acceso directo de stop quedaron resueltas en el follow-up Launcher UX sin introducir todavía una GUI completa.

## Fuentes

- OpenAI tunnel-client v0.0.14: https://github.com/openai/tunnel-client/releases/tag/v0.0.14
- OpenAI tunnel-client LICENSE (Apache-2.0): https://github.com/openai/tunnel-client/blob/v0.0.14/LICENSE
- OpenAI tunnel-client NOTICE: https://github.com/openai/tunnel-client/blob/v0.0.14/NOTICE
- Profiles/state/key split: https://github.com/openai/tunnel-client/blob/v0.0.14/plugins/tunnel-mcp/skills/tunnel-mcp/references/profiles-state-and-keys.md
- Runtime lifecycle/status: https://github.com/openai/tunnel-client/blob/v0.0.14/plugins/tunnel-mcp/README.md
- Microsoft DPAPI / ProtectedData: https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata
- Microsoft CryptProtectData: https://learn.microsoft.com/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata
- Microsoft icacls: https://learn.microsoft.com/windows-server/administration/windows-commands/icacls
- .NET self-contained deployment: https://learn.microsoft.com/dotnet/core/deploying/
