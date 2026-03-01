using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.Prefabs2;

namespace CalradiaTavern.UI.Map.Patches
{
    [PrefabExtension("MapBar", "descendant::ListPanel[@Id='MapBar']")]
    internal sealed class MapBarTavernButtonPatch : PrefabExtensionInsertPatch
    {
        [PrefabExtensionFileName]
        public string FileName => "MapBar_TavernButton";

        public override InsertType Type => InsertType.Child;

        public override int Index => 999;
    }
}
