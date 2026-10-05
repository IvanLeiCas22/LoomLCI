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

La conversación donde se hizo el upgrade conservó el catálogo de 19 acciones cargado antes del update. Por eso el smoke visual de la nueva `filesystem_view_image` requirió refrescar las acciones de la app. En un chat refrescado se confirmó posteriormente la tool, pero ChatGPT no materializó su `ImageContentBlock` como visión del modelo; quedó documentado como bloqueo upstream del cliente.

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

G1.2 lleva el catálogo del Host normal de 20 a **21 tools**. La conversación del upgrade conserva las 20 acciones cargadas antes del cutover, por lo que `filesystem_read_pdf` no puede invocarse directamente hasta refrescar el catálogo o abrir un chat nuevo. La ruta ChatGPT -> app -> tunnel -> runtime nuevo quedó verificada con una tool preexistente; el smoke directo de la tool 21 es el único pendiente operativo de este update.

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

Estas mejoras ya pueden evaluarse por separado ahora que el deployment portable real quedó validado en dos PCs. UX detectada durante la prueba: las ventanas de Setup/Start se cierran apenas termina la operación, por lo que conviene más adelante agregar confirmación visible/pause controlado o una UI mínima; también sería útil un acceso directo `Detener LoomLCI`. No bloquea Computer.

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
