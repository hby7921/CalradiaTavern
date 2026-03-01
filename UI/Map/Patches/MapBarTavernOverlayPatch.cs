using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.Prefabs2;

namespace CalradiaTavern.UI.Map.Patches
{
    [PrefabExtension("MapBar", "descendant::ListPanel[@Id='MapBar']/../Children")]
    internal sealed class MapBarTavernOverlayPatch : PrefabExtensionInsertPatch
    {
        [PrefabExtensionFileName]
        public string FileName => "MapBar_TavernOverlay";

        public override InsertType Type => InsertType.Child;

        public override int Index => 999;
    }
}
