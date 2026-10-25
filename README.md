# prt — PR Review Tracker

Herramienta de terminal para darle trazabilidad a las revisiones de Pull Requests de **Bitbucket Cloud** hechas con agentes CLI (**Claude Code**, **Codex CLI**, **opencode**).

- Cada PR queda asociado a la **sesión del agente** con la que se revisó: `prt review <pr>` retoma siempre la misma sesión, con todo su contexto.
- Cada vez que abrís la sesión se registra una **ronda**; al salir, el agente escribe un **memo** (hallazgos, riesgos de arquitectura, impacto de negocio, pendientes, veredicto) en la misma sesión, y podés sumar tu nota.
- Si el PR recibió commits desde la última revisión, `prt` lo detecta y le pide al agente que revise solo lo nuevo.
- **Panel de revisores** (`prt analyze`): arquitectura, bugs, performance y seguridad corren en paralelo, cada uno en su propia sesión, y responden con un **formato de reporte estándar** que es el mismo sin importar el proveedor de IA. Los revisores, el estándar y el contexto se configuran **por proyecto**. Ver [docs/REVIEW_STANDARD.md](docs/REVIEW_STANDARD.md).
- Todo se guarda en una base SQLite local y se **exporta a Obsidian**: una nota por PR con frontmatter, sesiones, panel, pendientes y timeline, un reporte por cada análisis y un índice.

## Instalación

Requiere el SDK de .NET 10.

```bash
dotnet pack src/Prt.Cli -c Release
dotnet tool install -g --add-source ./nupkg PrReviewTracker
```

Para actualizar: `dotnet tool update -g --add-source ./nupkg PrReviewTracker` (subí `<Version>` en `src/Prt.Cli/Prt.Cli.csproj`).

## Configuración

```bash
prt init                       # interactivo: vault, carpeta, agente por defecto, credenciales
prt map acme/payments ~/src/payments   # checkout local donde corre el agente para ese repo
```

Bitbucket Cloud: creá un **API token** de Atlassian con permiso de lectura de pull requests y repositorios, y usá tu email de Atlassian como usuario (también funcionan usuario + app password). Las variables `PRT_BB_USER` y `PRT_BB_TOKEN` tienen prioridad sobre el config.

| Qué | Dónde |
|---|---|
| Config | `~/.config/prt/config.json` (Windows: `%APPDATA%\prt\config.json`), permisos 600 |
| Base de datos | `~/.local/share/prt/prt.db` (Windows: `%LOCALAPPDATA%\prt\prt.db`) |
| Overrides | `PRT_CONFIG_DIR`, `PRT_DATA_DIR` |

## Uso

```bash
prt review https://bitbucket.org/acme/payments/pull-requests/42   # o acme/payments#42
prt review acme/payments#42 --agent codex      # otra sesión, con otro agente
prt review acme/payments#42 --new              # forzar sesión nueva
prt review acme/payments#42 -p "Enfocate en la migración"
prt analyze acme/payments#42                   # panel completo de revisores, en paralelo
prt analyze acme/payments#42 -r security,bugs  # solo algunos revisores
prt analyze acme/payments#42 --agent codex     # todos los revisores con un mismo agente
prt report acme/payments#42 [--markdown|--json]
prt review acme/payments#42 --reviewer security   # abrir la sesión del revisor para discutir sus hallazgos
prt reviewers init acme/payments               # copiar estándares y revisores a <checkout>/.prt para personalizarlos
prt reviewers list acme/payments               # qué revisores aplican y de dónde sale cada uno
prt resume                                     # elegir de la lista de PRs registrados
prt list --state OPEN
prt show acme/payments#42                      # sesiones, rondas y memos
prt memo acme/payments#42 -m "Ok de negocio con Producto" --kind decision
prt link acme/payments#42 --agent claude --session <id>   # asociar una sesión que ya existía
prt tag acme/payments#42 arquitectura pagos
prt sync                                       # refrescar PRs abiertos desde Bitbucket
prt export                                     # regenerar todas las notas
```

`prt review` sin `--agent` usa el agente de la última sesión del PR (para retomar la misma), o el agente por defecto si el PR es nuevo.

### Flujo de una ronda

1. Lee el PR y sus commits de Bitbucket (si no hay red y el PR ya está registrado, sigue con los datos guardados).
2. Si no hay sesión para ese agente: abre una nueva con el prompt inicial (URL, ramas, descripción, foco de revisión). Si hay: la retoma y, si hay commits nuevos, ofrece enviar el prompt de re-revisión.
3. Trabajás con el agente normalmente. Al salir:
4. Pide el memo al agente en modo headless **dentro de la misma sesión** y lo guarda; extrae el veredicto y los pendientes.
5. Pregunta si querés agregar una nota propia (`$VISUAL`/`$EDITOR`, o en línea terminando con `.`).
6. Exporta la nota del PR y el índice al vault.

## Panel de revisores

`prt analyze <pr>`:

