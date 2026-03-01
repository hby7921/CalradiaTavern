using System;
using CalradiaTavern.UI.ViewModels;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace CalradiaTavern.UI
{
    internal sealed class CalradiaTavernScreen : ScreenBase
    {
        private const string MovieName = "CalradiaTavern/CalradiaTavernScreen";

        private GauntletLayer _gauntletLayer;
        private TavernScreenVM _vm;
        private bool _isClosing;
        private bool _closeRequested;

        protected override void OnInitialize()
        {
            base.OnInitialize();
            CalradiaTavernDebug.Trace("Screen", "OnInitialize");
            _vm = new TavernScreenVM(RequestClose);
        }

        protected override void OnActivate()
        {
            base.OnActivate();
            CalradiaTavernDebug.Trace("Screen", "OnActivate begin");
            _isClosing = false;
            _closeRequested = false;

            _gauntletLayer = new GauntletLayer("GauntletLayer", 250, false)
            {
                IsFocusLayer = true,
            };

            try
            {
                CalradiaTavernDebug.Trace("Screen", "LoadMovie begin");
                _gauntletLayer.LoadMovie(MovieName, _vm);
                CalradiaTavernDebug.Trace("Screen", "LoadMovie ok");
            }
            catch (Exception ex)
            {
                CalradiaTavernDebug.ReportException("Screen.OnActivate.LoadMovie", ex);
                if (ScreenManager.TopScreen == this)
                {
                    ScreenManager.PopScreen();
                }
                return;
            }

            _gauntletLayer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
            AddLayer(_gauntletLayer);
            ScreenManager.TrySetFocus(_gauntletLayer);
            _vm.OnActivated();
            CalradiaTavernDebug.Trace("Screen", "OnActivate end");
        }

        protected override void OnDeactivate()
        {
            base.OnDeactivate();
            CalradiaTavernDebug.Trace("Screen", "OnDeactivate begin");
            _isClosing = true;

            try
            {
                _vm?.OnDeactivated();
            }
            catch (Exception ex)
            {
                CalradiaTavernDebug.ReportException("Screen.OnDeactivate.OnDeactivated", ex);
            }

            if (_gauntletLayer != null)
            {
                try
                {
                    _gauntletLayer.InputRestrictions.ResetInputRestrictions();
                }
                catch (Exception ex)
                {
                    CalradiaTavernDebug.ReportException("Screen.OnDeactivate.ResetInputRestrictions", ex);
                }
            }

            _gauntletLayer = null;
            CalradiaTavernDebug.Trace("Screen", "OnDeactivate end");
        }

        protected override void OnFinalize()
        {
            CalradiaTavernDebug.Trace("Screen", "OnFinalize begin");
            base.OnFinalize();
            CalradiaTavernDebug.Trace("Screen", "OnFinalize end");
        }

        protected override void OnFrameTick(float dt)
        {
            base.OnFrameTick(dt);

            if (Input.IsKeyReleased(InputKey.Escape))
            {
                RequestClose();
            }

            try
            {
                _vm?.Tick(dt);
            }
            catch (Exception ex)
            {
                CalradiaTavernDebug.ReportException("Screen.OnFrameTick.Tick", ex);
            }

            TryCloseNow();
        }

        private void RequestClose()
        {
            if (_isClosing)
            {
                CalradiaTavernDebug.Trace("Screen", "RequestClose ignored: isClosing");
                return;
            }

            CalradiaTavernDebug.Trace("Screen", "RequestClose accepted");
            _closeRequested = true;
        }

        private void TryCloseNow()
        {
            if (!_closeRequested || _isClosing)
            {
                return;
            }

            if (ScreenManager.TopScreen != this)
            {
                CalradiaTavernDebug.Trace("Screen", "TryCloseNow canceled: top screen changed");
                _closeRequested = false;
                return;
            }

            _isClosing = true;
            try
            {
                CalradiaTavernDebug.Trace("Screen", "TryCloseNow PopScreen");
                ScreenManager.PopScreen();
            }
            catch (Exception ex)
            {
                _isClosing = false;
                CalradiaTavernDebug.ReportException("Screen.TryCloseNow.PopScreen", ex);
            }
        }
    }
}
