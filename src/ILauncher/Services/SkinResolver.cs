using ILauncher.Core.Services;

namespace ILauncher.Services;

/// <summary>
/// 决定启动器界面应该显示哪张皮肤：开启「使用正版账号皮肤」且账号皮肤已缓存时用账号皮肤，
/// 否则退回皮肤库里选中的本地皮肤。账户页、主页、设置页和顶栏头像都走这里，避免各显示各的。
/// </summary>
public static class SkinResolver
{
    public static bool IsMicrosoftAccount(SettingsService settings) =>
        string.Equals(settings.Settings.AccountType, "微软账户", StringComparison.Ordinal) &&
        (!string.IsNullOrWhiteSpace(settings.Settings.MicrosoftAccessToken) ||
         !string.IsNullOrWhiteSpace(settings.Settings.MicrosoftAccessTokenProtected) ||
         !string.IsNullOrWhiteSpace(settings.Settings.MicrosoftRefreshToken) ||
         !string.IsNullOrWhiteSpace(settings.Settings.MicrosoftRefreshTokenProtected));

    public static string Resolve(SettingsService settings, SkinService? skins)
    {
        if (skins == null)
        {
            return settings.Settings.SkinPath;
        }

        if (settings.Settings.UseAccountSkin && IsMicrosoftAccount(settings))
        {
            var uuid = string.IsNullOrWhiteSpace(settings.Settings.MicrosoftUuid)
                ? settings.Settings.ProfileUuid
                : settings.Settings.MicrosoftUuid;
            if (skins.GetAccountSkinPath(uuid) is { Length: > 0 } accountSkin)
            {
                return accountSkin;
            }
        }

        return skins.Resolve(settings.Settings.SkinPath);
    }
}