1. Refresca el PR, descarga el diff de Bitbucket y arma una carpeta de contexto por corrida (diff, descripción y commits, contexto del proyecto y hallazgos anteriores de cada revisor).
2. Resuelve el perfil del proyecto (revisores, estándar, contexto, agente por revisor) por capas: incorporado → `~/.config/prt` → `<checkout>/.prt` → `~/.config/prt/projects/<ws>/<repo>`.
3. Lanza cada revisor en modo headless y solo lectura, en paralelo (`maxParallel`). La primera vez abre una sesión por revisor; las siguientes la **retoman** con los commits nuevos y los hallazgos anteriores, para que marque cada uno como resuelto o abierto.
4. Valida y normaliza cada respuesta (severidades, veredicto coherente, ids). Si alguna no cumple el formato, pide una corrección en la misma sesión.
5. Consolida el veredicto (el peor), lo guarda y exporta el reporte a Obsidian.

El estándar completo (severidades, veredicto, contrato JSON, estructura del reporte y personalización) está en [docs/REVIEW_STANDARD.md](docs/REVIEW_STANDARD.md).

## Agentes

| Agente | Sesión nueva | Cómo se obtiene el id | Retomar | Memo headless |
|---|---|---|---|---|
| Claude Code | `claude --session-id <uuid> "<prompt>"` | lo genera `prt` | `claude --resume <id>` | `claude -p --resume <id> --output-format json` |
| Codex CLI | `codex "<prompt>"` | rollout nuevo en `$CODEX_HOME/sessions` (filtrado por cwd) | `codex resume <id>` | `codex exec resume <id> --json` |
| opencode | `opencode --prompt "<prompt>"` | diff de `opencode session list --format json` | `opencode --session <id>` | `opencode run --session <id> --format json` |

Si el id no se puede detectar, `prt` lo pide; también se puede asociar después con `prt link`.

Para el panel, cada agente corre en modo headless y solo lectura (`reviewNewArgs` / `reviewResumeArgs`):

| Agente | Nueva sesión de revisor | Id de sesión |
|---|---|---|
| Claude Code | `claude -p --allowedTools Read,Grep,Glob,Bash(git diff/log/show) --add-dir <contexto> --session-id <uuid> --output-format json` | lo genera `prt` |
| Codex CLI | `codex exec --json --sandbox read-only --skip-git-repo-check` | evento `thread.started` |
| opencode | `opencode run --format json` | campo `sessionID` de los eventos |

Además de `{sessionId}` y `{prompt}`, estos templates aceptan `{contextDir}`: la carpeta con el diff y el contexto, que el agente tiene que poder leer.

Las CLIs cambian seguido, así que el comando y los argumentos de cada agente se pueden sobrescribir en el config (placeholders `{sessionId}` y `{prompt}`; lo que no se indique usa el valor por defecto):

```json
{
  "agents": {
    "codex": {
      "command": "/usr/local/bin/codex",
      "askArgs": ["exec", "--json", "resume", "{sessionId}", "{prompt}"]
    }
  }
}
```

Los prompts (`prompts.initial`, `prompts.reReview`, `prompts.summary`) también son editables. Placeholders: `{key} {url} {title} {author} {source} {destination} {description}` y, en la re-revisión, `{count} {fromCommit} {toCommit} {commits}`. El memo debe mantener la sección `## Pendientes` y la línea `Veredicto: ...` para que se extraigan.

## Obsidian

```
<vault>/PR Reviews/
├── Index.md                          # tabla por estado + consulta Dataview
└── acme/payments/
    └── PR-42/                                # una carpeta por PR
        ├── PR-42 - Agregar pagos QR.md       # nota principal
        └── Análisis 2026-10-25 1403 (run 3).md   # un reporte por cada `prt analyze`
```

Cada nota tiene:

- **Frontmatter** con `pr`, `url`, `author`, `state`, ramas, `verdict`, `last_review`, `rounds`, `sessions` (agente, id y comando para retomar) y `tags` (`pr-review` + los de `prt tag`). Sirve para Dataview.
- **Sesiones**, **Pendientes** (del último memo del agente) y **Timeline** con cada ronda: prompt enviado, memo del agente y tus notas, en callouts.
- Lo generado va entre `<!-- prt:begin -->` y `<!-- prt:end -->`. **Todo lo que escribas fuera de esos marcadores se conserva** al regenerar (por ejemplo, la sección "Notas libres"). El frontmatter se regenera siempre. Si cambia el título del PR, la nota se renombra.

La base SQLite es la fuente de verdad: `prt export` regenera el vault en cualquier momento. Las notas creadas con versiones anteriores a la 0.3 (fuera de la carpeta del PR) se mueven solas a su carpeta en el siguiente export.

En la consola clásica de Windows (sin Windows Terminal), `prt` usa texto en lugar de emojis (`BLOQ`, `IMP`, `MEN`, `INFO`). Para forzarlo en cualquier terminal: `PRT_ASCII=1`.

## Desarrollo

```bash
dotnet build
dotnet test
```

- `src/Prt.Core`: dominio, SQLite (`Storage`), cliente de Bitbucket, adaptadores de agentes, export a Obsidian, el flujo de revisión (`Review/ReviewService`) y el panel (`Panel/`: estándar, parser de reportes, perfiles por proyecto, orquestación y renderizado). Los revisores y el estándar incorporados están en `Panel/Defaults/`.
- `src/Prt.Cli`: comandos (`System.CommandLine`) e interacción (`Spectre.Console`).
- `tests/Prt.Core.Tests`: xUnit, con runner de procesos, Bitbucket y UI falsos.
