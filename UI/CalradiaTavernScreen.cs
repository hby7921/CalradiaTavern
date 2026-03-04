using System;
using System.Reflection;
using CalradiaTavern.UI.ViewModels;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace CalradiaTavern.UI
{
    internal sealed class CalradiaTavernScreen : ScreenBase
    {
        private const string MovieName = "CalradiaTavernScreen";
        private const string MovieNameFallback = "CalradiaTavern/CalradiaTavernScreen";
        private const string ChatScrollbarWidgetId = "ChatScrollbar";
        private const string OnlinePlayersScrollbarWidgetId = "OnlinePlayersScrollbar";
        private const string TradePlayersScrollbarWidgetId = "LeftMyMarketScrollbar";
        private const string ChatClipWidgetId = "ChatClip";
        private const string ChatListWidgetId = "ChatList";
        private const string PlayersClipWidgetId = "PlayersClip";
        private const string PlayersListWidgetId = "PlayersList";
        private const string TradePlayersClipWidgetId = "LeftMyMarketClip";
        private const string TradePlayersListWidgetId = "LeftMyMarketList";
        private const string TradePlayersScrollPanelId = "TradePlayersScrollPanel";
        private const string TradeItemsClipWidgetId = "MarketClip";
        private const string TradeItemsListWidgetId = "MarketList";
        private const string ChatScrollPanelId = "ChatScrollPanel";
        private const float WheelStep = 7f;

        private GauntletLayer _gauntletLayer;
        private GauntletMovieIdentifier _movieId;
        private TavernScreenVM _vm;
        private bool _movieLoaded;
        private bool _loadAttempted;
        private bool _layerAdded;
        private bool _isClosing;
        private bool _closeRequested;
        private int _lastChatVisualVersion = -1;
        private int _pendingScrollToBottomFrames;
        private object _chatScrollbarWidget;
        private object _onlinePlayersScrollbarWidget;
        private object _tradePlayersScrollbarWidget;
        private object _chatClipWidget;
        private object _chatListWidget;
        private object _playersClipWidget;
        private object _playersListWidget;
        private object _tradePlayersClipWidget;
        private object _tradePlayersListWidget;
        private object _tradePlayersScrollPanelWidget;
        private object _tradeItemsClipWidget;
        private object _tradeItemsListWidget;
        private object _chatScrollPanelWidget;
        private long _nextScrollDiagLogMs;
        private long _nextMissingWidgetLogMs;

        protected override void OnInitialize()
        {
            base.OnInitialize();
            CalradiaTavernDebug.Trace("Screen", "OnInitialize");
            EnsureUiCreated();
        }

        protected override void OnActivate()
        {
            base.OnActivate();
            CalradiaTavernDebug.Trace("Screen", "OnActivate begin");

            _isClosing = false;
            _closeRequested = false;
            _lastChatVisualVersion = -1;
            _pendingScrollToBottomFrames = 3;
            _chatScrollbarWidget = null;
            _onlinePlayersScrollbarWidget = null;
            _tradePlayersScrollbarWidget = null;
            _chatClipWidget = null;
            _chatListWidget = null;
            _playersClipWidget = null;
            _playersListWidget = null;
            _tradePlayersClipWidget = null;
            _tradePlayersListWidget = null;
            _tradePlayersScrollPanelWidget = null;
            _tradeItemsClipWidget = null;
            _tradeItemsListWidget = null;
            _chatScrollPanelWidget = null;
            _nextScrollDiagLogMs = 0;
            _nextMissingWidgetLogMs = 0;

            EnsureUiCreated();
            if (!_movieLoaded)
            {
                CalradiaTavernDebug.Trace("Screen", "OnActivate movie not loaded; closing to avoid input lock.");
                RequestClose();
                return;
            }

            if (_gauntletLayer != null)
            {
                _gauntletLayer.IsFocusLayer = true;
                _gauntletLayer.InputRestrictions.SetMouseVisibility(true);
                _gauntletLayer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                ScreenManager.TrySetFocus(_gauntletLayer);
            }

            _vm?.OnActivated();
            CalradiaTavernDebug.Trace("Screen", "OnActivate end");
        }

        protected override void OnDeactivate()
        {
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

            ReleaseInputAndLayer("OnDeactivate");

            base.OnDeactivate();
            CalradiaTavernDebug.Trace("Screen", "OnDeactivate end");
        }

        protected override void OnFinalize()
        {
            CalradiaTavernDebug.Trace("Screen", "OnFinalize begin");
            ReleaseInputAndLayer("OnFinalize");
            _gauntletLayer = null;
            _movieId = default(GauntletMovieIdentifier);
            _vm = null;
            _movieLoaded = false;
            _loadAttempted = false;
            _layerAdded = false;
            _chatScrollbarWidget = null;
            _onlinePlayersScrollbarWidget = null;
            _tradePlayersScrollbarWidget = null;
            _chatClipWidget = null;
            _chatListWidget = null;
            _playersClipWidget = null;
            _playersListWidget = null;
            _tradePlayersClipWidget = null;
            _tradePlayersListWidget = null;
            _tradePlayersScrollPanelWidget = null;
            _tradeItemsClipWidget = null;
            _tradeItemsListWidget = null;
            _chatScrollPanelWidget = null;
            _nextScrollDiagLogMs = 0;
            _nextMissingWidgetLogMs = 0;
            _pendingScrollToBottomFrames = 0;
            _lastChatVisualVersion = -1;

            base.OnFinalize();
            CalradiaTavernDebug.Trace("Screen", "OnFinalize end");
        }

        protected override void OnFrameTick(float dt)
        {
            base.OnFrameTick(dt);

            if (_isClosing)
            {
                return;
            }

            if (Input.IsKeyReleased(InputKey.Escape))
            {
                RequestClose();
            }

            try
            {
                _vm?.Tick(dt);
                TrackChatVisualVersionAndScheduleScroll();
                FlushPendingScrollToBottom();
                LogScrollWheelDiagnostics();
            }
            catch (Exception ex)
            {
                CalradiaTavernDebug.ReportException("Screen.OnFrameTick.Tick", ex);
            }

            TryCloseNow();
        }

        private void EnsureUiCreated()
        {
            if (_vm == null)
            {
                _vm = new TavernScreenVM(RequestClose);
            }

            if (_gauntletLayer == null)
            {
                _gauntletLayer = new GauntletLayer("GauntletLayer", 5000, true) { IsFocusLayer = true };
            }

            if (_loadAttempted || _gauntletLayer == null || _vm == null)
            {
                return;
            }

            _loadAttempted = true;
            try
            {
                CalradiaTavernDebug.Trace("Screen", "LoadMovie begin");
                string loadedMovieName = MovieName;
                _movieId = _gauntletLayer.LoadMovie(MovieName, _vm);
                bool hasMovie = _movieId != null && _movieId.Movie != null;
                bool hasRoot = hasMovie && _movieId.Movie.RootWidget != null;
                if (!hasRoot)
                {
                    CalradiaTavernDebug.Trace("Screen", "LoadMovie primary hasRoot=false. Try fallback movie name.");
                    loadedMovieName = MovieNameFallback;
                    _movieId = _gauntletLayer.LoadMovie(MovieNameFallback, _vm);
                    hasMovie = _movieId != null && _movieId.Movie != null;
                    hasRoot = hasMovie && _movieId.Movie.RootWidget != null;
                }
                _movieLoaded = hasRoot;
                CalradiaTavernDebug.Trace(
                    "Screen",
                    "LoadMovie result movieLoaded="
                        + _movieLoaded
                        + " hasMovie="
                        + hasMovie
                        + " hasRoot="
                        + hasRoot
                        + " movieName="
                        + loadedMovieName
                );
                if (!_movieLoaded)
                {
                    _movieId = default(GauntletMovieIdentifier);
                }
            }
            catch (Exception ex)
            {
                _movieLoaded = false;
                CalradiaTavernDebug.ReportException("Screen.EnsureUiCreated.LoadMovie", ex);
            }

            if (!_movieLoaded || _layerAdded)
            {
                return;
            }

            try
            {
                AddLayer(_gauntletLayer);
                _layerAdded = true;
            }
            catch (Exception ex)
            {
                CalradiaTavernDebug.ReportException("Screen.EnsureUiCreated.AddLayer", ex);
            }
        }

        private void RequestClose()
        {
            if (_isClosing)
            {
                return;
            }

            _closeRequested = true;
            CalradiaTavernDebug.Trace("Screen", "RequestClose accepted");
        }

        private void TryCloseNow()
        {
            if (!_closeRequested || _isClosing)
            {
                return;
            }

            if (ScreenManager.TopScreen != this)
            {
                _closeRequested = false;
                return;
            }

            _isClosing = true;
            try
            {
                ReleaseInputAndLayer("TryCloseNow");
                CalradiaTavernDebug.Trace("Screen", "TryCloseNow PopScreen");
                ScreenManager.PopScreen();
            }
            catch (Exception ex)
            {
                _isClosing = false;
                CalradiaTavernDebug.ReportException("Screen.TryCloseNow.PopScreen", ex);
            }
        }

        private void ReleaseInputAndLayer(string stage)
        {
            if (_gauntletLayer != null)
            {
                try
                {
                    _gauntletLayer.IsFocusLayer = false;
                    _gauntletLayer.InputRestrictions.ResetInputRestrictions();
                }
                catch (Exception ex)
                {
                    CalradiaTavernDebug.ReportException("Screen." + stage + ".ResetInputRestrictions", ex);
                }
            }

            if (_gauntletLayer != null && _movieLoaded && _movieId != null)
            {
                try
                {
                    _gauntletLayer.ReleaseMovie(_movieId);
                }
                catch (Exception ex)
                {
                    CalradiaTavernDebug.ReportException("Screen." + stage + ".ReleaseMovie", ex);
                }
                finally
                {
                    _movieId = default(GauntletMovieIdentifier);
                    _movieLoaded = false;
                }
            }

            if (_gauntletLayer != null && _layerAdded)
            {
                try
                {
                    RemoveLayer(_gauntletLayer);
                }
                catch (Exception ex)
                {
                    CalradiaTavernDebug.ReportException("Screen." + stage + ".RemoveLayer", ex);
                }
                finally
                {
                    _layerAdded = false;
                }
            }
        }

        private void TrackChatVisualVersionAndScheduleScroll()
        {
            if (_vm == null || !_vm.IsChatTab)
            {
                return;
            }

            int visualVersion = _vm.ChatVisualVersion;
            if (visualVersion == _lastChatVisualVersion)
            {
                return;
            }

            _lastChatVisualVersion = visualVersion;
            _pendingScrollToBottomFrames = 3;
            CalradiaTavernDebug.Trace(
                "Screen",
                "ChatVisualVersion changed version="
                    + visualVersion
                    + " lines="
                    + (_vm.ChatLines?.Count ?? 0)
                    + " pendingScrollFrames="
                    + _pendingScrollToBottomFrames
            );
        }

        private void FlushPendingScrollToBottom()
        {
            if (_pendingScrollToBottomFrames <= 0 || _vm == null || !_vm.IsChatTab)
            {
                return;
            }

            if (Input.IsKeyPressed(InputKey.MouseScrollUp) || Input.IsKeyPressed(InputKey.MouseScrollDown))
            {
                return;
            }

            if (TryScrollChatToBottom())
            {
                _pendingScrollToBottomFrames--;
            }
        }

        private bool TryScrollChatToBottom()
        {
            object scrollbar = ResolveWidgetById(ref _chatScrollbarWidget, ChatScrollbarWidgetId);
            if (scrollbar == null)
            {
                long nowMs = CalradiaTavernDebug.NowMs;
                if (nowMs >= _nextMissingWidgetLogMs)
                {
                    _nextMissingWidgetLogMs = nowMs + 1000L;
                    CalradiaTavernDebug.Trace("Screen", "TryScrollChatToBottom failed: ChatScrollbar not found");
                }
                return false;
            }

            float minValue = ReadFloatProperty(scrollbar, "MinValue", 0f);
            float maxValue = ReadFloatProperty(scrollbar, "MaxValue", 0f);
            // In Gauntlet scrollbar, chat "bottom" maps to MinValue for this screen setup.
            float targetValue = minValue;
            float beforeValue = ReadFloatProperty(
                scrollbar,
                "ValueFloat",
                ReadFloatProperty(scrollbar, "Value", 0f)
            );
            MethodInfo setValueForcedMethod = scrollbar.GetType().GetMethod(
                "SetValueForced",
                BindingFlags.Public | BindingFlags.Instance
            );
            if (setValueForcedMethod != null)
            {
                setValueForcedMethod.Invoke(scrollbar, new object[] { targetValue });
                float afterValue = ReadFloatProperty(
                    scrollbar,
                    "ValueFloat",
                    ReadFloatProperty(scrollbar, "Value", 0f)
                );
                CalradiaTavernDebug.Trace(
                    "Screen",
                    "TryScrollChatToBottom SetValueForced before="
                        + beforeValue.ToString("0.###")
                        + " after="
                        + afterValue.ToString("0.###")
                        + " target="
                        + targetValue.ToString("0.###")
                        + " min="
                        + minValue.ToString("0.###")
                        + " max="
                        + maxValue.ToString("0.###")
                );
                return true;
            }

            PropertyInfo valueFloatProperty = scrollbar.GetType().GetProperty(
                "ValueFloat",
                BindingFlags.Public | BindingFlags.Instance
            );
            if (valueFloatProperty == null || !valueFloatProperty.CanWrite)
            {
                CalradiaTavernDebug.Trace(
                    "Screen",
                    "TryScrollChatToBottom failed: no SetValueForced and ValueFloat not writable"
                );
                return false;
            }

            valueFloatProperty.SetValue(scrollbar, targetValue, null);
            float afterSetValue = ReadFloatProperty(
                scrollbar,
                "ValueFloat",
                ReadFloatProperty(scrollbar, "Value", 0f)
            );
            CalradiaTavernDebug.Trace(
                "Screen",
                "TryScrollChatToBottom ValueFloat before="
                    + beforeValue.ToString("0.###")
                    + " after="
                    + afterSetValue.ToString("0.###")
                    + " target="
                    + targetValue.ToString("0.###")
                    + " min="
                    + minValue.ToString("0.###")
                    + " max="
                    + maxValue.ToString("0.###")
            );
            return true;
        }

        private void LogScrollWheelDiagnostics()
        {
            long nowMs = CalradiaTavernDebug.NowMs;
            bool wheelUp = Input.IsKeyPressed(InputKey.MouseScrollUp);
            bool wheelDown = Input.IsKeyPressed(InputKey.MouseScrollDown);
            if (!wheelUp && !wheelDown && nowMs < _nextScrollDiagLogMs)
            {
                return;
            }

            _nextScrollDiagLogMs = nowMs + (wheelUp || wheelDown ? 120L : 1500L);
            CalradiaTavernDebug.Trace(
                "Screen",
                "ScrollInput wheelUp="
                    + wheelUp
                    + " wheelDown="
                    + wheelDown
                    + " isChatTab="
                    + (_vm?.IsChatTab ?? false)
            );

            LogScrollbarState(ChatScrollbarWidgetId, ref _chatScrollbarWidget);
            LogScrollbarState(OnlinePlayersScrollbarWidgetId, ref _onlinePlayersScrollbarWidget);
            LogScrollbarState(TradePlayersScrollbarWidgetId, ref _tradePlayersScrollbarWidget);
            LogScrollablePanelState(
                "chat",
                ChatClipWidgetId,
                ref _chatClipWidget,
                ChatListWidgetId,
                ref _chatListWidget,
                _vm?.ChatLines?.Count ?? -1
            );
            LogScrollablePanelState(
                "players",
                PlayersClipWidgetId,
                ref _playersClipWidget,
                PlayersListWidgetId,
                ref _playersListWidget,
                _vm?.OnlinePlayers?.Count ?? -1
            );
            LogScrollablePanelState(
                "tradePlayers",
                TradePlayersClipWidgetId,
                ref _tradePlayersClipWidget,
                TradePlayersListWidgetId,
                ref _tradePlayersListWidget,
                _vm?.MyMarketListings?.Count ?? -1
            );
            LogScrollablePanelState(
                "tradeItems",
                TradeItemsClipWidgetId,
                ref _tradeItemsClipWidget,
                TradeItemsListWidgetId,
                ref _tradeItemsListWidget,
                _vm?.MarketListings?.Count ?? -1
            );
        }

        private void HandleManualScrollFallback()
        {
            bool wheelUp = Input.IsKeyPressed(InputKey.MouseScrollUp);
            bool wheelDown = Input.IsKeyPressed(InputKey.MouseScrollDown);
            if (!wheelUp && !wheelDown)
            {
                return;
            }

            float delta = wheelDown ? WheelStep : -WheelStep;
            bool moved = false;

            if (_vm != null && _vm.IsChatTab)
            {
                moved = TryNudgeScrollbar(
                    ChatScrollbarWidgetId,
                    ref _chatScrollbarWidget,
                    delta,
                    ChatClipWidgetId,
                    ref _chatClipWidget,
                    ChatListWidgetId,
                    ref _chatListWidget
                );
                if (!moved)
                {
                    moved = TryNudgeScrollPanel(ChatScrollPanelId, ref _chatScrollPanelWidget, delta);
                }
            }
            else if (_vm != null && _vm.IsMarketTab)
            {
                moved = TryNudgeScrollPanel(TradePlayersScrollPanelId, ref _tradePlayersScrollPanelWidget, delta);
                if (!moved)
                {
                    moved = TryNudgeScrollbar(
                        TradePlayersScrollbarWidgetId,
                        ref _tradePlayersScrollbarWidget,
                        delta,
                        TradePlayersClipWidgetId,
                        ref _tradePlayersClipWidget,
                        TradePlayersListWidgetId,
                        ref _tradePlayersListWidget
                    );
                }
            }

            if (moved)
            {
                CalradiaTavernDebug.Trace(
                    "Screen",
                    "ManualWheelFallback moved delta=" + delta.ToString("0.###")
                );
            }
        }

        private bool TryNudgeScrollbar(
            string scrollbarId,
            ref object scrollbarCache,
            float delta,
            string clipId,
            ref object clipCache,
            string listId,
            ref object listCache
        )
        {
            object clip = ResolveWidgetById(ref clipCache, clipId);
            object list = ResolveWidgetById(ref listCache, listId);
            if (clip == null || list == null)
            {
                return false;
            }

            float clipHeight = ReadWidgetHeight(clip);
            float listHeight = ReadWidgetHeight(list);
            bool overflow = clipHeight > 0f && listHeight > clipHeight + 0.5f;
            if (!overflow)
            {
                return false;
            }

            object scrollbar = ResolveWidgetById(ref scrollbarCache, scrollbarId);
            if (scrollbar == null)
            {
                return false;
            }

            float minValue = ReadFloatProperty(scrollbar, "MinValue", 0f);
            float maxValue = ReadFloatProperty(scrollbar, "MaxValue", 100f);
            float currentValue = ReadFloatProperty(
                scrollbar,
                "ValueFloat",
                ReadFloatProperty(scrollbar, "Value", minValue)
            );
            float targetValue = Math.Min(maxValue, Math.Max(minValue, currentValue + delta));
            if (Math.Abs(targetValue - currentValue) < 0.001f)
            {
                return false;
            }

            MethodInfo setValueForcedMethod = scrollbar.GetType().GetMethod(
                "SetValueForced",
                BindingFlags.Public | BindingFlags.Instance
            );
            if (setValueForcedMethod != null)
            {
                setValueForcedMethod.Invoke(scrollbar, new object[] { targetValue });
                return true;
            }

            PropertyInfo valueFloatProperty = scrollbar.GetType().GetProperty(
                "ValueFloat",
                BindingFlags.Public | BindingFlags.Instance
            );
            if (valueFloatProperty != null && valueFloatProperty.CanWrite)
            {
                valueFloatProperty.SetValue(scrollbar, targetValue, null);
                return true;
            }

            return false;
        }

        private bool TryNudgeScrollPanel(string panelId, ref object panelCache, float delta)
        {
            object panel = ResolveWidgetById(ref panelCache, panelId);
            if (panel == null)
            {
                return false;
            }

            if (
                TryAdjustNumericProperty(panel, "VerticalScrollValue", delta)
                || TryAdjustNumericProperty(panel, "ScrollOffset", delta)
                || TryAdjustNumericProperty(panel, "TargetScrollOffset", delta)
                || TryAdjustNumericProperty(panel, "CurrentScrollOffset", delta)
            )
            {
                return true;
            }

            if (
                TryInvokeScrollMethod(panel, "SetVerticalScrollValue", delta)
                || TryInvokeScrollMethod(panel, "SetTargetScrollOffset", delta)
                || TryInvokeScrollMethod(panel, "ScrollByAmount", delta)
                || TryInvokeScrollMethod(panel, "ScrollBy", delta)
            )
            {
                return true;
            }

            return false;
        }

        private static bool TryInvokeScrollMethod(object target, string methodName, float delta)
        {
            if (target == null || string.IsNullOrEmpty(methodName))
            {
                return false;
            }

            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
            {
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 1)
            {
                return false;
            }

            Type type = parameters[0].ParameterType;
            try
            {
                if (type == typeof(float) || type == typeof(double))
                {
                    method.Invoke(target, new object[] { delta });
                    return true;
                }

                if (type == typeof(int))
                {
                    method.Invoke(target, new object[] { (int)Math.Round(delta) });
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryAdjustNumericProperty(object target, string name, float delta)
        {
            if (target == null || string.IsNullOrEmpty(name))
            {
                return false;
            }

            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanRead || !property.CanWrite)
            {
                return false;
            }

            object value = property.GetValue(target, null);
            if (value is float f)
            {
                property.SetValue(target, f + delta, null);
                return true;
            }

            if (value is double d)
            {
                property.SetValue(target, d + delta, null);
                return true;
            }

            if (value is int i)
            {
                property.SetValue(target, i + (int)Math.Round(delta), null);
                return true;
            }

            return false;
        }

        private void LogScrollbarState(string widgetId, ref object cachedWidget)
        {
            object widget = ResolveWidgetById(ref cachedWidget, widgetId);
            if (widget == null)
            {
                long nowMs = CalradiaTavernDebug.NowMs;
                if (nowMs >= _nextMissingWidgetLogMs)
                {
                    _nextMissingWidgetLogMs = nowMs + 1000L;
                    CalradiaTavernDebug.Trace("Screen", "ScrollDiag missing widget id=" + widgetId);
                }
                return;
            }

            float minValue = ReadFloatProperty(widget, "MinValue", 0f);
            float maxValue = ReadFloatProperty(widget, "MaxValue", 0f);
            float value = ReadFloatProperty(widget, "ValueFloat", ReadFloatProperty(widget, "Value", 0f));
            int childCount = ReadIntProperty(widget, "ChildCount", 0);
            CalradiaTavernDebug.Trace(
                "Screen",
                "ScrollDiag id="
                    + widgetId
                    + " value="
                    + value.ToString("0.###")
                    + " min="
                    + minValue.ToString("0.###")
                    + " max="
                    + maxValue.ToString("0.###")
                    + " childCount="
                    + childCount
            );
        }

        private void LogScrollablePanelState(
            string name,
            string clipWidgetId,
            ref object clipCache,
            string listWidgetId,
            ref object listCache,
            int vmCount
        )
        {
            object clip = ResolveWidgetById(ref clipCache, clipWidgetId);
            object list = ResolveWidgetById(ref listCache, listWidgetId);
            if (clip == null || list == null)
            {
                return;
            }

            float clipHeight = ReadWidgetHeight(clip);
            float listHeight = ReadWidgetHeight(list);
            int listChildCount = ReadIntProperty(list, "ChildCount", -1);
            bool hasOverflow = clipHeight > 0f && listHeight > clipHeight + 0.5f;
            CalradiaTavernDebug.Trace(
                "Screen",
                "ScrollDiagPanel name="
                    + name
                    + " clipH="
                    + clipHeight.ToString("0.###")
                    + " listH="
                    + listHeight.ToString("0.###")
                    + " overflow="
                    + hasOverflow
                    + " listChildCount="
                    + listChildCount
                    + " vmCount="
                    + vmCount
            );
        }

        private static float ReadWidgetHeight(object widget)
        {
            if (widget == null)
            {
                return 0f;
            }

            float direct = ReadFloatProperty(widget, "ScaledSuggestedHeight", float.NaN);
            if (!float.IsNaN(direct) && direct > 0f)
            {
                return direct;
            }

            direct = ReadFloatProperty(widget, "SuggestedHeight", float.NaN);
            if (!float.IsNaN(direct) && direct > 0f)
            {
                return direct;
            }

            float fromSize = ReadVectorYProperty(widget, "ScaledSize");
            if (fromSize > 0f)
            {
                return fromSize;
            }

            fromSize = ReadVectorYProperty(widget, "Size");
            if (fromSize > 0f)
            {
                return fromSize;
            }

            fromSize = ReadVectorYProperty(widget, "LocalSize");
            if (fromSize > 0f)
            {
                return fromSize;
            }

            return 0f;
        }

        private static float ReadVectorYProperty(object target, string propertyName)
        {
            if (target == null || string.IsNullOrEmpty(propertyName))
            {
                return 0f;
            }

            PropertyInfo prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanRead)
            {
                return 0f;
            }

            object value = prop.GetValue(target, null);
            return ReadVectorYValue(value);
        }

        private static float ReadVectorYValue(object value)
        {
            if (value == null)
            {
                return 0f;
            }

            if (value is float f)
            {
                return f;
            }

            if (value is double d)
            {
                return (float)d;
            }

            if (value is int i)
            {
                return i;
            }

            Type type = value.GetType();
            PropertyInfo yProp = type.GetProperty("Y", BindingFlags.Public | BindingFlags.Instance)
                ?? type.GetProperty("y", BindingFlags.Public | BindingFlags.Instance);
            if (yProp != null && yProp.CanRead)
            {
                object y = yProp.GetValue(value, null);
                return ReadVectorYValue(y);
            }

            FieldInfo yField = type.GetField("Y", BindingFlags.Public | BindingFlags.Instance)
                ?? type.GetField("y", BindingFlags.Public | BindingFlags.Instance);
            if (yField != null)
            {
                object y = yField.GetValue(value);
                return ReadVectorYValue(y);
            }

            return 0f;
        }

        private object ResolveWidgetById(ref object cachedWidget, string widgetId)
        {
            if (cachedWidget != null)
            {
                return cachedWidget;
            }

            object rootWidget = _movieId?.Movie?.RootWidget;
            if (rootWidget == null || string.IsNullOrEmpty(widgetId))
            {
                return null;
            }

            cachedWidget = FindWidgetRecursive(rootWidget, widgetId);
            return cachedWidget;
        }

        private static object FindWidgetRecursive(object widget, string widgetId)
        {
            if (widget == null)
            {
                return null;
            }

            string id = ReadStringProperty(widget, "Id");
            if (string.Equals(id, widgetId, StringComparison.Ordinal))
            {
                return widget;
            }

            int childCount = ReadIntProperty(widget, "ChildCount", 0);
            if (childCount <= 0)
            {
                return null;
            }

            MethodInfo getChildMethod = widget.GetType().GetMethod("GetChild", BindingFlags.Public | BindingFlags.Instance);
            if (getChildMethod == null)
            {
                return null;
            }

            for (int i = 0; i < childCount; i++)
            {
                object child = null;
                try
                {
                    child = getChildMethod.Invoke(widget, new object[] { i });
                }
                catch
                {
                    child = null;
                }

                object found = FindWidgetRecursive(child, widgetId);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static string ReadStringProperty(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanRead)
            {
                return string.Empty;
            }

            object value = property.GetValue(target, null);
            return value as string ?? string.Empty;
        }

        private static int ReadIntProperty(object target, string name, int fallback)
        {
            if (target == null || string.IsNullOrEmpty(name))
            {
                return fallback;
            }

            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanRead)
            {
                return fallback;
            }

            object value = property.GetValue(target, null);
            if (value is int asInt)
            {
                return asInt;
            }

            if (value is float asFloat)
            {
                return (int)Math.Round(asFloat);
            }

            if (value is double asDouble)
            {
                return (int)Math.Round(asDouble);
            }

            return fallback;
        }

        private static float ReadFloatProperty(object target, string name, float fallback)
        {
            if (target == null || string.IsNullOrEmpty(name))
            {
                return fallback;
            }

            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanRead)
            {
                return fallback;
            }

            object value = property.GetValue(target, null);
            if (value is float asFloat)
            {
                return asFloat;
            }

            if (value is int asInt)
            {
                return asInt;
            }

            if (value is double asDouble)
            {
                return (float)asDouble;
            }

            return fallback;
        }
    }
}
