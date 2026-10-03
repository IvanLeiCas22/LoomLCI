# Python Runtime

> Estado: investigación; propuesta arquitectónica todavía no confirmada.

## Corrección de alcance

La idea relevante no es "usar PyAutoGUI" sino ofrecer al modelo un entorno Python persistente de ejecución general. PyAutoGUI sería sólo una librería disponible dentro de ese entorno para Computer Use.

## Qué hace OpenAI públicamente

La guía actual de Computer Use recomienda code execution para GPT-6 Astra. Para escritorio, OpenAI muestra un tool `exec_py` que ejecuta Python en un proceso persistente y mantiene variables/globales entre llamadas.

El sample oficial `openai-cua-sample-app` confirma que el worker Python usa `exec(compile(...), namespace)` con `__builtins__` normales y `pyautogui` precargado. Es decir: no es un DSL de Computer Use; es Python real en un proceso persistente, capaz de importar módulos disponibles en ese entorno. PyAutoGUI es una de sus capacidades.

OpenAI aclara que ese repositorio demuestra el patrón y que sus productos usan runtimes propios con controles adicionales. Por lo tanto no debemos asumir que ChatGPT/Codex internamente usan exactamente ese worker.

OpenAI además ofrece otros dos patrones distintos:
- Code Interpreter: Python general dentro de una VM/container sandboxed hospedada por OpenAI.
- Shell: terminal general, hospedada o local.

Esto muestra una separación útil: compute general y Computer Use son capacidades relacionadas, pero no idénticas.

## Qué aporta a LoomLCI

Un Python Runtime persistente tiene valor incluso sin GUI:
- cálculos y transformaciones de datos
- manipulación de imágenes
- parsing y generación de archivos
- loops y condiciones locales
- reducción de round trips modelo↔tools
- estado en memoria entre llamadas (variables, imports, funciones)
- composición de varias acciones en una sola ejecución

Para Computer Use, el mismo runtime puede tener PyAutoGUI disponible y también un módulo `loom` que exponga las primitivas nativas de Loom.

## Relación con otras capabilities

### Process
`Process` sigue siendo necesario para lanzar y administrar procesos reales, terminales, ConPTY, Job Objects y handles de proceso.

Python puede llamar `subprocess`, pero eso sería un escape hatch general y no reemplaza la semántica estructurada de Process.

### Filesystem
Python puede leer/escribir archivos directamente en Full Trust. Las tools estructuradas de Filesystem siguen siendo preferibles cuando el modelo sólo necesita descubrir, leer o aplicar patches porque son más directas y producen resultados estructurados.

### Computer
Computer sigue teniendo backend nativo en C# (UIA, Windows.Graphics.Capture, SendInput, HWND/PID, multi-monitor y observaciones).

El Python Runtime puede usar:
- `loom.computer.*` como camino preferido
- `pyautogui` como API de conveniencia/escape hatch

## Arquitectura propuesta

LoomLCI
- Structured capabilities
  - Filesystem
  - Process
  - Computer
- General code runtime
  - Python Runtime (persistente)
    - builtins/imports normales
    - `log()` / salida de texto
    - `display()` / imágenes
    - librerías incluidas, entre ellas PyAutoGUI
    - futuro módulo `loom`
- Agent Support
  - Work Plan

El Python Runtime debe ser un proceso hijo separado del Core. No ejecutar código generado por el modelo dentro del proceso C# de Loom.

## Lifecycle propuesto

- Un worker Python por Work Session/handle, no un intérprete global compartido por todos los agentes.
- Globals persistentes entre llamadas del mismo worker.
- Una ejecución activa por worker a la vez.
- Timeout y cancelación.
- Si el worker se bloquea o excede timeout: terminarlo y crear uno nuevo.
- stdout/stderr capturados y acotados.
- salida estructurada de texto e imágenes.
- cleanup de teclas/botones mantenidos si PyAutoGUI participó y la ejecución se aborta.

En Full Trust, el worker corre con el mismo usuario de Windows y no tiene restricciones adicionales de filesystem/red impuestas por Loom. La separación de proceso es para aislamiento de fallos y lifecycle, no para fingir un sandbox.

## Observabilidad

Una llamada Python puede realizar muchas operaciones internas que Loom no ve individualmente si usa `os`, `subprocess`, etc. Eso es una consecuencia deliberada de ofrecer ejecución general Full Trust.

Loom sí puede registrar:
- inicio/fin de cada ejecución Python
- duración
- estado
- stdout/stderr/output
- worker lifecycle

Cuando el script usa `loom.*`, las operaciones internas también pueden atravesar las capabilities estructuradas y conservar su trazabilidad detallada.

## Conclusión propuesta

Sí incluir conceptualmente un Python Runtime general como capability de ejecución de primera clase.

PyAutoGUI no es la arquitectura: es una librería disponible dentro de ese runtime.

No generalizar todavía a múltiples lenguajes. Python es suficiente como primer code runtime; una abstracción interna puede permitir otros runtimes en el futuro si aparece una necesidad real.

## Fuentes

- OpenAI Computer Use: https://developers.openai.com/api/docs/guides/tools-computer-use
- OpenAI CUA sample app: https://github.com/openai/openai-cua-sample-app
- Python worker: https://github.com/openai/openai-cua-sample-app/blob/main/python-app/app/desktop/worker.py
- OpenAI Code Interpreter: https://developers.openai.com/api/docs/guides/tools-code-interpreter
- OpenAI Shell: https://developers.openai.com/api/docs/guides/tools-shell
- Programmatic Tool Calling: https://developers.openai.com/api/docs/guides/tools-programmatic-tool-calling
