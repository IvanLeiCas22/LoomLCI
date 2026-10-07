# LoomLCI Python worker

Fuente versionada del worker persistente de Python Runtime.

E1.2 definió el worker y su protocolo privado. E1.3 embebe los assets en `LoomLCI.Windows`, auto-provisiona el CPython privado y conecta el worker con `IPythonRuntimeProvider`/Job Objects. E1.4 expone `python_execute`/`python_reset` por MCP; E1.5 validó el flujo real por Secure MCP Tunnel/fresh-agent y cerró E1.

Python 2 P2.0 llevó el protocolo privado a **v2** y agregó el asset `loom_bridge.py`. Durante un `python_execute`, el mismo Named Pipe puede multiplexar `bridge_call`/`bridge_result` antes del `result` final. P2.1 agregó el router modular y `loom.fs`; P2.2 agregó `loom.process` con ownership validado contra la WorkSession activa.

Python 3 P3.0 lleva el protocolo Worker/Host a **v3** y la API `loom` a bridge version **2**. `loom.display_image(bytes|bytearray|memoryview)` acumula outputs de imagen execution-local sin usar RPC del bridge; el worker los codifica como base64 sólo en el `result` final. P3.0 limita a 4 outputs, 6 MiB por imagen y 6 MiB raw agregado, y eleva el frame final privado a 40 MiB. El transporte MCP visual de esos outputs se implementa recién en P3.1.

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
