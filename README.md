# Yorimashi Modder

VRChat avatar 改模助手 —— Unity Editor 端插件。

## 状态

**M1-B1** — WSS 客户端 + envelope framing（本地 echo 冒烟）。

功能：
- 打开 EditorWindow（Window / Yorimashi Modder）
- 填 Server URL，点 Connect 连一个 wss 服务器
- 发 `yorimashi/ping` req，收到 res 会在日志显示
- 手动断开连接

**未实现**（后续里程碑）：
- 鉴权（M1-C4 Windows 联调后加）
- 真实 chat UI（M3）
- MCP 桥接到 CoplayDev Unity MCP（M1-B2/B3）

## 本地测试步骤

**Windows 上**：

```powershell
# 1. 装依赖
pip install websockets

# 2. 起本地 echo server
python scripts/b1_local_server.py
# 会打印：Yorimashi B1 local echo server listening on ws://127.0.0.1:19870/hub/plugin

# 3. Unity 里打开 Window / Yorimashi Modder
# 4. Server URL 保持默认 ws://127.0.0.1:19870/hub/plugin
# 5. 点 Connect → 状态变 Connected → 日志出现 [connect] handshake ok
# 6. 点 Send Ping → 日志出现 [recv] res cid=N ...
# 7. 点 Disconnect → 状态回 Disconnected
```

## 安装（Unity 侧）

1. Window / Package Manager
2. 左上 `+` → **Add package from disk...**
3. 选 `unity-package/com.yorimashi.modder/package.json`
4. 菜单栏出现 **Window / Yorimashi Modder**

## 卸载

Package Manager → Yorimashi Modder → Remove

## Sentinel

verify 脚本会在编译产物里搜以下字符串：
- `M1-B1 ENVELOPE`
- `M1-B1 MINIJSON`
- `M1-B1 WSSCLIENT`
- `M1-B1 CHATWINDOW`
