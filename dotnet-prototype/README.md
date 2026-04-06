# XLab Unity MCP .NET Prototype

Минимальный скелет для первого рабочего прототипа:

- `src/XLab.UnityMcp.Protocol` - общие DTO/версия протокола
- `src/XLab.UnityMcp.Server` - .NET console app, MCP host через stdio JSON-RPC
- `unity/com.xlabkm.unity-mcp` - Unity Editor package (стартовый bridge)
- `contracts/breach-tools.schema.json` - формализованный JSON schema контракт для 23 шагов BREACH
  - теперь это единый источник истины для `tools/list` и runtime-валидации входных аргументов в сервере

## Что умеет сервер v0.3.0 (Breach Pipeline)

- `initialize`
- `tools/list`
- `tools/call` для:
  - `project_root.set`, `project.info`, `editor.state`, `project.health_check`
  - `scene.create`, `scene.open`, `scene.save`
  - `hierarchy.list`, `hierarchy.find`
  - `gameobject.create`, `gameobject.modify`
  - `component.add`, `component.set`
  - `prefab.create`, `prefab.open`, `prefab.save`, `prefab.instantiate`
  - `script.create_or_edit`, `scriptableobject.create_or_edit`
  - `console.read`
  - `screenshot.scene`, `screenshot.game`
  - `tests.run_editmode`, `tests.run_all`, `tests.results`
  - `build_settings_scenes`
  - `graph.open_or_create`, `graph.connect`, `graph.edit`, `graph.validate`
  - `scene.validate_refs`, `prefab.validate`
  - `asset.create_folder`, `asset.exists`, `asset.refresh`, `asset.list_modified`
  - `editor.compile_status`, `playmode.enter`, `playmode.exit`
  - `ui.create_or_edit`, `localization.key_add`
  - `change.summary`, `project.docs_update`

Editor-side команды (scene/hierarchy/gameobject/prefab/console/screenshot/tests/build settings) отправляются через bridge-очередь:
- `Library/XLabMcpBridge/commands/*.json`
- `Library/XLabMcpBridge/responses/*.json`

В Unity package добавлен processor:
- `unity/com.xlabkm.unity-mcp/Editor/McpBridgeProcessor.cs`

Реализация bridge-команд:
- `tests.run_editmode` / `tests.run_all`: запускает Unity Test Runner API
- `build_settings_scenes`: поддерживает `get` и `set`

## Быстрый старт

1. Установить .NET SDK 8+
2. Сборка сервера:
   - `dotnet build .\dotnet-prototype\XLab.UnityMcp.sln`
3. Запуск серверного прототипа:
   - `dotnet run --project .\dotnet-prototype\src\XLab.UnityMcp.Server\XLab.UnityMcp.Server.csproj`
4. Тесты:
   - `dotnet test .\dotnet-prototype\XLab.UnityMcp.sln`
5. Добавить Unity package в проект через local path (`dotnet-prototype/unity/com.xlabkm.unity-mcp`)
6. В Unity вызвать меню `XLab/MCP/Start Prototype Server`

## Следующий шаг

- Вынести полноценный transport/client для Unity package
- Подключить реальный executor на editor-side
- Добавить интеграционные тесты server<->Unity bridge
