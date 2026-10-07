# Python 1 - Paquetes administrados

> Estado: **implementado, instalado y validado.** El único pendiente no bloqueante es el smoke directo de `python_packages_prepare` desde un chat nuevo con catálogo MCP refrescado.
>
> Objetivo: permitir que el Python privado de LoomLCI use paquetes de terceros durante una tarea sin depender del Python del sistema, sin mutar el runtime base y conservando aislamiento, versionado, hashes, lifecycle y reproducibilidad.

## Baseline actual

Python Runtime E1 ya está cerrado:

- CPython **3.14.8 x64 embeddable** privado;
- runtime fijado por URL + SHA-256 en `runtime/python/runtime.json`;
- instalación lazy bajo `%LOCALAPPDATA%\LoomLCI\runtimes\python\3.14.8-amd64`;
- worker persistente y SessionOwned por WorkSession;
- `python_execute` / `python_reset` como superficie MCP;
- launcher del worker con `-I -B -u -X utf8 -X faulthandler -X thread_inherit_context=1`;
- `PYTHONHOME`, `PYTHONPATH`, `PYTHONSTARTUP` y `PYTHONUSERBASE` eliminados del entorno del worker.

El `python314._pth` instalado contiene sólo:

```
python314.zip
.

# Uncomment to run site.main() automatically
#import site
```

Por lo tanto, el worker actual no ve paquetes globales del usuario ni del sistema. Una comprobación real mediante `python_execute` confirmó `pip=None`, `numpy=None` y `pandas=None`.

## Hallazgo clave: no convertir el embeddable en un Python normal

La documentación oficial de Python indica que la distribución embeddable está pensada como parte de una aplicación, no incluye pip y no soporta usar pip como gestor normal de dependencias. Recomienda que la aplicación gestione sus paquetes de terceros.

Conclusión: **no bootstrappear pip dentro del runtime privado y no instalar paquetes directamente en `runtimes/python/...`.**

Eso preserva el runtime base como asset verificable e intercambiable y evita que una instalación de paquetes pueda dejar el intérprete compartido en un estado parcial o incompatible.

## Alternativas evaluadas

### 1. pip dentro del embeddable — descartado

Ventaja: familiar.

Problemas:

- contradice el modelo recomendado para la distribución embeddable;
- exige habilitar/modificar el entorno base;
- mezcla runtime y dependencias mutables;
- una actualización puede afectar todas las WorkSessions;
- complica rollback y corrupción parcial;
- facilita builds desde source y efectos laterales no deseados.

### 2. Resolver/instalar wheels manualmente en LoomLCI — descartado

Daría máximo control, pero obligaría a reimplementar resolución PEP 440/508, markers, tags de plataforma, metadata, wheels, hashes y conflictos. Demasiada complejidad para una capability auxiliar.

### 3. venv por WorkSession — no preferido

Aísla dependencias, pero duplica estructura de entornos y agrega fricción innecesaria cuando LoomLCI ya tiene un único CPython privado fijado. El problema real puede resolverse con un directorio de paquetes externo y explícito.

### 4. `uv` standalone + target separado — recomendado

`uv` es un ejecutable standalone y su interfaz `uv pip` no necesita ni invoca pip. Permite:

- seleccionar explícitamente el Python privado con `--python`;
- impedir que descargue otro Python con `--no-python-downloads`;
- ignorar configuración del usuario/proyecto con `--no-config`;
- instalar en un directorio arbitrario con `--target`;
- prohibir source distributions/builds con `--only-binary :all:`;
- generar locks y hashes;
- mantener un cache controlado por LoomLCI.

Las releases Windows x64 son standalone; desde uv 0.12.12 los ejecutables Windows están firmados con Authenticode. El proyecto usa licencia MIT OR Apache-2.0.

## Spike real realizado

Todo el spike se hizo fuera del repo, bajo un directorio temporal, sin modificar LoomLCI.

### uv

Se validó `uv 0.12.23` (2026-10-03) x64:

