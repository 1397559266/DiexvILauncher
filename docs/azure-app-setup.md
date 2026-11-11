# 注册一个用于 Minecraft 正版登录的 Azure 应用（完整步骤）

这份文档从零开始，覆盖「创建 Azure 应用 → 配置到能登录 → 提交 Minecraft API 审核 → 审核通过后验证」的全过程。
目标读者是想使用**自己**的 Client ID 的开发者；普通玩家用发布版即可，不需要看这一页。

> 结论先行：Azure 应用注册本身 10 分钟就能完成，**真正决定能不能登录的是 Minecraft Services 的白名单审核**。
> 审核不通过时，OAuth、Xbox Live、XSTS 都会成功，最后一步 `login_with_xbox` 返回 `HTTP 403 Invalid app registration`。

---

## 0. 准备

| 项目 | 说明 |
| --- | --- |
| Microsoft 账户 | 用于登录 Azure 门户，个人账户即可 |
| Azure 租户（目录） | 应用注册必须挂在某个目录下。已有可用目录就不用新建 |
| 权限 | 在该目录里有「创建应用注册」的权限（自己创建的目录默认就有） |
| .NET 8 SDK | 只在需要自己编译启动器时用；发布版不需要 |
| 项目地址 | 审核要填，建议是公开仓库 + 一个可下载的 Release |

如果你**已经有**一个在用的 Azure 应用（比如之前提交过审核的那个），**不需要重新建租户**，
直接在第 1 步里新建一个应用注册即可。被拒或被 block 的旧 appID 建议弃用，换新的 GUID 重提。

---

## 1. 新建应用注册

1. 打开 <https://portal.azure.com> 并登录。
2. 右上角账户菜单确认**目录**选的是你要用的那个（有多个目录时尤其注意，建错目录会让审核对不上）。
3. 搜索并进入 **Microsoft Entra ID**（旧名 Azure Active Directory）。
4. 左侧 **应用注册** → 顶部 **新注册**。
5. 填写：

   | 字段 | 填什么 | 说明 |
   | --- | --- | --- |
   | 名称 | `ILauncher` / 你的产品名 | 会显示在微软的授权同意页上，别填得让人误会是官方 |
   | 受支持的账户类型 | **任何组织目录中的账户和个人 Microsoft 账户**，或 **仅个人 Microsoft 账户** | Minecraft 账号是个人账户，必须包含「个人 Microsoft 账户」 |
   | 重定向 URI | 先留空 | 下一步在「身份验证」里按平台添加更稳 |

   > 不要选「仅此组织目录中的账户」，否则个人微软账户根本登不进来。
   > 账户类型创建后**可以改**，但改了要重新确认下面的平台配置。

6. 点 **注册**。
7. 在应用的 **概述** 页面，记下 **应用程序(客户端) ID**（形如 `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`）。

   ⚠️ 只复制 **应用程序(客户端) ID**。**不要**复制「目录(租户) ID」或「对象 ID」——
   审核白名单认的是客户端 ID，填错这两个是「审核完成但仍 403」最常见的原因。

---

## 2. 配置为公共客户端（无密钥）

启动器是桌面程序，不能保存密钥，必须按 **public client** 配置。

1. 应用左侧 **身份验证**。
2. **高级设置** → **允许公共客户端流** → 选 **是** → 保存。

   > 不开这一项，设备码流程会返回 `AADSTS70002`，授权码 + PKCE 也会失败。

3. 同一页确认：
   - **隐式授权** 两项目前不需要，保持关闭。
   - **前端通道注销 URL**、**注销 URL** 留空。
   - 不需要「实时 SDK 支持」（Live SDK）。
4. **不要**创建客户端密码（证书和密码 → 保持为空）。桌面应用用不上，泄露风险反而更大。

---

## 3. 添加回环重定向 URI

浏览器授权码流程需要把用户送回本机端口，所以必须登记一个 loopback URI。

1. 仍在 **身份验证** → **添加平台** → 选择 **移动和桌面应用程序**。
2. 在「重定向 URI」里勾选或手动添加：

   ```text
   http://localhost
   ```

