# A3 — Streamable HTTP

**Estado 2026-10-08:** A3.1 implementada y validada localmente; NO instalada ni conectada a ChatGPT. A3.2 (prueba mediante túnel HTTP separado) pendiente de investigación/aprobación.

## Objetivo

Ampliar el paralelismo de ChatGPT normal usando LoomLCI local, sin consumir cupos de Codex/Work ni API OpenAI.

## A1 y A2: causa y alternativa

- Host STDIO directo: 3 process_run con espera de 1,6 s terminaron en 1,87 s en paralelo vs. 5,54 s secuencial.
- ChatGPT/túnel STDIO compartido: 3 requests terminaron en 11,23 s, escalonadas; la implementación de tunnel-client v0.0.14 serializa solicitudes de ese canal.
- Mediante un solo python_execute con loom.process.start: tres procesos superpuestos en ~1,95 s de trabajo local. Es el workaround disponible ahora.
- La versión instalada de tunnel-client ofrece --mcp-server-url y cabeceras MCP. ModelContextProtocol.AspNetCore 2.2.0 está disponible.

## A3.1: implementación

Proyecto experimental fuera de solución: experiments/LoomLCI.HttpPoc/ (README y smoke.py). Sin cambios en Host STDIO, Launcher, perfil, runtime instalado o secreto del túnel.

- ASP.NET Core + MCP Stateless, ruta /mcp, health protegido.
- Kestrel sólo en 127.0.0.1, puerto configurable.
- Bearer aleatorio obligatorio (mínimo 32 bytes), hash SHA-256 comparado en tiempo constante, Host estricto, Origin rechazado, sin CORS; arranque sin secreto falla.
- Mismas 25 clases de herramientas y capacidades Core/Windows/PdfWorker, con WorkSessions persistentes en el Host.
- Incluye worker PDF internal para conservar compatibilidad de tools.

## Pruebas y evidencias

- Build Release: OK, 0 warnings, 0 errors. Suite Release de la solución existente: **366/366 PASS**.
- Autenticación: 401 ausente/incorrecta, 403 Host/Origin falso, 200 autorizado, fail-closed sin token.
- Protocolo initialize MCP 2025-11-25 + tools/list: 25.
- Contrato idéntico a Release 5 STDIO: 0 diferencias en nombres, títulos, descripciones, inputSchema, outputSchema o annotations.
- WorkSession, work_plan_update/get y filesystem_read_files entre múltiples solicitudes HTTP: PASS.
- 3 process_run de 1,6 s: secuencial 5,547 s; paralelo 1,867 s, todos código 0.
- Smoke repetible de 1,2 s: 4,345 s vs. 1,494 s; todas las fases PASS.
- WorkSession cerrada y Host de prueba detenido.

## Limitaciones y siguientes pasos

A3.1 demuestra concurrencia local, NO demuestra mejora real desde ChatGPT. Falta probar la autenticación del túnel HTTP y su paralelismo real. El PoC no está diseñado como servicio productivo, no tiene integración con Launcher o rollback ni storage de clave local persistente. No modificar LoomLCI instalado ni sustituir STDIO sin pruebas aisladas de un túnel distinto y plan de retorno. Aunque los 25 contratos coinciden, validar PDF/Python/imágenes por separado antes de adoptar.

Fuente SDK: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/stateless/stateless.md