- ZIP Windows descargado desde la release oficial;
- SHA-256 publicado: `75d05de6762778c31ee183398de7dd15093fad0ed90b1f236d8205ea5ec00c90`;
- SHA-256 calculado localmente: idéntico;
- `uv --version`: `0.12.23 (46b84fd0b 2026-10-03 x86_64-pc-windows-msvc)`.

### NumPy / Pandas

Se usó el CPython privado real de LoomLCI:

```
%LOCALAPPDATA%\LoomLCI\runtimes\python\3.14.8-amd64\python.exe
```

`uv` resolvió e instaló en un target separado, sin tocar el runtime:

- NumPy **2.5.3**;
- Pandas **3.0.6**;
- python-dateutil **2.9.0.post0**;
- six **1.17.0**;
- tzdata **2026.5**.

Se generó un lock con versiones exactas y hashes y se sincronizó con `--require-hashes --only-binary :all:`.

Después de `python_reset`, un worker limpio de LoomLCI recibió el target mediante `site.addsitedir(...)` e importó correctamente NumPy y Pandas. Se ejecutaron operaciones reales con ambos.

### Compatibilidad representativa de CPython 3.14

Con `uv 0.12.23`, CPython 3.14.8, Windows x64 y builds desde source deshabilitados, se resolvió correctamente un conjunto de **23 paquetes** que incluía:

- NumPy 2.5.3;
- Pandas 3.0.6;
- SciPy 1.18.1;
- Matplotlib 3.11.2;
- OpenCV Python 5.0.0.93;
- openpyxl 3.1.5;
- scikit-learn 1.9.1;
- trimesh 5.1.1;
- dependencias transitivas correspondientes.

También se resolvió PyTorch **2.14.1** y sus dependencias con source builds deshabilitados. No se descargó/instaló PyTorch en el spike por su tamaño.

Conclusión: CPython 3.14 no representa hoy un bloqueo práctico para el conjunto principal de paquetes que interesa a LoomLCI.

## Formato de lock

PEP 751 / `pylock.toml` ya es un estándar PyPA para entornos reproducibles.

Sin embargo, el `uv 0.12.23` actual todavía clasifica el consumo de `pylock.toml` en `uv pip install/sync` como preview y muestra advertencia explícita.

Para Python 1 se recomienda:

1. **inicialmente:** lock estilo `requirements.txt` con versiones exactas + SHA-256, generado por `uv pip compile --generate-hashes`;
2. instalación con `uv pip sync --require-hashes --only-binary :all:`;
3. migrar a `pylock.toml` cuando el soporte de la herramienta deje de ser preview.

El lock clásico puede contener hashes de artefactos adicionales, pero `--only-binary :all:` garantiza que LoomLCI sólo acepte wheels.

## Diseño recomendado

### Runtime base inmutable

No modificar:

- `python314._pth`;
- el contenido del runtime provisionado;
- la política `-I`;
- variables de entorno aisladas.

El runtime Python sigue siendo un asset independiente y verificable.

### Provisioning privado de uv

Agregar un manifest de asset equivalente al de CPython:

- versión fijada;
- arquitectura;
- URL;
- SHA-256 embebido en LoomLCI.

Ruta conceptual:

```
%LOCALAPPDATA%\LoomLCI\tools\python\uv\<version>\uv.exe
```

Provisioning lazy, staging + verificación + publicación atómica. No ejecutar scripts de instalación externos.

La versión 0.12.23 queda **validada como candidata**, pero la versión exacta se fija en el manifest al implementar.

### Package store separado

No instalar dentro del runtime. Usar algo conceptualmente similar a:

```
%LOCALAPPDATA%\LoomLCI\packages\python\3.14.8-amd64\
    envs\<environmentId>\
        site\
        requirements.lock.txt
        .loom-packages.json
    cache\
```

Cada environment es inmutable una vez publicado.

`environmentId` debe depender de:

- fingerprint exacto del runtime Python;
- lock exacto;
- versión del schema/policy del package store.

Instalación:

1. resolver a staging;
2. generar lock exacto;
3. verificar/instalar sólo wheels con hashes;
4. escribir marker;
5. publicar atómicamente;
6. si cualquier paso falla, conservar el environment anterior de la WorkSession.

Entornos idénticos pueden reutilizarse entre WorkSessions.

### Cache

El cache de `uv` debe vivir bajo un root controlado por LoomLCI, no en el cache global del usuario.

Los environments persistentes son cache, no datos del proyecto. Debe existir GC/LRU que:

- nunca elimine un environment ligado a una WorkSession activa;
- opere con un presupuesto total de disco;
- pueda eliminar environments antiguos sin afectar el runtime base.

P1.4 midió ~74,8 MiB para NumPy+Pandas, ~40,1 MiB para NumPy y ~76,5 MiB de cache de `uv` durante el smoke. Se fijó un presupuesto inicial de **2 GiB para environments** y **1 GiB para el cache de `uv`**. La poda es LRU/best-effort: nunca elimina environments ligados a WorkSessions activas ni a workers vivos; si sólo los protegidos exceden el presupuesto, se conserva su integridad y el exceso se tolera.

El spike confirmó además una restricción real de Windows: al intentar eliminar el target mientras el worker tenía NumPy cargado, varios `.pyd` y la DLL de OpenBLAS quedaron bloqueados y el borrado falló con acceso denegado. Tras `python_reset`, el mismo directorio se eliminó completamente. Por lo tanto, el GC **no puede** borrar environments ligados a workers vivos.

El uninstall actual ya elimina `%LOCALAPPDATA%\LoomLCI`, por lo que tools, cache y package store quedan dentro del cleanup convencional si se alojan bajo ese root.

### Fuente inicial

Python 1 inicial:

- sólo **PyPI oficial**;
- sin índices privados;
- sin URLs directas;
- sin Git/VCS;
- sin paths locales/editables;
- sin source distributions;
- sin builds PEP 517.

Esto reduce dependency confusion, configuración externa y ejecución de código durante builds.

Los índices privados/autenticados pueden agregarse más adelante con un contrato explícito.

### Entrada estructurada

No aceptar líneas de requirements arbitrarias del modelo porque podrían contener opciones de installer.

La superficie MCP debe recibir requisitos estructurados, por ejemplo conceptualmente:

```
packages:
  - name: numpy
    version: 2.5.3   # opcional
  - name: pandas
```

El nombre se normaliza/valida en LoomLCI. La versión puede omitirse para elegir la estable compatible; la resolución exacta siempre queda registrada en el lock y en la respuesta.

La lista representa el **set completo deseado** para la WorkSession. Una lista vacía vuelve al Python base sin terceros.

Extras y rangos complejos pueden añadirse posteriormente si los evals muestran necesidad real; no son necesarios para el primer corte.

## Superficie MCP propuesta

Agregar **una** tool:

### `python_packages_prepare`

Responsabilidad:

- recibe `workId` + set deseado de paquetes;
- asegura runtime privado;
- asegura `uv`;
- resuelve/lockea;
- reutiliza o crea environment;
- liga ese environment a la WorkSession;
- devuelve versiones exactas y si el worker debe reiniciarse.

Respuesta conceptual:

- `environmentId`;
- `pythonVersion`;
- `packages[]` con nombre/versión exactos;
- `reused`;
- `workerRestartRequired`.

No agregar inicialmente herramientas separadas de install/uninstall/list. El package environment es declarativo: la lista deseada reemplaza la anterior.

El catálogo normal pasa de 24 a **25 tools**.

## Semántica con el worker persistente

No modificar un environment que ya está siendo usado por un intérprete vivo.

Si `python_packages_prepare` cambia el environment mientras existe un worker:

1. prepara y liga el nuevo environment como deseado;
2. **no** mata el worker silenciosamente;
3. devuelve `workerRestartRequired=true`;
4. el agente llama `python_reset`;
5. el siguiente `python_execute` crea un worker con el environment nuevo.