3. 保存。

关于这个值的行为（实测，Entra 的匹配规则）：

| 你登记的 | `http://localhost:<任意端口>/` | `http://localhost:<端口>/callback` |
| --- | --- | --- |
| `http://localhost` | ✅ 匹配（端口不敏感，路径需为 `/`） | ❌ 不匹配（路径不同） |
| `http://localhost:48291/callback` | ❌ | ✅ 仅端口 48291 |

所以**推荐登记 `http://localhost`**：启动器可以自行挑空闲端口，换端口也不用改 Azure 配置。
如果你更想登记精确地址，填 `http://localhost:48291/callback` 也可以——启动器的登录窗口会先试根路径，
再回退到精确地址，只要有一个对上就能登录。

> **设备码流程不需要任何重定向 URI**（它靠用户手动输入 code），所以只登记回环地址就够了。
> 如果门户或表单要求再填一个，可以加 `https://login.microsoftonline.com/common/oauth2/nativeclient`。

---

## 4. API 权限

**不用配。**

`XboxLive.signin` 不是能在 Azure 门户里勾选的权限，它是在登录时通过 scope 直接请求的；
并且必须用**消费者（个人账户）端点**：

```text
https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize
https://login.microsoftonline.com/consumers/oauth2/v2.0/token
https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode
```

用 `/common` 请求 `XboxLive.signin` 会得到 `AADSTS70011 invalid_scope`，这是很多人卡住的地方。

---

## 5. 把 Client ID 填进启动器

三种方式，任选其一：

1. **环境变量**（不改代码，适合先验证）：

   ```powershell
   $env:MS_CLIENT_ID = "你的 Application (client) ID"
   .\DiexvMinecraftLauncher.exe
   ```

   也支持 `ILAUNCHER_MS_CLIENT_ID`。环境变量优先级最高。

2. **登录窗口**：「账户」→「正版登录」→ 窗口里的「更换应用 ID」，粘贴后保存，会写进
   `%APPDATA%\ILauncher\settings.json` 的 `MicrosoftClientId`。

3. **内置常量**（发布版做法）：修改
   `src/ILauncher.Core/Services/SettingsService.cs` 里的 `BundledMicrosoftClientId`，然后重新打包发布。

> `settings.json` 里的值会覆盖内置常量。测试完记得清掉，否则换回内置 ID 不生效。
> 检查方法：`%APPDATA%\ILauncher\settings.json` 里 `MicrosoftClientId` 必须是空或你要的那个 GUID。

---

## 6. 先本地验证到 403（这一步就该到 403）

按上面配置好后点登录：

| 现象 | 含义 | 处理 |
| --- | --- | --- |
| 浏览器报 `AADSTS50011` | 重定向 URI 没登记对上 | 回到第 3 步，或直接登记 `http://localhost` |
| `AADSTS70002` / `7000218` | 没开「允许公共客户端流」 | 回到第 2 步 |
| `AADSTS700016` / `50059` | 客户端 ID 错，或应用不在该目录 | 重新复制「应用程序(客户端) ID」 |
| `AADSTS70011` | 用了 `/common` 请求 Xbox scope | 确认端点是 `/consumers` |
| 卡在「正在读取 Minecraft 档案」并提示无所有权 | 账号没买 Java 版 | 换账号，或这就是正常提示 |
| **`HTTP 403 Invalid app registration`** | **一切正常，只差白名单** | 进入第 7 步 |

启动器的回环监听和 PKCE 都在这之前就验证过了：能走到 403，说明 OAuth → Xbox Live → XSTS 全通。

---

## 7. 提交 Minecraft API 审核

1. 打开官方说明：<https://help.minecraft.net/hc/en-us/articles/16254801392141>
   （API 返回的 `aka.ms/AppRegInfo` 就是跳到这一篇。）
