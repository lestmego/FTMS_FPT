namespace FTMS.Desktop;

internal static class FtmsStickyPagerScript
{
    internal const string Value = """
        (() => {
          if (window !== window.top || window.__ftmsCompanionStretchGridInstalled) return;
          window.__ftmsCompanionStretchGridInstalled = true;
          let frame = 0;
          const setStyle = (element, name, value) => {
            if (element.style.getPropertyValue(name) === value &&
                element.style.getPropertyPriority(name) === 'important') return;
            element.style.setProperty(name, value, 'important');
          };
          const clear = grid => {
            for (const element of [grid, grid?.querySelector('.k-grid-content')]) {
              if (!element) continue;
              for (const name of ['height','min-height','max-height']) element.style.removeProperty(name);
            }
          };
          const sync = () => {
            frame = 0;
            const grid = document.querySelector('#list-grid');
            if (!grid) return;
            if (location.hostname.toLowerCase() !== 'ftms.fpt.net' ||
                !/^\/ihub\/list\/?$/i.test(location.pathname)) {
              clear(grid);
              return;
            }
            const pager = grid.querySelector('.k-grid-pager, .k-pager-wrap');
            const content = grid.querySelector('.k-grid-content');
            if (!pager || !content) return;
            const gridTop = grid.getBoundingClientRect().top;
            const footer = document.querySelector('footer, .main-footer, #footer, .footer');
            const footerRect = footer?.getBoundingClientRect();
            const bottomLimit = footerRect && footerRect.top > gridTop && footerRect.top <= innerHeight
              ? footerRect.top - 10
              : innerHeight - 10;
            const targetGridHeight = Math.max(180, bottomLimit - gridTop);
            const nonContentHeight = Math.max(0, grid.getBoundingClientRect().height - content.getBoundingClientRect().height);
            const targetContentHeight = Math.max(100, targetGridHeight - nonContentHeight);
            setStyle(grid, 'height', `${targetGridHeight}px`);
            setStyle(grid, 'min-height', `${targetGridHeight}px`);
            setStyle(content, 'height', `${targetContentHeight}px`);
            setStyle(content, 'min-height', `${targetContentHeight}px`);
            setStyle(content, 'max-height', `${targetContentHeight}px`);
          };
          const schedule = () => {
            if (!frame) frame = requestAnimationFrame(sync);
          };
          const install = () => {
            schedule();
            new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
            new ResizeObserver(schedule).observe(document.documentElement);
            addEventListener('resize', schedule, { passive: true });
            addEventListener('scroll', schedule, { passive: true, capture: true });
          };
          if (document.body) install();
          else addEventListener('DOMContentLoaded', install, { once: true });
        })();
        """;
}
