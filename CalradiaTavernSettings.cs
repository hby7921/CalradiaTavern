using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace CalradiaTavern
{
    public sealed class CalradiaTavernSettings : AttributeGlobalSettings<CalradiaTavernSettings>
    {
        public const string BuiltInServerUrl = "http://120.26.183.181:18080";

        public override string Id => "CalradiaTavernSettings";

        public override string DisplayName => "Calradia Tavern";

        public override string FolderName => "CalradiaTavern";

        public override string FormatType => "json";

        [SettingPropertyGroup("Network", GroupOrder = 0)]
        [SettingPropertyText(
            "Server URL (Optional)",
            Order = 0,
            RequireRestart = false,
            HintText = "留空时默认使用内置服务器。"
        )]
        public string ServerUrl { get; set; } = string.Empty;

        [SettingPropertyGroup("Profile", GroupOrder = 1)]
        [SettingPropertyText(
            "Chat User Name",
            Order = 0,
            RequireRestart = false,
            HintText = "可输入中文或英文，聊天时显示该用户名。留空则使用游戏角色名。"
        )]
        public string UserName { get; set; } = string.Empty;
    }
}
