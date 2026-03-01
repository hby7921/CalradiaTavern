using Bannerlord.UIExtenderEx.ViewModels;
using TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapBar;

namespace CalradiaTavern.UI.Map
{
    // Disabled on purpose: map overlay chat was causing lifecycle/input instability.
    // F8 quick chat remains available, and full panel is opened from tavern menu option.
    internal sealed class MapBarTavernMixin : BaseViewModelMixin<MapBarVM>
    {
        public MapBarTavernMixin(MapBarVM vm)
            : base(vm) { }
    }
}
