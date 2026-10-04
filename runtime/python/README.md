# LoomLCI Python worker

Fuente versionada del worker persistente de Python Runtime.

E1.2 define el worker y su protocolo privado. E1.3 embebe estos assets en `LoomLCI.Windows`, auto-provisiona el CPython privado y conecta el worker con `IPythonRuntimeProvider`/Job Objects. E1.4 expone `python_execute`/`python_reset` por MCP; E1.5 validó el flujo real por Secure MCP Tunnel/fresh-agent y cerró E1.

Runtime fijado:

- CPython 3.14.8 x64;
- distribución embeddable oficial;
- hash y URL en `runtime.json`.

Lanzamiento previsto:

```
python.exe -I -B -u -X utf8 -X faulthandler -X thread_inherit_context=1 worker.py --pipe-name <name>
```

El canal de control es un Named Pipe privado, separado de stdout/stderr.
