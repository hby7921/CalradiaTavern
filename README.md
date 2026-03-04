# CalradiaTavern

`CalradiaTavern` 是《骑马与砍杀2：霸主》v1.3.15 的酒馆界面 Mod，提供：

- 酒馆聊天（全服聊天）
- 在线玩家列表（分页）
- 玩家交易（目标玩家选择 + 物品筛选 + 发送）
- 本地交易机器人（单机自测交易流程）

## 当前版本说明

- 交易页采用分页显示（默认每页 20 条）。
- 在线玩家列表、可交易玩家列表采用分页显示。
- 聊天区、交易区均已接入调试日志，便于排查滚轮/排序问题。
- 聊天与交易共用酒馆主界面（聊天/交易两个页签）。

## 目录结构

- `SubModule.cs` / `SubModule.xml`：模组入口
- `Behaviors/CalradiaTavernCampaignBehavior.cs`：聊天/交易核心逻辑与轮询
- `UI/CalradiaTavernScreen.cs`：Gauntlet 屏幕层与输入/滚动处理
- `UI/ViewModels/*`：聊天、在线玩家、交易页面 VM
- `GUI/Prefabs/CalradiaTavern/CalradiaTavernScreen.xml`：酒馆界面 XML
- `Networking/TavernApiClient.cs`：HTTP 接口调用
- `Server/mock_server.py`：本地联调服务

## 构建

```powershell
dotnet build d:\Amods\CalradiaTavern\CalradiaTavern.csproj -c Release
```

构建产物：

- `bin\Release\net472\CalradiaTavern.dll`

## 部署到游戏目录

将以下文件复制到游戏模组目录：

- `CalradiaTavern.dll` -> `Mount & Blade II Bannerlord\Modules\CalradiaTavern\bin\Win64_Shipping_Client\`
- `CalradiaTavernScreen.xml` -> `Mount & Blade II Bannerlord\Modules\CalradiaTavern\GUI\Prefabs\CalradiaTavern\`

## 本地服务启动（联调）

```powershell
python d:\Amods\CalradiaTavern\Server\mock_server.py
```

默认地址：`http://127.0.0.1:18080`

## 调试日志

游戏运行日志默认写入：

- `Mount & Blade II Bannerlord\Modules\CalradiaTavern\bin\Win64_Shipping_Client\CalradiaTavern.debug.log`

建议定位问题时重点关注：

- `RefreshChatLines rebuilt`
- `ChatVisualVersion changed`
- `TryScrollChatToBottom`
- `ScrollDiagPanel`

## 常用控制台命令（可选）

- `ctavern.open`
- `ctavern.chat_send <消息>`
- `ctavern.chat_pull`
- `ctavern.send_item <玩家名> <物品ID> <数量>`
- `ctavern.set_name <昵称>`
- `ctavern.set_server <http://ip:port>`
