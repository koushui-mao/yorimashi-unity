# Changelog

All notable changes to `com.yorimashi.modder`.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows Yorimashi M-phase tags (`<version>-M<milestone><step>`).

## [0.1.0-M3T2] — 2026-07-14

### Added
- `hierarchy/get_children` tool — list direct children of a GameObject by path, with component type names.
- `hierarchy/find_by_name` tool — search active scene for GameObjects by name (exact / contains, includeInactive toggle).
- `hierarchy/get_active` tool — full details of a GameObject: active state / tag / layer / transform (local + world) / component list.
- `AppendComponentTypeNames` helper — surfaces `MissingScript` explicitly so agents can detect broken references.
- `ExtractBool` helper in `YorimashiTools` (for tool params with boolean flags).
- `scripts/pack-unity-bundle.sh` — enforces the "complete UPM package" rule: verifies required files + `.meta` companions, checks version bump, validates `.cs` count matches source.
- `docs/M3-plan.md` — full M3 roadmap: Tier 0-4 tool inventory + milestone breakdown.
- `gateway/tests/test_m3_tool_schemas.py` — Python-side schema contract tests (10 tests, no Unity needed). Detects any accidental tool schema drift + prevents write tools from sneaking in before M3-W1.

### Notes
- All 7 registered tools remain **read-only**. Write tools (blendshape/set, scene/save, etc.) are gated behind M3-W1 and must implement `dry_run` parameter (user safety rule).
- Bundle: `/tmp/yorimashi-0_1_0-M3T2-bundle.zip` (complete package — includes all 6 `.cs` files, not a supplement).

## [0.1.0-M1B1] — 2026-07-13

### Added
- `YorimashiEnvelope.cs` — C# envelope v=1 encode/decode，对齐服务端 `server/mcp_wss_frame.py`。
- `MiniJsonParser.cs` — 精简 JSON 解析，支持提取原始 `mcp` 子对象。
- `YorimashiWssClient.cs` — 基于 .NET 内建 `ClientWebSocket` 的最小客户端；后台 receive loop + 主线程 marshal（`EditorApplication.update` pump 队列）。
- `ChatWindow` 升级：URL 输入框、Connect/Disconnect/Send Ping/Clear Log 按钮、状态色灯、滚动日志窗（500 行上限）。
- `scripts/b1_local_server.py` — Windows 上跑的本地 echo server，验证 envelope round-trip。

### Verified
- Linux 侧 Python↔Python round-trip PASS：
  `{"pong": true, "echo": {"hello": "world"}}` 正确返回，correlation_id 匹配。

### Notes
- 无鉴权（M1-C4 逻辑等 Windows 侧确认可用后再挂）。
- 无自动重连（M1-C1 已在 Python 侧实现，Unity 侧 M1-B2 加）。
- 未接 CoplayDev Unity MCP（M1-B2 才做）。

## [0.1.0-M1B0] — 2026-07-10

### Added
- Initial UPM package skeleton.
- `Window / Yorimashi Modder` menu entry that opens a placeholder
  `ChatWindow` (`M1-B0 SKELETON` status label).
- `YorimashiModder.Editor` asmdef, Editor-only.

### Notes
- No wire yet. WebSocket client + Editor↔MCP bridge arrive in M1-B2 and B3.
- No dependency on `com.coplaydev.unity-mcp` declared in `package.json` —
  users install both packages side-by-side via `Packages/manifest.json`.