2. 从文章里进入 AppID 审核表单；社区常用的直链：
   <https://forms.cloud.microsoft/Pages/ResponsePage.aspx?id=v4j5cvGGr0GRqy180BHbR-ajEQ1td1ROpz00KtS8Gd5UNVpPTkVLNFVROVQxNkdRMEtXVjNQQjdXVC4u>
3. 按下面的模板填写（英文更稳）：

   ```text
   Application name: ILauncher / DiexvMinecraftLauncher
   Application (client) ID: <粘贴 GUID>
   Project / website: https://github.com/1397559266/DiexvMinecraftLauncher
   Download: https://github.com/1397559266/DiexvMinecraftLauncher/releases/latest
   Privacy policy: https://github.com/1397559266/DiexvMinecraftLauncher/blob/main/PRIVACY.md

   Description:
   An independent, free, non-commercial Minecraft: Java Edition launcher for Windows
   (WPF/.NET 8). Users sign in with their own Microsoft account through OAuth 2.0
   (authorization code + PKCE, system browser). After sign-in we call
   /entitlements/mcstore and refuse to launch when the account does not own Java Edition.
   Offline mode is limited to single-player and offline-mode servers and never emulates a
   licensed session. We do not distribute or modify any Minecraft game files; the launcher
   downloads official files from Mojang's own servers.
   ```

4. 提交前自查（这几条是社区里最常见的被拒原因）：

   - [ ] Release 里有可直接运行的构建，审核方能双击跑起来
   - [ ] 无法验证游戏所有权时确实不让进游戏
   - [ ] 仓库/README 是独立项目的样子，不是别的启动器的克隆说明
   - [ ] 表单里的 GUID 与构建里实际使用的完全一致
   - [ ] 提供了隐私政策链接
   - [ ] 没有借用其他启动器的 Client ID

5. 审核按批次进行，通常几天到几周。**结果不一定单独通知**——判断标准是接口是否放行。

---

## 8. 审核之后的验证

1. 在启动器里**重新登录**（不要只依赖旧令牌，虽然 appid 不变，但重登能确认整条链路）。
2. 预期：`login_with_xbox` 返回 200，日志里出现「正在校验游戏所有权」并通过，账号显示正版登录。
3. 如果仍然 403：
   - 确认实际使用的 Client ID 与提交的一致（第 5 步的检查方法）；
   - 确认不是旧的 `settings.json` 覆盖；
   - 回复 enforcement 的邮件，附上：Client ID、提交时间/批次、UTC 时间戳、完整响应体
     （`{"path":"/authentication/login_with_xbox","errorMessage":"Invalid app registration..."}`）、项目与 Release 链接。
4. 如果收到的答复是 **blocked**：没有公开申诉流程，整改后用**新的**应用注册重新提交，别在原 appID 上反复投。

---

## 9. 安全与运维

- **Client ID 不是密钥**，可以公开写在仓库和 Release 里；不要把它当秘密管理。
- **绝对不要**给桌面应用配客户端密码，也不要把它提交进仓库。
- 令牌保存在 `%APPDATA%\ILauncher\settings.json`，用 Windows DPAPI 按当前用户加密；换机器需要重新登录。
- 想撤销所有已发出的令牌：在应用里删除该用户的企业应用授权（Entra ID → 企业应用 → 用户设置），
  或直接停用应用注册。
- 每次更换 Client ID 都要重新走一遍审核；审核通过的是「这个 GUID」，不是「这个项目」。

---

## 附：检查清单

```text
[ ] 应用注册在正确的目录
[ ] 受支持的账户类型包含「个人 Microsoft 账户」
[ ] 「允许公共客户端流」= 是
[ ] 平台「移动和桌面应用程序」已添加
[ ] 重定向 URI：http://localhost（推荐）或 http://localhost:48291/callback
[ ] 没有创建客户端密码
[ ] 复制的是「应用程序(客户端) ID」
[ ] 启动器实际使用的 ID 与提交的一致
[ ] 本地测试能走到 403 Invalid app registration
[ ] 已提交 AppID 审核（附 Release / 隐私政策 / 所有权校验说明）
```
