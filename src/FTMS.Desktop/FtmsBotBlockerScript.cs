namespace FTMS.Desktop;

internal static class FtmsBotBlockerScript
{
    internal const string Source = "https://ftmslite.fpt.vn/agent-ai/js/bot.js";

    internal const string Value = """
        (() => {
          if (window !== window.top || window.__ftmsCompanionBotBlockerInstalled) return;
          window.__ftmsCompanionBotBlockerInstalled = true;
          const removeBot = () => {
            document.querySelectorAll('script[src*="ftmslite.fpt.vn/agent-ai/js/bot.js" i]').forEach(x => x.remove());
            document.querySelectorAll('[id*="agent-ai" i], [class*="agent-ai" i], [id*="chatbot" i], [class*="chatbot" i], iframe[src*="agent-ai" i], iframe[src*="ftmslite.fpt.vn" i]')
              .forEach(x => x.remove());
          };
          const install = () => {
            removeBot();
            new MutationObserver(removeBot).observe(document.body, { childList: true, subtree: true });
          };
          if (document.body) install();
          else addEventListener('DOMContentLoaded', install, { once: true });
        })();
        """;
}