Como guard de corrección, `python_execute` debe detectar si el environment deseado difiere del environment del worker vivo y devolver conflict indicando que hace falta `python_reset`.

Motivo: un reset implícito perdería globals/estado sin que el agente lo sepa, mientras que modificar paquetes bajo un proceso vivo puede mezclar versiones o DLLs nativas cargadas.

`python_reset` conserva el package binding deseado. `work_close` / expiry limpia el binding lógico, pero no necesariamente el environment cacheado.

## Integración del environment en Python

Extender el start spec privado del worker con el path del environment.

Al arrancar:

```python
import site
site.addsitedir(package_site_path)
```

Esto:

- añade sólo el directorio administrado por Loom;
- conserva `-I`;
- no habilita user-site ni paquetes del sistema;
- procesa `.pth` legítimos de wheels instalados.

Las líneas ejecutables de `.pth` pueden ejecutar código al iniciar Python. Esto es aceptable dentro del modelo Full Trust porque instalar/importar un paquete de terceros ya equivale a confiar en código de ese paquete; debe quedar documentado y nunca ocurrir desde fuentes implícitas.

## Concurrencia y lifecycle

- provisioning de `uv`: gate corto semejante al runtime;
- package preparation: invocación ligada a WorkSession y cancelable;
- staging siempre limpiado tras error/cancelación;
- publicación de environment atómica;
- environment existente nunca se muta;
- prepares concurrentes del mismo environment deben converger a una sola publicación;
- prepares diferentes pueden ejecutarse en paralelo cuando sea seguro;
- los procesos internos de `uv` deben usar `IProcessProvider` / Job Objects, sin ProcessHandle MCP público.

## Seguridad / reproducibilidad

Invariantes:

- Python exacto fijado por Loom;
- uv exacto fijado por Loom;
- `--python <private-python.exe>`;
- `--no-python-downloads`;
- `--no-config`;
- cache privado;
- PyPI oficial explícito;
- wheels-only / `--only-binary :all:`;
- lock exacto;
- `--require-hashes`;
- staging + publish atómico;
- environment inmutable;
- no instalación silenciosa al fallar un `import`.

Los hashes protegen la integridad/repetibilidad del artefacto resuelto, no convierten un paquete malicioso de PyPI en seguro. Los paquetes se ejecutan Full Trust con los permisos normales del usuario, coherente con el modelo general de LoomLCI.

## Fuera de alcance de Python 1

- bridge `loom.*` hacia capabilities de LoomLCI: Python 2;
- salida binaria/imágenes desde Python al modelo: Python 3;
- source builds / compiladores;
- Conda;
- CUDA/toolchains especiales;
- índices privados y credenciales;
- paquetes desde Git/VCS/path;
- aislamiento de seguridad/sandbox de paquetes;
- package CLI tools como superficie propia;
- auto-install implícito al detectar `ModuleNotFoundError`.

## Subetapas propuestas

### P1.0 - Package manager foundation

- manifest/versionado de `uv`;
- provisioner con URL+SHA-256, staging y atomic publish;
- cache/root privado;
- tests de hash, concurrencia, cancelación y corrupción;
- incluir notices/licencia correspondientes.

### P1.1 - Immutable package environments

- contratos Core del package provider;
- resolución y lock;
- wheel-only + hashes;
- environment store + marker;
- staging/rollback;
- reutilización;
- pruebas reales NumPy/Pandas y fallo sin wheel compatible.

### P1.2 - WorkSession + worker binding

- estado de package binding dentro de WorkSession;
- start spec con environment;
- `site.addsitedir` en worker;
- mismatch -> conflict;
- `python_reset` aplica el nuevo environment;
- close/expiry limpia binding;
- tests de lifecycle, concurrencia y DLL/native wheel.

### P1.3 - MCP

- `python_packages_prepare`;
- input estructurado;
- respuesta con versiones exactas / reuse / restartRequired;
- límites y errores claros;
- snapshot/plugin metadata actualizado por el mecanismo ya existente.

### P1.4 - Evaluation + deployment

