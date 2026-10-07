# LoomLCI Python worker

Fuente versionada del worker persistente de Python Runtime.

E1.2 definió el worker y su protocolo privado. E1.3 embebe los assets en `LoomLCI.Windows`, auto-provisiona el CPython privado y conecta el worker con `IPythonRuntimeProvider`/Job Objects. E1.4 expone `python_execute`/`python_reset` por MCP; E1.5 validó el flujo real por Secure MCP Tunnel/fresh-agent y cerró E1.

Python 2 P2.0 lleva el protocolo privado a **v2** y agrega el asset `loom_bridge.py`. Durante un `python_execute`, el mismo Named Pipe puede multiplexar `bridge_call`/`bridge_result` antes del `result` final. El módulo privado `loom` queda disponible dentro del worker; P2.0 sólo expone la foundation (`loom.__bridge_version__`, `loom.LoomError`, `loom.capabilities()`). Las capabilities reales de Filesystem/Process se agregan en P2.1/P2.2.

Runtime fijado:

- CPython 3.14.8 x64;
- distribución embeddable oficial;
- hash y URL en `runtime.json`;
- package-store schema v2 reserva el top-level `loom`.

Lanzamiento:

```
python.exe -I -B -u -X utf8 -X faulthandler -X thread_inherit_context=1 worker.py --pipe-name <name> --bridge-script <loom_bridge.py>
```

El canal de control es un Named Pipe privado full-duplex, separado de stdout/stderr. Los callbacks del bridge se ligan al `python_execute` activo por `requestId`/`callId`; errores normales del bridge son recuperables y corrupción/cancel/timeout siguen invalidando el worker.
