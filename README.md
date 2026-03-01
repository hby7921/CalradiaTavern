# Calradia Tavern

骑砍2单机模组：安装该模组的玩家可通过外部中转服进行公屏聊天和定向物品交易。

## 你要的形态

- 地图界面左下角 `MapBar` 新增按钮：`酒馆`
- 点击后打开酒馆窗口，含两个页签：
  - `聊天`：输入消息、改昵称、查看消息历史
  - `交易`：输入目标玩家名，选择背包物品并发送
- 不打开窗口时，仍会在战役界面看到其他玩家的新消息提示

## 模块结构

- `SubModule.cs` / `SubModule.xml`: 模组入口
- `Behaviors/CalradiaTavernCampaignBehavior.cs`: 聊天/交易逻辑与轮询
- `UI/Map/*`: 左下酒馆按钮注入
- `UI/CalradiaTavernScreen.cs`: 酒馆弹窗
- `UI/ViewModels/*`: 酒馆窗口 VM
- `GUI/PrefabExtensions/Map/MapBar_TavernButton.xml`: MapBar 按钮补丁 XML
- `GUI/Prefabs/CalradiaTavern/CalradiaTavernScreen.xml`: 酒馆 UI XML
- `Server/mock_server.py`: 联调中转服

## 编译与部署

1. 检查 `CalradiaTavern.csproj` 中的 `BannerlordGameDir`
2. 构建：

```powershell
dotnet build d:\Amods\CalradiaTavern\CalradiaTavern.csproj -c Release
```

3. 构建后自动复制到：
`<游戏目录>\Modules\CalradiaTavern`

## 启动中转服

```powershell
python d:\Amods\CalradiaTavern\Server\mock_server.py
```

默认监听：`http://127.0.0.1:18080`

## 控制台命令（可选）

- `ctavern.open`
- `ctavern.chat_send <消息>`
- `ctavern.chat_pull`
- `ctavern.send_item <玩家名> <物品ID> <数量>`
- `ctavern.set_name <昵称>`
- `ctavern.set_server <http://ip:port>`

## 中转服说明

`mock_server.py` 是内存服，重启会丢数据。  
正式服建议加：登录鉴权、签名、防刷、持久化数据库。
