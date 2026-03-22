# DC-Kook Bot Relay

基于 .NET 9 的 Discord 与 KOOK 消息互通机器人，支持文本、图片/附件、@、表情、链接等格式的基础转发。配置仅使用环境变量，支持 Docker 与本地运行。

## 环境变量

推荐使用 `.environment` 文件（已提供 `.environment.example` 模板）。

必填：
- `DISCORD_TOKEN`
- `KOOK_TOKEN`

可选：
- `DISCORD_GUILD_ID`
- `KOOK_GUILD_ID`
- `DISCORD_CHANNEL_MAP`（示例：`123:456,789:012` 表示 Discord 频道映射到 KOOK 频道）
- `KOOK_CHANNEL_MAP`（反向映射，可省略，留空时自动反推）
- `DISCORD_TO_KOOK_ENABLED`（默认 `true`，是否启用 Discord -> KOOK 转发）
- `KOOK_TO_DISCORD_ENABLED`（默认 `true`，是否启用 KOOK -> Discord 转发）
- `DISCORD_BOT_MESSAGE_TO_KOOK_ENABLED`（默认 `false`，是否将 Discord 机器人消息转发到 KOOK）
- `RELAY_EVERYONE_MENTION_ENABLED`（默认 `false`，开启后 Discord/KOOK 的 @所有人 会转发到另一侧）
- `TRANSLATION_ENABLED`（默认 `true`，是否启用翻译）
- `TRANSLATION_PROVIDER`（`auto` / `tencent` / `baidu`，默认 `auto`）
- `TRANSLATION_AUTO_DETECT_SOURCE`（默认 `true`，自动识别原文语言）
- `DISCORD_TO_KOOK_SOURCE_LANG`（默认 `auto`）
- `DISCORD_TO_KOOK_TARGET_LANG`（默认 `zh`）
- `KOOK_TO_DISCORD_SOURCE_LANG`（默认 `auto`）
- `KOOK_TO_DISCORD_TARGET_LANG`（默认 `zh`）
- `TENCENT_SECRET_ID`
- `TENCENT_SECRET_KEY`
- `TENCENT_REGION`（默认 `ap-guangzhou`）
- `BAIDU_APP_ID`
- `BAIDU_APP_KEY`
- `TRANSLATION_BOT_NAME`（默认 `DC-Kook Bot`，用于 KOOK 卡片底部署名，如 `WananBot`）

## 双语翻译转发

已支持 Discord 与 KOOK 双向翻译转发（默认自动识别源语言 -> 中文 `zh`）：

- Discord -> KOOK：使用 **卡片消息**，内容包含英文原文（代码块）+ 空行 + “翻译”标题 + 中文译文，并在底部附译文来源超链接。
- KOOK -> Discord：普通消息发送，英文原文走正常文本，中文译文使用代码块，并在底部附译文来源超链接。

翻译源默认支持：
- 腾讯云机器翻译（TMT）
- 百度翻译开放平台

`TRANSLATION_PROVIDER=auto` 时会按可用凭据自动选择并在失败时回退。

`TRANSLATION_AUTO_DETECT_SOURCE=true` 时，会先按文本内容自动识别源语言（常见中/英/日/韩），再调用翻译服务。

## 本地运行

1. 复制模板并填写：

```powershell
Copy-Item .environment.example .environment
```

2. 运行：

```powershell
Get-Content .environment | ForEach-Object {
	if ($_ -match '^(\w+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2]) }
}

dotnet run --project .\DcKookBot\DcKookBot.csproj
```

## Docker 运行

```powershell
Copy-Item .environment.example .environment
# 编辑 .environment 后执行
docker compose up --build
```

## 说明

- 当前实现是基础互通骨架，@/表情/链接采用文本化处理。
- 如果需要更完整的消息卡片/富文本解析，可进一步扩展 `MessageFormatter`。
