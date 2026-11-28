# 示例插件：内存占用

这是 WinIslands 最小组件插件的完整示例，复制到插件目录即可运行。

## 目录

```text
plugin-hello-world/
├─ plugin.json
└─ run.ps1
```

## 安装

1. 打开 WinIslands → 设置 → 插件 → 「打开插件目录」。
2. 把整个 `plugin-hello-world` 文件夹复制进去。
3. 回到设置页点击「重新扫描」，或在托盘菜单中重新扫描插件。
4. 无媒体播放时，灵动岛会显示「内存 xx%」组件。

## 修改后注意

修改 `run.ps1` 后文件哈希会变化，需要在插件管理器中重新「校验并信任」，
或在 `plugin.json` 中更新 `sha256` 字段。

## 输出协议

插件读取标准输出中的 JSON：

```json
{
  "components": [
    {
      "id": "hello-memory",
      "text": "内存 42%",
      "icon": "🧠",
      "tooltip": "示例插件",
      "show_when_playing": true,
      "show_when_idle": true,
      "order": 50
    }
  ]
}
```

更多字段见 [插件开发指南](../../PLUGIN_DEVELOPMENT.md)。