- smoke real ChatGPT -> tunnel -> prepare -> reset -> execute;
- fresh-agent;
- matriz de paquetes representativos;
- cache reuse / offline reuse cuando ya existe environment;
- corrupción y recuperación;
- cancellation/timeout;
- concurrencia entre WorkSessions;
- medir footprint de paquetes ligeros y pesados y fijar presupuesto LRU;
- update portable/installed y validación end-to-end.

## Criterio de cierre

Python 1 se considera cerrado cuando un fresh-agent pueda, únicamente mediante LoomLCI:

1. crear una WorkSession;
2. pedir NumPy/Pandas sin depender del Python del sistema;
3. obtener un environment fijado/verificado;
4. ejecutar un worker que importe y use esos paquetes;
5. reutilizar el environment sin reinstalación innecesaria;
6. cambiar el set de paquetes con reset explícito y sin corromper estado;
7. sobrevivir correctamente a fallos, timeout, close/expiry y actualización del Host.

## Implementación y validación final

Python 1 quedó implementado con el diseño anterior y estas decisiones finales:

- `uv 0.12.23` x64 provisionado lazy desde la release oficial, con URL + SHA-256 fijados y publicación atómica;
- runtime CPython 3.14.8 base inmutable;
- requirements estructurados, sólo PyPI oficial;
- resolución/instalación **wheels-only** mediante `--only-binary :all:`;
- lock determinista con versiones exactas + hashes usando `--no-header --no-annotate`; esto evita que paths temporales cambien el `environmentId`;
- environments content-addressed e inmutables fuera del runtime;
- `site.addsitedir` sólo al arrancar un worker ligado al environment;
- cambio de environment con worker vivo -> `workerRestartRequired=true`; `python_execute` devuelve `conflict` hasta `python_reset`;
- GC LRU/best-effort con **2 GiB para environments** y **1 GiB para cache de uv**, protegiendo bindings de WorkSessions activas y environments todavía cargados por workers vivos;
- nueva tool pública `python_packages_prepare`; catálogo normal con Work Plan: **25 tools**;
- snapshot MCP canónico regenerado desde Host Release real y verifier del plugin OK con 25 tools.

Suite Release final antes de deployment: **298/298** = Core 101 + Windows 149 + MCP 5 + PdfWorker 6 + Launcher 20 + Integration 17.

Smoke MCP real contra un Host self-contained publicado: **OK**. Flujo ejercitado:

1. `work_create`;
2. `python_packages_prepare` con NumPy 2.5.3 + Pandas 3.0.6;
3. `python_execute` importó ambos y ejecutó cálculo real;
4. repetición del mismo set -> `reused=true`;
5. cambio a NumPy-only -> `workerRestartRequired=true`;
6. `python_execute` antes del reset -> `conflict`;
7. `python_reset`;
8. nuevo `python_execute` -> NumPy disponible correctamente;
9. `work_close`.

El segundo smoke sobre el código final reutilizó desde disco el environment determinista NumPy+Pandas sin reinstalarlo. Tras el cutover, el mismo flujo se repitió contra `C:\Users\ivanl\AppData\Local\Programs\LoomLCI\versions\0.1.0-dev-python1\LoomLCI.Host.exe` y terminó con `PYTHON1_SMOKE_OK`. Además, los IntegrationTests contra la DLL instalada pasaron **17/17**.

## Fuentes principales

- Python embeddable: https://docs.python.org/3/using/windows.html#the-embeddable-package
- Python `._pth`: https://docs.python.org/3.14/library/sys_path_init.html#pth-files
- `site.addsitedir`: https://docs.python.org/3.14/library/site.html#site.addsitedir
- PyPA `pylock.toml`: https://packaging.python.org/en/latest/specifications/pylock-toml/
- uv pip interface: https://docs.astral.sh/uv/pip/
- uv CLI/settings: https://docs.astral.sh/uv/reference/cli/
- uv preview features: https://docs.astral.sh/uv/concepts/preview/
- pip secure installs: https://pip.pypa.io/en/stable/topics/secure-installs/
