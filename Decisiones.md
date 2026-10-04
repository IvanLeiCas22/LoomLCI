# Decisiones

## Confirmadas hasta ahora

- LoomLCI será un runtime/capa local, no un agente.
- Full Trust será el caso de uso principal: LoomLCI no impondrá restricciones adicionales sobre lo que permita la cuenta de Windows.
- Full Trust no implica elevación administrativa; UAC/elevación será un concepto separado.
- Las ubicaciones/proyectos conocidos serán contexto y accesos rápidos, no límites de seguridad.
- MCP será una frontera/adaptador externo; el modelo interno no dependerá de MCP.
- Las capabilities estructuradas coexistirán con ejecución general de procesos/shell.
- Se preservará una abstracción de ExecutionContext aunque la primera implementación sea Host/Full Trust.
- Todo debe poder observarse, pero observabilidad no implica persistencia inmediata en disco.
- Programmatic Tool Calling (PTC) será un objetivo de compatibilidad para el diseño de las tools, no un componente interno de LoomLCI.
- LoomLCI debe funcionar correctamente con direct tool calling; si el harness superior soporta PTC, debe poder componer las mismas tools sin cambios en el Core.
- El Work Plan permitirá múltiples pasos activos o en espera al mismo tiempo; no se impondrá la restricción de un único `in_progress`.
- El estado lógico del Work Plan estará separado del lifecycle real de procesos, invocaciones y otros recursos; en v0.1 no habrá acoplamiento automático entre steps y resource handles.
- Python Runtime usará un CPython privado/versionado por LoomLCI, no el Python del PATH/usuario; E1 fija CPython 3.14.8 x64 embeddable.
- Python Runtime tendrá un único worker lazy y session-owned por WorkSession; `workId` será suficiente como identidad pública y no se expondrá `PythonHandle` en E1.
- El IPC Python ↔ Loom usará Named Pipe privado/versionado separado de stdout/stderr; el backend Windows reutilizará `IProcessProvider`/Job Objects para lifecycle y cleanup.
- `worker.py` y `runtime.json` se embeben en `LoomLCI.Windows`; el Host no dependerá del repo ni de su cwd para localizar assets de Python.
- El CPython privado se auto-provisionará on-demand bajo `%LOCALAPPDATA%\LoomLCI`, con descarga acotada, SHA-256 fijado, staging y publicación por rename; los tests del provisioner usarán HTTP/ZIP falsos en vez de Internet.
- E1 de Python será stdlib-only y textual; PyAutoGUI, imágenes, paquetes de terceros y `loom.*` quedan diferidos.
