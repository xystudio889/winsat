using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;

namespace winsat.helpers
{
    /// <summary>
    /// 依据 <see cref="AppSettings.Background"/> 创建窗口背景材质：
    /// Mica、Mica Alt、Arcylic、Arcylic Thin；None 表示不使用任何透明效果。
    /// 系统不支持所请求的材质时会自动回退，全部不可用时返回 null（使用系统默认背景）。
    /// </summary>
    internal static class AppBackground
    {
        /// <summary>创建背景材质。传入 None 或材质均不受支持时返回 null。</summary>
        public static SystemBackdrop? Create(string background)
        {
            bool micaSupported = MicaController.IsSupported();
            bool acrylicSupported = DesktopAcrylicController.IsSupported();

            switch (background)
            {
                case AppSettings.None:
                    // 不使用任何透明效果，由调用方绘制不透明背景。
                    return null;

                case AppSettings.MicaAlt when micaSupported:
                    return new MicaBackdrop { Kind = MicaKind.BaseAlt };

                case AppSettings.ArcylicThin when acrylicSupported:
                    return new AcrylicThinBackdrop();

                case AppSettings.Arcylic when acrylicSupported:
                    return new DesktopAcrylicBackdrop();

                case AppSettings.Mica when micaSupported:
                    return new MicaBackdrop { Kind = MicaKind.Base };
            }

            // 回退：优先 Mica，其次是 Acrylic，都不支持时由系统绘制默认背景。
            if (micaSupported)
            {
                return new MicaBackdrop { Kind = MicaKind.Base };
            }

            return acrylicSupported ? new DesktopAcrylicBackdrop() : null;
        }

        /// <summary>
        /// Arcylic Thin 材质。WinUI 内置的 <see cref="DesktopAcrylicBackdrop"/> 只提供 Base，
        /// Thin 变体需要直接通过 <see cref="DesktopAcrylicController"/> 实现。
        /// </summary>
        private sealed class AcrylicThinBackdrop : SystemBackdrop
        {
            private DesktopAcrylicController? _controller;

            protected override void OnTargetConnected(
                ICompositionSupportsSystemBackdrop connectedTarget,
                XamlRoot xamlRoot)
            {
                base.OnTargetConnected(connectedTarget, xamlRoot);

                if (_controller is not null)
                {
                    return;
                }

                try
                {
                    _controller = new DesktopAcrylicController
                    {
                        Kind = DesktopAcrylicKind.Thin,
                    };
                    _controller.SetSystemBackdropConfiguration(
                        GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
                    _controller.AddSystemBackdropTarget(connectedTarget);
                }
                catch (Exception)
                {
                    // 系统不支持 Thin 变体：退回内置 Acrylic（Base）。
                    _controller = null;
                }
            }

            protected override void OnTargetDisconnected(
                ICompositionSupportsSystemBackdrop disconnectedTarget)
            {
                base.OnTargetDisconnected(disconnectedTarget);

                if (_controller is null)
                {
                    return;
                }

                _controller.RemoveSystemBackdropTarget(disconnectedTarget);
                _controller.Dispose();
                _controller = null;
            }

            protected override void OnDefaultSystemBackdropConfigurationChanged(
                ICompositionSupportsSystemBackdrop target,
                XamlRoot xamlRoot)
            {
                _controller?.SetSystemBackdropConfiguration(
                    GetDefaultSystemBackdropConfiguration(target, xamlRoot));
            }
        }
    }
}
