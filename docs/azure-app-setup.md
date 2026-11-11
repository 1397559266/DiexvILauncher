# 注册 Azure 应用并通过 Minecraft API 审核

普通玩家不需要看这一页：发布版已经内置了用于审核的 Application (client) ID。
只有想使用**自己**的 Azure 应用（开发、自建构建、或想完全掌控注册信息的团队）时才需要按下面的步骤操作。

> 不要借用 PCL2、HMCL、官方启动器或其他任何项目的 Client ID。审核是按应用发放的，
> 借用别人的 ID 违反对方许可，也可能被随时吊销。

## 1. 准备一个能注册应用的目录

如果 Azure 提示“在目录外部创建应用程序的功能已被弃用”，说明当前登录的 Microsoft 账户还没有可用租户。
可以任选一种方式获得租户：

- [Azure 免费账户](https://azure.microsoft.com/free/)：创建默认目录与订阅；应用注册本身不收费。
- [Azure for Students](https://azure.microsoft.com/free/students/)：学生身份验证通过后通常不需要信用卡。
- 已有工作/学校账户：直接去 [Entra 管理中心](https://entra.microsoft.com) 的应用注册；看不到入口时需要管理员给 Application Developer 权限。

## 2. 注册应用

1. 打开 [Azure Portal](https://portal.azure.com)，确认右上角切换到目标目录。
2. **Microsoft Entra ID** → **应用注册** → **新注册**。
3. 名称填 `ILauncher`（或你自己的产品名）。
4. 受支持的账户类型选 **“任何组织目录中的账户和个人 Microsoft 账户”**，只登录个人 Minecraft 账户时选 **“个人 Microsoft 账户”** 也可以。
   **不要**选“仅此组织目录”，否则个人账户无法登录。
5. 注册完成后，在 **概述** 复制 **应用程序(客户端) ID**（形如 `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`）。

## 3. 配置公共客户端与回环重定向

1. **身份验证** → **高级设置** → 把 **“允许公共客户端流”** 设为 **“是”**（启动器没有客户端密码）。
2. **身份验证** → **添加平台** → **移动和桌面应用程序**。
3. 在重定向 URI 里添加：

   ```text
   http://localhost
   ```

   按这个形式登记时**端口不敏感**，启动器可以自行选择空闲端口；路径需要是 `/`。
   如果更想登记精确地址，也可以填 `http://localhost:48291/callback`，启动器会优先尝试根路径、
   再回退到精确地址，两者任一登记成功即可。

4. 保存。

## 4. 填入启动器

发布版内置了已提交审核的 ID。自建构建可以用环境变量覆盖：

```powershell
$env:MS_CLIENT_ID = "你的 Application (client) ID"
.\DiexvMinecraftLauncher.exe
```

也支持 `ILAUNCHER_MS_CLIENT_ID`，或在「正版登录」窗口里点「更换应用 ID」后粘贴。

## 5. 提交 Minecraft API 审核

新应用在通过审核前，`POST https://api.minecraftservices.com/authentication/login_with_xbox` 会返回：

```json
{
  "path": "/authentication/login_with_xbox",
  "errorMessage": "Invalid app registration, see https://aka.ms/AppRegInfo for more information"
}
```

这说明 OAuth、Xbox Live、XSTS 都已经成功，只是这个 Client ID 还不在 Minecraft Services 的白名单里。

- 官方说明：[Minecraft: Java Edition 第三方启动器 AppID 审核](https://help.minecraft.net/hc/en-us/articles/16254801392141)
- 提交入口：该文章里的 AppID 审核表单（社区常用的直链是 [Microsoft Forms](https://forms.cloud.microsoft/Pages/ResponsePage.aspx?id=v4j5cvGGr0GRqy180BHbR-ajEQ1td1ROpz00KtS8Gd5UNVpPTkVLNFVROVQxNkdRMEtXVjNQQjdXVC4u)）。
- 审核按批次进行，通常需要数天到数周；结果不一定单独通知，**以接口是否放行为准**。

提交时建议附上：

- Application (client) ID（严格照抄，不要填成目录/租户 ID）
- 项目主页与 Release 下载地址
- 一句话说明：启动器用途、是否要求用户拥有正版、是否分发游戏文件
- 隐私政策链接

## 6. 常见被拒原因

按社区反馈整理，供排查：

- 没有可下载的发布包，审核方无法实际运行。
- 允许在无法验证游戏所有权的情况下进入游戏（这是目前最容易踩的一条）。
- 项目描述像是其他启动器的克隆，或仓库里没有任何可评估的独立实现。
- 提交的标识不是 Application (client) ID，或与构建里实际使用的不一致。
- 反复提交同一个未通过的应用；被 block 后通常没有公开申诉渠道，建议整改后用**新的**应用重新提交。

审核层面不提供技术排障，邮件回复一般是模板，因此提交前把上面这些自查一遍比事后申诉更有效。
