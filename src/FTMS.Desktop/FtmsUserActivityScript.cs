namespace FTMS.Desktop;

internal static class FtmsUserActivityScript
{
    internal const string Value = """
        (() => {
          if (window !== window.top || window.__ftmsCompanionActivityInstalled) return;
          window.__ftmsCompanionActivityInstalled = true;
          let lastSent = 0;
          const report = () => {
            const now = Date.now();
            window.__ftmsCompanionLastInputAt = now;
            if (now - lastSent < 400) return;
            lastSent = now;
            window.chrome?.webview?.postMessage('ftms-user-activity');
          };
          for (const eventName of ['pointermove', 'pointerdown', 'keydown', 'input',
                                   'wheel', 'touchstart', 'scroll'])
            document.addEventListener(eventName, report, { capture: true, passive: true });
          window.addEventListener('focus', report);
        })();
        """;
}
