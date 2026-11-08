// procure-ai: works around blank tables in the Aspire dashboard (plan 08b finding 2; dashboard 13.6.2 and 13.6.4).
//
// Structured logs and Traces are virtualized grids (Blazor Virtualize inside a Fluent data grid). The page first renders the grid with
// no items, then with the real count, and keeps it scrolled to the newest row at the bottom. When the browser is far from the dashboard
// (~180 ms round trip from South Africa to the VM), the spacer measurements the browser sends race with the server's renders, and the
// grid can end in a state where it waits for rows that it never asks for: the body holds only its two spacers (no rows), both spacers stay
// in view, so the browser's IntersectionObserver never fires again and nothing asks the server for rows. The page stays blank until
// something resizes a spacer (resizing the window does; that re-observes the spacers). Measured in a lab (the dashboard image in Docker,
// a proxy adding 90 ms each way): 9 of 10 loads blank without this script, 0 of 10 without the added delay.
//
// This script does what the window resize does, only when needed: once a second it looks for a data grid that has rows
// (aria-rowcount > 1, the header row counts as one) but has rendered none for at least a second, and narrows that table by one pixel for
// two animation frames. The spacers' ResizeObserver then re-observes them and the grid asks for its rows. A grid with rows, an empty grid
// and every other page are left alone. Served by the dashboard pod's proxy (dashboard-proxy.conf), same origin, so the dashboard's CSP
// (script-src 'self') allows it. Remove it (dashboard.virtualizeNudge.enabled) once an Aspire release fixes the race: the live check
// procure-ai-gitops tests/Test-Dashboard.ps1 -NoNudge shows whether the tables load without it.
(function () {
  'use strict';
  if (window.__procureVirtualizeNudge) { return; }
  window.__procureVirtualizeNudge = { nudges: 0 };

  var CHECK_MS = 500;
  var BLANK_MS = 1000;
  var blankSince = new WeakMap();

  function isBlank(table) {
    var count = Number(table.getAttribute('aria-rowcount'));
    return count > 1 && table.querySelectorAll('tbody [role="row"]').length === 0;
  }

  function nudge(table) {
    var width = table.style.width;
    table.style.width = 'calc(100% - 1px)';
    window.requestAnimationFrame(function () {
      window.requestAnimationFrame(function () { table.style.width = width; });
    });
    window.__procureVirtualizeNudge.nudges++;
  }

  window.setInterval(function () {
    var tables = document.querySelectorAll('table.fluent-data-grid');
    for (var i = 0; i < tables.length; i++) {
      var table = tables[i];
      if (!isBlank(table)) { blankSince.delete(table); continue; }
      var since = blankSince.get(table);
      var now = Date.now();
      if (since === undefined) { blankSince.set(table, now); continue; }
      if (now - since >= BLANK_MS) { nudge(table); blankSince.set(table, now); }
    }
  }, CHECK_MS);
})();
